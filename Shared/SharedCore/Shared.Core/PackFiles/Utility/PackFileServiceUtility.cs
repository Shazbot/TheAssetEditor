using Shared.Core.PackFiles.Models;

namespace Shared.Core.PackFiles.Utility
{
    public static class PackFileServiceUtility
    {
        public static List<PackFile> GetAllAnimPacks(IPackFileService pfs)
        {
            var animPacks = FindAllWithExtention(pfs, @".animpack");
            var itemsToRemove = animPacks.Where(x => pfs.GetFullPath(x).Contains("animation_culture_packs", StringComparison.InvariantCultureIgnoreCase)).ToList();
            foreach (var item in itemsToRemove)
                animPacks.Remove(item);

            return animPacks;
        }

        public static List<PackFile> FindAllWithExtention(IPackFileExtensionLookup pfs, string extention, IPackFileContainer? packFileContainer = null)
        {
            return FindAllWithExtentionIncludePaths(pfs, extention, packFileContainer).Select(x => x.Item2).ToList();
        }

        public static List<(string FileName, PackFile Pack)> FindAllWithExtentionIncludePaths(IPackFileExtensionLookup pfs, string extention, IPackFileContainer? packFileContainer = null)
        {
            if (packFileContainer != null)
            {
                var normalizedExtension = extention.ToLower();
                return packFileContainer.GetAllFiles()
                    .Where(x => Path.GetExtension(x.Key) == normalizedExtension)
                    .Select(x => (x.Key, x.Value))
                    .ToList();
            }

            return pfs.FindAllWithExtention(extention, packFileContainer);
        }


    }
}
