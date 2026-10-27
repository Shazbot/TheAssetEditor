using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Editors.PackOptimizer.Core;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel;

// lodscan: runs EditorLodDetector over packs and/or folders of .rigid_model_v2 and writes one TSV row per rigid.
//
//   PackOptimizerBench --lodscan "<pack-or-folder>;<pack-or-folder>..." [--lodscan-out <tsv>] [--lodscan-game true]
//
// --lodscan-game true additionally scans every rigid of the WH3 game packs (the editor's game container).
internal static class LodScan
{
    private const int MaxLods = 6;

    public static int Run(IPackFileContainerLoader loader, string inputs, string outputPath, bool includeGame)
    {
        var rows = new ConcurrentBag<(string Source, string Path, string Line)>();
        var errors = new ConcurrentBag<string>();
        var counts = new Dictionary<string, (int Total, int Positive, int Single)>();
        var evidence = new ConcurrentDictionary<(string, EditorLodEvidence), int>();
        var sources = new List<(string Name, Func<IEnumerable<(string Path, Func<byte[]> Read)>> Files)>();

        foreach (var input in inputs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(input))
            {
                sources.Add((input, () => Directory.EnumerateFiles(input, "*.rigid_model_v2", SearchOption.AllDirectories)
                    .Select(file => (file, (Func<byte[]>)(() => File.ReadAllBytes(file))))));
            }
            else if (File.Exists(input))
            {
                var container = loader.CreateFromPackFile(PackFileContainerType.Normal, input, loadAsReadOnly: true);
                sources.Add((input, () => EnumerateContainer(container)));
            }
            else
            {
                Console.Error.WriteLine($"Input not found: {input}");
                return 2;
            }
        }

        if (includeGame)
        {
            var game = loader.CreateFromGameEnum(PackFileContainerType.Database, GameTypeEnum.Warhammer3);
            if (game == null)
            {
                Console.Error.WriteLine("Could not load the WH3 game packs.");
                return 3;
            }

            sources.Add(("<WH3 game packs>", () => EnumerateContainer(game)));
        }

        foreach (var (name, files) in sources)
        {
            var total = 0;
            var positive = 0;
            var single = 0;
            var list = files().ToList();
            Console.WriteLine($"{name}: {list.Count:N0} rigid(s)");
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
            {
                try
                {
                    var rigid = ModelFactory.Create().Load(item.Read());
                    var verdict = EditorLodDetector.Analyze(rigid);
                    Interlocked.Increment(ref total);
                    if (verdict.IsEditorGenerated)
                        Interlocked.Increment(ref positive);
                    evidence.AddOrUpdate((name, verdict.Evidence), 1, (_, count) => count + 1);
                    if (verdict.Signals.LodCount < 2)
                        Interlocked.Increment(ref single);
                    rows.Add((name, item.Path, FormatRow(name, item.Path, verdict)));
                }
                catch (Exception exception)
                {
                    errors.Add($"{item.Path}: {exception.Message}");
                }
            });

