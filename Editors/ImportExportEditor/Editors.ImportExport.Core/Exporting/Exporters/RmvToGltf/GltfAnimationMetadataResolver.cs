using System.Diagnostics;
using System.Numerics;
using GameWorld.Core.Animation;
using Shared.ByteParsing;
using Shared.Core.Misc;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.GameFormats.Animation;
using Shared.GameFormats.AnimationPack;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf;

internal sealed record GltfTransformMetadataRule(
    int TargetNode,
    int SourceNode,
    Vector3 Position,
    Vector4 Orientation,
    float StartTime,
    float EndTime);

internal sealed record GltfDockEquipmentMetadataRule(
    int PropBoneId,
    string AnimationSlotName,
    IReadOnlyList<string> SkeletonNameAlternatives,
    float StartTime,
    float EndTime,
    AnimationClip DockAnimation);

internal sealed record GltfAnimationMetadataContext(
    string FragmentPath,
    IReadOnlyList<GltfTransformMetadataRule> TransformRules,
    IReadOnlyList<GltfDockEquipmentMetadataRule> DockRules,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasRules => TransformRules.Count != 0 || DockRules.Count != 0;
}

internal sealed class GltfAnimationMetadataContextResolver
{
    private static readonly ILogger Logger = Logging.Create<GltfAnimationMetadataContextResolver>();

    private readonly IHeadlessPackFileService _packFileService;
    private readonly GltfAnimationMetadataLookupCache? _cache;
    private readonly object _indexLock = new();
    private Dictionary<string, List<FragmentEntryContext>>? _contextsByAnimation;

    public GltfAnimationMetadataContextResolver(
        IHeadlessPackFileService packFileService,
        GltfAnimationMetadataLookupCacheOptions? cacheOptions = null)
    {
        _packFileService = packFileService;
        _cache = cacheOptions == null
            ? null
            : new GltfAnimationMetadataLookupCache(cacheOptions);
    }

    public GltfAnimationMetadataContext? Resolve(PackFile animationFile, GameSkeleton skeleton)
    {
        var animationPath = NormalizePath(_packFileService.GetFullPath(animationFile));
        var contexts = GetIndex();
        if (!contexts.TryGetValue(animationPath, out var candidates))
            return null;

        // SuperView only presents fragments matching the active skeleton, so do not
        // borrow metadata from a same-path entry belonging to another skeleton.
        var matching = candidates
            .Where(x => string.Equals(x.SkeletonName, skeleton.SkeletonName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0)
            return null;

        var diagnostics = new List<string>();
        var context = matching.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.MetaPath) == false) ?? matching[0];
        if (matching.Count > 1)
        {
            diagnostics.Add(
                $"Animation '{animationPath}' appears in {matching.Count} fragments for skeleton '{skeleton.SkeletonName}'; "
                + $"using '{context.FragmentPath}'.");
        }

        var selectedMetadata = ReadMetadata(context.MetaPath, diagnostics);
        var persistentMetadata = selectedMetadata.DisablePersistent
            ? DecodedAnimationMetadata.Empty
            : ReadMetadata(context.PersistentMetaPath, diagnostics);

        var transformRules = persistentMetadata.TransformRules
            .Concat(selectedMetadata.TransformRules)
            .ToList();
        var dockSpecs = persistentMetadata.DockRules
            .Concat(selectedMetadata.DockRules)
            .ToList();

