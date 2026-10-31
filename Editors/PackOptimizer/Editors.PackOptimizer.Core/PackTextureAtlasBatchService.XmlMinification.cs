using System.IO;
using System.Text;
using System.Xml;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.PackFiles.Utility;

namespace Editors.PackOptimizer.Core
{
    internal enum XmlFileMinificationOutcome
    {
        Minified,
        NoSizeReduction,
        InvalidOrUnsupported,
    }

    internal readonly record struct XmlFileMinificationAttempt(
        XmlFileMinificationOutcome Outcome,
        byte[]? MinifiedData);

    internal sealed record XmlMinificationSummary(
        int FilesScanned,
        int XmlCandidates,
        int FilesMinified,
        long BytesSaved,
        int ConsumerBlockedFiles,
        int InvalidOrUnsupportedFiles,
        int NoSizeReductionFiles,
        int CompressedBinaryFilesSkipped);

    /// <summary>
    /// Strict XML parser and whitespace-aware writer shared by the pack pass and its focused tests.
    /// </summary>
    internal static class XmlContentMinifier
    {
        private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

        internal static bool IsXmlHeader(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return false;

            var encoding = XmlHeaderEncoding.Utf8;
            var offset = 0;

            if (HasPrefix(data, 0x00, 0x00, 0xFE, 0xFF))
            {
                encoding = XmlHeaderEncoding.Utf32BigEndian;
                offset = 4;
            }
            else if (HasPrefix(data, 0xFF, 0xFE, 0x00, 0x00))
            {
                encoding = XmlHeaderEncoding.Utf32LittleEndian;
                offset = 4;
            }
            else if (HasPrefix(data, 0xEF, 0xBB, 0xBF))
            {
                offset = 3;
            }
            else if (HasPrefix(data, 0xFE, 0xFF))
            {
                encoding = XmlHeaderEncoding.Utf16BigEndian;
                offset = 2;
            }
            else if (HasPrefix(data, 0xFF, 0xFE))
            {
                encoding = XmlHeaderEncoding.Utf16LittleEndian;
                offset = 2;
            }
            else if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && IsXmlStartByte(data[3]))
            {
                encoding = XmlHeaderEncoding.Utf32BigEndian;
            }
            else if (data.Length >= 4 && IsXmlStartByte(data[0]) && data[1] == 0 && data[2] == 0 && data[3] == 0)
            {
                encoding = XmlHeaderEncoding.Utf32LittleEndian;
            }
            else if (data.Length >= 2 && data[0] == 0 && IsXmlStartByte(data[1]))
            {
                encoding = XmlHeaderEncoding.Utf16BigEndian;
            }
            else if (data.Length >= 2 && IsXmlStartByte(data[0]) && data[1] == 0)
            {
                encoding = XmlHeaderEncoding.Utf16LittleEndian;
            }

            return encoding switch
            {
                XmlHeaderEncoding.Utf8 => HasUtf8Markup(data, offset),
                XmlHeaderEncoding.Utf16LittleEndian => HasUtf16Markup(data, offset, littleEndian: true),
                XmlHeaderEncoding.Utf16BigEndian => HasUtf16Markup(data, offset, littleEndian: false),
                XmlHeaderEncoding.Utf32LittleEndian => HasUtf32Markup(data, offset, littleEndian: true),
                XmlHeaderEncoding.Utf32BigEndian => HasUtf32Markup(data, offset, littleEndian: false),
                _ => false,
            };
        }

