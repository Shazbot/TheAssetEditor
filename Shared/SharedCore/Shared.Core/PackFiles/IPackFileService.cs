using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace Shared.Core.PackFiles
{
    public interface IPackFileService : IHeadlessPackFileService
    {
        bool EnableFileLookUpEvents { get; set; }
        bool EnforceGameFilesMustBeLoaded { get; set; }

        IPackFileContainer? AddContainer(IPackFileContainer container, bool setToMainPackIfFirst = false);
        List<PackFile> AddFilesToPack(IPackFileContainer container, List<NewPackFileEntry> newFiles);
        void CopyFileFromOtherPackFile(IPackFileContainer source, string path, IPackFileContainer target);
        IPackFileContainer CreateNewPackFileContainer(string name, PackFileVersion packFileVersion, PackFileCAType type, bool setEditablePack = false);
        void DeleteFile(IPackFileContainer pf, PackFile file);
        void DeleteFolder(IPackFileContainer pf, string folder);
        bool IsPackFileLoaded(string packFilePath);
        IPackFileContainer? GetEditablePack();
        void MoveFile(IPackFileContainer pf, PackFile file, string newFolderPath);
        void RenameDirectory(IPackFileContainer pf, string currentNodeName, string newName);
        void RenameFile(IPackFileContainer pf, PackFile file, string newName);
        void SaveFile(PackFile file, byte[] data);
        void SavePackContainer(IPackFileContainer pf, string path, bool createBackup, GameInformation gameInformation);
        void SetEditablePack(IPackFileContainer? pf);
        void UnloadPackContainer(IPackFileContainer pf);
    }
}