        var dockRules = new List<GltfDockEquipmentMetadataRule>();
        foreach (var spec in dockSpecs)
        {
            var dockAnimationPath = FindDockAnimation(context.SlotAnimations, spec.AnimationSlotName);
            if (dockAnimationPath == null)
            {
                diagnostics.Add(
                    $"Unable to apply {spec.TagName}: fragment '{context.FragmentPath}' has no "
                    + $"'{spec.AnimationSlotName}' or '{spec.AnimationSlotName}_2' animation.");
                continue;
            }

            var dockAnimationFile = _packFileService.FindFile(dockAnimationPath);
            if (dockAnimationFile == null)
            {
                diagnostics.Add(
                    $"Unable to apply {spec.TagName}: docking animation '{dockAnimationPath}' was not found.");
                continue;
            }

            try
            {
                var dockAnimation = new AnimationClip(AnimationFile.Create(dockAnimationFile), skeleton);
                dockRules.Add(new GltfDockEquipmentMetadataRule(
                    spec.PropBoneId,
                    spec.AnimationSlotName,
                    spec.SkeletonNameAlternatives,
                    spec.StartTime,
                    spec.EndTime,
                    dockAnimation));
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"Unable to apply {spec.TagName}: docking animation '{dockAnimationPath}' could not be loaded. "
                    + exception.Message);
            }
        }

        foreach (var diagnostic in diagnostics)
            Logger.Here().Warning(diagnostic);

        return new GltfAnimationMetadataContext(
            context.FragmentPath,
            transformRules,
            dockRules,
            diagnostics);
    }

    private Dictionary<string, List<FragmentEntryContext>> GetIndex()
    {
        lock (_indexLock)
        {
            if (_contextsByAnimation != null)
                return _contextsByAnimation;

            var stopwatch = Stopwatch.StartNew();
            var index = new Dictionary<string, List<FragmentEntryContext>>(StringComparer.OrdinalIgnoreCase);
            var animPacks = PackFileServiceUtility.GetAllAnimPacks(_packFileService);
            var fragmentCount = 0;
            var entryCount = 0;
            var cachedVanillaPackCount = 0;
            var parsedVanillaAnimPackCount = 0;
            var parsedUncachedAnimPackCount = 0;

            var animPacksByContainer = new Dictionary<IPackFileContainer, List<PackFile>>(
                ReferenceEqualityComparer.Instance);
            var unownedAnimPacks = new List<PackFile>();

            foreach (var animPack in animPacks)
            {
                var container = _packFileService.GetPackFileContainer(animPack);
                if (container == null)
                {
                    unownedAnimPacks.Add(animPack);
                    continue;
                }

                if (!animPacksByContainer.TryGetValue(container, out var containerAnimPacks))
                {
                    containerAnimPacks = [];
                    animPacksByContainer.Add(container, containerAnimPacks);
                }

                containerAnimPacks.Add(animPack);
            }

            foreach (var pair in animPacksByContainer)
            {
                var container = pair.Key;
                var cachedFragments = _cache?.TryLoad(container);
                if (cachedFragments != null)
                {
                    AddCachedFragments(
                        cachedFragments,
                        index,
                        ref fragmentCount,
                        ref entryCount);
                    cachedVanillaPackCount++;
                    continue;
                }

                var cacheFragments = _cache?.IsCacheable(container) == true
                    ? new List<GltfAnimationMetadataLookupCache.CachedFragment>()
                    : null;

                foreach (var animPack in pair.Value)
                {
                    ParseAnimPack(
                        animPack,
                        index,
                        cacheFragments,
                        ref fragmentCount,
                        ref entryCount);

                    if (cacheFragments != null)
                        parsedVanillaAnimPackCount++;
                    else
                        parsedUncachedAnimPackCount++;
                }

                if (cacheFragments != null)
                    _cache!.Save(container, cacheFragments);
            }

            foreach (var animPack in unownedAnimPacks)
            {
                ParseAnimPack(
                    animPack,
                    index,
                    cacheFragments: null,
                    ref fragmentCount,
                    ref entryCount);
                parsedUncachedAnimPackCount++;
            }

            _cache?.Flush();

            stopwatch.Stop();
            Logger.Here().Information(
                "GLTF animation metadata index built in {ElapsedMs:F1}ms: animPacks={AnimPackCount}, fragments={FragmentCount}, entries={EntryCount}, uniqueAnimations={AnimationCount}, cachedVanillaPacks={CachedVanillaPackCount}, parsedVanillaAnimPacks={ParsedVanillaAnimPackCount}, parsedUncachedAnimPacks={ParsedUncachedAnimPackCount}",
                stopwatch.Elapsed.TotalMilliseconds,
                animPacks.Count,
                fragmentCount,
                entryCount,
                index.Count,
                cachedVanillaPackCount,
                parsedVanillaAnimPackCount,
                parsedUncachedAnimPackCount);

            _contextsByAnimation = index;
            return index;
        }
    }

    private void ParseAnimPack(
        PackFile animPack,
        Dictionary<string, List<FragmentEntryContext>> index,
        List<GltfAnimationMetadataLookupCache.CachedFragment>? cacheFragments,
        ref int fragmentCount,
        ref int entryCount)
    {
        try
        {
            var database = AnimationPackSerializer.Load(animPack, _packFileService);
            foreach (var fragment in database.GetGenericAnimationSets())
            {
                fragmentCount++;
                var entries = fragment.Entries;
                var persistentMetaPath =
                    entries.FirstOrDefault(x => x.SlotName == "PERSISTENT_METADATA_ALIVE")?.MetaFile;
                if (string.IsNullOrWhiteSpace(persistentMetaPath))
                {
                    persistentMetaPath =
                        entries.FirstOrDefault(x => x.SlotName == "PERSISTENT_METADATA_FLYING")?.MetaFile;
                }

                var slotAnimations = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!IsRelevantDockingSlot(entry.SlotName)
                        || string.IsNullOrWhiteSpace(entry.AnimationFile)
                        || slotAnimations.ContainsKey(entry.SlotName))
                        continue;

                    slotAnimations.Add(entry.SlotName, NormalizePath(entry.AnimationFile));
                }

                GltfAnimationMetadataLookupCache.CachedFragment? cachedFragment = null;
                if (cacheFragments != null)
                {
                    cachedFragment = new GltfAnimationMetadataLookupCache.CachedFragment
                    {
                        FragmentPath = fragment.FullPath,
                        SkeletonName = fragment.SkeletonName,
                        PersistentMetaPath = persistentMetaPath,
                        SlotAnimations = slotAnimations
                            .Select(pair => new GltfAnimationMetadataLookupCache.CachedSlotAnimation
                            {
                                SlotName = pair.Key,
                                AnimationPath = pair.Value
                            })
                            .ToList()
                    };
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.AnimationFile))
                        continue;

                    entryCount++;
                    var animationPath = NormalizePath(entry.AnimationFile);
                    AddContext(
                        index,
                        animationPath,
                        new FragmentEntryContext(
                            fragment.FullPath,
                            fragment.SkeletonName,
                            entry.MetaFile,
                            persistentMetaPath,
                            slotAnimations));

                    cachedFragment?.Entries.Add(
                        new GltfAnimationMetadataLookupCache.CachedEntry
                        {
                            AnimationPath = animationPath,
                            MetaPath = entry.MetaFile
                        });
                }

                if (cachedFragment != null)
                    cacheFragments!.Add(cachedFragment);
            }
        }
        catch (Exception exception)
        {
            Logger.Here().Warning(
                $"Unable to index animation metadata from '{_packFileService.GetFullPath(animPack)}': {exception.Message}");
        }
    }

    private static void AddCachedFragments(
        IReadOnlyList<GltfAnimationMetadataLookupCache.CachedFragment> fragments,
        Dictionary<string, List<FragmentEntryContext>> index,
        ref int fragmentCount,
        ref int entryCount)
    {
        foreach (var fragment in fragments)
        {
            fragmentCount++;

            var slotAnimations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in fragment.SlotAnimations ?? [])
            {
                if (string.IsNullOrWhiteSpace(slot.SlotName)
                    || string.IsNullOrWhiteSpace(slot.AnimationPath)
                    || slotAnimations.ContainsKey(slot.SlotName))
                    continue;

                slotAnimations.Add(slot.SlotName, slot.AnimationPath);
            }

            foreach (var entry in fragment.Entries ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.AnimationPath))
                    continue;

                entryCount++;
                AddContext(
                    index,
                    entry.AnimationPath,
                    new FragmentEntryContext(
                        fragment.FragmentPath,
                        fragment.SkeletonName,
                        entry.MetaPath,
                        fragment.PersistentMetaPath,
                        slotAnimations));
            }
        }
    }

    private static void AddContext(
        Dictionary<string, List<FragmentEntryContext>> index,
        string animationPath,
        FragmentEntryContext context)
    {
        if (!index.TryGetValue(animationPath, out var contexts))
        {
            contexts = [];
            index.Add(animationPath, contexts);
        }

        contexts.Add(context);
    }

    private static bool IsRelevantDockingSlot(string? slotName)
    {
        return slotName is
            "DOCK_EQUIPMENT_RIGHT_HAND" or
            "DOCK_EQUIPMENT_RIGHT_HAND_2" or
            "DOCK_EQUIPMENT_LEFT_HAND" or
            "DOCK_EQUIPMENT_LEFT_HAND_2" or
            "DOCK_EQUIPMENT_RIGHT_WAIST" or
            "DOCK_EQUIPMENT_RIGHT_WAIST_2" or
            "DOCK_EQUIPMENT_LEFT_WAIST" or
            "DOCK_EQUIPMENT_LEFT_WAIST_2" or
            "DOCK_EQUIPMENT_BACK" or
            "DOCK_EQUIPMENT_BACK_2";
    }

    private DecodedAnimationMetadata ReadMetadata(string? path, List<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DecodedAnimationMetadata.Empty;

        var normalizedPath = NormalizePath(path);
        var file = _packFileService.FindFile(normalizedPath);
        if (file == null)
        {
            diagnostics.Add($"Animation metadata '{normalizedPath}' was not found.");
            return DecodedAnimationMetadata.Empty;
        }

        try
        {
            return GltfAnimationMetadataDecoder.Decode(file.DataSource.ReadData());
        }
        catch (Exception exception)
        {
            diagnostics.Add($"Unable to decode animation metadata '{normalizedPath}': {exception.Message}");
            return DecodedAnimationMetadata.Empty;
        }
    }

    private static string? FindDockAnimation(
        IReadOnlyDictionary<string, string> slotAnimations,
        string animationSlotName)
    {
        if (slotAnimations.TryGetValue(animationSlotName, out var path))
            return path;
        if (slotAnimations.TryGetValue(animationSlotName + "_2", out path))
            return path;
        return null;
    }

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').Trim().TrimStart('\\');

    private sealed record FragmentEntryContext(
        string FragmentPath,
        string SkeletonName,
        string? MetaPath,
        string? PersistentMetaPath,
        IReadOnlyDictionary<string, string> SlotAnimations);
}

