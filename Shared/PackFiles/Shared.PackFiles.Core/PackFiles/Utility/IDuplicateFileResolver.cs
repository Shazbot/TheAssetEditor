namespace Shared.Core.PackFiles.Utility
{
    public interface IDuplicateFileResolver
    {
        bool CheckForDuplicates { get; }
        bool KeepDuplicateFile(string fileName);
    }

    public class CaPackDuplicateFileResolver : IDuplicateFileResolver
    {
        public bool CheckForDuplicates => false;
        public bool KeepDuplicateFile(string fileName) => false;
    }
}
