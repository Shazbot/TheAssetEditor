using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using DirectXTexNet;

// Compares BCnEncoder.Net with DirectXTexNet on the encodes a real optimizer run performed.
//   BcnCompare --inputs <capture dir> --out <result dir> [--repeat 3]
// Quality is measured on the stored (sRGB-encoded) 8-bit values against the uncompressed input,
// and every result is decoded with the same decoder (DirectXTex), so only the encoder differs.
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
            options[args[i].TrimStart('-')] = args[i + 1];
        var inputs = options.GetValueOrDefault("inputs", @"K:\projects\assedfork-bench\bcn-compare\inputs");
        var outDir = options.GetValueOrDefault("out", @"K:\projects\assedfork-bench\bcn-compare");
        var repeat = int.Parse(options.GetValueOrDefault("repeat", "3"), Inv);
        Directory.CreateDirectory(outDir);

        if (options.GetValueOrDefault("parity", "false") == "true")
            return Parity.Run(inputs.Split(';'), outDir);

        var captures = Capture.LoadAll(inputs);
        Console.WriteLine($"{captures.Count} captured encodes.");
        if (options.GetValueOrDefault("parity", "false") == "true")
            return Parity.Run(inputs.Split(';'), outDir);

        if (options.TryGetValue("pngs", out var pngOld))
        {
            SheetAnalysis.WritePngs(captures, pngOld, options["new"], outDir, options["pick"].Split(','));
            return 0;
        }

        if (options.TryGetValue("sheets", out var sheetsOld))
        {
            SheetAnalysis.Run(captures, options["new"], sheetsOld, outDir);
            return 0;
        }

        if (options.GetValueOrDefault("alpha-threshold", "false") == "true")
        {
            AlphaThreshold(outDir);
            return 0;
        }

        if (options.TryGetValue("dumpmip", out var dump))
        {
            var c = captures.Single(x => x.Sequence == int.Parse(dump, Inv));
            using var full = c.BuildImage(c.Level0Before!, c.Width, c.Height, c.InputFormat);
            using var chain = full.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            File.WriteAllBytes(Path.Combine(outDir, $"dx_mip1_{c.Sequence}.raw"), ReadImage(chain.GetImage(1, 0, 0)));
            File.Copy(Path.Combine(inputs, c.Sequence.ToString("D4", Inv), "level0_before.raw"), Path.Combine(outDir, $"level0_{c.Sequence}.raw"), true);
            return 0;
        }

        if (options.TryGetValue("probe", out var probe))
        {
            Probe(captures.Single(c => c.Sequence == int.Parse(probe, Inv)));
            return 0;
        }

        if (options.GetValueOrDefault("only", "") == "mips")
        {
            MipAndResizeComparison(captures, outDir);
            return 0;
        }

        var settings = new List<Setting>
        {
            new("DXT-Default", Encoder.DirectXTex, TEX_COMPRESS_FLAGS.DEFAULT, 0, false),
            new("DXT-Parallel", Encoder.DirectXTex, TEX_COMPRESS_FLAGS.PARALLEL, 0, true),
            new("DXT-Uniform", Encoder.DirectXTex, TEX_COMPRESS_FLAGS.UNIFORM, 0, false),
            new("BCn-Fast-MT", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Fast, true),
            new("BCn-Balanced-MT", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, true),
            new("BCn-Best-MT", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.BestQuality, true),
            new("BCn-Fast-ST", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Fast, false),
            new("BCn-Balanced-ST", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, false),
            new("BCn-Best-ST", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.BestQuality, false),
        };

        var csv = new StringBuilder();
        csv.AppendLine("sequence,source,stage,targetFormat,width,height,mips,inputHasAlphaBelow255,setting,encoder,threads,encode_ms_min,encode_ms_mean,cpu_ms_min,compressed_bytes," +
                       "rgb_psnr_mip0,r_psnr_mip0,g_psnr_mip0,b_psnr_mip0,a_psnr_mip0,maxabs_rgb_mip0,maxabs_a_mip0,wpsnr_mip0," +
                       "rgb_psnr_chain,r_psnr_chain,g_psnr_chain,b_psnr_chain,a_psnr_chain,maxabs_rgb_chain,maxabs_a_chain,wpsnr_chain,alpha_cut_mismatch_mip0,alpha_cut_mismatch_chain,alpha_lt128_texels_mip0");

        // Warm up both encoders so JIT and native load are not billed to the first texture.
        Warmup();

        foreach (var capture in captures)
        {
            Console.WriteLine($"[{capture.Sequence}] {capture.Target} {capture.Width}x{capture.Height} {capture.Source}");
            foreach (var setting in settings)
            {
                var result = Run(capture, setting, repeat);
                csv.AppendLine(string.Join(",",
                    capture.Sequence, Q(capture.Source), capture.Stage, capture.Target, capture.Width, capture.Height, capture.MipLevels,
                    capture.HasAlphaBelow255 ? 1 : 0, setting.Name, setting.Encoder, result.Threads,
                    F(result.MinMs), F(result.MeanMs), F(result.MinCpuMs), result.Bytes,
                    F(result.Metrics.Rgb0), F(result.Metrics.R0), F(result.Metrics.G0), F(result.Metrics.B0), F(result.Metrics.A0), result.Metrics.MaxAbsRgb0, result.Metrics.MaxAbsA0, F(result.Metrics.W0),
                    F(result.Metrics.RgbChain), F(result.Metrics.RChain), F(result.Metrics.GChain), F(result.Metrics.BChain), F(result.Metrics.AChain), result.Metrics.MaxAbsRgbChain, result.Metrics.MaxAbsAChain, F(result.Metrics.WChain),
                    result.Metrics.CutMismatch0, result.Metrics.CutMismatchChain, result.Metrics.Lt128Texels0));
            }

            File.WriteAllText(Path.Combine(outDir, "results.csv"), csv.ToString());
        }

        DecoderAgreement(captures, outDir);
        MipAndResizeComparison(captures, outDir);
        Console.WriteLine("Done.");
        return 0;
    }

    // Which input alpha values become transparent (decoded alpha 0) in BC1 punch-through mode, per encoder.
    private static void AlphaThreshold(string outDir)
    {
        var lines = new StringBuilder("alpha,directxtex_alphaRef_0.5_decoded_alpha,bcn_bc1withalpha_decoded_alpha\n");
        int firstDx = -1, firstBcn = -1;
        for (var alpha = 0; alpha < 256; alpha++)
        {
            // A 4x4 block of mid-grey texels, all with this alpha.
            var bgra = new byte[16 * 4];
            for (var i = 0; i < 16; i++)
            {
                bgra[i * 4] = 90; bgra[i * 4 + 1] = 120; bgra[i * 4 + 2] = 200; bgra[i * 4 + 3] = (byte)alpha;
            }

            var capture = new Capture { Sequence = 0, Target = DXGI_FORMAT.BC1_UNORM, InputFormat = DXGI_FORMAT.B8G8R8A8_UNORM, Width = 4, Height = 4, MipLevels = 1, MipBgra = [bgra], HasAlphaBelow255 = true };
            var dx = DecodeWithDirectXTex(capture, EncodeDirectXTex(capture, new Setting("d", Encoder.DirectXTex, TEX_COMPRESS_FLAGS.DEFAULT, 0, false)))[0][3];
            var bcn = DecodeWithDirectXTex(capture, EncodeBcn(capture, new Setting("b", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, false)))[0][3];
            lines.Append(alpha).Append(',').Append(dx).Append(',').Append(bcn).Append('\n');
            if (dx == 255 && firstDx < 0) firstDx = alpha;
            if (bcn == 255 && firstBcn < 0) firstBcn = alpha;
        }

        File.WriteAllText(Path.Combine(outDir, "alpha_threshold.csv"), lines.ToString());
        Console.WriteLine($"First alpha that stays opaque: DirectXTex {firstDx}, BCnEncoder {firstBcn}");
    }

    // Prints the 4x4 block with the largest BCn error next to what each encoder produced for it.
    private static void Probe(Capture capture)
    {
        var dx = DecodeWithDirectXTex(capture, EncodeDirectXTex(capture, new Setting("d", Encoder.DirectXTex, TEX_COMPRESS_FLAGS.DEFAULT, 0, false)));
        foreach (var quality in new[] { CompressionQuality.Balanced, CompressionQuality.BestQuality })
        {
            var bcn = DecodeWithDirectXTex(capture, EncodeBcn(capture, new Setting("b", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, quality, true)));
            var input = capture.MipBgra[0];
            var w = capture.Width;
            var worst = (err: -1, bx: 0, by: 0);
            for (var by = 0; by < capture.Height / 4; by++)
            {
                for (var bx = 0; bx < w / 4; bx++)
                {
                    var err = 0;
                    for (var y = 0; y < 4; y++)
                        for (var x = 0; x < 4; x++)
                        {
                            var i = ((by * 4 + y) * w + bx * 4 + x) * 4;
                            for (var c = 0; c < 3; c++)
                                err += Math.Abs(input[i + c] - bcn[0][i + c]);
                        }

                    if (err > worst.err)
                        worst = (err, bx, by);
                }
            }

            Console.WriteLine($"-- {quality}: worst block ({worst.bx},{worst.by}) abs error sum {worst.err}; input / DirectXTex / BCn as R,G,B,A");
            for (var y = 0; y < 4; y++)
            {
                var line = new StringBuilder();
                foreach (var data in new[] { input, dx[0], bcn[0] })
                {
                    for (var x = 0; x < 4; x++)
                    {
                        var i = ((worst.by * 4 + y) * w + worst.bx * 4 + x) * 4;
                        line.Append($"({data[i + 2]},{data[i + 1]},{data[i]},{data[i + 3]}) ");
                    }

                    line.Append(" | ");
                }

                Console.WriteLine(line);
            }
        }
    }

    // ------------------------------------------------------------------ encoding

    internal enum Encoder { DirectXTex, Bcn }

    internal sealed record Setting(string Name, Encoder Encoder, TEX_COMPRESS_FLAGS Flags, CompressionQuality Quality, bool Parallel);

    private sealed record RunResult(double MinMs, double MeanMs, double MinCpuMs, long Bytes, string Threads, Metrics Metrics);

    private static RunResult Run(Capture capture, Setting setting, int repeat)
    {
        var times = new List<double>();
        var cpu = new List<double>();
        byte[][]? blocks = null;
        for (var attempt = 0; attempt < repeat; attempt++)
        {
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            blocks = setting.Encoder == Encoder.DirectXTex ? EncodeDirectXTex(capture, setting) : EncodeBcn(capture, setting);
            stopwatch.Stop();
            process.Refresh();
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            cpu.Add((process.TotalProcessorTime - cpuBefore).TotalMilliseconds);
        }

        var decoded = DecodeWithDirectXTex(capture, blocks!);
        var metrics = Metrics.Compute(capture, decoded);
        var threads = setting.Encoder == Encoder.DirectXTex
            ? (setting.Parallel ? "PARALLEL flag" : "DEFAULT flags")
            : (setting.Parallel ? $"IsParallel ({Environment.ProcessorCount} tasks)" : "single");
        return new RunResult(times.Min(), times.Average(), cpu.Min(), blocks!.Sum(b => (long)b.Length), threads, metrics);
    }

    internal static byte[][] EncodeDirectXTex(Capture capture, Setting setting)
    {
        // Same call as the optimizer: the uncompressed chain, target format, alphaRef 0.5.
        using var chain = capture.BuildChain();
        using var compressed = chain.Compress(capture.Target, setting.Flags, 0.5f);
        var blocks = new byte[capture.MipLevels][];
        for (var mip = 0; mip < capture.MipLevels; mip++)
        {
            var image = compressed.GetImage(mip, 0, 0);
            blocks[mip] = new byte[checked((int)image.SlicePitch)];
            Marshal.Copy(image.Pixels, blocks[mip], 0, blocks[mip].Length);
        }

        return blocks;
    }

    internal static byte[][] EncodeBcn(Capture capture, Setting setting)
    {
        var baseFormat = capture.Target is DXGI_FORMAT.BC1_UNORM or DXGI_FORMAT.BC1_UNORM_SRGB ? CompressionFormat.Bc1 : CompressionFormat.Bc3;
        // Mirrors the host's BcnDdsEncoder: punch-through alpha only when something is not opaque.
        var format = baseFormat == CompressionFormat.Bc1 && capture.HasAlphaBelow255 ? CompressionFormat.Bc1WithAlpha : baseFormat;
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Format = format;
        encoder.OutputOptions.Quality = setting.Quality;
        encoder.Options.IsParallel = setting.Parallel;
        var blocks = new byte[capture.MipLevels][];
        for (var mip = 0; mip < capture.MipLevels; mip++)
        {
            var rgba = capture.MipRgba(mip);
            blocks[mip] = encoder.EncodeToRawBytes(rgba, capture.MipWidth(mip), capture.MipHeight(mip), BCnEncoder.Encoder.PixelFormat.Rgba32, 0, out _, out _);
        }

        return blocks;
    }

    /// <summary>Decodes compressed blocks per mip to BGRA with DirectXTex (the one decoder for every result).</summary>
    internal static byte[][] DecodeWithDirectXTex(Capture capture, byte[][] blocks)
    {
        using var compressed = TexHelper.Instance.Initialize2D(capture.Target, capture.Width, capture.Height, 1, capture.MipLevels, CP_FLAGS.NONE);
        for (var mip = 0; mip < capture.MipLevels; mip++)
        {
            var image = compressed.GetImage(mip, 0, 0);
            if (blocks[mip].Length != (int)image.SlicePitch)
                throw new InvalidDataException($"mip {mip}: encoder produced {blocks[mip].Length} bytes, expected {image.SlicePitch}.");
            Marshal.Copy(blocks[mip], 0, image.Pixels, blocks[mip].Length);
        }

        var decodedFormat = capture.Srgb ? DXGI_FORMAT.B8G8R8A8_UNORM_SRGB : DXGI_FORMAT.B8G8R8A8_UNORM;
        using var decoded = compressed.Decompress(decodedFormat);
        var result = new byte[capture.MipLevels][];
        for (var mip = 0; mip < capture.MipLevels; mip++)
            result[mip] = ReadImage(decoded.GetImage(mip, 0, 0));
        return result;
    }

    private static void Warmup()
    {
        var pixels = new byte[16 * 16 * 4];
        new Random(1).NextBytes(pixels);
        foreach (var setting in new[] { new Setting("w", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, true) })
        {
            var encoder = new BcEncoder();
            encoder.OutputOptions.GenerateMipMaps = false;
            encoder.OutputOptions.Format = CompressionFormat.Bc1;
            encoder.EncodeToRawBytes(pixels, 16, 16, BCnEncoder.Encoder.PixelFormat.Rgba32, 0, out _, out _);
        }

        using var image = TexHelper.Instance.Initialize2D(DXGI_FORMAT.B8G8R8A8_UNORM, 16, 16, 1, 1, CP_FLAGS.NONE);
        Marshal.Copy(pixels, 0, image.GetImage(0, 0, 0).Pixels, pixels.Length);
        using var compressed = image.Compress(DXGI_FORMAT.BC1_UNORM, TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
    }

    // ------------------------------------------------------------------ metrics

    internal sealed record Metrics(
        double Rgb0, double R0, double G0, double B0, double A0, int MaxAbsRgb0, int MaxAbsA0, double W0,
        double RgbChain, double RChain, double GChain, double BChain, double AChain, int MaxAbsRgbChain, int MaxAbsAChain, double WChain,
        long CutMismatch0, long CutMismatchChain, long Lt128Texels0)
    {
        public static Metrics Compute(Capture capture, byte[][] decoded)
        {
            // Channel order in the BGRA buffers: 0 = B, 1 = G, 2 = R, 3 = A.
            var perMip = new List<(double[] Mse, int MaxRgb, int MaxA, long Cut, long Lt128)>();
            for (var mip = 0; mip < capture.MipLevels; mip++)
            {
                var input = capture.MipBgra[mip];
                var output = decoded[mip];
                var sums = new double[4];
                int maxRgb = 0, maxA = 0;
                long cut = 0;
                long lt128 = 0;
                var checkCut = capture.IsBc1 && capture.HasAlphaBelow255;
                for (var i = 0; i < input.Length; i += 4)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        var d = input[i + c] - output[i + c];
                        sums[c] += (double)d * d;
                        if (c < 3)
                            maxRgb = Math.Max(maxRgb, Math.Abs(d));
                        else
                            maxA = Math.Max(maxA, Math.Abs(d));
                    }

                    if (input[i + 3] < 128)
                        lt128++;
                    if (checkCut)
                    {
                        // DirectXTex: a texel is transparent when alpha / 255 < alphaRef (0.5), i.e. alpha <= 127.
                        var expected = input[i + 3] >= 128 ? 255 : 0;
                        if (output[i + 3] != expected)
                            cut++;
                    }
                }

                var texels = (double)(input.Length / 4);
                perMip.Add((sums.Select(x => x / texels).ToArray(), maxRgb, maxA, cut, lt128));
            }

            double Psnr(double mse) => mse <= 0 ? 100 : Math.Min(100, 10 * Math.Log10(255.0 * 255.0 / mse));
            double Rgb(double[] mse) => Psnr((mse[0] + mse[1] + mse[2]) / 3);
            // DirectXTex's default (perceptual) colour weights: R 0.2125, G 0.7154, B 0.0721.
            double Weighted(double[] mse) => Psnr(0.2125 * mse[2] + 0.7154 * mse[1] + 0.0721 * mse[0]);
            double Chain(Func<double[], double> f) => perMip.Average(m => f(m.Mse));
            return new Metrics(
                Rgb(perMip[0].Mse), Psnr(perMip[0].Mse[2]), Psnr(perMip[0].Mse[1]), Psnr(perMip[0].Mse[0]), Psnr(perMip[0].Mse[3]), perMip[0].MaxRgb, perMip[0].MaxA, Weighted(perMip[0].Mse),
                Chain(Rgb), Chain(m => Psnr(m[2])), Chain(m => Psnr(m[1])), Chain(m => Psnr(m[0])), Chain(m => Psnr(m[3])), perMip.Max(m => m.MaxRgb), perMip.Max(m => m.MaxA), Chain(Weighted),
                perMip[0].Cut, perMip.Sum(m => m.Cut), perMip[0].Lt128);
        }
    }

    // ------------------------------------------------------------------ decoder agreement

    private static void DecoderAgreement(List<Capture> captures, string outDir)
    {
        var lines = new StringBuilder();
        lines.AppendLine("sequence,mip,format,max_abs_diff_between_decoders,texels_differing,texels");
        var decoder = new BcDecoder();
        foreach (var capture in captures.Take(8))
        {
            var setting = new Setting("BCn-Balanced-MT", Encoder.Bcn, TEX_COMPRESS_FLAGS.DEFAULT, CompressionQuality.Balanced, true);
            var blocks = EncodeBcn(capture, setting);
            var dx = DecodeWithDirectXTex(capture, blocks);
            foreach (var mip in new[] { 0, Math.Min(3, capture.MipLevels - 1) })
            {
                var format = capture.IsBc1 ? (capture.HasAlphaBelow255 ? CompressionFormat.Bc1WithAlpha : CompressionFormat.Bc1) : CompressionFormat.Bc3;
                var bcn = decoder.DecodeRaw(blocks[mip], capture.MipWidth(mip), capture.MipHeight(mip), format);
                var max = 0;
                var differing = 0;
                for (var i = 0; i < bcn.Length; i++)
                {
                    var o = i * 4;
                    var d = Math.Max(Math.Max(Math.Abs(bcn[i].b - dx[mip][o]), Math.Abs(bcn[i].g - dx[mip][o + 1])), Math.Max(Math.Abs(bcn[i].r - dx[mip][o + 2]), Math.Abs(bcn[i].a - dx[mip][o + 3])));
                    max = Math.Max(max, d);
                    if (d != 0)
                        differing++;
                }

                lines.AppendLine(string.Join(",", capture.Sequence, mip, capture.Target, max, differing, bcn.Length));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "decoder_agreement.csv"), lines.ToString());
    }

    // ------------------------------------------------------------------ mips and resize

    private static void MipAndResizeComparison(List<Capture> captures, string outDir)
    {
        var mips = new StringBuilder();
        mips.AppendLine("sequence,source,format,srgb,width,height,mip,candidate,psnr_rgb,psnr_a,max_abs,exact_match_percent");
        var resize = new StringBuilder();
        resize.AppendLine("sequence,source,format,srgb,from,to,candidate,psnr_rgb,psnr_a,max_abs,exact_match_percent");
        foreach (var capture in captures)
        {
            if (capture.Level0Before == null)
                continue;

            var inputFormat = capture.InputFormat;
            using var full = capture.BuildImage(capture.Level0Before, capture.Width, capture.Height, inputFormat);
            using var chain = full.GenerateMipMaps(TEX_FILTER_FLAGS.DEFAULT, 0);
            var dxMips = Enumerable.Range(0, chain.GetMetadata().MipLevels).Select(m => ReadImage(chain.GetImage(m, 0, 0))).ToArray();

            // Managed candidates, each building level n from the previous *generated* level.
            foreach (var candidate in Enum.GetValues<FilterSpace>())
            {
                if (candidate is FilterSpace.LinearBox or FilterSpace.LinearAlphaWeighted && !capture.Srgb)
                    continue;

                // One step at a time: level n is built from DirectXTex's own level n-1, so the
                // comparison isolates the filter and does not accumulate rounding drift.
                for (var mip = 1; mip < dxMips.Length; mip++)
                {
                    var previous = mip == 1 ? capture.Level0Before : dxMips[mip - 1];
                    var w = capture.MipWidth(mip - 1);
                    var h = capture.MipHeight(mip - 1);
                    var (next, _, _) = ManagedFilter.Box(previous, w, h, Math.Max(1, w / 2), Math.Max(1, h / 2), candidate, capture.Srgb);
                    var stats = Compare(next, dxMips[mip]);
                    mips.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), capture.InputFormat, capture.Srgb ? 1 : 0, capture.Width, capture.Height, mip, candidate, F(stats.Rgb), F(stats.A), stats.MaxAbs, F(stats.ExactPercent)));
                }
            }

            // The captured chain's early levels (before any overlay) must equal this tool's DirectXTex mips.
            // Resize with BOX: halves and quarters, plus a non-integral ratio.
            foreach (var (nw, nh) in new[] { (capture.Width / 2, capture.Height / 2), (capture.Width / 4, capture.Height / 4), (capture.Width * 3 / 4, capture.Height * 3 / 4) })
            {
                if (nw < 1 || nh < 1)
                    continue;

                ScratchImage resized;
                try
                {
                    resized = full.Resize(nw, nh, TEX_FILTER_FLAGS.BOX);
                }
                catch (Exception exception)
                {
                    resize.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), capture.InputFormat, capture.Srgb ? 1 : 0, $"{capture.Width}x{capture.Height}", $"{nw}x{nh}", "DirectXTex-failed:" + exception.GetType().Name, "", "", "", ""));
                    continue;
                }

                using var resizedScope = resized;
                var dx = ReadImage(resized.GetImage(0, 0, 0));
                foreach (var candidate in Enum.GetValues<FilterSpace>())
                {
                    if (candidate is FilterSpace.LinearBox or FilterSpace.LinearAlphaWeighted && !capture.Srgb)
                        continue;

                    var (managed, _, _) = ManagedFilter.Box(capture.Level0Before, capture.Width, capture.Height, nw, nh, candidate, capture.Srgb);
                    var stats = Compare(managed, dx);
                    resize.AppendLine(string.Join(",", capture.Sequence, Q(capture.Source), capture.InputFormat, capture.Srgb ? 1 : 0, $"{capture.Width}x{capture.Height}", $"{nw}x{nh}", candidate, F(stats.Rgb), F(stats.A), stats.MaxAbs, F(stats.ExactPercent)));
                }
            }
        }

        File.WriteAllText(Path.Combine(outDir, "mips.csv"), mips.ToString());
        File.WriteAllText(Path.Combine(outDir, "resize.csv"), resize.ToString());
    }

    private sealed record CompareStats(double Rgb, double A, int MaxAbs, double ExactPercent);

    private static CompareStats Compare(byte[] a, byte[] b)
    {
        var sums = new double[4];
        var maxAbs = 0;
        long exact = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var same = true;
            for (var c = 0; c < 4; c++)
            {
                var d = a[i + c] - b[i + c];
                sums[c] += (double)d * d;
                maxAbs = Math.Max(maxAbs, Math.Abs(d));
                same &= d == 0;
            }

            if (same)
                exact++;
        }

        var texels = a.Length / 4.0;
        double Psnr(double mse) => mse <= 0 ? 100 : Math.Min(100, 10 * Math.Log10(255.0 * 255.0 / mse));
        return new CompareStats(Psnr((sums[0] + sums[1] + sums[2]) / 3 / texels), Psnr(sums[3] / texels), maxAbs, exact / texels * 100);
    }

    // ------------------------------------------------------------------ helpers

    internal static byte[] ReadImage(Image image)
    {
        var rowBytes = checked(image.Width * 4);
        var pitch = checked((int)image.RowPitch);
        var buffer = new byte[rowBytes * image.Height];
        for (var y = 0; y < image.Height; y++)
            Marshal.Copy(IntPtr.Add(image.Pixels, y * pitch), buffer, y * rowBytes, rowBytes);
        return buffer;
    }

    private static string F(double value) => value.ToString("0.####", Inv);
    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