internal sealed record DecodedDockEquipmentMetadata(
    string TagName,
    int PropBoneId,
    string AnimationSlotName,
    IReadOnlyList<string> SkeletonNameAlternatives,
    float StartTime,
    float EndTime);

internal sealed record DecodedAnimationMetadata(
    bool DisablePersistent,
    IReadOnlyList<GltfTransformMetadataRule> TransformRules,
    IReadOnlyList<DecodedDockEquipmentMetadata> DockRules)
{
    public static DecodedAnimationMetadata Empty { get; } = new(false, [], []);
}

/// <summary>
/// Trim-safe decoder for the animation metadata that changes bone transforms in SuperView.
/// The editor's MetaDataFileParser discovers layouts with reflection and is deliberately not
/// used by the single-file AssetHost.
/// </summary>
internal static class GltfAnimationMetadataDecoder
{
    public static DecodedAnimationMetadata Decode(byte[] fileContent)
    {
        if (fileContent.Length < 8)
            return DecodedAnimationMetadata.Empty;

        var fileVersion = BitConverter.ToInt32(fileContent, 0);
        if (fileVersion != 2)
            throw new InvalidDataException($"Unsupported animation metadata file version {fileVersion}.");

        var transformRules = new List<GltfTransformMetadataRule>();
        var dockRules = new List<DecodedDockEquipmentMetadata>();
        var disablePersistent = false;

        foreach (var attribute in ReadAttributes(fileContent))
        {
            if (attribute.Name == "DISABLE_PERSISTENT" && attribute.Version == 10)
            {
                disablePersistent = true;
                continue;
            }

            if (attribute.Name == "TRANSFORM" && attribute.Version == 10)
            {
                transformRules.Add(ReadTransform(attribute.Data));
                continue;
            }

            if (attribute.Version >= 10
                && TryGetDockDefinition(
                    attribute.Name,
                    out var animationSlotName,
                    out var skeletonNameAlternatives))
            {
                dockRules.Add(ReadDockEquipment(
                    attribute.Name,
                    animationSlotName,
                    skeletonNameAlternatives,
                    attribute.Data));
            }
        }

        return new DecodedAnimationMetadata(disablePersistent, transformRules, dockRules);
    }

