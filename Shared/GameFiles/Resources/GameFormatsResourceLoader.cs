using System.Reflection;

namespace Shared.GameFormats.Resources;

internal static class GameFormatsResourceLoader
{
    public static string[] LoadStringArray(string resourcePath)
        => LoadString(resourcePath)
            .Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

    public static byte[] LoadBytes(string resourcePath)
    {
        using var resourceStream = Open(resourcePath);
        using var memoryStream = new MemoryStream();
        resourceStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static string LoadString(string resourcePath)
    {
        using var stream = Open(resourcePath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream Open(string resourcePath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".{resourcePath}", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            throw new InvalidOperationException($"Unable to load embedded GameFormats resource '{resourcePath}'.");

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded GameFormats resource '{resourcePath}'.");
    }
}
