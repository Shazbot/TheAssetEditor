using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using GameWorld.Core.Services;
using Shared.Core.PackFiles;
using Shared.GameFormats.RigidModel;
using Shared.GameFormats.Vmd;

namespace WH3AssetHost;

/// <summary>
/// Produces a pack-ready loose-file variant from the currently selected VMD
/// composition. Only model/material assets that actually reference a painted
/// BaseColor source are cloned; unaffected components continue to reference
/// their original game assets.
/// </summary>
internal sealed class PaintedVariantExporter
{
    private readonly IHeadlessPackFileService _packFileService;
    private readonly VariantMeshCompositionResolver _compositionResolver;

    public PaintedVariantExporter(
        IHeadlessPackFileService packFileService,
        VariantMeshCompositionResolver compositionResolver)
    {
        _packFileService = packFileService;
        _compositionResolver = compositionResolver;
    }

    public AssetHostPaintedVariantResult Export(AssetHostPaintedVariantRequest request)
    {
        var warnings = new List<string>();
        var writtenVirtualFiles = new List<string>();

        try
        {
            var inputFile = _packFileService.FindFile(request.AssetPath);
            if (inputFile == null)
                return Failure("AssetNotFound", $"Asset '{request.AssetPath}' was not found.");

            if (!inputFile.Name.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    "UnsupportedPaintedAsset",
                    "Painted variant export currently requires a .variantmeshdefinition source.");
            }

            var selections = request.VariantSelections?
                .Select(selection => new VariantMeshSelection(selection.SlotPath, selection.ChoiceIndex))
                .ToArray() ?? Array.Empty<VariantMeshSelection>();
            var composition = selections.Length == 0
                ? _compositionResolver.Resolve(inputFile)
                : _compositionResolver.Resolve(inputFile, selections);

            warnings.AddRange(composition.Diagnostics);
            if (!composition.HasRenderableContent || composition.Root == null)
                return Failure("VariantMeshResolutionFailed", "The selected variant mesh has no renderable model components.", warnings);

            var components = EnumerateComponents(composition.Root).ToArray();
            if (components.Length == 0)
                return Failure("VariantMeshResolutionFailed", "The selected variant mesh contains no model components.", warnings);

            var variantName = SanitizeVariantName(request.VariantName);
            if (string.IsNullOrWhiteSpace(variantName))
                return Failure("InvalidVariantName", "The painted variant name did not contain any usable characters.", warnings);

            Directory.CreateDirectory(request.OutputDirectory);
            var assetRoot = $"variantmeshes\\whmm_unit_painter\\{variantName}";
            var vmdVirtualPath =
                $"variantmeshes\\variantmeshdefinitions\\whmm_unit_painter\\{variantName}.variantmeshdefinition";

            var paintedSources = request.Textures
                .Select(texture => new
                {
                    Texture = texture,
                    Source = NormalizeVirtualPath(texture.SourceVirtualPath)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Source))
                .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();

            if (paintedSources.Length == 0)
                return Failure("NoPaintedTextures", "No usable painted texture inputs were provided.", warnings);

            var usedSourceTextures = components
                .SelectMany(component => component.Asset.PartsByLod)
                .SelectMany(parts => parts)
                .SelectMany(part => part.Material.Textures.Values)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeVirtualPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var painted in paintedSources)
            {
                if (!usedSourceTextures.Contains(painted.Source))
                {
                    warnings.Add($"Painted texture '{painted.Source}' is not used by the selected VMD composition and was skipped.");
                    continue;
                }

                var sourceStem = SafeStem(Path.GetFileNameWithoutExtension(painted.Source));
                var sourceHash = ShortHash(painted.Source);
                var ddsVirtualPath = $"{assetRoot}\\textures\\{sourceStem}_{sourceHash}_painted.dds";
                var ddsBytes = Dxt5DdsEncoder.EncodePngFile(painted.Texture.PngPath);
                WriteVirtualFile(request.OutputDirectory, ddsVirtualPath, ddsBytes);
                writtenVirtualFiles.Add(ddsVirtualPath);
                replacements[painted.Source] = ddsVirtualPath;
            }

            if (replacements.Count == 0)
            {
                return Failure(
                    "NoMatchingPaintedTextures",
                    "None of the painted textures are used by the selected VMD composition.",
                    warnings);
            }