        internal static bool IsXmlLikePath(string path)
        {
            var normalized = path.Replace('/', '\\');
            return normalized.EndsWith("xml", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".material", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".xsl", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".xslt", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".plist", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsKnownBinaryPath(string path)
        {
            var normalized = path.Replace('/', '\\');
            return normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".ktx", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".astc", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".rigid_model_v2_table", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".animpack", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".esf", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".bank", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);
        }

        internal static XmlFileMinificationAttempt TryMinify(
            byte[] source,
            CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = new MemoryStream(source, writable: false);
                using var reader = XmlReader.Create(
                    input,
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreWhitespace = false,
                        CheckCharacters = true,
                        ConformanceLevel = ConformanceLevel.Document,
                        CloseInput = false,
                    });

                var document = new XmlDocument
                {
                    PreserveWhitespace = true,
                    XmlResolver = null,
                };
                document.Load(reader);

                var declaration = document.ChildNodes
                    .OfType<XmlDeclaration>()
                    .FirstOrDefault();
                // XmlWriter supports XML 1.0. Keep uncommon or future versions byte-for-byte.
                if (declaration is { Version: not "1.0" })
                {
                    return new XmlFileMinificationAttempt(
                        XmlFileMinificationOutcome.InvalidOrUnsupported,
                        null);
                }

                cancellationToken.ThrowIfCancellationRequested();
                RemoveDocumentTrivia(document);
                if (document.DocumentElement is { } root)
                    RemoveFormattingWhitespace(root, inheritedPreserve: false, cancellationToken);

                using var output = new MemoryStream();
                using (var writer = XmlWriter.Create(
                           output,
                           new XmlWriterSettings
                           {
                               Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                               OmitXmlDeclaration = declaration == null,
                               Indent = false,
                               NewLineHandling = NewLineHandling.Entitize,
                               CheckCharacters = true,
                               ConformanceLevel = ConformanceLevel.Document,
                               CloseOutput = false,
                           }))
                {
                    if (declaration != null)
                    {
                        var standalone = declaration.Standalone;
                        if (standalone == "yes")
                            writer.WriteStartDocument(standalone: true);
                        else if (standalone == "no")
                            writer.WriteStartDocument(standalone: false);
                        else
                            writer.WriteStartDocument();
                    }

                    foreach (XmlNode child in document.ChildNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (child is XmlDeclaration or XmlComment)
                            continue;
                        child.WriteTo(writer);
                    }

                    if (declaration != null)
                        writer.WriteEndDocument();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var minified = output.ToArray();
                return minified.Length < source.Length
                    ? new XmlFileMinificationAttempt(XmlFileMinificationOutcome.Minified, minified)
                    : new XmlFileMinificationAttempt(XmlFileMinificationOutcome.NoSizeReduction, null);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return new XmlFileMinificationAttempt(
                    XmlFileMinificationOutcome.InvalidOrUnsupported,
                    null);
            }
        }

        internal static bool IsReadFailure(Exception exception)
            => IsRecoverable(exception);

        private static void RemoveDocumentTrivia(XmlDocument document)
        {
            foreach (XmlNode node in document.ChildNodes.Cast<XmlNode>().ToArray())
            {
                if (node is XmlComment || node.NodeType == XmlNodeType.Whitespace)
                    document.RemoveChild(node);
            }
        }