internal enum FilterSpace { StoredBox, LinearBox, StoredAlphaWeighted, LinearAlphaWeighted }

internal sealed class Capture
{
    public int Sequence { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public DXGI_FORMAT Target { get; init; }
    public DXGI_FORMAT InputFormat { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipLevels { get; init; }
    public string MipMethod { get; init; } = string.Empty;
    public byte[][] MipBgra { get; init; } = [];
    public byte[]? Level0Before { get; init; }
    public bool Srgb => InputFormat == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB;
    public bool IsBc1 => Target is DXGI_FORMAT.BC1_UNORM or DXGI_FORMAT.BC1_UNORM_SRGB;
    public bool HasAlphaBelow255 { get; init; }

    public int MipWidth(int mip) => Math.Max(1, Width >> mip);
    public int MipHeight(int mip) => Math.Max(1, Height >> mip);

    public byte[] MipRgba(int mip)
    {
        var bgra = MipBgra[mip];
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return rgba;
    }

    public ScratchImage BuildChain()
    {
        var chain = TexHelper.Instance.Initialize2D(InputFormat, Width, Height, 1, MipLevels, CP_FLAGS.NONE);
        for (var mip = 0; mip < MipLevels; mip++)
            CopyIn(chain.GetImage(mip, 0, 0), MipBgra[mip]);
        return chain;
    }

    public ScratchImage BuildImage(byte[] bgra, int width, int height, DXGI_FORMAT format)
    {
        var image = TexHelper.Instance.Initialize2D(format, width, height, 1, 1, CP_FLAGS.NONE);
        CopyIn(image.GetImage(0, 0, 0), bgra);
        return image;
    }

    private static void CopyIn(Image image, byte[] bgra)
    {
        var rowBytes = image.Width * 4;
        var pitch = checked((int)image.RowPitch);
        for (var y = 0; y < image.Height; y++)
            Marshal.Copy(bgra, y * rowBytes, IntPtr.Add(image.Pixels, y * pitch), rowBytes);
    }

    public static List<Capture> LoadAll(string root)
    {
        var captures = new List<Capture>();
        foreach (var directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, "meta.json")))
                continue;

            var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "meta.json"))).RootElement;
            var width = meta.GetProperty("width").GetInt32();
            var height = meta.GetProperty("height").GetInt32();
            var mips = meta.GetProperty("mipLevels").GetInt32();
            var levels = Enumerable.Range(0, mips).Select(m => File.ReadAllBytes(Path.Combine(directory, $"mip{m}.raw"))).ToArray();
            var level0Path = Path.Combine(directory, "level0_before.raw");
            var hasAlpha = false;
            for (var i = 3; i < levels[0].Length && !hasAlpha; i += 4)
                hasAlpha = levels[0][i] < 255;
            captures.Add(new Capture
            {
                Sequence = meta.GetProperty("sequence").GetInt32(),
                Source = meta.GetProperty("source").GetString()!,
                Stage = meta.GetProperty("stage").GetString()!,
                Target = Enum.Parse<DXGI_FORMAT>(meta.GetProperty("targetFormat").GetString()!),
                InputFormat = Enum.Parse<DXGI_FORMAT>(meta.GetProperty("inputFormat").GetString()!),
                Width = width,
                Height = height,
                MipLevels = mips,
                MipMethod = meta.GetProperty("mipMethod").GetString()!,
                MipBgra = levels,
                Level0Before = File.Exists(level0Path) ? File.ReadAllBytes(level0Path) : null,
                HasAlphaBelow255 = hasAlpha,
            });
        }

        return captures;
    }
}

