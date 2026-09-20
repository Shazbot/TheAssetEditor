using System.Collections.ObjectModel;
using GameWorld.Core.Services;
using Shared.Core.PackFiles.Models;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

/// <summary>
/// The animation catalog available for an RMV2, WSModel, or composed VMD
/// export.  It retains the original animation references and their pack-file
/// containers so callers can resolve the exact selected file later.
/// </summary>
public sealed class GltfAnimationCatalog
{
    public GltfAnimationCatalog(
        string? skeletonName,
        bool hasSkeletonFile,
        IReadOnlyList<AnimationReference> animations,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<GltfAnimationCatalogSelection>? selections = null)
    {
        SkeletonName = skeletonName;
        HasSkeletonFile = hasSkeletonFile;
        Animations = new ReadOnlyCollection<AnimationReference>(animations.ToList());
        Diagnostics = new ReadOnlyCollection<string>(diagnostics.ToList());
        Selections = new ReadOnlyCollection<GltfAnimationCatalogSelection>((selections ?? []).ToList());
    }

    public string? SkeletonName { get; }
    public bool HasSkeletonFile { get; }
    public IReadOnlyList<AnimationReference> Animations { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<GltfAnimationCatalogSelection> Selections { get; }
}

/// <summary>
/// A catalog item retains the pack and fragment-entry identity behind the
/// displayed animation path. The UI may still display only AnimationFile.
/// </summary>
public sealed record GltfAnimationCatalogSelection(
    string AnimationFile,
    IPackFileContainer Container,
    string? FragmentPath,
    string? MetadataPath);

public interface IGltfAnimationCatalogResolver
{
    GltfAnimationCatalog Resolve(PackFile inputFile);
}

/// <summary>
/// Resolves the animation catalog using the same model, VMD composition, and
/// skeleton lookup services as the exporter. VMD components are visited in the
/// same order as exporter flattening: the node's model, its model reference,
/// then selected slot children.
/// </summary>
public sealed class GltfAnimationCatalogResolver : IGltfAnimationCatalogResolver
{
    private readonly IModelAssetResolver _modelAssetResolver;
    private readonly IVariantMeshCompositionResolver _variantMeshResolver;
    private readonly ISkeletonAnimationLookUpHelper _skeletonAnimationLookUpHelper;
    private readonly IGltfAnimationContextReferenceProvider? _metadataResolver;

    public GltfAnimationCatalogResolver(
        IModelAssetResolver modelAssetResolver,
        IVariantMeshCompositionResolver variantMeshResolver,
        ISkeletonAnimationLookUpHelper skeletonAnimationLookUpHelper,
        IGltfAnimationContextReferenceProvider? metadataResolver = null)
    {
        _modelAssetResolver = modelAssetResolver;
        _variantMeshResolver = variantMeshResolver;
        _skeletonAnimationLookUpHelper = skeletonAnimationLookUpHelper;
        _metadataResolver = metadataResolver;
    }

    public GltfAnimationCatalog Resolve(PackFile inputFile)
    {
        ArgumentNullException.ThrowIfNull(inputFile);

        var source = IsVariantMeshDefinition(inputFile)
            ? ResolveVariantMeshSource(inputFile)
            : ResolveModelSource(inputFile);

        var diagnostics = new List<string>(source.Diagnostics);
        var skeletonName = source.SkeletonName;
        if (string.IsNullOrWhiteSpace(skeletonName))
            return new GltfAnimationCatalog(null, false, [], diagnostics);

        try
        {
            var skeletonFile = _skeletonAnimationLookUpHelper.GetSkeletonFileFromName(skeletonName);
            if (skeletonFile == null)
            {
                diagnostics.Add($"Skeleton '{skeletonName}' was not found in the loaded packs.");
                return new GltfAnimationCatalog(skeletonName, false, [], diagnostics);
            }

            var animations = _skeletonAnimationLookUpHelper.GetAnimationsForSkeleton(skeletonName);
            var selections = BuildSelections(animations, skeletonName);
            return new GltfAnimationCatalog(skeletonName, true, animations, diagnostics, selections);
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Unable to load animation catalog for skeleton '{skeletonName}': {exception.Message}");
            return new GltfAnimationCatalog(skeletonName, false, [], diagnostics);
        }
    }