    private static IReadOnlyList<RawMetadataAttribute> ReadAttributes(byte[] fileContent)
    {
        var expectedAttributeCount = BitConverter.ToUInt32(fileContent, 4);
        var capacity = expectedAttributeCount > int.MaxValue ? int.MaxValue : (int)expectedAttributeCount;
        var output = new List<RawMetadataAttribute>(capacity);
        var currentIndex = 8;

        while (currentIndex < fileContent.Length && output.Count < expectedAttributeCount)
        {
            if (!ByteParsers.String.TryDecode(
                    fileContent,
                    currentIndex,
                    out var tagName,
                    out var tagBytesRead,
                    out var error))
            {
                throw new InvalidDataException(
                    $"Unable to decode animation metadata tag at byte {currentIndex}: {error}");
            }

            var dataStart = currentIndex + tagBytesRead;
            var nextAttribute = dataStart;
            while (nextAttribute < fileContent.Length)
            {
                if (StringSanitizer.IsAllCapsCaString(nextAttribute, fileContent))
                    break;
                nextAttribute++;
            }

            var dataLength = nextAttribute - dataStart;
            if (dataLength < sizeof(int))
                throw new InvalidDataException($"Animation metadata tag '{tagName}' has no version payload.");

            var data = new byte[dataLength];
            Array.Copy(fileContent, dataStart, data, 0, dataLength);
            output.Add(new RawMetadataAttribute(
                tagName,
                BitConverter.ToInt32(data, 0),
                data));
            currentIndex = nextAttribute;
        }

        if (output.Count != expectedAttributeCount)
        {
            throw new InvalidDataException(
                $"Animation metadata declared {expectedAttributeCount} tags but {output.Count} were found.");
        }

        return output;
    }