/// <summary>
/// Plain managed area-weighted box filter (the managed stand-in for DirectXTex's Resize(BOX) and
/// for each GenerateMipMaps step). Separable; a destination pixel averages the fractional
/// coverage of the source pixels it spans. Works either on the stored values or, for sRGB
/// images, on linear light for colour (alpha is always averaged as stored).
/// </summary>
internal static class ManagedFilter
{
    private static readonly float[] ToLinear = Enumerable.Range(0, 256)
        .Select(v => { var c = v / 255.0; return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)); })
        .ToArray();

    public static (byte[] Pixels, int Width, int Height) Box(byte[] bgra, int width, int height, int newWidth, int newHeight, FilterSpace space, bool srgb)
    {
        var linear = space is FilterSpace.LinearBox or FilterSpace.LinearAlphaWeighted;
        var alphaWeighted = space is FilterSpace.StoredAlphaWeighted or FilterSpace.LinearAlphaWeighted;
        // 8 channels per pixel: premultiplied-or-plain colour (3), alpha (1), plain colour (3) as the
        // fallback when a footprint has no alpha at all, one spare.
        const int N = 8;
        var source = new float[width * height * N];
        for (var i = 0; i < width * height; i++)
        {
            var a = bgra[i * 4 + 3] / 255f;
            for (var c = 0; c < 3; c++)
            {
                var v = linear ? ToLinear[bgra[i * 4 + c]] : bgra[i * 4 + c] / 255f;
                source[i * N + c] = alphaWeighted ? v * a : v;
                source[i * N + 4 + c] = v;
            }

            source[i * N + 3] = a;
        }

        var horizontal = new float[newWidth * height * N];
        var columnWeights = Weights(width, newWidth);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < newWidth; x++)
            {
                var (start, weights) = columnWeights[x];
                for (var c = 0; c < N; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += source[(y * width + start + k) * N + c] * weights[k];
                    horizontal[(y * newWidth + x) * N + c] = (float)sum;
                }
            }
        }

        var result = new byte[newWidth * newHeight * 4];
        var rowWeights = Weights(height, newHeight);
        var acc = new double[N];
        for (var y = 0; y < newHeight; y++)
        {
            var (start, weights) = rowWeights[y];
            for (var x = 0; x < newWidth; x++)
            {
                for (var c = 0; c < N; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < weights.Length; k++)
                        sum += horizontal[((start + k) * newWidth + x) * N + c] * weights[k];
                    acc[c] = sum;
                }

                for (var c = 0; c < 3; c++)
                {
                    var v = alphaWeighted ? (acc[3] > 1e-9 ? acc[c] / acc[3] : acc[4 + c]) : acc[c];
                    var stored = linear ? FromLinear(v) : Math.Clamp(v, 0, 1);
                    result[(y * newWidth + x) * 4 + c] = (byte)Math.Clamp((int)Math.Floor(stored * 255.0 + 0.5), 0, 255);
                }

                result[(y * newWidth + x) * 4 + 3] = (byte)Math.Clamp((int)Math.Floor(acc[3] * 255.0 + 0.5), 0, 255);
            }
        }

        return (result, newWidth, newHeight);
    }

    private static double FromLinear(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    }

    // For each destination pixel: first source index and the normalised coverage weights.
    private static (int Start, double[] Weights)[] Weights(int sourceSize, int destinationSize)
    {
        var scale = (double)sourceSize / destinationSize;
        var result = new (int, double[])[destinationSize];
        for (var d = 0; d < destinationSize; d++)
        {
            var from = d * scale;
            var to = (d + 1) * scale;
            var first = (int)Math.Floor(from + 1e-9);
            var last = Math.Min(sourceSize - 1, (int)Math.Ceiling(to - 1e-9) - 1);
            var weights = new double[Math.Max(1, last - first + 1)];
            double total = 0;
            for (var s = first; s <= last; s++)
            {
                var overlap = Math.Min(to, s + 1) - Math.Max(from, s);
                weights[s - first] = Math.Max(0, overlap);
                total += weights[s - first];
            }

            for (var k = 0; k < weights.Length; k++)
                weights[k] /= total <= 0 ? 1 : total;
            result[d] = (first, weights);
        }

        return result;
    }
}
