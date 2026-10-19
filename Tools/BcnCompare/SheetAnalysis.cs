using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BCnEncoder.Encoder;
using DirectXTexNet;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Serialization;
using Shared.Core.PackFiles.Utility;

// Final sheets (after block copy) against the uncompressed composed input the optimizer handed to
// DirectXTex, plus how well each encoder does on just the blocks that are still encoded.
//   BcnCompare --inputs <capture with copymaps> --sheets <old pack> --new <new pack> --out <dir>
internal static class SheetAnalysis
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // SharedRepackBlockFate values.
    private static readonly string[] FateNames = ["gutter", "generated", "partial", "misaligned", "beyond-mips", "format-mismatch", "unreadable", "copied"];

    public static void Run(List<Capture> captures, string newPack, string oldPack, string outDir)
    {
        var inputRoot = captures.Count == 0 ? string.Empty : Path.GetDirectoryName(Directory.GetDirectories(Path.GetDirectoryName(Path.GetFullPath(newPack))!).FirstOrDefault() ?? string.Empty) ?? string.Empty;
        _ = inputRoot;
        var inputs = Environment.GetEnvironmentVariable("BCN_COMPARE_INPUTS") ?? @"K:\projects\assedfork-bench\bcn-compare\inputs-bc";
        var oldContainer = Load(oldPack);
        var newContainer = Load(newPack);
        var sheets = new StringBuilder("sequence,source,slot,format,width,height,version,rgb_psnr_mip0,a_psnr_mip0,maxabs_rgb_mip0,wpsnr_mip0,rgb_psnr_chain,maxabs_rgb_chain\n");
        var remaining = new StringBuilder("sequence,source,slot,format,level,level_width,level_height,fate,blocks,texels,encoder,sse_rgb,sse_a,max_rgb,max_a\n");
        var coverage = new StringBuilder("sequence,source,slot,format,width,height,level,level_width,level_height,blocks_total,copied,gutter,generated,partial,misaligned,beyond_mips,format_mismatch,unreadable\n");

        var settings = new (string Name, Program.Setting Setting)[]
        {
            ("DXT-Default", new Program.Setting("d", Program.Encoder.DirectXTex, TEX_COMPRESS_FLAGS.DEFAULT, 0, false)),
            ("BCn-Balanced", new Program.Setting("b", Program.Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, true)),
            ("BCn-Best", new Program.Setting("x", Program.Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.BestQuality, true)),
        };

        foreach (var capture in captures)
        {
            var slot = new[] { "base_colour", "material_map", "normal", "mask" }.FirstOrDefault(name => capture.Source.EndsWith("_" + name + ".dds", StringComparison.OrdinalIgnoreCase)) ?? "other";
            var format = capture.IsBc1 ? "BC1" : "BC3";
            Console.WriteLine($"[{capture.Sequence}] {slot} {format} {capture.Width}x{capture.Height}");

            // The sheet as encoded, before any block copy (what DirectXTex produced), and the two final sheets.
            var precopy = Program.DecodeWithDirectXTex(capture, Program.EncodeDirectXTex(capture, settings[0].Setting));
            AddSheet(sheets, capture, slot, format, "dxt-encode-only", precopy);
            var oldFinal = DecodePackTexture(oldContainer, capture);
            if (oldFinal != null)
                AddSheet(sheets, capture, slot, format, "old-final", oldFinal);
            var newFinal = DecodePackTexture(newContainer, capture);
            if (newFinal != null)
                AddSheet(sheets, capture, slot, format, "new-final", newFinal);

            // Block fates for this sheet.
            var map = ReadCopyMap(Path.Combine(inputs, "copymaps", capture.Source.Replace('\\', '_').Replace('/', '_') + ".copymap"));
            for (var level = 0; level < capture.MipLevels; level++)
            {
                var counts = new long[FateNames.Length];
                foreach (var fate in map[level].Fates)
                    counts[fate]++;
                coverage.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), slot, format, capture.Width, capture.Height, level, map[level].BlocksWide, map[level].BlocksHigh, map[level].Fates.Length, counts[7], counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6]));
            }

            // Encoder comparison restricted to the blocks that stay encoded.
            foreach (var (name, setting) in settings)
            {
                var blocks = setting.Encoder == Program.Encoder.DirectXTex ? Program.EncodeDirectXTex(capture, setting) : Program.EncodeBcn(capture, setting);
                var decoded = Program.DecodeWithDirectXTex(capture, blocks);
                for (var level = 0; level < capture.MipLevels; level++)
                {
                    var perFate = new Accumulator[FateNames.Length];
                    for (var i = 0; i < perFate.Length; i++)
                        perFate[i] = new Accumulator();
                    var width = capture.MipWidth(level);
                    var height = capture.MipHeight(level);
                    var input = capture.MipBgra[level];
                    var output = decoded[level];
                    var cells = map[level];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var fate = cells.Fates[(y / 4) * cells.BlocksWide + x / 4];
                            perFate[fate].Add(input, output, (y * width + x) * 4);
                        }
                    }

                    for (var fate = 0; fate < FateNames.Length; fate++)
                    {
                        if (perFate[fate].Texels == 0)
                            continue;
                        var blockCount = map[level].Fates.Count(f => f == fate);
                        remaining.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), slot, format, level, width, height, FateNames[fate], blockCount, perFate[fate].Texels, name,
                            perFate[fate].SseRgb.ToString("R", Inv), perFate[fate].SseA.ToString("R", Inv), perFate[fate].MaxRgb, perFate[fate].MaxA));
                    }
                }
            }
        }

        File.WriteAllText(Path.Combine(outDir, "sheets.csv"), sheets.ToString());
        File.WriteAllText(Path.Combine(outDir, "coverage.csv"), coverage.ToString());
        File.WriteAllText(Path.Combine(outDir, "remaining.csv"), remaining.ToString());
    }

    /// <summary>
    /// Writes level-0 (and level-4) PNGs of chosen sheets: the DirectXTex encode alone, the old final sheet
    /// and the new final sheet, cropped to at most 1024 pixels around the densest part. RGB only.
    /// </summary>
    public static void WritePngs(List<Capture> captures, string oldPack, string newPack, string outDir, string[] pick)
    {
        var inputs = Environment.GetEnvironmentVariable("BCN_COMPARE_INPUTS") ?? @"K:\projects\assedfork-bench\bcn-compare\inputs-bc";
        var oldContainer = Load(oldPack);
        var newContainer = Load(newPack);
        Directory.CreateDirectory(outDir);
        foreach (var capture in captures.Where(c => pick.Any(p => c.Source.Contains(p, StringComparison.OrdinalIgnoreCase))))
        {
            var name = Path.GetFileNameWithoutExtension(capture.Source);
            var encodeOnly = Program.DecodeWithDirectXTex(capture, Program.EncodeDirectXTex(capture, new Program.Setting("d", Program.Encoder.DirectXTex, TEX_COMPRESS_FLAGS.DEFAULT, 0, false)));
            var oldFinal = DecodePackTexture(oldContainer, capture);
            var newFinal = DecodePackTexture(newContainer, capture);
            if (oldFinal == null || newFinal == null)
                continue;

            var map = ReadCopyMap(Path.Combine(inputs, "copymaps", capture.Source.Replace('\\', '_').Replace('/', '_') + ".copymap"));
            foreach (var level in new[] { 0, 4 })
            {
                var width = capture.MipWidth(level);
                var height = capture.MipHeight(level);
                var window = 1024;
                var cropW = Math.Min(window, width);
                var cropH = Math.Min(window, height);
                // The window with the most copied (i.e. content) blocks.
                var bestX = 0;
                var bestY = 0;
                var bestScore = -1;
                for (var y = 0; y + cropH <= height; y += Math.Max(4, cropH / 4))
                {
                    for (var x = 0; x + cropW <= width; x += Math.Max(4, cropW / 4))
                    {
                        var score = 0;
                        for (var by = y / 4; by < (y + cropH) / 4; by += 2)
                            for (var bx = x / 4; bx < (x + cropW) / 4; bx += 2)
                                if (map[level].Fates[by * map[level].BlocksWide + bx] == 7)
                                    score++;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                void Save(byte[] bgra, string version)
                {
                    using var bitmap = new System.Drawing.Bitmap(cropW, cropH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    for (var y = 0; y < cropH; y++)
                    {
                        for (var x = 0; x < cropW; x++)
                        {
                            var i = ((bestY + y) * width + bestX + x) * 4;
                            bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, bgra[i + 2], bgra[i + 1], bgra[i]));
                        }
                    }

                    bitmap.Save(Path.Combine(outDir, $"{name}_L{level}_{version}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }

                Save(encodeOnly[level], "1_encode-only");
                Save(oldFinal[level], "2_old-final");
                Save(newFinal[level], "3_new-final");
                Console.WriteLine($"{name} L{level}: {cropW}x{cropH} at ({bestX},{bestY}) of {width}x{height}");
            }
        }
    }

    private sealed class Accumulator
    {
        public long Texels;
        public double SseRgb;
        public double SseA;
        public int MaxRgb;
        public int MaxA;

        public void Add(byte[] input, byte[] output, int offset)
        {
            Texels++;
            for (var c = 0; c < 3; c++)
            {
                var d = input[offset + c] - output[offset + c];
                SseRgb += d * d;
                MaxRgb = Math.Max(MaxRgb, Math.Abs(d));
            }

            var a = input[offset + 3] - output[offset + 3];
            SseA += a * a;
            MaxA = Math.Max(MaxA, Math.Abs(a));
        }
    }

    private static void AddSheet(StringBuilder sheets, Capture capture, string slot, string format, string version, byte[][] decoded)
    {
        var shown = decoded.Length == capture.MipLevels
            ? capture
            : new Capture
            {
                Sequence = capture.Sequence, Source = capture.Source, Stage = capture.Stage, Target = capture.Target, InputFormat = capture.InputFormat,
                Width = capture.Width, Height = capture.Height, MipLevels = decoded.Length, MipBgra = capture.MipBgra.Take(decoded.Length).ToArray(), HasAlphaBelow255 = capture.HasAlphaBelow255,
            };
        var metrics = Program.Metrics.Compute(shown, decoded);
        sheets.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), slot, format, capture.Width, capture.Height, version,
            metrics.Rgb0.ToString("0.####", Inv), metrics.A0.ToString("0.####", Inv), metrics.MaxAbsRgb0, metrics.W0.ToString("0.####", Inv),
            metrics.RgbChain.ToString("0.####", Inv), metrics.MaxAbsRgbChain));
    }

    private static PackFileContainer Load(string packPath)
    {
        using var stream = File.OpenRead(packPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        return PackFileSerializerLoader.Load(packPath, new FileInfo(packPath).Length, reader, new CaPackDuplicateFileResolver());
    }

    private static byte[][]? DecodePackTexture(PackFileContainer pack, Capture capture)
    {
        var file = pack.FindFile(capture.Source) ?? throw new InvalidDataException($"{capture.Source} is not in the pack");
        var dds = file.DataSource.ReadData();
        var handle = GCHandle.Alloc(dds, GCHandleType.Pinned);
        try
        {
            using var image = TexHelper.Instance.LoadFromDDSMemory(handle.AddrOfPinnedObject(), dds.Length, DDS_FLAGS.NONE);
            using var uncompressed = image.Decompress(capture.Srgb ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB : DXGI_FORMAT.B8G8R8A8_UNORM);
            var available = uncompressed.GetMetadata().MipLevels;
            if (uncompressed.GetMetadata().Width != capture.Width || uncompressed.GetMetadata().Height != capture.Height)
            {
                Console.WriteLine($"  note: {capture.Source} is {uncompressed.GetMetadata().Width}x{uncompressed.GetMetadata().Height} in the final pack (sheet was {capture.Width}x{capture.Height}); replaced after the repack, not comparable");
                return null;
            }

            if (available != capture.MipLevels)
                Console.WriteLine($"  note: {capture.Source} has {available} mip(s) in the final pack, the sheet had {capture.MipLevels}");
            var result = new byte[Math.Min(available, capture.MipLevels)][];
            for (var mip = 0; mip < result.Length; mip++)
                result[mip] = Program.ReadImage(uncompressed.GetImage(mip, 0, 0));
            return result;
        }
        finally
        {
            handle.Free();
        }
    }

    private sealed record LevelMap(int BlocksWide, int BlocksHigh, byte[] Fates);

    private static LevelMap[] ReadCopyMap(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var levels = reader.ReadInt32();
        var result = new LevelMap[levels];
        for (var level = 0; level < levels; level++)
        {
            var w = reader.ReadInt32();
            var h = reader.ReadInt32();
            result[level] = new LevelMap(w, h, reader.ReadBytes(w * h));
        }

        return result;
    }

    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
