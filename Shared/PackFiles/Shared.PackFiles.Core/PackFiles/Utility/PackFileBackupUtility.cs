namespace Shared.Core.PackFiles.Utility;

public static class PackFileBackupUtility
{
    private const string BackupFolderName = "Backup";

    public static void CreateFileBackup(string originalFileName)
    {
        if (!File.Exists(originalFileName))
            return;

        var directoryName = Path.GetDirectoryName(originalFileName);
        var fileName = Path.GetFileNameWithoutExtension(originalFileName);
        var extension = Path.GetExtension(originalFileName);
        var backupDirectory = Path.Combine(directoryName!, BackupFolderName);
        var backupFileName = GetIndexedFileName(Path.Combine(backupDirectory, fileName), extension);

        Directory.CreateDirectory(backupDirectory);
        File.Copy(originalFileName, backupFileName);
    }

    private static string GetIndexedFileName(string stub, string extension)
    {
        var index = 0;
        string fileName;
        do
        {
            index++;
            fileName = $"{stub}{index}{extension}";
        } while (File.Exists(fileName));

        return fileName;
    }
}