        private static void RemoveFormattingWhitespace(
            XmlElement element,
            bool inheritedPreserve,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preserveSpace = GetPreserveSpace(element, inheritedPreserve);
            var hasElementChildren = element.ChildNodes.Cast<XmlNode>().Any(node => node is XmlElement);
            var hasMixedText = hasElementChildren && element.ChildNodes
                .Cast<XmlNode>()
                .Any(node =>
                    node is XmlCDataSection ||
                    (node.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace &&
                     !IsXmlWhitespace(node.Value)));

            foreach (XmlNode child in element.ChildNodes.Cast<XmlNode>().ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child is XmlComment)
                {
                    element.RemoveChild(child);
                    continue;
                }

                // Keep whitespace-only leaves, mixed-content spacing, same-line separators,
                // and all whitespace covered by xml:space="preserve". Pretty-printed
                // line-break indentation between elements is the only text removed.
                if (!preserveSpace && hasElementChildren && !hasMixedText && IsFormattingWhitespace(child))
                {
                    element.RemoveChild(child);
                    continue;
                }

                if (child is XmlElement nested)
                    RemoveFormattingWhitespace(nested, preserveSpace, cancellationToken);
            }
        }

        private static bool GetPreserveSpace(XmlElement element, bool inheritedPreserve)
        {
            var space = element.GetAttributeNode("space", XmlNamespace)?.Value;
            return space switch
            {
                "preserve" => true,
                "default" => false,
                null or "" => inheritedPreserve,
                _ => true,
            };
        }

        private static bool IsFormattingWhitespace(XmlNode node)
        {
            if (node.NodeType is not (XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.Text) ||
                !IsXmlWhitespace(node.Value))
            {
                return false;
            }

            var value = node.Value!;
            return value.Contains('\n') || value.Contains('\r');
        }

        private static bool IsXmlWhitespace(string? value)
            => value != null && value.All(character => character is ' ' or '\t' or '\r' or '\n');

        private static bool HasUtf8Markup(ReadOnlySpan<byte> data, int offset)
        {
            for (; offset < data.Length; offset++)
            {
                var value = data[offset];
                if (value is 0x20 or 0x09 or 0x0A or 0x0D)
                    continue;
                return value == (byte)'<';
            }

            return false;
        }

        private static bool HasPrefix(ReadOnlySpan<byte> data, params byte[] prefix)
        {
            if (data.Length < prefix.Length)
                return false;
            for (var index = 0; index < prefix.Length; index++)
            {
                if (data[index] != prefix[index])
                    return false;
            }

            return true;
        }

        private static bool HasUtf16Markup(ReadOnlySpan<byte> data, int offset, bool littleEndian)
        {
            while (offset + 1 < data.Length)
            {
                var value = littleEndian
                    ? (char)(data[offset] | data[offset + 1] << 8)
                    : (char)(data[offset] << 8 | data[offset + 1]);
                if (!IsXmlWhitespace(value))
                    return value == '<';
                offset += 2;
            }

            return false;
        }

        private static bool HasUtf32Markup(ReadOnlySpan<byte> data, int offset, bool littleEndian)
        {
            while (offset + 3 < data.Length)
            {
                var value = littleEndian
                    ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
                    : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
                if (value is 0x20 or 0x09 or 0x0A or 0x0D)
                {
                    offset += 4;
                    continue;
                }

                return value == '<';
            }

            return false;
        }

        private static bool IsXmlWhitespace(char value)
            => value is ' ' or '\t' or '\r' or '\n';

        private static bool IsXmlStartByte(byte value)
            => value is (byte)'<' or 0x20 or 0x09 or 0x0A or 0x0D;

        private static bool IsRecoverable(Exception exception)
            => exception is XmlException or IOException or InvalidDataException or
                DecoderFallbackException or InvalidOperationException or NotSupportedException or ArgumentException;

        private enum XmlHeaderEncoding
        {
            Utf8,
            Utf16LittleEndian,
            Utf16BigEndian,
            Utf32LittleEndian,
            Utf32BigEndian,
        }
    }

    public sealed partial class PackTextureAtlasBatchService
    {
        private const int XmlMinificationHeaderBytes = 4096;
        private const string XmlMinificationStage = "XML minification";

        private void MinifyXmlFiles(
            BatchState state,
            CancellationToken cancellationToken,
            IProgress<TextureAtlasPackProgress>? progress)
        {
            var files = state.Output.GetAllFiles()
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidates = 0;
            var minifiedCount = 0;
            var bytesSaved = 0L;
            var consumerBlocked = 0;
            var invalidOrUnsupported = 0;
            var noSizeReduction = 0;
            var compressedBinarySkipped = 0;
            var replacements = new List<NewPackFileEntry>();

            ReportProgress(
                progress,
                "Minifying XML files",
                0,
                files.Length,
                $"{files.Length:N0} output file(s)");

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, packFile) = files[index];
                var normalizedPath = Normalize(path);
                var source = packFile.DataSource;
                var compressed = source.CompressionFormat != CompressionFormat.None ||
                                 source is PackedFileSource { IsCompressed: true };
                var xmlLikePath = XmlContentMinifier.IsXmlLikePath(normalizedPath);

                if (compressed && XmlContentMinifier.IsKnownBinaryPath(normalizedPath))
                {
                    compressedBinarySkipped++;
                    ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                    continue;
                }

                byte[] bytes;
                var candidateCounted = false;
                try
                {
                    if (compressed)
                    {
                        // PackedFileSource.PeekData decompresses to the requested byte count, which
                        // is not a safe header probe. Read XML-like compressed assets directly;
                        // known binary textures/models were excluded above.
                        if (xmlLikePath)
                        {
                            candidates++;
                            candidateCounted = true;
                            if (state.Consumers?.TryBlock(normalizedPath, XmlMinificationStage) == true)
                            {
                                consumerBlocked++;
                                ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                                continue;
                            }
                        }

                        bytes = source.ReadData();
                        if (!xmlLikePath &&
                            !XmlContentMinifier.IsXmlHeader(bytes.AsSpan(0, Math.Min(bytes.Length, XmlMinificationHeaderBytes))))
                        {
                            ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                            continue;
                        }

                        if (!candidateCounted)
                        {
                            candidates++;
                            candidateCounted = true;
                            if (state.Consumers?.TryBlock(normalizedPath, XmlMinificationStage) == true)
                            {
                                consumerBlocked++;
                                ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        if (source.Size <= 0 || source.Size > int.MaxValue)
                        {
                            ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                            continue;
                        }

                        var headerLength = (int)Math.Min(source.Size, XmlMinificationHeaderBytes);
                        var header = source.PeekData(headerLength);
                        if (!xmlLikePath && !XmlContentMinifier.IsXmlHeader(header))
                        {
                            ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                            continue;
                        }

                        candidates++;
                        candidateCounted = true;
                        if (state.Consumers?.TryBlock(normalizedPath, XmlMinificationStage) == true)
                        {
                            consumerBlocked++;
                            ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                            continue;
                        }

                        bytes = source.ReadData();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (XmlContentMinifier.IsReadFailure(exception))
                {
                    if (!candidateCounted)
                        candidates++;
                    invalidOrUnsupported++;
                    ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
                    continue;
                }

                var attempt = XmlContentMinifier.TryMinify(bytes, cancellationToken);
                switch (attempt.Outcome)
                {
                    case XmlFileMinificationOutcome.Minified:
                        var minified = attempt.MinifiedData!;
                        replacements.Add(CreateReplacementEntry(normalizedPath, minified));
                        minifiedCount++;
                        bytesSaved += bytes.Length - minified.Length;
                        break;
                    case XmlFileMinificationOutcome.NoSizeReduction:
                        noSizeReduction++;
                        break;
                    default:
                        invalidOrUnsupported++;
                        break;
                }

                ReportProgress(progress, "Minifying XML files", index + 1, files.Length, normalizedPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (replacements.Count != 0)
            {
                _packFileService.AddFilesToPack(state.Output, replacements);
            }

            state.XmlMinification = new XmlMinificationSummary(
                files.Length,
                candidates,
                minifiedCount,
                bytesSaved,
                consumerBlocked,
                invalidOrUnsupported,
                noSizeReduction,
                compressedBinarySkipped);

            ReportProgress(
                progress,
                "Minifying XML files",
                files.Length,
                files.Length,
                $"{minifiedCount:N0} file(s) minified; {bytesSaved:N0} bytes saved");
        }

        private static void AppendXmlMinificationSection(StringBuilder report, BatchState state)
        {
            if (state.XmlMinification is not { } result)
                return;

            report.AppendLine("XML minification");
            report.AppendLine("----------------");
            report.AppendLine($"Output files scanned: {result.FilesScanned:N0}");
            report.AppendLine($"XML candidates: {result.XmlCandidates:N0}");
            report.AppendLine($"Files minified: {result.FilesMinified:N0}");
            report.AppendLine($"Uncompressed bytes saved: {result.BytesSaved:N0}");
            report.AppendLine($"Consumer-protected files skipped: {result.ConsumerBlockedFiles:N0}");
            report.AppendLine($"Malformed or unsupported XML skipped: {result.InvalidOrUnsupportedFiles:N0}");
            report.AppendLine($"No size reduction: {result.NoSizeReductionFiles:N0}");
            report.AppendLine($"Compressed binary textures/models skipped: {result.CompressedBinaryFilesSkipped:N0}");
            report.AppendLine();
        }
    }
}
