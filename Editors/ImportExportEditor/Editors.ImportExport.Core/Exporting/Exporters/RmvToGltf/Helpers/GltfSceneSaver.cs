using System.Linq;
using SharpGLTF.Schema2;

namespace Editors.ImportExport.Exporting.Exporters.RmvToGltf.Helpers;

public interface IGltfSceneSaver
{
    void Save(ModelRoot modelRoot, string fullSystemPath);

    /// <summary>
    /// Saves a scene and, for binary glTF output, removes the exact generated
    /// texture intermediates supplied by the exporter after the save succeeds.
    /// The default implementation preserves compatibility with existing saver
    /// implementations.
    /// </summary>
    void Save(
        ModelRoot modelRoot,
        string fullSystemPath,
        IReadOnlyCollection<string> generatedTexturePaths)
        => Save(modelRoot, fullSystemPath);
}

/// <summary>
/// Scene saver for console/service callers. It lets the caller observe save
/// failures directly.
/// </summary>
public sealed class HeadlessGltfSceneSaver : IGltfSceneSaver
{
    public void Save(ModelRoot modelRoot, string fullSystemPath)
        => Save(modelRoot, fullSystemPath, Array.Empty<string>());

    public void Save(
        ModelRoot modelRoot,
        string fullSystemPath,
        IReadOnlyCollection<string> generatedTexturePaths)
    {
        var outputDirectory = Path.GetDirectoryName(fullSystemPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) == false)
            Directory.CreateDirectory(outputDirectory);

        // SharpGLTF chooses the container from the requested extension.
        // Exceptions deliberately propagate to HeadlessGltfExportService.
        modelRoot.Save(fullSystemPath);

        if (string.Equals(Path.GetExtension(fullSystemPath), ".glb", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var texturePath in generatedTexturePaths
                .Where(x => string.Equals(Path.GetExtension(x), ".png", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Delete only paths explicitly returned by the texture
                // handler. Auxiliary mask files are intentionally not
                // returned and therefore remain discoverable by the host.
                if (File.Exists(texturePath))
                    File.Delete(texturePath);
            }
        }
    }
}