            var assetCloneCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var materialCloneCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var exportedComponents = new List<ExportedComponent>(components.Length);

            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var originalPath = GetVirtualPath(component.Asset.InputFile);
                var affected = ComponentUsesPaintedTexture(component.Asset, replacements);
                var exportedPath = originalPath;

                if (affected)
                {
                    if (!assetCloneCache.TryGetValue(originalPath, out exportedPath!))
                    {
                        exportedPath = CloneAffectedAsset(
                            component.Asset,
                            index,
                            assetRoot,
                            request.OutputDirectory,
                            replacements,
                            materialCloneCache,
                            writtenVirtualFiles,
                            warnings);
                        assetCloneCache[originalPath] = exportedPath;
                    }
                }

                exportedComponents.Add(new ExportedComponent(exportedPath, component.AttachmentPoint));
            }

            var sourceDefinition = VariantMeshDefinitionLoader.Load(inputFile);
            if (!string.IsNullOrWhiteSpace(sourceDefinition.ImposterModel))
            {
                warnings.Add(
                    $"Source VMD imposter model '{sourceDefinition.ImposterModel}' is not copied into the painted variant, "
                    + "because it would still render the original unpainted appearance.");
            }

            var vmdBytes = BuildFlattenedVariantMesh(exportedComponents, sourceDefinition);
            WriteVirtualFile(request.OutputDirectory, vmdVirtualPath, vmdBytes);
            writtenVirtualFiles.Add(vmdVirtualPath);

            var manifestVirtualPath = $"whmm_unit_painter_manifest_{variantName}.json";
            WriteManifest(
                request.OutputDirectory,
                manifestVirtualPath,
                request.AssetPath,
                vmdVirtualPath,
                replacements,
                writtenVirtualFiles,
                warnings);
            writtenVirtualFiles.Add(manifestVirtualPath);

            return new AssetHostPaintedVariantResult(
                true,
                vmdVirtualPath,
                writtenVirtualFiles,
                warnings,
                Array.Empty<AssetHostError>());
        }
        catch (Exception exception)
        {
            return new AssetHostPaintedVariantResult(
                false,
                null,
                writtenVirtualFiles,
                warnings,
                [new AssetHostError("PaintedVariantExportFailed", exception.Message, exception.ToString())]);
        }
    }

    private string CloneAffectedAsset(
        ResolvedModelAsset asset,
        int componentIndex,
        string assetRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, string> replacements,
        Dictionary<string, string> materialCloneCache,
        List<string> writtenVirtualFiles,
        List<string> warnings)
    {
        var hasEffectiveWsMaterials = asset.PartsByLod
            .SelectMany(parts => parts)
            .Any(part => part.Material.UsesWsModelMaterial);

        if (hasEffectiveWsMaterials && asset.WsModelFile != null)
        {
            var wsModelPath = CloneWsModel(
                asset,
                componentIndex,
                assetRoot,
                outputDirectory,
                replacements,
                materialCloneCache,
                writtenVirtualFiles);
            if (wsModelPath != null)
                return wsModelPath;

            // A WSModel can inherit a slot from the RMV2 material when its XML
            // does not override that texture. Clone the geometry in that case,
            // then keep every original WS material mapping while pointing the
            // cloned WSModel at the painted RMV2.
            var clonedGeometryPath = CloneRmvModel(
                asset,
                componentIndex,
                assetRoot,
                outputDirectory,
                replacements,
                writtenVirtualFiles);
            return CloneWsModelWithGeometry(
                asset.WsModelFile,
                componentIndex,
                assetRoot,
                outputDirectory,
                clonedGeometryPath,
                writtenVirtualFiles);
        }

        return CloneRmvModel(
            asset,
            componentIndex,
            assetRoot,
            outputDirectory,
            replacements,
            writtenVirtualFiles);
    }

    private string? CloneWsModel(
        ResolvedModelAsset asset,
        int componentIndex,
        string assetRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, string> replacements,
        Dictionary<string, string> materialCloneCache,
        List<string> writtenVirtualFiles)
    {
        var wsModelFile = asset.WsModelFile;
        if (wsModelFile == null)
            return null;

        var document = LoadXml(wsModelFile.DataSource.ReadData());
        var materialNodes = document.SelectNodes("/model/materials/material");
        if (materialNodes == null)
            return null;

        var changed = false;
        foreach (XmlNode materialNode in materialNodes)
        {
            var sourceMaterialPath = NormalizeVirtualPath(materialNode.InnerText);
            if (string.IsNullOrWhiteSpace(sourceMaterialPath))
                continue;

            if (!materialCloneCache.TryGetValue(sourceMaterialPath, out var clonedMaterialPath))
            {
                clonedMaterialPath = CloneMaterialIfAffected(
                    sourceMaterialPath,
                    assetRoot,
                    outputDirectory,
                    replacements,
                    writtenVirtualFiles) ?? sourceMaterialPath;
                materialCloneCache[sourceMaterialPath] = clonedMaterialPath;
            }

            if (string.Equals(sourceMaterialPath, clonedMaterialPath, StringComparison.OrdinalIgnoreCase))
                continue;

            materialNode.InnerText = clonedMaterialPath;
            changed = true;
        }

        if (!changed)
            return null;

        var sourcePath = GetVirtualPath(wsModelFile);
        var fileName = $"{componentIndex:D3}_{SafeStem(Path.GetFileNameWithoutExtension(sourcePath))}_{ShortHash(sourcePath)}.wsmodel";
        var targetVirtualPath = $"{assetRoot}\\models\\{fileName}";
        WriteVirtualFile(outputDirectory, targetVirtualPath, SaveXml(document));
        writtenVirtualFiles.Add(targetVirtualPath);
        return targetVirtualPath;
    }

    private static string CloneWsModelWithGeometry(
        Shared.Core.PackFiles.Models.PackFile wsModelFile,
        int componentIndex,
        string assetRoot,
        string outputDirectory,
        string geometryVirtualPath,
        List<string> writtenVirtualFiles)
    {
        var document = LoadXml(wsModelFile.DataSource.ReadData());
        var geometryNode = document.SelectSingleNode("/model/geometry")
            ?? throw new InvalidOperationException(
                $"WSModel '{GetVirtualPath(wsModelFile)}' does not contain a geometry node.");
        geometryNode.InnerText = geometryVirtualPath;

        var sourcePath = GetVirtualPath(wsModelFile);
        var fileName =
            $"{componentIndex:D3}_{SafeStem(Path.GetFileNameWithoutExtension(sourcePath))}_{ShortHash(sourcePath)}.wsmodel";
        var targetVirtualPath = $"{assetRoot}\\models\\{fileName}";
        WriteVirtualFile(outputDirectory, targetVirtualPath, SaveXml(document));
        writtenVirtualFiles.Add(targetVirtualPath);
        return targetVirtualPath;
    }

    private string? CloneMaterialIfAffected(
        string sourceMaterialPath,
        string assetRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, string> replacements,
        List<string> writtenVirtualFiles)
    {
        var sourceFile = _packFileService.FindFile(sourceMaterialPath);
        if (sourceFile == null)
            return null;

        var document = LoadXml(sourceFile.DataSource.ReadData());
        var textureNodes = document.SelectNodes("//texture");
        if (textureNodes == null)
            return null;

        var changed = false;
        foreach (XmlNode textureNode in textureNodes)
        {
            var textNodes = textureNode.SelectNodes(".//text()");
            if (textNodes == null)
                continue;

            foreach (XmlNode textNode in textNodes)
            {
                var currentPath = NormalizeVirtualPath(textNode.Value ?? string.Empty);
                if (!replacements.TryGetValue(currentPath, out var replacement))
                    continue;
                textNode.Value = replacement;
                changed = true;
            }
        }

        if (!changed)
            return null;

        var fileName =
            $"{SafeStem(Path.GetFileNameWithoutExtension(sourceMaterialPath))}_{ShortHash(sourceMaterialPath)}.xml.material";
        var targetVirtualPath = $"{assetRoot}\\materials\\{fileName}";
        WriteVirtualFile(outputDirectory, targetVirtualPath, SaveXml(document));
        writtenVirtualFiles.Add(targetVirtualPath);
        return targetVirtualPath;
    }

    private static string CloneRmvModel(
        ResolvedModelAsset asset,
        int componentIndex,
        string assetRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, string> replacements,
        List<string> writtenVirtualFiles)
    {
        var model = ModelFactory.Create().Load(asset.GeometryFile.DataSource.ReadData());
        var changed = false;

        foreach (var lod in model.ModelList)
        {
            foreach (var part in lod)
            {
                var textures = part.Material.GetAllTextures().ToArray();
                foreach (var texture in textures)
                {
                    var sourcePath = NormalizeVirtualPath(texture.Path);
                    if (!replacements.TryGetValue(sourcePath, out var replacement))
                        continue;

                    part.Material.SetTexture(texture.TexureType, replacement);
                    changed = true;
                }
            }
        }

        if (!changed)
            throw new InvalidOperationException(
                $"Model '{GetVirtualPath(asset.GeometryFile)}' was marked as painted but no RMV2 texture reference could be replaced.");

        model.RecalculateOffsets();
        var bytes = ModelFactory.Create().Save(model);
        var sourcePathForName = GetVirtualPath(asset.GeometryFile);
        var fileName =
            $"{componentIndex:D3}_{SafeStem(Path.GetFileNameWithoutExtension(sourcePathForName))}_{ShortHash(sourcePathForName)}.rigid_model_v2";
        var targetVirtualPath = $"{assetRoot}\\models\\{fileName}";
        WriteVirtualFile(outputDirectory, targetVirtualPath, bytes);
        writtenVirtualFiles.Add(targetVirtualPath);
        return targetVirtualPath;
    }

    private static bool ComponentUsesPaintedTexture(
        ResolvedModelAsset asset,
        IReadOnlyDictionary<string, string> replacements)
        => asset.PartsByLod
            .SelectMany(parts => parts)
            .SelectMany(part => part.Material.Textures.Values)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeVirtualPath)
            .Any(replacements.ContainsKey);

    private static byte[] BuildFlattenedVariantMesh(
        IReadOnlyList<ExportedComponent> components,
        VariantMeshDefinition.VariantMesh sourceDefinition)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = true,
            NewLineChars = "\n"
        };
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartElement("VARIANT_MESH");

        if (!string.IsNullOrWhiteSpace(sourceDefinition.DecalDiffuse))
            writer.WriteAttributeString("decal_diffuse", sourceDefinition.DecalDiffuse);
        if (!string.IsNullOrWhiteSpace(sourceDefinition.DecalNormal))
            writer.WriteAttributeString("decal_normal", sourceDefinition.DecalNormal);
        if (!string.IsNullOrWhiteSpace(sourceDefinition.use_different_attach_point_parts))
        {
            writer.WriteAttributeString(
                "use_different_attach_point_parts",
                sourceDefinition.use_different_attach_point_parts);
        }

        var rootIndex = -1;
        for (var index = 0; index < components.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(components[index].AttachmentPoint))
            {
                rootIndex = index;
                writer.WriteAttributeString("model", components[index].ModelPath);
                break;
            }
        }

        for (var index = 0; index < components.Count; index++)
        {
            if (index == rootIndex)
                continue;

            var component = components[index];
            writer.WriteStartElement("SLOT");
            writer.WriteAttributeString("name", $"whmm_painted_{index:D3}");
            writer.WriteAttributeString("attach_point", component.AttachmentPoint ?? string.Empty);
            writer.WriteAttributeString("probability", "1");

            writer.WriteStartElement("VARIANT_MESH");
            writer.WriteAttributeString("model", component.ModelPath);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        foreach (var metadata in sourceDefinition.MetaDataList ?? [])
        {
            if (string.IsNullOrWhiteSpace(metadata.Value))
                continue;
            writer.WriteElementString("META_DATA", metadata.Value);
        }

        writer.WriteEndElement();
        writer.Flush();
        return stream.ToArray();
    }

    private static IEnumerable<ResolvedComponent> EnumerateComponents(
        ResolvedVariantMeshNode node,
        string attachmentPoint = "")
    {
        if (node.ModelAsset != null)
            yield return new ResolvedComponent(node.ModelAsset, attachmentPoint);

        if (node.ResolvedModelReference != null)
        {
            foreach (var component in EnumerateComponents(node.ResolvedModelReference, attachmentPoint))
                yield return component;
        }

        foreach (var slot in node.Slots)
        {
            if (slot.SelectedChild == null)
                continue;

            foreach (var component in EnumerateComponents(slot.SelectedChild, slot.AttachmentPoint))
                yield return component;
        }
    }

    private static XmlDocument LoadXml(byte[] bytes)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        var text = Encoding.UTF8.GetString(bytes);
        var firstElement = text.IndexOf('<');
        if (firstElement > 0)
            text = text[firstElement..];
        document.LoadXml(text);
        return document;
    }

    private static byte[] SaveXml(XmlDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = true
        };
        using var writer = XmlWriter.Create(stream, settings);
        document.Save(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteVirtualFile(string outputDirectory, string virtualPath, byte[] bytes)
    {
        var relativePath = virtualPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(outputDirectory, relativePath));
        var relativeToOutput = Path.GetRelativePath(outputDirectory, fullPath);
        if (relativeToOutput == ".."
            || relativeToOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativeToOutput))
        {
            throw new InvalidOperationException($"Generated path '{virtualPath}' escaped the output directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private static void WriteManifest(
        string outputDirectory,
        string manifestVirtualPath,
        string sourceAssetPath,
        string variantMeshPath,
        IReadOnlyDictionary<string, string> replacements,
        IReadOnlyList<string> files,
        IReadOnlyList<string> warnings)
    {
        var relativePath = manifestVirtualPath.Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.Combine(outputDirectory, relativePath);
        using var stream = File.Create(fullPath);
        using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteString("sourceAssetPath", sourceAssetPath);
        writer.WriteString("variantMeshPath", variantMeshPath);

        writer.WriteStartArray("textures");
        foreach (var pair in replacements)
        {
            writer.WriteStartObject();
            writer.WriteString("source", pair.Key);
            writer.WriteString("painted", pair.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("files");
        foreach (var file in files)
            writer.WriteStringValue(file);
        writer.WriteEndArray();

        writer.WriteStartArray("warnings");
        foreach (var warning in warnings)
            writer.WriteStringValue(warning);
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
    }

    private static string GetVirtualPath(Shared.Core.PackFiles.Models.PackFile file)
        => NormalizeVirtualPath(file.Name);

    private static string NormalizeVirtualPath(string path)
        => path.Replace('/', '\\').Trim().TrimStart('\\');

    private static string SanitizeVariantName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                builder.Append(character);
            else if (character is '-' or ' ' or '.')
                builder.Append('_');
        }

        var result = builder.ToString().Trim('_');
        while (result.Contains("__", StringComparison.Ordinal))
            result = result.Replace("__", "_", StringComparison.Ordinal);
        return result.Length <= 80 ? result : result[..80];
    }

    private static string SafeStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "texture";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
                builder.Append(character);
            else
                builder.Append('_');
        }
        return builder.ToString().Trim('_');
    }

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))
            .ToLowerInvariant()[..8];

    private static AssetHostPaintedVariantResult Failure(
        string code,
        string message,
        IReadOnlyList<string>? warnings = null)
        => new(
            false,
            null,
            Array.Empty<string>(),
            warnings ?? Array.Empty<string>(),
            [new AssetHostError(code, message)]);

    private sealed record ResolvedComponent(ResolvedModelAsset Asset, string AttachmentPoint);
    private sealed record ExportedComponent(string ModelPath, string AttachmentPoint);
}

