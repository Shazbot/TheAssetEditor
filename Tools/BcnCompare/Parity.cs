using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Dx = DirectXTexNet;
using Sh = Editors.ImportExport.Dds.Native;

// Byte-for-byte parity of the native DirectXTex shim against DirectXTexNet (the library it replaces),
// over captured optimizer inputs and synthetic cases:
//   BcnCompare --parity true --inputs <dir>[;<dir>...] --out <dir>
internal static class Parity
{
    private sealed record Info(int Width, int Height, int Format, long RowPitch, long SlicePitch);

    // Everything an operation produced, as plain managed data, or the exception it raised.
    private sealed record Outcome(Info[]? Infos, byte[][]? Pixels, byte[]? Blob, int? HResult, string? ExceptionType)
    {
        public static Outcome Failure(Exception e) => new(null, null, null, e.HResult, e.GetType().Name);
    }

    private interface IBackend
    {
        string Name { get; }
        Outcome Initialize(int format, int width, int height, int mips, byte[][] data);
        Outcome Mips(int format, byte[] level0, int width, int height, uint filter, int levels);
        Outcome Resize(int format, byte[] level0, int width, int height, int newWidth, int newHeight, uint filter);
        Outcome Compress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, uint flags, float alphaRef, uint ddsFlags);
        Outcome Decompress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, int decodedFormat);
        Outcome LoadWic(byte[] png, uint flags, bool viaFile);
        Outcome CopyRectangle(int format, byte[] source, int sourceWidth, int sourceHeight, int x, int y, int w, int h, int destinationWidth, int destinationHeight, int xOffset, int yOffset, uint filter);
        Outcome CreateImageCopy(int format, byte[] level0, int width, int height);
    }

    private sealed class DxBackend : IBackend
    {
        public string Name => "DirectXTexNet";

        private static Dx.ScratchImage Build(int format, int width, int height, byte[][] mips)
        {
            var image = Dx.TexHelper.Instance.Initialize2D((Dx.DXGI_FORMAT)format, width, height, 1, mips.Length, Dx.CP_FLAGS.NONE);
            for (var mip = 0; mip < mips.Length; mip++)
                Fill(image.GetImage(mip, 0, 0), mips[mip]);
            return image;
        }

        private static void Fill(Dx.Image image, byte[] data)
        {
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(data, y * rowBytes, IntPtr.Add(image.Pixels, y * (int)image.RowPitch), rowBytes);
        }

        private static Info InfoOf(Dx.Image i) => new(i.Width, i.Height, (int)i.Format, i.RowPitch, i.SlicePitch);

        private static Outcome Collect(Dx.ScratchImage image, bool compressed = false)
        {
            var count = image.GetMetadata().MipLevels;
            var infos = new Info[count];
            var pixels = new byte[count][];
            for (var mip = 0; mip < count; mip++)
            {
                var i = image.GetImage(mip, 0, 0);
                infos[mip] = InfoOf(i);
                if (compressed)
                {
                    pixels[mip] = new byte[i.SlicePitch];
                    Marshal.Copy(i.Pixels, pixels[mip], 0, pixels[mip].Length);
                }
                else
                {
                    pixels[mip] = new byte[i.Width * i.Height * 4];
                    for (var y = 0; y < i.Height; y++)
                        Marshal.Copy(IntPtr.Add(i.Pixels, y * (int)i.RowPitch), pixels[mip], y * i.Width * 4, i.Width * 4);
                }
            }

            return new Outcome(infos, pixels, null, null, null);
        }

        public Outcome Initialize(int format, int width, int height, int mips, byte[][] data)
        {
            try { using var image = Build(format, width, height, data); return Collect(image); }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Mips(int format, byte[] level0, int width, int height, uint filter, int levels)
        {
            try
            {
                using var image = Build(format, width, height, [level0]);
                using var chain = image.GenerateMipMaps((Dx.TEX_FILTER_FLAGS)filter, levels);
                return Collect(chain);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Resize(int format, byte[] level0, int width, int height, int newWidth, int newHeight, uint filter)
        {
            try
            {
                using var image = Build(format, width, height, [level0]);
                using var resized = image.Resize(newWidth, newHeight, (Dx.TEX_FILTER_FLAGS)filter);
                return Collect(resized);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Compress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, uint flags, float alphaRef, uint ddsFlags)
        {
            try
            {
                using var image = Build(sourceFormat, width, height, mips);
                using var compressed = image.Compress((Dx.DXGI_FORMAT)targetFormat, (Dx.TEX_COMPRESS_FLAGS)flags, alphaRef);
                using var stream = compressed.SaveToDDSMemory((Dx.DDS_FLAGS)ddsFlags);
                var blob = new byte[stream.Length];
                stream.ReadExactly(blob, 0, blob.Length);
                var result = Collect(compressed, compressed: true);
                return result with { Blob = blob };
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Decompress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, int decodedFormat)
        {
            try
            {
                using var image = Build(sourceFormat, width, height, mips);
                using var compressed = image.Compress((Dx.DXGI_FORMAT)targetFormat, Dx.TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                using var decoded = compressed.Decompress((Dx.DXGI_FORMAT)decodedFormat);
                return Collect(decoded);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome LoadWic(byte[] png, uint flags, bool viaFile)
        {
            try
            {
                Dx.ScratchImage image;
                if (viaFile)
                {
                    var path = Path.Combine(Path.GetTempPath(), "parity-" + Guid.NewGuid().ToString("N") + ".png");
                    File.WriteAllBytes(path, png);
                    try { image = Dx.TexHelper.Instance.LoadFromWICFile(path, (Dx.WIC_FLAGS)flags); }
                    finally { File.Delete(path); }
                }
                else
                {
                    var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
                    try { image = Dx.TexHelper.Instance.LoadFromWICMemory(handle.AddrOfPinnedObject(), png.Length, (Dx.WIC_FLAGS)flags); }
                    finally { handle.Free(); }
                }

                using (image)
                    return Collect(image);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome CopyRectangle(int format, byte[] source, int sourceWidth, int sourceHeight, int x, int y, int w, int h, int destinationWidth, int destinationHeight, int xOffset, int yOffset, uint filter)
        {
            try
            {
                using var src = Build(format, sourceWidth, sourceHeight, [source]);
                using var dst = Build(format, destinationWidth, destinationHeight, [new byte[destinationWidth * destinationHeight * 4]]);
                Dx.TexHelper.Instance.CopyRectangle(src.GetImage(0, 0, 0), x, y, w, h, dst.GetImage(0, 0, 0), (Dx.TEX_FILTER_FLAGS)filter, xOffset, yOffset);
                return Collect(dst);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome CreateImageCopy(int format, byte[] level0, int width, int height)
        {
            try
            {
                using var src = Build(format, width, height, [level0]);
                using var copy = src.CreateImageCopy(0, false, Dx.CP_FLAGS.NONE);
                return Collect(copy);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }
    }

    private sealed class ShBackend : IBackend
    {
        public string Name => "shim";

        private static Sh.ScratchImage Build(int format, int width, int height, byte[][] mips)
        {
            var image = Sh.TexHelper.Instance.Initialize2D((Sh.DXGI_FORMAT)format, width, height, 1, mips.Length, Sh.CP_FLAGS.NONE);
            for (var mip = 0; mip < mips.Length; mip++)
                Fill(image.GetImage(mip, 0, 0), mips[mip]);
            return image;
        }

        private static void Fill(Sh.Image image, byte[] data)
        {
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(data, y * rowBytes, IntPtr.Add(image.Pixels, y * (int)image.RowPitch), rowBytes);
        }

        private static Info InfoOf(Sh.Image i) => new(i.Width, i.Height, (int)i.Format, i.RowPitch, i.SlicePitch);

        private static Outcome Collect(Sh.ScratchImage image, bool compressed = false)
        {
            var count = image.GetMetadata().MipLevels;
            var infos = new Info[count];
            var pixels = new byte[count][];
            for (var mip = 0; mip < count; mip++)
            {
                var i = image.GetImage(mip, 0, 0);
                infos[mip] = InfoOf(i);
                if (compressed)
                {
                    pixels[mip] = new byte[i.SlicePitch];
                    Marshal.Copy(i.Pixels, pixels[mip], 0, pixels[mip].Length);
                }
                else
                {
                    pixels[mip] = new byte[i.Width * i.Height * 4];
                    for (var y = 0; y < i.Height; y++)
                        Marshal.Copy(IntPtr.Add(i.Pixels, y * (int)i.RowPitch), pixels[mip], y * i.Width * 4, i.Width * 4);
                }
            }

            return new Outcome(infos, pixels, null, null, null);
        }

        public Outcome Initialize(int format, int width, int height, int mips, byte[][] data)
        {
            try { using var image = Build(format, width, height, data); return Collect(image); }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Mips(int format, byte[] level0, int width, int height, uint filter, int levels)
        {
            try
            {
                using var image = Build(format, width, height, [level0]);
                using var chain = image.GenerateMipMaps((Sh.TEX_FILTER_FLAGS)filter, levels);
                return Collect(chain);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Resize(int format, byte[] level0, int width, int height, int newWidth, int newHeight, uint filter)
        {
            try
            {
                using var image = Build(format, width, height, [level0]);
                using var resized = image.Resize(newWidth, newHeight, (Sh.TEX_FILTER_FLAGS)filter);
                return Collect(resized);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Compress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, uint flags, float alphaRef, uint ddsFlags)
        {
            try
            {
                using var image = Build(sourceFormat, width, height, mips);
                using var compressed = image.Compress((Sh.DXGI_FORMAT)targetFormat, (Sh.TEX_COMPRESS_FLAGS)flags, alphaRef);
                using var stream = compressed.SaveToDDSMemory((Sh.DDS_FLAGS)ddsFlags);
                var blob = new byte[stream.Length];
                stream.ReadExactly(blob, 0, blob.Length);
                var result = Collect(compressed, compressed: true);
                return result with { Blob = blob };
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome Decompress(int sourceFormat, int targetFormat, int width, int height, byte[][] mips, int decodedFormat)
        {
            try
            {
                using var image = Build(sourceFormat, width, height, mips);
                using var compressed = image.Compress((Sh.DXGI_FORMAT)targetFormat, Sh.TEX_COMPRESS_FLAGS.DEFAULT, 0.5f);
                using var decoded = compressed.Decompress((Sh.DXGI_FORMAT)decodedFormat);
                return Collect(decoded);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome LoadWic(byte[] png, uint flags, bool viaFile)
        {
            try
            {
                Sh.ScratchImage image;
                if (viaFile)
                {
                    var path = Path.Combine(Path.GetTempPath(), "parity-" + Guid.NewGuid().ToString("N") + ".png");
                    File.WriteAllBytes(path, png);
                    try { image = Sh.TexHelper.Instance.LoadFromWICFile(path, (Sh.WIC_FLAGS)flags); }
                    finally { File.Delete(path); }
                }
                else
                {
                    var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
                    try { image = Sh.TexHelper.Instance.LoadFromWICMemory(handle.AddrOfPinnedObject(), png.Length, (Sh.WIC_FLAGS)flags); }
                    finally { handle.Free(); }
                }

                using (image)
                    return Collect(image);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome CopyRectangle(int format, byte[] source, int sourceWidth, int sourceHeight, int x, int y, int w, int h, int destinationWidth, int destinationHeight, int xOffset, int yOffset, uint filter)
        {
            try
            {
                using var src = Build(format, sourceWidth, sourceHeight, [source]);
                using var dst = Build(format, destinationWidth, destinationHeight, [new byte[destinationWidth * destinationHeight * 4]]);
                Sh.TexHelper.Instance.CopyRectangle(src.GetImage(0, 0, 0), x, y, w, h, dst.GetImage(0, 0, 0), (Sh.TEX_FILTER_FLAGS)filter, xOffset, yOffset);
                return Collect(dst);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }

        public Outcome CreateImageCopy(int format, byte[] level0, int width, int height)
        {
            try
            {
                using var src = Build(format, width, height, [level0]);
                using var copy = src.CreateImageCopy(0, false, Sh.CP_FLAGS.NONE);
                return Collect(copy);
            }
            catch (Exception e) { return Outcome.Failure(e); }
        }
    }

    private static readonly IBackend A = new DxBackend();
    private static readonly IBackend B = new ShBackend();
    private static int _cases;
    private static int _failures;
    private static readonly Dictionary<string, (int Cases, int Identical, int BothThrow)> PerOp = [];
    private static readonly StringBuilder Log = new();

    public static int Run(string[] inputRoots, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var native = Sh.TexHelper.NativeVersion;
        Log.AppendLine($"shim: DirectXTex version {native.Version}, {native.Tag} ({native.Commit})");

        foreach (var root in inputRoots)
        {
            var captures = Capture.LoadAll(root);
            Log.AppendLine($"inputs {root}: {captures.Count} captured encodes");
            Console.WriteLine($"{root}: {captures.Count} captures");
            foreach (var c in captures)
            {
                var label = $"[{Path.GetFileName(root)}#{c.Sequence}] {c.Target} {c.Width}x{c.Height} {Path.GetFileName(c.Source)}";
                var srgb = c.Srgb;
                var inFormat = (int)c.InputFormat;
                var target = (int)c.Target;
                Check("Initialize2D+copy (captured chain)", label, A.Initialize(inFormat, c.Width, c.Height, c.MipLevels, c.MipBgra), B.Initialize(inFormat, c.Width, c.Height, c.MipLevels, c.MipBgra));
                if (c.Level0Before != null)
                {
                    foreach (var (name, filter) in new[] { ("DEFAULT", 0u), ("BOX", 0x400000u), ("LINEAR", 0x200000u), ("POINT", 0x100000u), ("TRIANGLE", 0x500000u) })
                        Check($"GenerateMipMaps {name}", label, A.Mips(inFormat, c.Level0Before, c.Width, c.Height, filter, 0), B.Mips(inFormat, c.Level0Before, c.Width, c.Height, filter, 0));
                    Check("CreateImageCopy", label, A.CreateImageCopy(inFormat, c.Level0Before, c.Width, c.Height), B.CreateImageCopy(inFormat, c.Level0Before, c.Width, c.Height));
                    foreach (var (num, den) in new[] { (1, 2), (1, 4), (3, 4) })
                    {
                        var nw = Math.Max(1, c.Width * num / den);
                        var nh = Math.Max(1, c.Height * num / den);
                        foreach (var (name, filter) in new[] { ("BOX", 0x400000u), ("DEFAULT", 0u), ("LINEAR", 0x200000u) })
                            Check($"Resize {name} {num}/{den}", label, A.Resize(inFormat, c.Level0Before, c.Width, c.Height, nw, nh, filter), B.Resize(inFormat, c.Level0Before, c.Width, c.Height, nw, nh, filter));
                    }
                }

                foreach (var (name, flags) in new[] { ("DEFAULT", 0u), ("PARALLEL", 0x10000000u), ("UNIFORM", 0x40000u) })
                {
                    Check($"Compress {name} + SaveToDDS NONE", label, A.Compress(inFormat, target, c.Width, c.Height, c.MipBgra, flags, 0.5f, 0), B.Compress(inFormat, target, c.Width, c.Height, c.MipBgra, flags, 0.5f, 0));
                }

                Check("Compress DEFAULT + SaveToDDS FORCE_DX10_EXT", label,
                    A.Compress(inFormat, target, c.Width, c.Height, c.MipBgra, 0, 0.5f, 0x10000), B.Compress(inFormat, target, c.Width, c.Height, c.MipBgra, 0, 0.5f, 0x10000));
                _ = srgb;
            }
        }

        Synthetic();
        File.WriteAllText(Path.Combine(outDir, "parity.txt"), Log.ToString());
        Console.WriteLine($"{_cases} cases, {_failures} difference(s).");
        foreach (var (op, v) in PerOp.OrderBy(p => p.Key))
            Console.WriteLine($"  {op}: {v.Cases} cases, {v.Identical} identical ({v.BothThrow} where both raised the same failure)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string op, string label, Outcome a, Outcome b)
    {
        _cases++;
        PerOp.TryGetValue(op, out var counts);
        counts.Cases++;
        var failure = Compare(a, b, out var detail);
        if (failure == null)
        {
            counts.Identical++;
            if (a.ExceptionType != null)
                counts.BothThrow++;
        }
        else
        {
            _failures++;
            var line = $"DIFF {op} {label}: {failure} {detail}";
            Log.AppendLine(line);
            Console.WriteLine(line);
        }

        PerOp[op] = counts;
    }

    private static string? Compare(Outcome a, Outcome b, out string detail)
    {
        detail = string.Empty;
        if (a.ExceptionType != null || b.ExceptionType != null)
        {
            if (a.ExceptionType == b.ExceptionType && a.HResult == b.HResult)
                return null;
            detail = $"DirectXTexNet: {a.ExceptionType} 0x{a.HResult:X}; shim: {b.ExceptionType} 0x{b.HResult:X}";
            return "different failure behavior";
        }

        if (a.Infos!.Length != b.Infos!.Length)
        {
            detail = $"{a.Infos.Length} vs {b.Infos.Length} images";
            return "image count differs";
        }

        for (var i = 0; i < a.Infos.Length; i++)
        {
            if (a.Infos[i] != b.Infos[i])
            {
                detail = $"image {i}: {a.Infos[i]} vs {b.Infos[i]}";
                return "image layout differs";
            }

            var x = a.Pixels![i];
            var y = b.Pixels![i];
            if (!x.AsSpan().SequenceEqual(y))
            {
                long differing = 0;
                double sq = 0;
                var max = 0;
                for (var k = 0; k < Math.Min(x.Length, y.Length); k++)
                {
                    var d = x[k] - y[k];
                    if (d != 0) differing++;
                    sq += d * d;
                    max = Math.Max(max, Math.Abs(d));
                }

                var mse = sq / Math.Max(1, x.Length);
                var psnr = mse <= 0 ? 100 : 10 * Math.Log10(255.0 * 255.0 / mse);
                detail = $"mip {i}: {differing:N0} of {x.Length:N0} bytes differ, PSNR {psnr:0.00} dB, max error {max}";
                return "pixel data differs";
            }
        }

        if (a.Blob != null || b.Blob != null)
        {
            if (a.Blob == null || b.Blob == null || !a.Blob.AsSpan().SequenceEqual(b.Blob))
            {
                var length = Math.Min(a.Blob?.Length ?? 0, b.Blob?.Length ?? 0);
                long differing = 0;
                for (var k = 0; k < length; k++)
                    if (a.Blob![k] != b.Blob![k]) differing++;
                detail = $"{a.Blob?.Length} vs {b.Blob?.Length} bytes, {differing:N0} differ";
                return "DDS file differs";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ synthetic cases

    private static byte[] Noise(int w, int h, int seed, Func<int, int, int, byte[]>? shape = null)
    {
        var random = new Random(seed);
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                if (shape != null)
                {
                    Array.Copy(shape(x, y, random.Next()), 0, data, o, 4);
                }
                else
                {
                    data[o] = (byte)random.Next(256);
                    data[o + 1] = (byte)random.Next(256);
                    data[o + 2] = (byte)random.Next(256);
                    data[o + 3] = (byte)random.Next(256);
                }
            }
        }

        return data;
    }

    private static byte[][] ChainOf(byte[] level0, int w, int h, int format)
    {
        // The reference chain comes from DirectXTexNet itself so both backends are handed identical input mips.
        using var image = Dx.TexHelper.Instance.Initialize2D((Dx.DXGI_FORMAT)format, w, h, 1, 1, Dx.CP_FLAGS.NONE);
        var i = image.GetImage(0, 0, 0);
        for (var y = 0; y < h; y++)
            Marshal.Copy(level0, y * w * 4, IntPtr.Add(i.Pixels, y * (int)i.RowPitch), w * 4);
        Dx.ScratchImage chain;
        try
        {
            chain = image.GenerateMipMaps(Dx.TEX_FILTER_FLAGS.DEFAULT, 0);
        }
        catch (Exception)
        {
            // DirectXTexNet cannot mip this size (reported by the GenerateMipMaps parity case); use level 0 only.
            return [level0];
        }

        using var chainScope = chain;
        var count = chain.GetMetadata().MipLevels;
        var result = new byte[count][];
        for (var mip = 0; mip < count; mip++)
        {
            var m = chain.GetImage(mip, 0, 0);
            result[mip] = new byte[m.Width * m.Height * 4];
            for (var y = 0; y < m.Height; y++)
                Marshal.Copy(IntPtr.Add(m.Pixels, y * (int)m.RowPitch), result[mip], y * m.Width * 4, m.Width * 4);
        }

        return result;
    }

    private static void Synthetic()
    {
        var sizes = new[] { (64, 64), (100, 60), (513, 257), (4, 4), (8, 4), (3, 7), (1, 1), (256, 128), (2048, 16) };
        var shapes = new (string Name, Func<int, int, int, byte[]>? Shape)[]
        {
            ("noise", null),
            ("punch-through", (x, y, r) => (x / 4 + y / 4) % 3 == 0 ? [0, 0, 0, 0] : [(byte)(x * 3), (byte)(y * 5), (byte)(r & 255), 255]),
            ("alpha gradient", (x, y, r) => [(byte)(x * 2), (byte)(y * 2), 40, (byte)((x + y) & 255)]),
            ("solid", (x, y, r) => [128, 128, 128, 255]),
        };
        foreach (var (w, h) in sizes)
        {
            foreach (var (shapeName, shape) in shapes)
            {
                foreach (var (srgbName, inFormat, bc1, bc3) in new[]
                         {
                             ("sRGB", (int)Dx.DXGI_FORMAT.B8G8R8A8_UNORM_SRGB, (int)Dx.DXGI_FORMAT.BC1_UNORM_SRGB, (int)Dx.DXGI_FORMAT.BC3_UNORM_SRGB),
                             ("UNORM", (int)Dx.DXGI_FORMAT.B8G8R8A8_UNORM, (int)Dx.DXGI_FORMAT.BC1_UNORM, (int)Dx.DXGI_FORMAT.BC3_UNORM),
                         })
                {
                    var label = $"synthetic {shapeName} {w}x{h} {srgbName}";
                    var level0 = Noise(w, h, w * 31 + h, shape);
                    foreach (var (name, filter) in new[] { ("DEFAULT", 0u), ("BOX", 0x400000u) })
                        Check($"GenerateMipMaps {name}", label, A.Mips(inFormat, level0, w, h, filter, 0), B.Mips(inFormat, level0, w, h, filter, 0));
                    foreach (var (num, den) in new[] { (1, 2), (1, 4), (3, 4) })
                    {
                        var nw = Math.Max(1, w * num / den);
                        var nh = Math.Max(1, h * num / den);
                        Check($"Resize BOX {num}/{den}", label, A.Resize(inFormat, level0, w, h, nw, nh, 0x400000), B.Resize(inFormat, level0, w, h, nw, nh, 0x400000));
                    }

                    var mips = ChainOf(level0, w, h, inFormat);
                    foreach (var target in new[] { bc1, bc3 })
                    {
                        Check("Compress DEFAULT + SaveToDDS NONE", label + " " + (Dx.DXGI_FORMAT)target, A.Compress(inFormat, target, w, h, mips, 0, 0.5f, 0), B.Compress(inFormat, target, w, h, mips, 0, 0.5f, 0));
                        Check("Compress DEFAULT + SaveToDDS FORCE_DX10_EXT", label + " " + (Dx.DXGI_FORMAT)target, A.Compress(inFormat, target, w, h, mips, 0, 0.5f, 0x10000), B.Compress(inFormat, target, w, h, mips, 0, 0.5f, 0x10000));
                        Check("Compress alphaRef 0.25/0.75", label + " " + (Dx.DXGI_FORMAT)target, A.Compress(inFormat, target, w, h, mips, 0, 0.25f, 0), B.Compress(inFormat, target, w, h, mips, 0, 0.25f, 0));
                        Check("Compress alphaRef 0.25/0.75", label + " " + (Dx.DXGI_FORMAT)target, A.Compress(inFormat, target, w, h, mips, 0, 0.75f, 0), B.Compress(inFormat, target, w, h, mips, 0, 0.75f, 0));
                        Check("Compress DITHER", label + " " + (Dx.DXGI_FORMAT)target, A.Compress(inFormat, target, w, h, mips, 0x30000, 0.5f, 0), B.Compress(inFormat, target, w, h, mips, 0x30000, 0.5f, 0));
                        Check("Decompress after Compress", label + " " + (Dx.DXGI_FORMAT)target, A.Decompress(inFormat, target, w, h, mips, inFormat), B.Decompress(inFormat, target, w, h, mips, inFormat));
                    }

                    if (w >= 8 && h >= 8)
                    {
                        var dw = w + 16;
                        var dh = h + 16;
                        Check("CopyRectangle", label, A.CopyRectangle(inFormat, level0, w, h, 2, 3, Math.Min(w - 2, 20), Math.Min(h - 3, 20), dw, dh, 5, 6, 0),
                            B.CopyRectangle(inFormat, level0, w, h, 2, 3, Math.Min(w - 2, 20), Math.Min(h - 3, 20), dw, dh, 5, 6, 0));
                    }
                }
            }
        }

        // WIC: PNGs written by System.Drawing, read with each default/ignore sRGB flag, from memory and from a file.
        foreach (var (w, h, opaque) in new[] { (64, 32, true), (33, 17, false), (200, 200, false) })
        {
            using var bitmap = new Bitmap(w, h, opaque ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
            var random = new Random(w);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(opaque ? 255 : random.Next(256), random.Next(256), random.Next(256), random.Next(256)));
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var png = stream.ToArray();
            foreach (var (name, flags) in new[] { ("DEFAULT_SRGB", 0x100u), ("IGNORE_SRGB", 0x20u), ("FORCE_RGB", 0x1u) })
            {
                Check($"LoadFromWICMemory {name}", $"png {w}x{h} opaque={opaque}", A.LoadWic(png, flags, false), B.LoadWic(png, flags, false));
                Check($"LoadFromWICFile {name}", $"png {w}x{h} opaque={opaque}", A.LoadWic(png, flags, true), B.LoadWic(png, flags, true));
            }
        }
    }
}