    private IReadOnlyList<GltfAnimationCatalogSelection> BuildSelections(
        IEnumerable<AnimationReference> animations,
        string skeletonName)
    {
        var output = new List<GltfAnimationCatalogSelection>();
        foreach (var animation in animations)
        {
            var contexts = _metadataResolver?.GetSelectionsForAnimation(
                animation.AnimationFile,
                skeletonName,
                animation.Container) ?? [];

            if (contexts.Count == 0)
            {
                output.Add(new GltfAnimationCatalogSelection(
                    animation.AnimationFile,
                    animation.Container,
                    null,
                    null));
                continue;
            }

            output.AddRange(contexts.Select(context => new GltfAnimationCatalogSelection(
                animation.AnimationFile,
                animation.Container,
                context.FragmentPath,
                context.MetadataPath)));
        }

        return output;
    }

    private RmvToGltfAnimationSource ResolveModelSource(PackFile inputFile)
    {
        try
        {
            var asset = _modelAssetResolver.Resolve(inputFile);
            var diagnostics = new List<string>(asset.Diagnostics);
            var skeletonName = SelectSharedSkeletonName([asset], diagnostics);
            return new RmvToGltfAnimationSource(skeletonName, diagnostics);
        }
        catch (Exception exception)
        {
            return new RmvToGltfAnimationSource(
                null,
                [$"Unable to resolve model '{inputFile.Name}' for animation export: {exception.Message}"]);
        }
    }

    private RmvToGltfAnimationSource ResolveVariantMeshSource(PackFile inputFile)
    {
        ResolvedVariantMeshComposition composition;
        try
        {
            composition = _variantMeshResolver.Resolve(inputFile);
        }
        catch (Exception exception)
        {
            return new RmvToGltfAnimationSource(
                null,
                [$"Unable to resolve VariantMeshDefinition '{inputFile.Name}' for animation export: {exception.Message}"]);
        }

        var diagnostics = new List<string>(composition.Diagnostics);

        if (composition.Root == null || composition.HasRenderableContent == false)
        {
            diagnostics.Add($"VariantMeshDefinition '{inputFile.Name}' has no renderable model components.");
            return new RmvToGltfAnimationSource(null, diagnostics);
        }

        var assets = EnumerateComponents(composition.Root)
            .Select(x => x.Asset)
            .ToList();
        var skeletonName = SelectSharedSkeletonName(assets, diagnostics);
        return new RmvToGltfAnimationSource(skeletonName, diagnostics);
    }

    /// <summary>
    /// Returns composed model components in the export order.  Keeping this
    /// traversal shared avoids the UI selecting animations for a different
    /// VMD root skeleton than the exporter uses.
    /// </summary>
    internal static IEnumerable<RmvToGltfResolvedComponent> EnumerateComponents(ResolvedVariantMeshNode node)
    {
        if (node.ModelAsset != null)
            yield return new RmvToGltfResolvedComponent(node.ModelAsset, string.Empty);

        if (node.ResolvedModelReference != null)
        {
            foreach (var component in EnumerateComponents(node.ResolvedModelReference))
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

    private static IEnumerable<RmvToGltfResolvedComponent> EnumerateComponents(
        ResolvedVariantMeshNode node,
        string attachmentPoint)
    {
        if (node.ModelAsset != null)
            yield return new RmvToGltfResolvedComponent(node.ModelAsset, attachmentPoint);

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

    /// <summary>
    /// Selects the first non-empty component skeleton, matching the composed
    /// exporter.  Different component skeletons are reported to the caller;
    /// they must not silently change the animation source.
    /// </summary>
    internal static string? SelectSharedSkeletonName(
        IEnumerable<ResolvedModelAsset> assets,
        ICollection<string>? diagnostics = null)
    {
        var names = assets
            .Select(x => x.Model.Header.SkeletonName)
            .Where(x => string.IsNullOrWhiteSpace(x) == false)
            .ToList();
        var selected = names.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected))
            return null;

        foreach (var other in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(other, selected, StringComparison.OrdinalIgnoreCase))
                continue;

            diagnostics?.Add(
                $"Composed models use different skeletons ('{selected}' and '{other}'); "
                + $"using '{selected}' for the shared glTF skeleton.");
            break;
        }

        return selected;
    }

    private static bool IsVariantMeshDefinition(PackFile file)
        => file.Name.EndsWith(".variantmeshdefinition", StringComparison.OrdinalIgnoreCase);

    private sealed class RmvToGltfAnimationSource
    {
        public RmvToGltfAnimationSource(string? skeletonName, IReadOnlyList<string> diagnostics)
        {
            SkeletonName = skeletonName;
            Diagnostics = diagnostics;
        }

        public string? SkeletonName { get; }
        public IReadOnlyList<string> Diagnostics { get; }
    }
}

internal sealed record RmvToGltfResolvedComponent(
    ResolvedModelAsset Asset,
    string AttachmentPoint);