/// <summary>
/// Small dependency-free BC3/DXT5 encoder used for painted BaseColor output.
/// It intentionally favors deterministic, fast export over offline compressor
/// quality; the generated DDS contains a complete mip chain and is accepted by
/// the same DirectX texture path used by WH3.
/// </summary>
internal static class Dxt5DdsEncoder
{
    private const uint DdsMagic = 0x20534444;
    private const uint DdpfFourCc = 0x00000004;
    private const uint DdsCapsTexture = 0x00001000;
    private const uint DdsCapsComplex = 0x00000008;
    private const uint DdsCapsMipMap = 0x00400000;
    private const uint DdsdCaps = 0x00000001;
    private const uint DdsdHeight = 0x00000002;
    private const uint DdsdWidth = 0x00000004;
    private const uint DdsdPixelFormat = 0x00001000;
    private const uint DdsdMipMapCount = 0x00020000;
    private const uint DdsdLinearSize = 0x00080000;
    private const uint FourCcDxt5 = 0x35545844;

    public static byte[] EncodePngFile(string pngPath)
    {
        var level = ReadPng(pngPath);
        var mipCount = CountMipLevels(level.Width, level.Height);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteHeader(writer, level.Width, level.Height, mipCount);

        for (var mip = 0; mip < mipCount; mip++)
        {
            EncodeLevel(writer, level);
            if (mip + 1 < mipCount)
                level = Downsample(level);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static RgbaImage ReadPng(string path)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(bitmapData.Stride);
            var raw = new byte[stride * bitmap.Height];
            Marshal.Copy(bitmapData.Scan0, raw, 0, raw.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];

            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceY = bitmapData.Stride < 0 ? bitmap.Height - 1 - y : y;
                var sourceRow = sourceY * stride;
                var targetRow = y * bitmap.Width * 4;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var sourceIndex = sourceRow + x * 4;
                    var targetIndex = targetRow + x * 4;
                    rgba[targetIndex] = raw[sourceIndex + 2];
                    rgba[targetIndex + 1] = raw[sourceIndex + 1];
                    rgba[targetIndex + 2] = raw[sourceIndex];
                    rgba[targetIndex + 3] = raw[sourceIndex + 3];
                }
            }

