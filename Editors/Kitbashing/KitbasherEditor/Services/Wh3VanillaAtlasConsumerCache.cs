using System.IO.Compression;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Core.Misc;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace Editors.KitbasherEditor.Services;

/// <summary>
/// Persistent, vanilla-only cache for the asset-local information used by atlas value
/// accounting. Mod-pack assets are intentionally never written here: they are cheap to
/// invalidate and may override a vanilla path at any time during an atlas run.
///
/// Entries are scoped to the CA container snapshot that supplied them. The selected source
/// pack is deliberately not part of the cache identity; source-pack relevance is filtered by
/// the caller for each atlas run.
/// </summary>
internal sealed class Wh3VanillaAtlasConsumerCache
{
    private const int CurrentVersion = 3;
    private const string ParserVersion = "wsmodel-texture-consumers-v3";
    private const string CacheFileName = "wh3-atlas-vanilla-consumers.json.gz";
    private const char ScopeSeparator = '\u001f';

    private readonly string _cachePath;
    private readonly string _game;
    private readonly CacheDocument _document;
    private readonly HashSet<string> _activeContainerScopes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<IPackFileContainer, string?> _containerScopes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPackFileContainer, VanillaContainerCacheDocument>
        _volatileContainers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<VanillaAtlasWsModelBindingReference>>
        _bindingsByMaterialPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<VanillaAtlasMaterialTextureReference>>
        _materialsByTexturePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _indexedBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _indexedMaterialTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    private Wh3VanillaAtlasConsumerCache(
        string cachePath,
        CacheDocument document,
        string game,
        IReadOnlyList<IPackFileContainer> loadedContainers)
    {
        _cachePath = cachePath;
        _game = game;
        _document = document;

        foreach (var container in loadedContainers.Where(x => x.IsCaPackFile))
        {
            var scope = BuildContainerScope(_game, container);
            _containerScopes[container] = scope;
            _activeContainerScopes.Add(scope ?? GetVolatileScope(container));
        }

        BuildReverseIndex();
    }

