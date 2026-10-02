using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Core.Misc;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace Editors.KitbasherEditor.Services;

/// <summary>
/// Persistent, vanilla-only cache for the asset-local information used by atlas value
/// accounting.  Mod-pack assets are intentionally never written here: they are cheap to
/// invalidate and may override a vanilla path at any time during an atlas run.
/// </summary>
internal sealed class Wh3VanillaAtlasConsumerCache
{
    private const int CurrentVersion = 2;
    private const string ParserVersion = "wsmodel-texture-consumers-v2";
    private const string CacheFileName = "wh3-atlas-vanilla-consumers.json.gz";

    private readonly string _cachePath;
    private readonly CacheDocument _document;
    private bool _dirty;

    private Wh3VanillaAtlasConsumerCache(
        string cachePath,
        CacheDocument document)
    {
        _cachePath = cachePath;
        _document = document;
    }

    public int WsModelCount => _document.WsModels.Count;
    public int MaterialCount => _document.Materials.Count;
    public int RigidCount => _document.Rigids.Count;
    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }

    public static Wh3VanillaAtlasConsumerCache? Load(
        ApplicationSettingsService settingsService,
        IReadOnlyList<IPackFileContainer> loadedContainers)
    {
        try
        {
            var identity = BuildIdentity(settingsService, loadedContainers);
            if (identity == null)
                return null;

            var cachePath = Path.Combine(DirectoryHelper.CacheDirectory, CacheFileName);
            var document = LoadDocument(cachePath, identity) ?? new CacheDocument
            {
                Version = CurrentVersion,
                Identity = identity,
            };

            return new Wh3VanillaAtlasConsumerCache(cachePath, document);
        }
        catch
        {
            // This is a disposable performance cache. Failure to inspect the game files
            // must never fail atlas processing or change its conservative decisions.
            return null;
        }
    }

    public bool TryGetWsModel(
        string path,
        out VanillaWsModelConsumerEntry entry)
    {
        if (_document.WsModels.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetWsModel(
        string path,
        VanillaWsModelConsumerEntry entry)
    {
        _document.WsModels[Normalize(path)] = entry;
        _dirty = true;
    }

    public bool TryGetMaterial(
        string path,
        out VanillaMaterialConsumerEntry entry)
    {
        if (_document.Materials.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetMaterial(
        string path,
        VanillaMaterialConsumerEntry entry)
    {
        _document.Materials[Normalize(path)] = entry;
        _dirty = true;
    }

    public bool TryGetRigid(
        string path,
        out VanillaRigidConsumerEntry entry)
    {
        if (_document.Rigids.TryGetValue(Normalize(path), out entry!))
        {
            CacheHits++;
            return true;
        }

        CacheMisses++;
        entry = null!;
        return false;
    }

    public void SetRigid(
        string path,
        VanillaRigidConsumerEntry entry)
    {
        _document.Rigids[Normalize(path)] = entry;
        _dirty = true;
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
            // This is a disposable performance cache.  Failure to write must never fail
            // atlas processing or change its conservative decisions.
        }
    }

    private static CacheDocument? LoadDocument(
        string cachePath,
        string identity)
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
                !string.Equals(document.Identity, identity, StringComparison.Ordinal))
            {
                return null;
            }

            document.WsModels = Rehydrate(document.WsModels);
            document.Materials = Rehydrate(document.Materials);
            document.Rigids = Rehydrate(document.Rigids);
            Sanitize(document);
            return document;
        }
        catch
        {
            return null;
        }
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
        foreach (var entry in document.WsModels.Values)
        {
            entry.GeometryPath ??= string.Empty;
            entry.Materials ??= [];
            entry.Materials.RemoveAll(binding => binding == null);
            foreach (var binding in entry.Materials)
                binding.MaterialPath ??= string.Empty;
            entry.CompatibilityRepairs = SanitizeRepairs(entry.CompatibilityRepairs);
        }

        foreach (var entry in document.Materials.Values)
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

        foreach (var entry in document.Rigids.Values)
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

    private static string? BuildIdentity(
        ApplicationSettingsService settingsService,
        IReadOnlyList<IPackFileContainer> loadedContainers)
    {
        var caContainers = loadedContainers
            .Where(container => container.IsCaPackFile)
            .ToArray();
        if (caContainers.Length == 0)
            return null;

        var identityBuilder = new StringBuilder()
            .Append("game=")
            .Append(settingsService.CurrentSettings.CurrentGame)
            .Append(";parser=")
            .Append(ParserVersion)
            .Append(';');

        // Tie this cache to the exact CA container snapshots currently being traversed,
        // rather than re-fingerprinting the live game directory. The database-backed CA
        // container path is derived from the pack fingerprint, so it remains stable for
        // the loaded snapshot even if Steam updates the game files while Asset Editor is
        // still running. If a CA container has no stable snapshot path, disable this
        // disposable cache rather than risk accepting stale consumer data.
        for (var index = 0; index < caContainers.Length; index++)
        {
            var container = caContainers[index];
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
                identityBuilder
                    .Append("ca[")
                    .Append(index)
                    .Append("]=")
                    .Append(container.Name)
                    .Append('|')
                    .Append(NormalizeFileSystemPath(snapshotPath))
                    .Append('|')
                    .Append(info.Length)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append(';');
            }
            catch
            {
                return null;
            }
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityBuilder.ToString())));
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
        public string Identity { get; set; } = string.Empty;
        public Dictionary<string, VanillaWsModelConsumerEntry> WsModels { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VanillaMaterialConsumerEntry> Materials { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VanillaRigidConsumerEntry> Rigids { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

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