            return new RgbaImage(bitmap.Width, bitmap.Height, rgba);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static void WriteHeader(BinaryWriter writer, int width, int height, int mipCount)
    {
        writer.Write(DdsMagic);
        writer.Write(124u);
        writer.Write(DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat | DdsdLinearSize
                     | (mipCount > 1 ? DdsdMipMapCount : 0u));
        writer.Write((uint)height);
        writer.Write((uint)width);
        writer.Write((uint)(
            Math.Max(1, (width + 3) / 4)
            * Math.Max(1, (height + 3) / 4)
            * 16));
        writer.Write(0u);
        writer.Write((uint)mipCount);
        for (var index = 0; index < 11; index++)
            writer.Write(0u);

        writer.Write(32u);
        writer.Write(DdpfFourCc);
        writer.Write(FourCcDxt5);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        var caps = DdsCapsTexture;
        if (mipCount > 1)
            caps |= DdsCapsComplex | DdsCapsMipMap;
        writer.Write(caps);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
    }

    private static void EncodeLevel(BinaryWriter writer, RgbaImage image)
    {
        var block = new RgbaPixel[16];
        for (var blockY = 0; blockY < image.Height; blockY += 4)
        {
            for (var blockX = 0; blockX < image.Width; blockX += 4)
            {
                var pixelIndex = 0;
                for (var y = 0; y < 4; y++)
                {
                    var sourceY = Math.Min(blockY + y, image.Height - 1);
                    for (var x = 0; x < 4; x++)
                    {
                        var sourceX = Math.Min(blockX + x, image.Width - 1);
                        var offset = (sourceY * image.Width + sourceX) * 4;
                        block[pixelIndex++] = new RgbaPixel(
                            image.Data[offset],
                            image.Data[offset + 1],
                            image.Data[offset + 2],
                            image.Data[offset + 3]);
                    }
                }

                EncodeAlphaBlock(writer, block);
                EncodeColorBlock(writer, block);
            }
        }
    }

