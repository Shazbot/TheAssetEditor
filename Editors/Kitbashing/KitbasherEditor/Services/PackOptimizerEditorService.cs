using Editors.PackOptimizer.Core;
using PackOptimizerApi = Editors.PackOptimizer.Core.PackOptimizer;
using Shared.Core.Misc;
using Shared.Core.PackFiles;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Utility;
using Shared.Core.Services;
using Shared.Core.Settings;

namespace Editors.KitbasherEditor.Services
{
    /// <summary>
    /// The editor's front end for the pack optimizer: the game check, the file picker, the
    /// options window and the result dialogs. The work itself is Editors.PackOptimizer.Core.
    /// </summary>
    public sealed class PackOptimizerEditorService
    {
        private readonly IPackFileService _packFileService;
        private readonly IPackFileContainerLoader _packFileContainerLoader;
        private readonly ApplicationSettingsService _settingsService;
        private readonly IStandardDialogs _standardDialogs;

        public PackOptimizerEditorService(
            IPackFileService packFileService,
            IPackFileContainerLoader packFileContainerLoader,
            ApplicationSettingsService settingsService,
            IStandardDialogs standardDialogs)
        {
            _packFileService = packFileService;
            _packFileContainerLoader = packFileContainerLoader;
            _settingsService = settingsService;
            _standardDialogs = standardDialogs;
        }

        public void Run()
        {
            if (_settingsService.CurrentSettings.CurrentGame != GameTypeEnum.Warhammer3)
            {
                _standardDialogs.ShowDialogBox(
                    "Pack texture atlasing currently supports Warhammer 3 packs only.",
                    "Texture Atlas Pack");
                return;
            }

            var browse = _standardDialogs.ShowSystemOpenFileDialog(false, "Pack files|*.pack");
            if (!browse.Result || browse.FilePaths.Count == 0)
                return;

            var sourcePath = browse.FilePaths[0];
            var outputPath = PackOptimizerApi.BuildOutputPath(sourcePath);

            PackOptimizerResult? result = null;
            var progressWindow = new TextureAtlasProgressWindow((
                mergeCompatibleMeshes,
                shareAtlasesAcrossVmds,
                optimizeGeometry,
                atlasAllVmds,
                scoreAllGameUnits,
                crossRigidOnly,
                crossRigidMerges,
                buildAtlases,
                downscaleTextures,
                sharedTextureRepack,
                compactTextures,
                dropUnreadAlpha,
                cancellationToken,
                progress) =>
            {
                result = Optimize(
                    sourcePath,
                    outputPath,
                    new PackOptimizerOptions
                    {
                        MergeCompatibleMeshes = mergeCompatibleMeshes,
                        ShareAtlasesAcrossVmds = shareAtlasesAcrossVmds,
                        OptimizeGeometry = optimizeGeometry,
                        AtlasAllVmds = atlasAllVmds,
                        ScoreAllGameUnits = scoreAllGameUnits,
                        CrossRigidOnly = crossRigidOnly,
                        CrossRigidMerges = crossRigidMerges,
                        BuildAtlases = buildAtlases,
                        DownscaleTextures = downscaleTextures,
                        SharedTextureRepack = sharedTextureRepack,
                        CompactTextures = compactTextures,
                        DropUnreadAlpha = dropUnreadAlpha,
                    },
                    progress,
                    cancellationToken);
            });

            if (System.Windows.Application.Current?.MainWindow != null)
                progressWindow.Owner = System.Windows.Application.Current.MainWindow;

            progressWindow.ShowDialog();

            if (progressWindow.Failure != null)
            {
                _standardDialogs.ShowExceptionWindow(
                    progressWindow.Failure,
                    "Failed to create texture atlas pack.");
                return;
            }

            if (progressWindow.WasCancelled || result == null ||
                result.Status != PackOptimizerStatus.Succeeded)
            {
                return;
            }

            _standardDialogs.ShowDialogBox(
                $"Texture atlas pack created successfully.\n\n" +
                $"Output: {outputPath}\n" +
                $"Atlas VMD roots: {result.VmdCount}\n" +
                $"Mesh parts atlased: {result.AtlasedMeshCount}\n" +
                $"Atlas textures generated: {result.GeneratedTextureCount}\n" +
                $"Unused asset files removed: {result.RemovedFileCount}\n" +
                $"Mesh parts skipped: {result.SkippedMeshCount}\n" +
                $"Report: {result.ReportPath}",
                "Texture Atlas Pack");
        }

        /// <summary>
        /// Builds the request from the editor's loaded containers and runs the optimizer. The
        /// loaded packs are the read-only consumers; the output is never registered with the
        /// pack file service.
        /// </summary>
        public PackOptimizerResult Optimize(
            string sourcePath,
            string outputPath,
            PackOptimizerOptions options,
            IProgress<PackOptimizerProgress>? progress,
            CancellationToken cancellationToken)
        {
            var source = _packFileContainerLoader.CreateFromPackFile(
                PackFileContainerType.Normal,
                sourcePath,
                loadAsReadOnly: true);
            var request = new PackOptimizerRequest(
                sourcePath,
                source,
                _packFileService.GetAllPackfileContainers(),
                outputPath,
                PackOptimizerApi.BuildReportPath(outputPath),
                _settingsService.CurrentSettings.CurrentGame,
                options,
                MissingTexturePolicy.Skip,
                DirectoryHelper.CacheDirectory);
            var result = PackOptimizerApi.Run(request, progress, cancellationToken);
            if (result.Status == PackOptimizerStatus.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            return result;
        }
    }
}
