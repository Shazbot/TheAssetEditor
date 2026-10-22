using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AssetEditor.Services;
using Editors.PackOptimizer.Core;
using PackOptimizerApi = Editors.PackOptimizer.Core.PackOptimizer;
using Microsoft.Extensions.DependencyInjection;
using Shared.Core.Misc;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Settings;

// Headless runner for PackOptimizer.Run, for measuring the batch tool's runtime
// and checking that an optimisation leaves its output unchanged (fingerprint.txt).
//
//   PackOptimizerBench [--source <pack>] [--game-dir <data folder>] [--out <folder>] [--tag <name>] [--atlases true] [--army-scope culture|subculture]
//                      [--consumer <pack>]... [--consumer-list <file>] [--source-index <n>]
//
// --consumer (repeatable) and --consumer-list (one pack path per line, # comments) name the other
// enabled mods in load order, first loads first and loses, last wins; --source-index is how many
// of them load before the source (default: all of them). Consumers are only read.
//   PackOptimizerBench --make-synthetic-consumer <out.pack> [--source <pack>]
//     writes a small test mod (a reskin of two source textures and a VMD reusing two WSModels).
//
// The output pack, the report and the fingerprint are written to <out>\<tag>\ (default
// K:\projects\assedfork-bench\run), never next to the source pack.
internal static class Program
{
    private const string DefaultSource = @"K:\wh3mods\albion\!ak_teb3.pack";
    private const string DefaultGameDirectory = @"K:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data";
    private const string DefaultOutputRoot = @"K:\projects\assedfork-bench";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = ParseArguments(args);
        var source = options.GetValueOrDefault("source", DefaultSource);
        var gameDirectory = options.GetValueOrDefault("game-dir", DefaultGameDirectory);
        var outputRoot = options.GetValueOrDefault("out", DefaultOutputRoot);
        var tag = options.GetValueOrDefault("tag", "run");
        var buildAtlases = options.GetValueOrDefault("atlases", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

        var runDirectory = Path.Combine(outputRoot, tag);
        if (Path.GetFullPath(runDirectory).StartsWith(@"K:\wh3mods", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFullPath(runDirectory).StartsWith(Path.GetDirectoryName(source) ?? "?", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to write next to the source pack or into the mod folder.");
            return 2;
        }

        Directory.CreateDirectory(runDirectory);
        var outputPath = Path.Combine(runDirectory, "bench_output.pack");
        var total = Stopwatch.StartNew();

        var services = new DependencyInjectionConfig(false).Build(false);
        var settings = services.GetRequiredService<ApplicationSettingsService>();
        settings.AllowSettingsUpdate = false;
        settings.CurrentSettings.CurrentGame = GameTypeEnum.Warhammer3;
        settings.CurrentSettings.LoadCaPacksByDefault = true;
        settings.CurrentSettings.GameDirectories.RemoveAll(x => x.Game == GameTypeEnum.Warhammer3);
        settings.CurrentSettings.GameDirectories.Add(new ApplicationSettings.GamePathPair(GameTypeEnum.Warhammer3, gameDirectory));

        var loader = services.GetRequiredService<IPackFileContainerLoader>();
        if (options.TryGetValue("make-synthetic-consumer", out var syntheticPath))
            return MakeSyntheticConsumer(loader, source, syntheticPath);

        var consumerPaths = ReadConsumerPaths(args, options);
        var loadAllConsumerFiles = options.GetValueOrDefault("consumer-load", "filtered")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        // Times only the loading of the consumer packs (the asset host's loader).
        if (options.GetValueOrDefault("load-only", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            var watch = Stopwatch.StartNew();
            var listed = 0;
            foreach (var consumerPath in consumerPaths)
                listed += WH3AssetHost.Optimize.HostVanillaContainers.LoadConsumer(consumerPath, loadAllConsumerFiles).GetFileCount();
            Console.WriteLine($"Loaded {consumerPaths.Count} consumer pack(s) ({(loadAllConsumerFiles ? "full" : "filtered")}) in {watch.Elapsed.TotalSeconds:0.0} s; {listed:N0} file(s) listed.");
            return 0;
        }

        // Fingerprint of an existing output (for runs made by another host, e.g. WH3AssetHost.exe):
        //   --fingerprint-pack <pack> --fingerprint-report <report> --fingerprint-to <file>
        if (options.TryGetValue("fingerprint-pack", out var fingerprintPack))
        {
            WriteFingerprint(
                loader,
                fingerprintPack,
                options.GetValueOrDefault("fingerprint-report", string.Empty),
                options.GetValueOrDefault("fingerprint-to", fingerprintPack + ".fingerprint.txt"));
            Console.WriteLine($"Fingerprint written to {options.GetValueOrDefault("fingerprint-to", fingerprintPack + ".fingerprint.txt")}.");
            return 0;
        }

        // Compares which pack wins a path between the editor's merged game container and the
        // per-pack containers the asset host builds from whmm's file index:
        //   --check-container-order true --vanilla-index <vanilla-pack-files-cache.bin>
        if (options.GetValueOrDefault("check-container-order", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
            return CheckContainerOrder(loader, gameDirectory, options.GetValueOrDefault("vanilla-index", string.Empty));

        var loadStopwatch = Stopwatch.StartNew();
        var caPacks = loader.CreateFromGameEnum(PackFileContainerType.Database, GameTypeEnum.Warhammer3);
        if (caPacks == null)
        {
            Console.Error.WriteLine($"Could not load the game packs from {gameDirectory}.");
            return 3;
        }

        Console.WriteLine($"Game packs loaded in {loadStopwatch.Elapsed.TotalSeconds:0.0} s.");

        var lastStage = string.Empty;
        var progress = new Progress<PackOptimizerProgress>(p =>
        {
            if (p.Stage == lastStage)
                return;

            Console.WriteLine($"  [{total.Elapsed.TotalSeconds,7:0.0} s] {p.Stage}");
            lastStage = p.Stage;
        });

        // The options of the editor's last real run: no atlases, everything else on, the
        // progress window's defaults for merge/share/optimize. The library gets everything
        // explicitly: the source pack, the read-only game packs, the output and report paths.
        var source_ = loader.CreateFromPackFile(PackFileContainerType.Normal, source, loadAsReadOnly: true);
        var consumerLoadStopwatch = Stopwatch.StartNew();
        var consumers = new List<IPackFileContainer>();
        foreach (var consumerPath in consumerPaths)
        {
            // The asset host's loader, so the bench measures what the host does.
            consumers.Add(WH3AssetHost.Optimize.HostVanillaContainers.LoadConsumer(consumerPath, loadAllConsumerFiles));
        }

        var consumerLoadSeconds = consumerLoadStopwatch.Elapsed.TotalSeconds;
        int? sourceIndex = options.TryGetValue("source-index", out var sourceIndexText)
            ? int.Parse(sourceIndexText, CultureInfo.InvariantCulture)
            : null;
        if (consumers.Count != 0)
            Console.WriteLine($"{consumers.Count} consumer pack(s) loaded in {consumerLoadSeconds:0.0} s; the source loads after {sourceIndex ?? consumers.Count} of them.");

        var armyScope = PackOptimizerArmyScope.Subculture;
        if (options.TryGetValue("army-scope", out var armyScopeText) &&
            !Enum.TryParse(armyScopeText, ignoreCase: true, out armyScope))
        {
            Console.Error.WriteLine($"Unknown --army-scope '{armyScopeText}' (culture|subculture).");
            return 2;
        }

        var request = new PackOptimizerRequest(
            source,
            source_,
            [caPacks],
            outputPath,
            PackOptimizerApi.BuildReportPath(outputPath),
            GameTypeEnum.Warhammer3,
            new PackOptimizerOptions
            {
                MergeCompatibleMeshes = true,
                ShareAtlasesAcrossVmds = true,
                OptimizeGeometry = true,
                AtlasAllVmds = false,
                ScoreAllGameUnits = false,
                LodAwareAtlasPlanning = true,
                ArmyScope = armyScope,
                CrossRigidOnly = false,
                CrossRigidMerges = true,
                BuildAtlases = buildAtlases,
                DownscaleTextures = true,
                SharedTextureRepack = true,
                CompactTextures = true,
                DropUnreadAlpha = true,
            },
            MissingTexturePolicy.Skip,
            // --cache-dir <folder> uses another folder for the vanilla consumer cache, "none" disables it.
            options.TryGetValue("cache-dir", out var cacheDir)
                ? (cacheDir.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : cacheDir)
                : DirectoryHelper.CacheDirectory,
            ModConsumers: consumers,
            SourceLoadOrderIndex: sourceIndex,
            DbSchema: options.TryGetValue("db-schema", out var dbSchemaPath)
                ? PackOptimizerDbSchema.Load(dbSchemaPath)
                : null);
        var processStopwatch = Stopwatch.StartNew();
        var result = PackOptimizerApi.Run(request, progress, CancellationToken.None);
        processStopwatch.Stop();
        Console.WriteLine($"Process finished in {processStopwatch.Elapsed.TotalSeconds:0.0} s (report: {result.ReportPath}).");
        Console.WriteLine(
            $"Game-pack lookups: {Shared.Core.PackFiles.Models.Containers.PackFileLookupDiagnostics.Lookups:N0}, " +
            $"cache hits {Shared.Core.PackFiles.Models.Containers.PackFileLookupDiagnostics.Hits:N0}");
        Console.WriteLine(
            $"GC: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, " +
            $"pause {GC.GetTotalPauseDuration().TotalSeconds:0.0} s, allocated {GC.GetTotalAllocatedBytes() / (1024 * 1024):N0} MiB, " +
            $"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024):N0} MiB, " +
            $"server GC {System.Runtime.GCSettings.IsServerGC}, concurrent {System.Runtime.GCSettings.LatencyMode}");

        var fingerprintStopwatch = Stopwatch.StartNew();
        WriteFingerprint(loader, outputPath, result.ReportPath, Path.Combine(runDirectory, "fingerprint.txt"));
        Console.WriteLine($"Fingerprint written in {fingerprintStopwatch.Elapsed.TotalSeconds:0.0} s.");
        File.WriteAllText(
            Path.Combine(runDirectory, "wall_time.txt"),
            $"process_seconds={processStopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
            $"total_seconds={total.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
            $"consumer_packs={consumers.Count}{Environment.NewLine}" +
            $"consumer_load_seconds={consumerLoadSeconds.ToString("0.0", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
            $"peak_working_set_mib={Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}{Environment.NewLine}" +
            $"externally_used_files={result.ExternallyUsedFileCount}{Environment.NewLine}" +
            $"frozen_files={result.FrozenFileCount}{Environment.NewLine}");
        // The phase lines of the report that matter for consumers (their unit is the report's own).
        File.WriteAllLines(
            Path.Combine(runDirectory, "phases.txt"),
            File.ReadAllLines(result.ReportPath).Where(line =>
                line.StartsWith("Scan consumer packs:", StringComparison.Ordinal) ||
                line.StartsWith("Index consumer references:", StringComparison.Ordinal) ||
                line.StartsWith("Resolve unit categories", StringComparison.Ordinal) ||
                line.StartsWith("Prune unused assets:", StringComparison.Ordinal) ||
                line.StartsWith("Validate output:", StringComparison.Ordinal)));
        foreach (var warning in result.Warnings ?? [])
            Console.WriteLine($"WARNING: {warning}");
        return 0;
    }

    private static List<string> ReadConsumerPaths(string[] args, Dictionary<string, string> options)
    {
        var paths = new List<string>();
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--consumer", StringComparison.OrdinalIgnoreCase))
                paths.Add(args[index + 1]);
        }

        if (options.TryGetValue("consumer-list", out var listPath))
        {
            paths.AddRange(File.ReadAllLines(listPath)
                .Select(line => line.Trim())
                .Where(line => line.Length != 0 && !line.StartsWith('#')));
        }

        return paths;
    }

    // A tiny stand-in for a reskin plus a submod, for manual end-to-end runs: two source textures
    // overridden with their own bytes, and a VMD of its own that reuses two source WSModels.
    private static int MakeSyntheticConsumer(IPackFileContainerLoader loader, string sourcePath, string outputPath)
    {
        var source = loader.CreateFromPackFile(PackFileContainerType.Normal, sourcePath, loadAsReadOnly: true);
        var files = source.GetAllFiles();
        var textures = files.Keys
            .Where(path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
                           path.Contains("\\morion\\tex\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        var wsModels = files.Keys
            .Where(path => path.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) &&
                           path.Contains("\\morion\\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        if (textures.Count < 2 || wsModels.Count < 2)
        {
            Console.Error.WriteLine("The source has no morion textures/WSModels to build the synthetic consumer from.");
            return 3;
        }

        var vmd = "<VARIANT_MESH>" + Environment.NewLine +
                  string.Concat(wsModels.Select((path, index) =>
                      $"\t<SLOT name=\"synth{index}\" >{Environment.NewLine}\t\t<VARIANT_MESH model=\"{path}\"  />{Environment.NewLine}\t</SLOT>{Environment.NewLine}")) +
                  "</VARIANT_MESH>";
        var container = Shared.Core.PackFiles.Models.Containers.PackFileContainer.CreatePackFile(
            Path.GetFileNameWithoutExtension(outputPath), null, Shared.Core.Settings.PackFileVersion.PFH5);
        var entries = new List<Shared.Core.PackFiles.Models.NewPackFileEntry>();
        foreach (var texture in textures)
        {
            entries.Add(new Shared.Core.PackFiles.Models.NewPackFileEntry(
                Path.GetDirectoryName(texture) ?? string.Empty,
                new Shared.Core.PackFiles.Models.PackFile(
                    Path.GetFileName(texture),
                    new Shared.Core.PackFiles.Models.FileSources.MemorySource(files[texture].DataSource.ReadData()))));
        }

        entries.Add(new Shared.Core.PackFiles.Models.NewPackFileEntry(
            "variantmeshes\\variantmeshdefinitions",
            new Shared.Core.PackFiles.Models.PackFile(
                "zz_synth_submod.variantmeshdefinition",
                new Shared.Core.PackFiles.Models.FileSources.MemorySource(Encoding.UTF8.GetBytes(vmd)))));
        ((Shared.Core.PackFiles.Models.IPackFileContainerInternal)container).AddFiles(entries);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        ((Shared.Core.PackFiles.Models.IPackFileContainerInternal)container).SaveToDisk(
            outputPath, false, GameInformationDatabase.GetGameById(GameTypeEnum.Warhammer3));
        Console.WriteLine($"Wrote {outputPath}: overrides {string.Join(", ", textures)}; VMD reuses {string.Join(", ", wsModels)}.");
        return 0;
    }

    private static int CheckContainerOrder(IPackFileContainerLoader loader, string dataFolder, string indexPath)
    {
        var editor = loader.CreateFromGameEnum(PackFileContainerType.Database, GameTypeEnum.Warhammer3);
        if (editor == null)
        {
            Console.Error.WriteLine("Could not load the editor's game container.");
            return 3;
        }

        var hostContainers = WH3AssetHost.Optimize.HostVanillaContainers.LoadVanilla(dataFolder, indexPath, Console.Error.WriteLine);
        Console.WriteLine($"Host containers: {hostContainers.Count}; order starts {string.Join(", ", hostContainers.Take(4).Select(c => c.Name))} ... ends {string.Join(", ", hostContainers.TakeLast(3).Select(c => c.Name))}");

        // Winner per path in the host order (later container wins), and how many containers hold it.
        var winner = new Dictionary<string, (IPackFileContainer Container, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in hostContainers)
        {
            foreach (var path in container.GetAllFiles().Keys)
                winner[path] = (container, winner.TryGetValue(path, out var previous) ? previous.Count + 1 : 1);
        }

        var duplicated = winner.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Console.WriteLine($"Host unique paths: {winner.Count:N0}; paths present in more than one pack: {duplicated.Count:N0}");

        var checkedCount = 0;
        var mismatches = new List<string>();
        var missingInEditor = 0;
        var step = Math.Max(1, duplicated.Count / 5000);
        for (var index = 0; index < duplicated.Count; index += step)
        {
            var path = duplicated[index];
            var hostFile = winner[path].Container.FindFile(path);
            var editorFile = editor.FindFile(path);
            if (editorFile == null || hostFile == null)
            {
                missingInEditor++;
                continue;
            }

            checkedCount++;
            var hostHash = Convert.ToHexString(SHA256.HashData(hostFile.DataSource.ReadData()));
            var editorHash = Convert.ToHexString(SHA256.HashData(editorFile.DataSource.ReadData()));
            if (hostHash != editorHash)
                mismatches.Add($"{path} (host winner {winner[path].Container.Name})");
        }

        // Paths unique to one pack must resolve identically too: sample those.
        var uniquePaths = winner.Where(pair => pair.Value.Count == 1).Select(pair => pair.Key).ToList();
        var uniqueStep = Math.Max(1, uniquePaths.Count / 3000);
        var uniqueChecked = 0;
        for (var index = 0; index < uniquePaths.Count; index += uniqueStep)
        {
            var path = uniquePaths[index];
            var hostFile = winner[path].Container.FindFile(path);
            var editorFile = editor.FindFile(path);
            if (editorFile == null || hostFile == null)
            {
                missingInEditor++;
                continue;
            }

            uniqueChecked++;
            if (Convert.ToHexString(SHA256.HashData(hostFile.DataSource.ReadData())) != Convert.ToHexString(SHA256.HashData(editorFile.DataSource.ReadData())))
                mismatches.Add($"{path} (unique, host {winner[path].Container.Name})");
        }

        Console.WriteLine($"Compared {checkedCount:N0} duplicated and {uniqueChecked:N0} unique paths; mismatches: {mismatches.Count}; not found on one side: {missingInEditor}");
        foreach (var mismatch in mismatches.Take(20))
            Console.WriteLine($"  MISMATCH {mismatch}");
        return mismatches.Count == 0 ? 0 : 1;
    }

    private static void WriteFingerprint(
        IPackFileContainerLoader loader,
        string outputPath,
        string reportPath,
        string fingerprintPath)
    {
        var container = loader.CreateFromPackFile(PackFileContainerType.Normal, outputPath, loadAsReadOnly: true);
        var lines = new List<string>();
        foreach (var (path, file) in container.GetAllFiles().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{path.ToLowerInvariant()} {Convert.ToHexString(SHA256.HashData(file.DataSource.ReadData()))}");

        var sb = new StringBuilder();
        sb.AppendLine($"files: {lines.Count}");
        foreach (var line in lines)
            sb.AppendLine(line);

        sb.AppendLine("== report (timing lines removed)");
        foreach (var line in File.ReadAllLines(reportPath))
        {
            if (!IsTimingLine(line))
                sb.AppendLine(line);
        }

        File.WriteAllText(fingerprintPath, sb.ToString());
    }

    // Lines that carry a duration or a wall-clock time. Stripping them is the only thing that
    // may differ between two runs of the same code; anything else that differs is a real
    // difference in the output.
    private static readonly Regex TimingPattern = new(
        @"(\d[\d.,]*\s?(ms|s|min)\b)|\bwall\b|\bduration|\belapsed\b|\btime\b|\btimes\b|^Generated:|^(Output|Report):|timing",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "DB schema source:" lines exist only when a schema was supplied; leaving them out lets such a
    // run be compared with one that decoded with the embedded schema.
    private static bool IsTimingLine(string line)
        => TimingPattern.IsMatch(line) || line.StartsWith("DB schema source:", StringComparison.Ordinal);

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
            result[args[index].TrimStart('-')] = args[index + 1];
        return result;
    }
}