    public int WsModelCount => GetPersistentDocuments().Sum(document => document.WsModels.Count) +
                                _volatileContainers.Values.Sum(document => document.WsModels.Count);
    public int MaterialCount => GetPersistentDocuments().Sum(document => document.Materials.Count) +
                                _volatileContainers.Values.Sum(document => document.Materials.Count);
    public int RigidCount => GetPersistentDocuments().Sum(document => document.Rigids.Count) +
                             _volatileContainers.Values.Sum(document => document.Rigids.Count);
    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }

    public static Wh3VanillaAtlasConsumerCache? Load(
        ApplicationSettingsService settingsService,
        IReadOnlyList<IPackFileContainer> loadedContainers)
    {
        try
        {
            var caContainers = loadedContainers
                .Where(container => container.IsCaPackFile)
                .ToArray();
            if (caContainers.Length == 0)
                return null;

            var cachePath = Path.Combine(DirectoryHelper.CacheDirectory, CacheFileName);
            var game = settingsService.CurrentSettings.CurrentGame.ToString();
            var document = LoadDocument(cachePath, game) ?? new CacheDocument
            {
                Version = CurrentVersion,
                ParserVersion = ParserVersion,
                Game = game,
            };

            return new Wh3VanillaAtlasConsumerCache(cachePath, document, game, caContainers);
        }
        catch
        {
            // This is a disposable performance cache. Failure to inspect the game files
            // must never fail atlas processing or change its conservative decisions.
            return null;
        }
    }

    public bool TryGetWsModel(
        IPackFileContainer container,
        string path,
        out VanillaWsModelConsumerEntry entry)
    {
        var document = TryGetContainerDocument(container);
        if (document != null &&
            document.WsModels.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetWsModel(
        IPackFileContainer container,
        string path,
        VanillaWsModelConsumerEntry entry)
    {
        var document = GetOrCreateContainerDocument(container);
        var normalizedPath = Normalize(path);
        document.WsModels[normalizedPath] = entry;
        AddWsModelToReverseIndex(GetContainerScope(container), normalizedPath, entry);
        MarkDirty(container);
    }

    public bool TryGetMaterial(
        IPackFileContainer container,
        string path,
        out VanillaMaterialConsumerEntry entry)
    {
        var document = TryGetContainerDocument(container);
        if (document != null &&
            document.Materials.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetMaterial(
        IPackFileContainer container,
        string path,
        VanillaMaterialConsumerEntry entry)
    {
        var document = GetOrCreateContainerDocument(container);
        var normalizedPath = Normalize(path);
        document.Materials[normalizedPath] = entry;
        AddMaterialToReverseIndex(GetContainerScope(container), normalizedPath, entry);
        MarkDirty(container);
    }

    public bool TryGetRigid(
        IPackFileContainer container,
        string path,
        out VanillaRigidConsumerEntry entry)
    {
        var document = TryGetContainerDocument(container);
        if (document != null &&
            document.Rigids.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetRigid(
        IPackFileContainer container,
        string path,
        VanillaRigidConsumerEntry entry)
    {
        var document = GetOrCreateContainerDocument(container);
        document.Rigids[Normalize(path)] = entry;
        MarkDirty(container);
    }

    public string GetContainerScope(IPackFileContainer container)
    {
        if (_containerScopes.TryGetValue(container, out var scope))
            return scope ?? GetVolatileScope(container);

        scope = BuildContainerScope(_game, container);
        _containerScopes[container] = scope;
        if (container.IsCaPackFile)
            _activeContainerScopes.Add(scope ?? GetVolatileScope(container));
        return scope ?? GetVolatileScope(container);
    }

    public IEnumerable<VanillaAtlasTextureConsumerReference> FindTextureConsumers(
        string texturePath,
        IReadOnlySet<string> reachableWsModels)
    {
        texturePath = Normalize(texturePath);
        if (!_materialsByTexturePath.TryGetValue(texturePath, out var materials))
            yield break;

        foreach (var material in materials)
        {
            if (!_activeContainerScopes.Contains(material.MaterialContainerScope))
                continue;

            if (!_bindingsByMaterialPath.TryGetValue(
                    material.MaterialPath,
                    out var bindings))
            {
                continue;
            }

            foreach (var binding in bindings)
            {
                if (!_activeContainerScopes.Contains(binding.WsModelContainerScope))
                    continue;

                if (!reachableWsModels.Contains(binding.WsModelPath))
                    continue;

                yield return new VanillaAtlasTextureConsumerReference(
                    binding.WsModelContainerScope,
                    material.MaterialContainerScope,
                    binding.WsModelPath,
                    binding.GeometryPath,
                    binding.MaterialPath,
                    binding.LodIndex,
                    binding.PartIndex,
                    material.Slot,
                    material.SlotOccurrence);
            }
        }
    }

    public IEnumerable<VanillaAtlasWsModelBindingReference> FindWsModelBindings(
        string materialPath,
        IReadOnlySet<string> reachableWsModels)
    {
        materialPath = Normalize(materialPath);
        if (!_bindingsByMaterialPath.TryGetValue(materialPath, out var bindings))
            yield break;

        foreach (var binding in bindings)
        {
            if (!_activeContainerScopes.Contains(binding.WsModelContainerScope))
                continue;

            if (reachableWsModels.Contains(binding.WsModelPath))
                yield return binding;
        }
    }

    public void Save()
    {
        if (!_dirty)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporaryPath = _cachePath + ".building";
            try
            {
                using (var file = new FileStream(
                           temporaryPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: false))
                {
                    JsonSerializer.Serialize(gzip, _document, JsonOptions);
                }

                File.Move(temporaryPath, _cachePath, overwrite: true);
                _dirty = false;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        catch
        {
            // This is a disposable performance cache. Failure to write must never fail
            // atlas processing or change its conservative decisions.
        }
    }

    private void BuildReverseIndex()
    {
        foreach (var pair in _document.Containers)
        {
            foreach (var wsModel in pair.Value.WsModels)
                AddWsModelToReverseIndex(pair.Key, wsModel.Key, wsModel.Value);
            foreach (var material in pair.Value.Materials)
                AddMaterialToReverseIndex(pair.Key, material.Key, material.Value);
        }

        foreach (var pair in _volatileContainers)
        {
            foreach (var wsModel in pair.Value.WsModels)
                AddWsModelToReverseIndex(GetVolatileScope(pair.Key), wsModel.Key, wsModel.Value);
            foreach (var material in pair.Value.Materials)
                AddMaterialToReverseIndex(GetVolatileScope(pair.Key), material.Key, material.Value);
        }
    }

    private void AddWsModelToReverseIndex(
        string containerScope,
        string wsModelPath,
        VanillaWsModelConsumerEntry entry)
    {
        foreach (var binding in entry.Materials)
        {
            var materialPath = Normalize(binding.MaterialPath);
            if (materialPath.Length == 0)
                continue;

            var indexKey = string.Join(
                ScopeSeparator,
                containerScope,
                Normalize(wsModelPath),
                entry.GeometryPath,
                materialPath,
                binding.LodIndex?.ToString() ?? string.Empty,
                binding.PartIndex?.ToString() ?? string.Empty);
            if (!_indexedBindings.Add(indexKey))
                continue;

            if (!_bindingsByMaterialPath.TryGetValue(materialPath, out var bindings))
            {
                bindings = [];
                _bindingsByMaterialPath[materialPath] = bindings;
            }

            bindings.Add(new VanillaAtlasWsModelBindingReference(
                containerScope,
                Normalize(wsModelPath),
                Normalize(entry.GeometryPath),
                materialPath,
                binding.LodIndex,
                binding.PartIndex));
        }
    }

    private void AddMaterialToReverseIndex(
        string containerScope,
        string materialPath,
        VanillaMaterialConsumerEntry entry)
    {
        materialPath = Normalize(materialPath);
        foreach (var texture in entry.Textures)
        {
            var texturePath = Normalize(texture.TexturePath);
            if (texturePath.Length == 0)
                continue;

            var indexKey = string.Join(
                ScopeSeparator,
                containerScope,
                materialPath,
                texturePath,
                texture.Slot,
                texture.SlotOccurrence);
            if (!_indexedMaterialTextures.Add(indexKey))
                continue;

            if (!_materialsByTexturePath.TryGetValue(texturePath, out var materials))
            {
                materials = [];
                _materialsByTexturePath[texturePath] = materials;
            }

            materials.Add(new VanillaAtlasMaterialTextureReference(
                containerScope,
                materialPath,
                texturePath,
                texture.Slot,
                texture.SlotOccurrence));
        }
    }

    private VanillaContainerCacheDocument? TryGetContainerDocument(
        IPackFileContainer container)
    {
        if (!_containerScopes.TryGetValue(container, out var scope))
            return null;

        if (scope != null)
            return _document.Containers.TryGetValue(scope, out var document)
                ? document
                : null;

        return _volatileContainers.TryGetValue(container, out var volatileDocument)
            ? volatileDocument
            : null;
    }

    private VanillaContainerCacheDocument GetOrCreateContainerDocument(
        IPackFileContainer container)
    {
        if (!_containerScopes.TryGetValue(container, out var scope))
        {
            scope = BuildContainerScope(_game, container);
            _containerScopes[container] = scope;
            if (container.IsCaPackFile)
                _activeContainerScopes.Add(scope ?? GetVolatileScope(container));
        }

        if (scope != null)
        {
            if (!_document.Containers.TryGetValue(scope, out var document))
            {
                document = new VanillaContainerCacheDocument();
                _document.Containers[scope] = document;
            }

            return document;
        }

        if (!_volatileContainers.TryGetValue(container, out var volatileDocument))
        {
            volatileDocument = new VanillaContainerCacheDocument();
            _volatileContainers[container] = volatileDocument;
        }

        return volatileDocument;
    }

    private void MarkDirty(IPackFileContainer container)
    {
        if (_containerScopes.TryGetValue(container, out var scope) && scope != null)
            _dirty = true;
    }

    private IEnumerable<VanillaContainerCacheDocument> GetPersistentDocuments()
        => _document.Containers.Values;

    private string GetVolatileScope(IPackFileContainer container)
        => "volatile:" + RuntimeHelpers.GetHashCode(container).ToString("X8");

    private static CacheDocument? LoadDocument(
        string cachePath,
        string game)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            using var file = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var document = JsonSerializer.Deserialize<CacheDocument>(gzip, JsonOptions);
            if (document == null ||
                document.Version != CurrentVersion ||
                !string.Equals(document.ParserVersion, ParserVersion, StringComparison.Ordinal) ||
                !string.Equals(document.Game, game, StringComparison.Ordinal))
            {
                return null;
            }

            document.Containers = RehydrateContainers(document.Containers);
            Sanitize(document);
            return document;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, VanillaContainerCacheDocument> RehydrateContainers(
        Dictionary<string, VanillaContainerCacheDocument>? source)
    {
        var result = new Dictionary<string, VanillaContainerCacheDocument>(
            StringComparer.OrdinalIgnoreCase);
        if (source == null)
            return result;

        foreach (var pair in source)
        {
            if (pair.Key != null && pair.Value != null)
            {
                pair.Value.WsModels = Rehydrate(pair.Value.WsModels);
                pair.Value.Materials = Rehydrate(pair.Value.Materials);
                pair.Value.Rigids = Rehydrate(pair.Value.Rigids);
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static Dictionary<string, T> Rehydrate<T>(
        Dictionary<string, T>? source)
        where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
            return result;

        foreach (var pair in source)
        {
            if (pair.Key != null && pair.Value != null)
                result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static void Sanitize(CacheDocument document)
    {
        foreach (var container in document.Containers.Values)
        {
            foreach (var entry in container.WsModels.Values)
            {
                entry.GeometryPath ??= string.Empty;
                entry.Materials ??= [];
                entry.Materials.RemoveAll(binding => binding == null);
                foreach (var binding in entry.Materials)
                    binding.MaterialPath ??= string.Empty;
                entry.CompatibilityRepairs = SanitizeRepairs(entry.CompatibilityRepairs);
            }

            foreach (var entry in container.Materials.Values)
            {
                entry.Textures ??= [];
                entry.Textures.RemoveAll(texture => texture == null);
                foreach (var texture in entry.Textures)
                {
                    texture.TexturePath ??= string.Empty;
                    texture.Slot ??= string.Empty;
                }
                entry.CompatibilityRepairs = SanitizeRepairs(entry.CompatibilityRepairs);
            }

            foreach (var entry in container.Rigids.Values)
            {
                entry.Textures ??= [];
                entry.Textures.RemoveAll(texture => texture == null);
                foreach (var texture in entry.Textures)
                {
                    texture.TexturePath ??= string.Empty;
                    texture.Slot ??= string.Empty;
                }
            }
        }
    }

    private static string? BuildContainerScope(
        string game,
        IPackFileContainer container)
    {
        if (!container.IsCaPackFile)
            return null;

        var snapshotPath = container.PackFileSettings.SaveLocationPath;
        if (container.ContainerType != PackFileContainerType.Database ||
            string.IsNullOrWhiteSpace(snapshotPath) ||
            !File.Exists(snapshotPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(snapshotPath);
            var identity = string.Join(
                "|",
                game,
                ParserVersion,
                NormalizeFileSystemPath(snapshotPath),
                info.Length,
                info.LastWriteTimeUtc.Ticks);
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SanitizeRepairs(List<string>? repairs)
        => (repairs ?? [])
            .Where(repair => !string.IsNullOrWhiteSpace(repair))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(repair => repair, StringComparer.Ordinal)
            .ToList();

    private static string Normalize(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\').Replace("\\\\", "\\").ToLowerInvariant();

    private static string NormalizeFileSystemPath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public string ParserVersion { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public Dictionary<string, VanillaContainerCacheDocument> Containers { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class VanillaContainerCacheDocument
    {
        public Dictionary<string, VanillaWsModelConsumerEntry> WsModels { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VanillaMaterialConsumerEntry> Materials { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VanillaRigidConsumerEntry> Rigids { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

internal readonly record struct VanillaAtlasWsModelBindingReference(
    string WsModelContainerScope,
    string WsModelPath,
    string GeometryPath,
    string MaterialPath,
    int? LodIndex,
    int? PartIndex);

internal readonly record struct VanillaAtlasMaterialTextureReference(
    string MaterialContainerScope,
    string MaterialPath,
    string TexturePath,
    string Slot,
    int SlotOccurrence);

internal readonly record struct VanillaAtlasTextureConsumerReference(
    string WsModelContainerScope,
    string MaterialContainerScope,
    string WsModelPath,
    string GeometryPath,
    string MaterialPath,
    int? LodIndex,
    int? PartIndex,
    string Slot,
    int SlotOccurrence);

internal sealed class VanillaWsModelConsumerEntry
{
    public string GeometryPath { get; set; } = string.Empty;
    public List<VanillaWsModelMaterialBinding> Materials { get; set; } = [];
    public List<string> CompatibilityRepairs { get; set; } = [];
}

internal sealed class VanillaWsModelMaterialBinding
{
    public string MaterialPath { get; set; } = string.Empty;
    public int? LodIndex { get; set; }
    public int? PartIndex { get; set; }
}

internal sealed class VanillaMaterialConsumerEntry
{
    public List<VanillaMaterialTextureReference> Textures { get; set; } = [];
    public List<string> CompatibilityRepairs { get; set; } = [];
}

internal sealed class VanillaMaterialTextureReference
{
    public string TexturePath { get; set; } = string.Empty;
    public string Slot { get; set; } = string.Empty;
    public int SlotOccurrence { get; set; }
}

internal sealed class VanillaRigidConsumerEntry
{
    public List<VanillaRigidTextureReference> Textures { get; set; } = [];
}

internal sealed class VanillaRigidTextureReference
{
    public string TexturePath { get; set; } = string.Empty;
    public int LodIndex { get; set; }
    public int PartIndex { get; set; }
    public string Slot { get; set; } = string.Empty;
    public int SlotOccurrence { get; set; }
}