            counts[name] = (total, positive, single);
        }

        var sb = new StringBuilder();
        sb.AppendLine(Header());
        foreach (var row in rows.OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(row.Line);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, sb.ToString());
        foreach (var (name, count) in counts)
            Console.WriteLine($"{name}: scanned {count.Total:N0}, single-LOD {count.Single:N0}, editor-LOD positives {count.Positive:N0}");
        foreach (var entry in evidence.OrderBy(x => x.Key.Item1).ThenBy(x => x.Key.Item2))
            Console.WriteLine($"  {entry.Key.Item1}: {entry.Key.Item2} = {entry.Value:N0}");
        if (!errors.IsEmpty)
        {
            Console.WriteLine($"{errors.Count:N0} rigid(s) failed to load; first: {errors.First()}");
            File.WriteAllLines(Path.ChangeExtension(outputPath, ".errors.txt"), errors.OrderBy(x => x));
        }

        Console.WriteLine($"Wrote {outputPath}");
        return 0;
    }

    private static IEnumerable<(string Path, Func<byte[]> Read)> EnumerateContainer(IPackFileContainer container)
    {
        foreach (var (path, file) in container.GetAllFiles())
        {
            if (path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase))
                yield return (path, () => file.DataSource.ReadData());
        }
    }

    private static string Header()
    {
        var columns = new List<string>
        {
            "source", "path", "verdict", "evidence", "reason", "lods", "parts0", "verts0", "tris0",
            "padding", "dist_quality", "colour_black", "merged_lod2plus", "no_cinematic_lod2plus", "lod0_cinematic", "informative", "matching", "l1_reuse", "mirror0", "mirror_last", "black_lods", "black_lod0",
        };
        for (var lod = 1; lod < MaxLods; lod++)
            columns.AddRange(new[] { "parts", "verts", "tris", "target", "ratio", "tri_ratio", "info", "match", "ttarget", "tt_ratio", "tt_match", "disc", "cmp", "cmp_ok" }.Select(x => $"l{lod}_{x}"));
        return string.Join('\t', columns);
    }

    private static string FormatRow(string source, string path, EditorLodVerdict verdict)
    {
        var s = verdict.Signals;
        var cells = new List<string>
        {
            source, path, verdict.IsEditorGenerated ? "EDITOR" : "no", verdict.Evidence.ToString(), verdict.Reason,
            s.LodCount.ToString(CultureInfo.InvariantCulture), s.Lod0Parts.ToString(CultureInfo.InvariantCulture),
            s.Lod0Vertices.ToString(CultureInfo.InvariantCulture), s.Lod0Triangles.ToString(CultureInfo.InvariantCulture),
            Flag(s.HeaderPaddingMatches), Flag(s.DefaultDistancesAndQuality), Flag(s.Lod0ColourAllBlack), Flag(s.MergedPartsAtLod2Plus),
            Flag(s.NoCinematicAtLod2Plus), Flag(s.Lod0HasCinematic),
            s.InformativeLevels.ToString(CultureInfo.InvariantCulture), s.MatchingInformativeLevels.ToString(CultureInfo.InvariantCulture),
            s.Lod1VertexReuse.ToString("0.000", CultureInfo.InvariantCulture), s.Lod0MirroredShare.ToString("0.000", CultureInfo.InvariantCulture),
            s.LastLodMirroredShare.ToString("0.000", CultureInfo.InvariantCulture),
            s.LodsBlackColourShare.ToString("0.000", CultureInfo.InvariantCulture), s.Lod0BlackColourShare.ToString("0.000", CultureInfo.InvariantCulture),
        };

        for (var lod = 1; lod < MaxLods; lod++)
        {
            var level = s.Levels.FirstOrDefault(x => x.LodIndex == lod);
            if (level == null)
            {
                cells.AddRange(Enumerable.Repeat(string.Empty, 14));
                continue;
            }

            var triRatio = s.Lod0Triangles == 0 ? 0 : level.Triangles / (double)s.Lod0Triangles;
            cells.AddRange(new[]
            {
                level.Parts.ToString(CultureInfo.InvariantCulture), level.Vertices.ToString(CultureInfo.InvariantCulture),
                level.Triangles.ToString(CultureInfo.InvariantCulture), level.ExpectedTriangles.ToString(CultureInfo.InvariantCulture),
                level.VertexTargetRatio.ToString("0.000", CultureInfo.InvariantCulture), triRatio.ToString("0.000", CultureInfo.InvariantCulture),
                Flag(level.Informative), Flag(level.VertexTargetMatches),
                level.TriangleBasedTarget.ToString(CultureInfo.InvariantCulture), level.TriangleTargetRatio.ToString("0.000", CultureInfo.InvariantCulture), Flag(level.TriangleTargetMatches), Flag(level.Discriminating),
                level.PartsCompared.ToString(CultureInfo.InvariantCulture), level.PartsMatching.ToString(CultureInfo.InvariantCulture),
            });
        }

        return string.Join('\t', cells);
    }

    private static string Flag(bool value) => value ? "1" : "0";
}
