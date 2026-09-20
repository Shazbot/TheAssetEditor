using BCnEncoder.Encoder;
using BCnEncoder.Shared;
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
                var ddsBytes = Bc7DdsEncoder.EncodePngFile(painted.Texture.PngPath);
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
/// Encodes painted BaseColor output as BC7 DDS with a full mip chain.
/// Fast-quality parallel compression keeps export responsive while retaining
/// substantially more color detail than the earlier BC3 proof of concept.
/// </summary>
internal static class Bc7DdsEncoder
{
    public static byte[] EncodePngFile(string pngPath)
    {
        var image = ReadPng(pngPath);
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.Fast;
        encoder.OutputOptions.Format = CompressionFormat.Bc7;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.Options.IsParallel = true;

        using var stream = new MemoryStream();
        encoder.EncodeToStream(
            image.Data,
            image.Width,
            image.Height,
            BCnEncoder.Encoder.PixelFormat.Rgba32,
            stream);
        return stream.ToArray();
    }

    private static RgbaImage ReadPng(string path)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
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

    private sealed record RgbaImage(int Width, int Height, byte[] Data);
}
