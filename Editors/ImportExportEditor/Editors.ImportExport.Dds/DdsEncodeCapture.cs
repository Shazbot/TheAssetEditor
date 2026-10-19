using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Editors.ImportExport.Dds.Native;

namespace Editors.ImportExport.Dds
{
    /// <summary>
    /// Opt-in recorder of every DirectXTex encode. Set the environment variable
    /// <c>PACK_OPTIMIZER_DDS_CAPTURE_DIR</c> to a folder to record, for each Compress call, the
    /// uncompressed mip chain handed to it (raw tightly packed pixels per level), the level-0
    /// image before any Resize or mip generation, the target format, flags, alpha reference, how the
    /// mips were made, the source path and the stage. Unset (the default) it does nothing and the
    /// callers' output is unchanged: nothing is read from or written to the images they pass.
    /// </summary>
    public static class DdsEncodeCapture
    {
        public const string DirectoryVariable = "PACK_OPTIMIZER_DDS_CAPTURE_DIR";

        private static readonly string? CaptureDirectory = ReadDirectory();
        private static readonly AsyncLocal<(string Stage, string Source)?> Context = new();
        private static int _sequence;

        public static bool Enabled => CaptureDirectory != null;

        /// <summary>Names the stage and source texture for the encodes made inside the scope.</summary>
        public static IDisposable? Scope(string stage, string source)
        {
            if (!Enabled)
                return null;

            var previous = Context.Value;
            Context.Value = (stage, source);
            return new Restore(previous);
        }

        public static void Record(
            string callSite,
            ScratchImage? level0BeforeResize,
            ScratchImage? afterResize,
            ScratchImage toCompress,
            DXGI_FORMAT target,
            TEX_COMPRESS_FLAGS flags,
            float alphaRef,
            string mipMethod)
        {
            if (CaptureDirectory == null)
                return;

            var sequence = Interlocked.Increment(ref _sequence);
            var directory = Path.Combine(CaptureDirectory, sequence.ToString("D4", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);

            var chainMeta = toCompress.GetMetadata();
            for (var mip = 0; mip < chainMeta.MipLevels; mip++)
                WriteLevel(Path.Combine(directory, $"mip{mip}.raw"), toCompress.GetImage(mip, 0, 0));
            if (level0BeforeResize != null)
                WriteLevel(Path.Combine(directory, "level0_before.raw"), level0BeforeResize.GetImage(0, 0, 0));
            if (afterResize != null)
                WriteLevel(Path.Combine(directory, "level0_resized.raw"), afterResize.GetImage(0, 0, 0));

            var context = Context.Value;
            var before = level0BeforeResize?.GetImage(0, 0, 0);
            var meta = new StringBuilder();
            meta.Append('{');
            Add(meta, "sequence", sequence);
            Add(meta, "callSite", callSite);
            Add(meta, "stage", context?.Stage ?? "unknown");
            Add(meta, "source", context?.Source ?? string.Empty);
            Add(meta, "targetFormat", target.ToString());
            Add(meta, "inputFormat", chainMeta.Format.ToString());
            Add(meta, "compressFlags", flags.ToString());
            Add(meta, "alphaRef", alphaRef);
            Add(meta, "mipMethod", mipMethod);
            Add(meta, "width", chainMeta.Width);
            Add(meta, "height", chainMeta.Height);
            Add(meta, "mipLevels", chainMeta.MipLevels);
            Add(meta, "level0BeforeWidth", before?.Width ?? 0);
            Add(meta, "level0BeforeHeight", before?.Height ?? 0, last: true);
            meta.Append('}');
            File.WriteAllText(Path.Combine(directory, "meta.json"), meta.ToString());
        }

        /// <summary>
        /// Records which blocks of a generated sheet are verbatim source blocks. One file per sheet under
        /// <c>copymaps</c>: for each mip level, int32 block width, int32 block height, then one byte per
        /// block (the <c>SharedRepackBlockFate</c> value; 7 means copied).
        /// </summary>
        public static void RecordCopyMap(string sheetPath, int[] blocksWide, int[] blocksHigh, byte[][] fates)
        {
            if (CaptureDirectory == null)
                return;

            var directory = Path.Combine(CaptureDirectory, "copymaps");
            Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, sheetPath.Replace('\\', '_').Replace('/', '_') + ".copymap"));
            using var writer = new BinaryWriter(stream);
            writer.Write(fates.Length);
            for (var level = 0; level < fates.Length; level++)
            {
                writer.Write(blocksWide[level]);
                writer.Write(blocksHigh[level]);
                writer.Write(fates[level]);
            }
        }

        private static void WriteLevel(string path, Image image)
        {
            var rowBytes = checked(image.Width * 4);
            var pitch = checked((int)image.RowPitch);
            var buffer = new byte[checked(rowBytes * image.Height)];
            for (var y = 0; y < image.Height; y++)
                Marshal.Copy(IntPtr.Add(image.Pixels, y * pitch), buffer, y * rowBytes, rowBytes);
            File.WriteAllBytes(path, buffer);
        }

        private static string? ReadDirectory()
        {
            var value = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            Directory.CreateDirectory(value);
            return value;
        }

        private static void Add(StringBuilder builder, string name, string value, bool last = false)
            => builder.Append('"').Append(name).Append("\":\"")
                .Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""))
                .Append('"').Append(last ? string.Empty : ",");

        private static void Add(StringBuilder builder, string name, int value, bool last = false)
            => builder.Append('"').Append(name).Append("\":")
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append(last ? string.Empty : ",");

        private static void Add(StringBuilder builder, string name, float value, bool last = false)
            => builder.Append('"').Append(name).Append("\":")
                .Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(last ? string.Empty : ",");

        private sealed class Restore((string Stage, string Source)? previous) : IDisposable
        {
            public void Dispose() => Context.Value = previous;
        }
    }
}