    private static GltfTransformMetadataRule ReadTransform(byte[] data)
    {
        var index = sizeof(int); // version
        var startTime = ReadSingle(data, ref index);
        var endTime = ReadSingle(data, ref index);
        _ = ReadString(data, ref index); // filter
        _ = ReadInt32(data, ref index); // id
        var targetNode = ReadInt32(data, ref index);
        var sourceNode = ReadInt32(data, ref index);
        var position = new Vector3(
            ReadSingle(data, ref index),
            ReadSingle(data, ref index),
            ReadSingle(data, ref index));
        var orientation = new Vector4(
            ReadSingle(data, ref index),
            ReadSingle(data, ref index),
            ReadSingle(data, ref index),
            ReadSingle(data, ref index));
        _ = ReadSingle(data, ref index); // blend in
        _ = ReadSingle(data, ref index); // blend out

        return new GltfTransformMetadataRule(
            targetNode,
            sourceNode,
            position,
            orientation,
            startTime,
            endTime);
    }

    private static DecodedDockEquipmentMetadata ReadDockEquipment(
        string tagName,
        string animationSlotName,
        IReadOnlyList<string> skeletonNameAlternatives,
        byte[] data)
    {
        var index = sizeof(int); // version
        var startTime = ReadSingle(data, ref index);
        var endTime = ReadSingle(data, ref index);
        _ = ReadString(data, ref index); // filter
        _ = ReadInt32(data, ref index); // id
        var propBoneId = ReadInt32(data, ref index);
        _ = ReadSingle(data, ref index); // blend in
        _ = ReadSingle(data, ref index); // blend out

        // v11+ game variants append fields. SuperView's docking behavior only
        // consumes the common v10 prefix decoded above.
        return new DecodedDockEquipmentMetadata(
            tagName,
            propBoneId,
            animationSlotName,
            skeletonNameAlternatives,
            startTime,
            endTime);
    }

    private static bool TryGetDockDefinition(
        string tagName,
        out string animationSlotName,
        out IReadOnlyList<string> skeletonNameAlternatives)
    {
        switch (tagName)
        {
            case "DOCK_EQPT_RHAND":
            case "DOCK_EQPT_RHAND_2":
                animationSlotName = "DOCK_EQUIPMENT_RIGHT_HAND";
                skeletonNameAlternatives = ["hand_right"];
                return true;

            case "DOCK_EQPT_LHAND":
            case "DOCK_EQPT_LHAND_2":
                animationSlotName = "DOCK_EQUIPMENT_LEFT_HAND";
                skeletonNameAlternatives = ["hand_left"];
                return true;

            case "DOCK_EQPT_RWAIST":
                animationSlotName = "DOCK_EQUIPMENT_RIGHT_WAIST";
                skeletonNameAlternatives = ["root"];
                return true;

            case "DOCK_EQPT_LWAIST":
                animationSlotName = "DOCK_EQUIPMENT_LEFT_WAIST";
                skeletonNameAlternatives = ["root"];
                return true;

            case "DOCK_EQPT_BACK":
                animationSlotName = "DOCK_EQUIPMENT_BACK";
                skeletonNameAlternatives = ["spine_2"];
                return true;

            default:
                animationSlotName = string.Empty;
                skeletonNameAlternatives = [];
                return false;
        }
    }

    private static int ReadInt32(byte[] data, ref int index)
    {
        EnsureBytes(data, index, sizeof(int));
        var value = BitConverter.ToInt32(data, index);
        index += sizeof(int);
        return value;
    }

    private static float ReadSingle(byte[] data, ref int index)
    {
        EnsureBytes(data, index, sizeof(float));
        var value = BitConverter.ToSingle(data, index);
        index += sizeof(float);
        return value;
    }

    private static string ReadString(byte[] data, ref int index)
    {
        if (!ByteParsers.String.TryDecode(data, index, out var value, out var bytesRead, out var error))
            throw new InvalidDataException($"Unable to decode animation metadata string at byte {index}: {error}");
        index += bytesRead;
        return value;
    }

    private static void EnsureBytes(byte[] data, int index, int count)
    {
        if (index < 0 || count < 0 || index > data.Length - count)
            throw new InvalidDataException("Animation metadata payload ended unexpectedly.");
    }

    private sealed record RawMetadataAttribute(string Name, int Version, byte[] Data);
}