    private static void EncodeAlphaBlock(BinaryWriter writer, ReadOnlySpan<RgbaPixel> pixels)
    {
        byte alpha0 = 0;
        byte alpha1 = 255;
        foreach (var pixel in pixels)
        {
            alpha0 = Math.Max(alpha0, pixel.A);
            alpha1 = Math.Min(alpha1, pixel.A);
        }

        writer.Write(alpha0);
        writer.Write(alpha1);

        Span<byte> palette = stackalloc byte[8];
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
                palette[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1 + 3) / 7);
        }
        else
        {
            for (var index = 1; index <= 4; index++)
                palette[index + 1] = (byte)(((5 - index) * alpha0 + index * alpha1 + 2) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong indices = 0;
        for (var pixelIndex = 0; pixelIndex < 16; pixelIndex++)
        {
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            for (var paletteIndex = 0; paletteIndex < 8; paletteIndex++)
            {
                var distance = Math.Abs(pixels[pixelIndex].A - palette[paletteIndex]);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                bestIndex = paletteIndex;
            }
            indices |= (ulong)bestIndex << (pixelIndex * 3);
        }

        for (var byteIndex = 0; byteIndex < 6; byteIndex++)
            writer.Write((byte)(indices >> (byteIndex * 8)));
    }

    private static void EncodeColorBlock(BinaryWriter writer, ReadOnlySpan<RgbaPixel> pixels)
    {
        var darkest = pixels[0];
        var lightest = pixels[0];
        var darkestScore = LuminanceScore(darkest);
        var lightestScore = darkestScore;
        foreach (var pixel in pixels)
        {
            var score = LuminanceScore(pixel);
            if (score < darkestScore)
            {
                darkestScore = score;
                darkest = pixel;
            }
            if (score > lightestScore)
            {
                lightestScore = score;
                lightest = pixel;
            }
        }

        var color0 = ToRgb565(lightest);
        var color1 = ToRgb565(darkest);
        if (color0 == color1)
        {
            if (color0 < ushort.MaxValue)
                color0++;
            else
                color1--;
        }
        if (color0 < color1)
            (color0, color1) = (color1, color0);

        writer.Write(color0);
        writer.Write(color1);

        Span<RgbPixel> palette = stackalloc RgbPixel[4];
        palette[0] = FromRgb565(color0);
        palette[1] = FromRgb565(color1);
        palette[2] = Mix(palette[0], palette[1], 2, 1, 3);
        palette[3] = Mix(palette[0], palette[1], 1, 2, 3);

        uint indices = 0;
        for (var pixelIndex = 0; pixelIndex < 16; pixelIndex++)
        {
            var pixel = pixels[pixelIndex];
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            for (var paletteIndex = 0; paletteIndex < 4; paletteIndex++)
            {
                var candidate = palette[paletteIndex];
                var dr = pixel.R - candidate.R;
                var dg = pixel.G - candidate.G;
                var db = pixel.B - candidate.B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                bestIndex = paletteIndex;
            }
            indices |= (uint)bestIndex << (pixelIndex * 2);
        }

        writer.Write(indices);
    }

    private static RgbaImage Downsample(RgbaImage source)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        var output = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sumR = 0;
                var sumG = 0;
                var sumB = 0;
                var sumA = 0;
                var samples = 0;
                for (var offsetY = 0; offsetY < 2; offsetY++)
                {
                    var sourceY = y * 2 + offsetY;
                    if (sourceY >= source.Height)
                        continue;
                    for (var offsetX = 0; offsetX < 2; offsetX++)
                    {
                        var sourceX = x * 2 + offsetX;
                        if (sourceX >= source.Width)
                            continue;
                        var sourceIndex = (sourceY * source.Width + sourceX) * 4;
                        sumR += source.Data[sourceIndex];
                        sumG += source.Data[sourceIndex + 1];
                        sumB += source.Data[sourceIndex + 2];
                        sumA += source.Data[sourceIndex + 3];
                        samples++;
                    }
                }

                var targetIndex = (y * width + x) * 4;
                output[targetIndex] = (byte)((sumR + samples / 2) / samples);
                output[targetIndex + 1] = (byte)((sumG + samples / 2) / samples);
                output[targetIndex + 2] = (byte)((sumB + samples / 2) / samples);
                output[targetIndex + 3] = (byte)((sumA + samples / 2) / samples);
            }
        }

        return new RgbaImage(width, height, output);
    }

    private static int CountMipLevels(int width, int height)
    {
        var levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            levels++;
        }
        return levels;
    }

    private static int LuminanceScore(RgbaPixel pixel)
        => pixel.R * 299 + pixel.G * 587 + pixel.B * 114;

    private static ushort ToRgb565(RgbaPixel pixel)
        => (ushort)(((pixel.R * 31 + 127) / 255 << 11)
                    | ((pixel.G * 63 + 127) / 255 << 5)
                    | ((pixel.B * 31 + 127) / 255));

    private static RgbPixel FromRgb565(ushort value)
    {
        var r5 = (value >> 11) & 31;
        var g6 = (value >> 5) & 63;
        var b5 = value & 31;
        return new RgbPixel(
            (byte)((r5 * 255 + 15) / 31),
            (byte)((g6 * 255 + 31) / 63),
            (byte)((b5 * 255 + 15) / 31));
    }

    private static RgbPixel Mix(RgbPixel first, RgbPixel second, int firstWeight, int secondWeight, int divisor)
        => new(
            (byte)((first.R * firstWeight + second.R * secondWeight + divisor / 2) / divisor),
            (byte)((first.G * firstWeight + second.G * secondWeight + divisor / 2) / divisor),
            (byte)((first.B * firstWeight + second.B * secondWeight + divisor / 2) / divisor));

    private sealed record RgbaImage(int Width, int Height, byte[] Data);
    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);
    private readonly record struct RgbPixel(byte R, byte G, byte B);
}
