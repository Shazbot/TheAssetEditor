using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Core.Misc;
using Shared.Core.Settings;

namespace Editors.KitbasherEditor.Services;

/// <summary>
/// Persistent, vanilla-only cache for the asset-local information used by atlas value
/// accounting.  Mod-pack assets are intentionally never written here: they are cheap to
/// invalidate and may override a vanilla path at any time during an atlas run.
/// </summary>
internal sealed class Wh3VanillaAtlasConsumerCache
{
    private const int CurrentVersion = 1;
    private const string ParserVersion = "wsmodel-texture-consumers-v1";
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
        ApplicationSettingsService settingsService)
    {
        try
        {
            var identity = BuildIdentity(settingsService);
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

    private static string? BuildIdentity(ApplicationSettingsService settingsService)
    {
        var dataFolder = settingsService.GetGamePathForCurrentGame();
        if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
            return null;

        var packPaths = GetVanillaPackPaths(dataFolder);
        if (packPaths.Count == 0)
            return null;

        var identityBuilder = new StringBuilder()
            .Append("game=")
            .Append(settingsService.CurrentSettings.CurrentGame)
            .Append(";parser=")
            .Append(ParserVersion)
            .Append(";data=")
            .Append(NormalizeFileSystemPath(dataFolder))
            .Append(';');

        foreach (var packPath in packPaths)
        {
            identityBuilder.Append(NormalizeFileSystemPath(packPath)).Append('|');
            try
            {
                var info = new FileInfo(packPath);
                identityBuilder
                    .Append(info.Exists ? info.Length : -1)
                    .Append('|')
                    .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1)
                    .Append(';');
            }
            catch
            {
                identityBuilder.Append("unreadable;");
            }
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityBuilder.ToString())));
    }

    private static List<string> GetVanillaPackPaths(string dataFolder)
    {
        var manifestPath = Path.Combine(dataFolder, "manifest.txt");
        if (File.Exists(manifestPath))
        {
            var fromManifest = new List<string>();
            foreach (var line in File.ReadLines(manifestPath))
            {
                var packName = line.Split('\t', 2)[0].Trim();
                if (!packName.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                    continue;

                fromManifest.Add(Path.GetFullPath(Path.Combine(dataFolder, packName)));
            }

            if (fromManifest.Count != 0)
                return fromManifest;
        }

        return Directory
            .EnumerateFiles(dataFolder, "*.pack", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToList();
    }

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
