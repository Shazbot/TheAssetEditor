using System.Xml;
using Editors.ImportExport.Dds.Native;
using Editors.ImportExport.Importing.Importers.PngToDds.Helpers;
using Editors.ImportExport.TextureAtlas;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;
using Shared.GameFormats.RigidModel.Types;

namespace Editors.PackOptimizer.Core
{
    public sealed partial class PackTextureAtlasBatchService
    {
        // REPORT-ONLY "Phase 0" net texture cost model of a cross-rigid shared atlas.
        // Nothing here feeds selection, gating, emission or atlas logic.
        //
        // For the accepted T1 payload set of one growth cap it estimates, per
        // component of materials that would have to share an atlas:
        //  * eligibility: the cheap subset of TryCreateCandidate that needs no
        //    texture decoding (see BuildCrossRigidSharedAtlasMaterial);
        //  * a dry-run pack with TextureAtlasBuilder.CreatePlan (padding 8, 4096 page);
        //  * source retirement with closure (a source texture is retired only
        //    when every material that names it is atlased);
        //  * lost shared-repack savings of the rigids the atlas would change;
        //  * a cost curve ordered by net texture bytes per saved draw.
        //
        // Draws are the same expected-army-draw units as the "saved ... draws"
        // figures of the payload accounting.

        private static readonly double[] CrossRigidSharedAtlasPriceLimitsMiB =
            [1, 2, 4, 8];

        private static readonly int[] CrossRigidSharedAtlasCurveRanks =
            [1, 2, 4, 8, 16, 32, 64];

        private const string SharedAtlasReasonNoMaterial =
            "material unreadable";
        private const string SharedAtlasReasonEmissive = "emissive";
        private const string SharedAtlasReasonDuplicateSlot =
            "conflicting duplicate slot";
        private const string SharedAtlasReasonNoBaseColour = "no base colour";
        private const string SharedAtlasReasonBaseUnresolved =
            "base colour unresolved";
        private const string SharedAtlasReasonBaseUnsized =
            "base colour not sizable BCn";
        private const string SharedAtlasReasonSecondaryMissing =
            "secondary texture unresolved";
        private const string SharedAtlasReasonNoUser =
            "no source WSModel user";
        private const string SharedAtlasReasonMultiMaterialMesh =
            "mesh part shared by several materials";
        private const string SharedAtlasReasonGameplayMesh =
            "mesh shared with gameplay consumer";
        private const string SharedAtlasReasonImmutableRigid =
            "rigid used outside the source pack";

        private const string SharedAtlasReasonTiledUv =
            "base colour UV0 tiles";

        private const string SharedAtlasBlockVanilla = "vanilla";
        private const string SharedAtlasBlockExternal = "external reference";

        internal sealed record SharedAtlasChannelTexture(
            string Slot,
            TextureType Type,
            string Path);

        // IneligibleReason null = eligible. Textures: the real (non
        // placeholder, resolvable) AtlasChannels slots. KeepAlphaSlots: BC1
        // channels whose alpha must be kept (atlas becomes BC3).
        internal sealed record SharedAtlasMaterial(
            string Path,
            string? IneligibleReason,
            SharedAtlasChannelTexture[] Textures,
            string[] KeepAlphaSlots);

        // CropFraction: area fraction of the measured UV0 bounding rectangles
        // (union over all parts), null when not measured.
        internal sealed record SharedAtlasTexture(
            long Bytes,
            int Width,
            int Height,
            double? CropFraction);

        internal sealed record SharedAtlasPayload(
            string Key,
            double Saving,
            long GeometryBytes,
            string[][] Groups);

        internal sealed record SharedAtlasRepackTexture(
            string Texture,
            long BytesSaved,
            IReadOnlyCollection<string> Rigids);

        internal sealed record SharedAtlasCostInput(
            IReadOnlyList<SharedAtlasPayload> Payloads,
            Func<string, SharedAtlasMaterial> GetMaterial,
            Func<string, SharedAtlasTexture?> GetTexture,
            // texture -> (material, slot) pairs naming it
            IReadOnlyDictionary<string, List<(string Material, string Slot)>>
                TextureReferrers,
            // null = a source texture that may be retired; else why not
            Func<string, string?> GetTextureBlockReason,
            Func<string, IReadOnlyCollection<string>> GetMaterialRigids,
            // null = coverage analysis not available
            IReadOnlyList<SharedAtlasRepackTexture>? RepackTextures,
            // Phase 0b: source texture mip the atlas is built from (every
            // source and crop size is divided by 2^MipShift, padding stays).
            int MipShift = 0,
            // Phase 0b: the atlas lives only in a generated payload, source
            // textures stay untouched: nothing is retired, no repack is lost.
            bool GeneratedOnly = false);

        internal sealed record SharedAtlasComponentCost(
            string[] Materials,
            bool Fits,
            bool FitsFullRect,
            int PageWidth,
            int PageHeight,
            long AtlasBytes,
            long AtlasBytesFullRect,
            int ChannelCount,
            int SubBlockCrops = 0);

        internal sealed record SharedAtlasCurvePoint(
            int Rank,
            string FirstMaterial,
            double Draws,
            long GeometryBytes,
            long NetBytes,
            long MarginalNetBytes,
            double MarginalDraws);

        internal sealed record SharedAtlasReachable(
            double LimitMiB,
            double AverageDraws,
            int AverageRank,
            double MarginalDraws,
            int MarginalComponents);

        internal sealed record SharedAtlasCostResult(
            int MaterialCount,
            int EligibleMaterialCount,
            IReadOnlyDictionary<string, int> IneligibleMaterialsByReason,
            IReadOnlyDictionary<string, double> LostSavingByReason,
            double AtlasPayloadSaving,
            double LostIneligibleSaving,
            double LostFitSaving,
            double EligibleSaving,
            int ComponentCount,
            int FitComponentCount,
            int FitFullRectComponentCount,
            int LargestPageWidth,
            int LargestPageHeight,
            int SelectedComponentCount,
            int SelectedMaterialCount,
            long AtlasBytes,
            long AtlasBytesFullRect,
            long RetiredBytes,
            int RetiredTextureCount,
            int KeptVanillaTextureCount,
            int KeptExternalTextureCount,
            long NetBytes,
            long? LostRepackBytes,
            long GeometryBytes,
            IReadOnlyList<SharedAtlasCurvePoint> Curve,
            IReadOnlyList<SharedAtlasReachable> Reachable,
            int SubBlockCropCount = 0,
            int MedianPageWidth = 0,
            int MedianPageHeight = 0);

        // ---- Pure dry-run pack ----

        // Packs the base-colour rectangles with the real planner. A crop is
        // expressed as a UV rectangle starting at the origin.
        internal static (bool Fits, int Width, int Height)
            TryPlanCrossRigidSharedAtlasPage(
                IReadOnlyList<(int Id, int Width, int Height, int CropWidth,
                    int CropHeight)> sources)
        {
            if (sources.Count == 0)
                return (false, 0, 0);

            try
            {
                var layout = sources
                    .Select(source => new TextureAtlasLayoutSource(
                        source.Id,
                        source.Width,
                        source.Height,
                        0f,
                        0f,
                        Math.Min(1f, (source.CropWidth - 0.5f) / source.Width),
                        Math.Min(1f, (source.CropHeight - 0.5f) / source.Height)))
                    .ToArray();
                var plan = TextureAtlasBuilder.CreatePlan(
                    layout,
                    TextureAtlasBuilder.DefaultPadding,
                    PackAtlasMaxSize);
                return (true, plan.Width, plan.Height);
            }
            catch (InvalidOperationException)
            {
                return (false, 0, 0);
            }
        }

        private static int GetCrossRigidSharedAtlasBytesPerBlock(
            TextureType type,
            bool keepAlpha)
        {
            var format = DDSFormatHelper.GetDDSFormat(
                GameTypeEnum.Warhammer3,
                type);
            if (keepAlpha && format == DXGI_FORMAT.BC1_UNORM_SRGB)
                format = DXGI_FORMAT.BC3_UNORM_SRGB;
            return GetAtlasBytesPerBlock(format);
        }

        private static (int Width, int Height) GetCrossRigidSharedAtlasCrop(
            SharedAtlasTexture texture,
            bool useCrop)
        {
            if (!useCrop ||
                texture.CropFraction is not { } fraction ||
                fraction <= 0 ||
                fraction >= 1)
            {
                return (texture.Width, texture.Height);
            }

            var side = Math.Sqrt(fraction);
            return (
                Math.Clamp((int)Math.Ceiling(texture.Width * side), 1,
                    texture.Width),
                Math.Clamp((int)Math.Ceiling(texture.Height * side), 1,
                    texture.Height));
        }

        // Dry-runs one component: the base-colour layout is the layout every
        // channel shares (UV0 is shared), each channel page is the base page
        // scaled by the largest member channel/base resolution ratio.
        internal static SharedAtlasComponentCost CostCrossRigidSharedAtlasComponent(
            string[] materials,
            SharedAtlasCostInput input)
        {
            var infos = materials.Select(input.GetMaterial).ToArray();
            var baseTextures = infos
                .Select(info => info.Textures.FirstOrDefault(texture =>
                    texture.Slot.Equals(
                        "t_xml_base_colour",
                        StringComparison.OrdinalIgnoreCase)))
                .Where(texture => texture != null)
                .Select(texture => texture!.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var sized = baseTextures
                .Select(path => (Path: path, Info: input.GetTexture(path)))
                .Where(item => item.Info != null)
                .ToArray();
            if (sized.Length == 0)
            {
                return new SharedAtlasComponentCost(
                    materials, false, false, 0, 0, 0, 0, 0);
            }

            var subBlockCrops = 0;
            (bool Fits, int Width, int Height) Plan(bool crop)
            {
                var counted = 0;
                var result = TryPlanCrossRigidSharedAtlasPage(
                    sized.Select((item, index) =>
                    {
                        var size = GetCrossRigidSharedAtlasCrop(
                            item.Info!,
                            crop);
                        var width = ShiftCrossRigidSharedAtlasSize(
                            item.Info!.Width,
                            input.MipShift);
                        var height = ShiftCrossRigidSharedAtlasSize(
                            item.Info.Height,
                            input.MipShift);
                        var cropWidth = Math.Min(
                            width,
                            ShiftCrossRigidSharedAtlasSize(
                                size.Width,
                                input.MipShift));
                        var cropHeight = Math.Min(
                            height,
                            ShiftCrossRigidSharedAtlasSize(
                                size.Height,
                                input.MipShift));
                        if (cropWidth < 4 || cropHeight < 4)
                            counted++;
                        return (index, width, height, cropWidth, cropHeight);
                    }).ToArray());
                if (crop)
                    subBlockCrops = counted;
                return result;
            }

            var primary = Plan(true);
            var full = Plan(false);

            long Bytes(int pageWidth, int pageHeight)
            {
                long total = 0;
                foreach (var channel in AtlasChannels)
                {
                    var members = infos
                        .Select(info => (
                            Info: info,
                            Texture: info.Textures.FirstOrDefault(texture =>
                                texture.Slot.Equals(
                                    channel.Slot,
                                    StringComparison.OrdinalIgnoreCase)),
                            Base: info.Textures.FirstOrDefault(texture =>
                                texture.Slot.Equals(
                                    "t_xml_base_colour",
                                    StringComparison.OrdinalIgnoreCase))))
                        .Where(item => item.Texture != null)
                        .ToArray();
                    if (members.Length == 0)
                        continue;

                    double ratioWidth = 0;
                    double ratioHeight = 0;
                    foreach (var member in members)
                    {
                        var channelInfo = input.GetTexture(member.Texture!.Path);
                        var baseInfo = member.Base == null
                            ? null
                            : input.GetTexture(member.Base.Path);
                        if (channelInfo == null || baseInfo == null)
                        {
                            ratioWidth = Math.Max(ratioWidth, 1);
                            ratioHeight = Math.Max(ratioHeight, 1);
                            continue;
                        }

                        ratioWidth = Math.Max(
                            ratioWidth,
                            (double)channelInfo.Width / baseInfo.Width);
                        ratioHeight = Math.Max(
                            ratioHeight,
                            (double)channelInfo.Height / baseInfo.Height);
                    }

                    // A channel texture of a source is at most PackAtlasMaxSize
                    // (at mip m: PackAtlasMaxSize >> m), so its page is too; a
                    // tiny base colour next to a large channel texture would
                    // otherwise inflate the ratio without bound.
                    var channelLimit = ShiftCrossRigidSharedAtlasSize(
                        PackAtlasMaxSize,
                        input.MipShift);
                    var width = Math.Clamp(
                        (int)Math.Ceiling(pageWidth * ratioWidth),
                        4,
                        Math.Max(4, channelLimit));
                    var height = Math.Clamp(
                        (int)Math.Ceiling(pageHeight * ratioHeight),
                        4,
                        Math.Max(4, channelLimit));
                    var keepAlpha = members.Any(item =>
                        item.Info.KeepAlphaSlots.Contains(
                            channel.Slot,
                            StringComparer.OrdinalIgnoreCase));
                    total += CalculateBcnMipChainBytes(
                        width,
                        height,
                        GetCrossRigidSharedAtlasBytesPerBlock(
                            channel.Type,
                            keepAlpha));
                }

                return total;
            }

            var channelCount = AtlasChannels.Count(channel => infos.Any(info =>
                info.Textures.Any(texture => texture.Slot.Equals(
                    channel.Slot,
                    StringComparison.OrdinalIgnoreCase))));
            return new SharedAtlasComponentCost(
                materials,
                primary.Fits,
                full.Fits,
                primary.Fits ? primary.Width : full.Width,
                primary.Fits ? primary.Height : full.Height,
                primary.Fits ? Bytes(primary.Width, primary.Height) : 0,
                full.Fits ? Bytes(full.Width, full.Height) : 0,
                channelCount,
                subBlockCrops);
        }

        // Size of a texture dimension at a lower source mip (ceil, at least 1).
        internal static int ShiftCrossRigidSharedAtlasSize(int size, int mipShift)
            => Math.Max(1, (size + (1 << mipShift) - 1) >> mipShift);

        // ---- Pure cost model ----

        internal static SharedAtlasCostResult ComputeCrossRigidSharedAtlasCost(
            SharedAtlasCostInput input)
        {
            var atlasPayloads = input.Payloads
                .Where(payload => payload.Groups.Length != 0)
                .ToArray();
            var materialInfo = new Dictionary<string, SharedAtlasMaterial>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var material in atlasPayloads
                         .SelectMany(payload => payload.Groups)
                         .SelectMany(group => group))
            {
                if (!materialInfo.ContainsKey(material))
                    materialInfo[material] = input.GetMaterial(material);
            }

            var ineligibleByReason = materialInfo.Values
                .Where(info => info.IneligibleReason != null)
                .GroupBy(info => info.IneligibleReason!)
                .ToDictionary(group => group.Key, group => group.Count());

            // Eligibility: a payload is lost when any member material is
            // ineligible (its group could not share the atlas).
            var lostByReason = new Dictionary<string, double>();
            double lostIneligible = 0;
            var eligiblePayloads = new List<SharedAtlasPayload>();
            foreach (var payload in atlasPayloads)
            {
                var reason = payload.Groups
                    .SelectMany(group => group)
                    .Where(material =>
                        materialInfo[material].IneligibleReason != null)
                    .OrderBy(material => material, StringComparer.Ordinal)
                    .Select(material => materialInfo[material].IneligibleReason)
                    .FirstOrDefault();
                if (reason == null)
                {
                    eligiblePayloads.Add(payload);
                    continue;
                }

                lostIneligible += payload.Saving;
                lostByReason[reason] =
                    lostByReason.GetValueOrDefault(reason) + payload.Saving;
            }

            var components = BuildCrossRigidIdealMergeAtlasComponents(
                eligiblePayloads.SelectMany(payload => payload.Groups));
            var componentIndexByMaterial = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var costs = new SharedAtlasComponentCost[components.Count];
            for (var index = 0; index < components.Count; index++)
            {
                foreach (var material in components[index])
                    componentIndexByMaterial[material] = index;
                costs[index] = CostCrossRigidSharedAtlasComponent(
                    components[index],
                    input);
            }

            // Payload -> components it needs; credited only when all fit.
            var payloadComponents = eligiblePayloads
                .Select(payload => payload.Groups
                    .SelectMany(group => group)
                    .Select(material => componentIndexByMaterial[material])
                    .Distinct()
                    .OrderBy(index => index)
                    .ToArray())
                .ToArray();
            double lostFit = 0;
            var credited = new List<int>();
            for (var index = 0; index < eligiblePayloads.Count; index++)
            {
                if (payloadComponents[index].All(c => costs[c].Fits))
                    credited.Add(index);
                else
                    lostFit += eligiblePayloads[index].Saving;
            }

            var selected = credited
                .SelectMany(index => payloadComponents[index])
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            var selectedSet = selected.ToHashSet();
            var selectedMaterials = new HashSet<string>(
                selected.SelectMany(index => components[index]),
                StringComparer.OrdinalIgnoreCase);

            // Retirement closure over the selected materials.
            var retireNeeds = new Dictionary<string, HashSet<int>>(
                StringComparer.OrdinalIgnoreCase);
            var keptVanilla = 0;
            var keptExternal = 0;
            foreach (var texture in selectedMaterials
                         .SelectMany(material => materialInfo[material].Textures)
                         .Select(texture => texture.Path)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(_ => !input.GeneratedOnly))
            {
                var block = input.GetTextureBlockReason(texture);
                if (block == SharedAtlasBlockVanilla)
                {
                    keptVanilla++;
                    continue;
                }

                var referrers = input.TextureReferrers.GetValueOrDefault(texture);
                if (block != null ||
                    referrers == null ||
                    referrers.Count == 0 ||
                    referrers.Any(referrer =>
                        !selectedMaterials.Contains(referrer.Material) ||
                        !AtlasChannels.Any(channel => channel.Slot.Equals(
                            referrer.Slot,
                            StringComparison.OrdinalIgnoreCase))))
                {
                    keptExternal++;
                    continue;
                }

                retireNeeds[texture] = referrers
                    .Select(referrer =>
                        componentIndexByMaterial[referrer.Material])
                    .ToHashSet();
            }

            long TextureBytes(string texture)
                => input.GetTexture(texture)?.Bytes ?? 0;

            // Ordering: standalone net bytes per attributed draw, retirement
            // of a texture shared by k components split equally, a payload
            // needing k components split equally.
            var standaloneNet = selected.ToDictionary(
                index => index,
                index => costs[index].AtlasBytes);
            var standaloneDraws = selected.ToDictionary(index => index, _ => 0.0);
            foreach (var (texture, needs) in retireNeeds)
            {
                var bytes = TextureBytes(texture);
                foreach (var index in needs)
                    standaloneNet[index] -= bytes / needs.Count;
            }

            foreach (var index in credited)
            {
                var needs = payloadComponents[index];
                foreach (var component in needs)
                {
                    standaloneDraws[component] +=
                        eligiblePayloads[index].Saving / needs.Length;
                }
            }

            // Components whose retirement pays for their atlas (net <= 0) come
            // first, most draws first; then ascending net bytes per draw;
            // components with no attributed draws last.
            double Ratio(int index)
                => standaloneNet[index] <= 0
                    ? -standaloneDraws[index]
                    : standaloneDraws[index] > AtlasValueGateExpectedDrawEpsilon
                        ? standaloneNet[index] / standaloneDraws[index]
                        : double.PositiveInfinity;
            var order = selected
                .OrderBy(index => standaloneNet[index] <= 0 ? 0 : 1)
                .ThenBy(Ratio)
                .ThenBy(index => components[index][0], StringComparer.Ordinal)
                .ToArray();

            // Replay: each payload counts toward the last of its components
            // added, each texture is retired when the last component that
            // needs it is added.
            var remainingPayload = credited.ToDictionary(
                index => index,
                index => payloadComponents[index].Length);
            var payloadsByComponent = new Dictionary<int, List<int>>();
            foreach (var index in credited)
            {
                foreach (var component in payloadComponents[index])
                {
                    if (!payloadsByComponent.TryGetValue(component, out var list))
                        payloadsByComponent[component] = list = [];
                    list.Add(index);
                }
            }

            var remainingTexture = retireNeeds.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Count,
                StringComparer.OrdinalIgnoreCase);
            var texturesByComponent = new Dictionary<int, List<string>>();
            foreach (var (texture, needs) in retireNeeds)
            {
                foreach (var component in needs)
                {
                    if (!texturesByComponent.TryGetValue(component, out var list))
                        texturesByComponent[component] = list = [];
                    list.Add(texture);
                }
            }

            var curve = new List<SharedAtlasCurvePoint>();
            var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double draws = 0;
            long geometry = 0;
            long net = 0;
            long retiredBytes = 0;
            long atlasBytes = 0;
            long atlasBytesFull = 0;
            foreach (var component in order)
            {
                long retireDelta = 0;
                foreach (var texture in texturesByComponent.GetValueOrDefault(
                             component) ?? [])
                {
                    if (--remainingTexture[texture] == 0)
                    {
                        retireDelta += TextureBytes(texture);
                        retired.Add(texture);
                    }
                }

                double drawsDelta = 0;
                long geometryDelta = 0;
                foreach (var payload in payloadsByComponent.GetValueOrDefault(
                             component) ?? [])
                {
                    if (--remainingPayload[payload] != 0)
                        continue;
                    drawsDelta += eligiblePayloads[payload].Saving;
                    geometryDelta += eligiblePayloads[payload].GeometryBytes;
                }

                var netDelta = costs[component].AtlasBytes - retireDelta;
                atlasBytes += costs[component].AtlasBytes;
                atlasBytesFull += costs[component].AtlasBytesFullRect;
                retiredBytes += retireDelta;
                net += netDelta;
                draws += drawsDelta;
                geometry += geometryDelta;
                curve.Add(new SharedAtlasCurvePoint(
                    curve.Count + 1,
                    components[component][0],
                    draws,
                    geometry,
                    net,
                    netDelta,
                    drawsDelta));
            }

            // Draws reachable at a net texture price per draw.
            var reachable = new List<SharedAtlasReachable>();
            foreach (var limit in CrossRigidSharedAtlasPriceLimitsMiB)
            {
                var limitBytes = limit * 1024 * 1024;
                double averageDraws = 0;
                var averageRank = 0;
                foreach (var point in curve)
                {
                    if (point.Draws > AtlasValueGateExpectedDrawEpsilon &&
                        point.NetBytes <= limitBytes * point.Draws &&
                        point.Draws > averageDraws)
                    {
                        averageDraws = point.Draws;
                        averageRank = point.Rank;
                    }
                }

                var marginal = curve.Where(point =>
                    point.MarginalDraws > AtlasValueGateExpectedDrawEpsilon &&
                    point.MarginalNetBytes <=
                    limitBytes * point.MarginalDraws).ToArray();
                reachable.Add(new SharedAtlasReachable(
                    limit,
                    averageDraws,
                    averageRank,
                    marginal.Sum(point => point.MarginalDraws),
                    marginal.Length));
            }

            // Repack savings lost on the rigids the atlas would change.
            long? lostRepack = null;
            if (input.RepackTextures != null && !input.GeneratedOnly)
            {
                var changed = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var material in selectedMaterials)
                    changed.UnionWith(input.GetMaterialRigids(material));
                lostRepack = input.RepackTextures
                    .Where(item => !retired.Contains(item.Texture) &&
                                   item.Rigids.Any(changed.Contains))
                    .Sum(item => item.BytesSaved);
            }

            var selectedPages = selected
                .Select(index => (costs[index].PageWidth,
                    costs[index].PageHeight))
                .OrderBy(page => (long)page.PageWidth * page.PageHeight)
                .ToArray();
            var medianPage = selectedPages.Length == 0
                ? (Width: 0, Height: 0)
                : (Width: selectedPages[selectedPages.Length / 2].PageWidth,
                    Height: selectedPages[selectedPages.Length / 2].PageHeight);

            return new SharedAtlasCostResult(
                materialInfo.Count,
                materialInfo.Values.Count(info => info.IneligibleReason == null),
                ineligibleByReason,
                lostByReason,
                atlasPayloads.Sum(payload => payload.Saving),
                lostIneligible,
                lostFit,
                credited.Sum(index => eligiblePayloads[index].Saving),
                components.Count,
                costs.Count(cost => cost.Fits),
                costs.Count(cost => cost.FitsFullRect),
                costs.Length == 0 ? 0 : costs.Max(cost => cost.PageWidth),
                costs.Length == 0 ? 0 : costs.Max(cost => cost.PageHeight),
                selected.Length,
                selectedMaterials.Count,
                atlasBytes,
                atlasBytesFull,
                retiredBytes,
                retired.Count,
                keptVanilla,
                keptExternal,
                net,
                lostRepack,
                geometry,
                curve,
                reachable,
                selected.Sum(index => costs[index].SubBlockCrops),
                medianPage.Width,
                medianPage.Height);
        }

        // ---- State adapter ----

        // Per-state, built once on first use, cleared with the accounting.
        private sealed class CrossRigidSharedAtlasIndex
        {
            public Dictionary<string, HashSet<string>> RigidsByMaterial { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> MeshBlockByMaterial { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<(string Material, string Slot)>>
                TextureReferrers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> BlockedTextures { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public bool EmbeddedReferencesKnown { get; set; }
            public List<SharedAtlasRepackTexture>? RepackTextures { get; set; }
            public Dictionary<string, SharedAtlasMaterial> Materials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            // Phase 0b: material-level checks only (no mesh-level blockers,
            // plus the UV0 tiling reason when coverage is known).
            public Dictionary<string, SharedAtlasMaterial> FarMaterials { get; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private static CrossRigidSharedAtlasIndex
            GetCrossRigidSharedAtlasIndex(BatchState state)
        {
            var accounting = state.CrossRigidIdealMergeAccounting;
            if (accounting.SharedAtlasIndex != null)
                return accounting.SharedAtlasIndex;

            var index = new CrossRigidSharedAtlasIndex();

            // Mesh-level blocks from the WSModel usage index, with the same
            // rules the real candidate discovery applies per mesh part.
            foreach (var (key, usages) in state.Usages)
            {
                var materials = usages
                    .Select(usage => Normalize(usage.MaterialPath))
                    .Where(path => path.Length != 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                string? block = null;
                if (materials.Length > 1)
                    block = SharedAtlasReasonMultiMaterialMesh;
                else if (!IsCandidateSharedMeshSafeForGameplay(
                             state,
                             key,
                             usages))
                {
                    block = SharedAtlasReasonGameplayMesh;
                }
                else if (state.ImmutableMeshMergeConsumersByRigid.TryGetValue(
                             key.GeometryPath,
                             out var immutable) &&
                         immutable.Count != 0)
                {
                    block = SharedAtlasReasonImmutableRigid;
                }

                foreach (var material in materials)
                {
                    if (!index.RigidsByMaterial.TryGetValue(
                            material,
                            out var rigids))
                    {
                        index.RigidsByMaterial[material] = rigids =
                            new HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);
                    }

                    rigids.Add(key.GeometryPath);
                    if (block != null)
                        index.MeshBlockByMaterial.TryAdd(material, block);
                }
            }

            // Texture -> materials naming it, over every source material.
            foreach (var (path, file) in state.Source.GetAllFiles() ??
                                         new Dictionary<string, PackFile>())
            {
                if (!path.EndsWith(
                        ".xml.material",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalized = Normalize(path);
                XmlDocument document;
                try
                {
                    document = state.MaterialDocuments.TryGetValue(
                        normalized,
                        out var cached)
                        ? cached
                        : LoadXml(file);
                }
                catch
                {
                    continue;
                }

                var nodes = document.SelectNodes("/material/textures/texture");
                if (nodes == null)
                    continue;
                foreach (XmlNode node in nodes)
                {
                    var texture = Normalize(
                        node.SelectSingleNode("source")?.InnerText ??
                        node.InnerText);
                    if (texture.Length == 0 || IsTexturePlaceholder(texture))
                        continue;
                    if (!index.TextureReferrers.TryGetValue(
                            texture,
                            out var list))
                    {
                        index.TextureReferrers[texture] = list = [];
                    }

                    list.Add((normalized, GetTextureSlot(node)));
                }
            }

            var coverage = state.VanillaTextureCoverage;
            if (coverage != null)
            {
                index.EmbeddedReferencesKnown = true;
                foreach (var embedded in coverage.EmbeddedMaterialsByPart.Values)
                {
                    foreach (var texture in embedded.Textures)
                        index.BlockedTextures.Add(Normalize(texture.Path));
                }

                index.BlockedTextures.UnionWith(coverage.OwnedRemovalBlockers);
                index.RepackTextures = [];
                foreach (var entry in coverage.Entries.Values
                             .OrderBy(entry => entry.Path,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    if (entry.NonMaterialReference != null)
                        index.BlockedTextures.Add(Normalize(entry.Path));
                    if (!entry.IsOwned ||
                        !entry.IsRepackable ||
                        entry.BytesSaved <= 0)
                    {
                        continue;
                    }

                    index.RepackTextures.Add(new SharedAtlasRepackTexture(
                        Normalize(entry.Path),
                        entry.BytesSaved,
                        entry.ModParts
                            .Select(part => Normalize(part.Rigid))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)));
                }
            }

            accounting.SharedAtlasIndex = index;
            return index;
        }

        // The checks of TryCreateCandidate that need neither geometry nor
        // texture decoding, in its order: emissive shader / slot, conflicting
        // duplicate slot, base colour present and resolvable, secondary
        // textures resolvable (unless the ignorable test_mask sentinel).
        // Not replicated: uniform-constant channel detection (needs a pixel
        // read; constant channels are costed as real textures), UV0 island
        // preparation, Faction3 mask canonicalisation, consumer-pack blocks.
        // Mesh-level rules (several materials on a mesh part, gameplay
        // sharing, rigid used outside the pack) come from the usage index.
        private static SharedAtlasMaterial BuildCrossRigidSharedAtlasMaterial(
            BatchState state,
            CrossRigidSharedAtlasIndex index,
            string materialPath,
            bool applyMeshBlocks = true)
        {
            var normalized = Normalize(materialPath);
            XmlDocument? document = null;
            if (!state.MaterialDocuments.TryGetValue(normalized, out document))
            {
                try
                {
                    var file = FindForReadStatic(state, normalized);
                    document = file == null ? null : LoadXml(file);
                }
                catch
                {
                    document = null;
                }
            }

            SharedAtlasMaterial Ineligible(string reason)
                => new(materialPath, reason, [], []);
            if (document == null)
                return Ineligible(SharedAtlasReasonNoMaterial);

            var shader = document.SelectSingleNode("/material/shader")
                ?.InnerText ?? string.Empty;
            if (shader.Contains("emissive", StringComparison.OrdinalIgnoreCase) ||
                document.SelectNodes("/material/textures/texture")?
                    .Cast<XmlNode>()
                    .Any(node => GetTextureSlot(node).Contains(
                        "emissive",
                        StringComparison.OrdinalIgnoreCase)) == true)
            {
                return Ineligible(SharedAtlasReasonEmissive);
            }

            if (FindConflictingDuplicateTextureSlot(document) != null)
                return Ineligible(SharedAtlasReasonDuplicateSlot);
            if (string.IsNullOrWhiteSpace(
                    GetTexturePath(document, "t_xml_base_colour")))
            {
                return Ineligible(SharedAtlasReasonNoBaseColour);
            }

            var textures = new List<SharedAtlasChannelTexture>();
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in AtlasChannels)
            {
                var path = GetTexturePath(document, channel.Slot);
                if (string.IsNullOrWhiteSpace(path) || IsTexturePlaceholder(path))
                    continue;
                var isBase = channel.Slot.Equals(
                    "t_xml_base_colour",
                    StringComparison.OrdinalIgnoreCase);
                if (FindForReadStatic(state, path) == null)
                {
                    if (isBase)
                        return Ineligible(SharedAtlasReasonBaseUnresolved);
                    if (IsIgnorableUnresolvedAtlasTexture(channel.Slot, path))
                        continue;
                    return Ineligible(SharedAtlasReasonSecondaryMissing);
                }

                if (isBase &&
                    GetCrossRigidIdealMergeTextureInfo(state, path) == null)
                {
                    return Ineligible(SharedAtlasReasonBaseUnsized);
                }

                textures.Add(new SharedAtlasChannelTexture(
                    channel.Slot,
                    channel.Type,
                    path));
                resolved.Add(channel.Slot);
            }

            var meshReason = ResolveCrossRigidSharedAtlasMeshLevelReason(
                applyMeshBlocks,
                index.RigidsByMaterial.ContainsKey(normalized),
                index.MeshBlockByMaterial.GetValueOrDefault(normalized));
            if (meshReason != null)
                return Ineligible(meshReason);

            string[] keepAlpha;
            try
            {
                keepAlpha = GetKeepAlphaReasons(
                        state,
                        document,
                        resolved,
                        new Dictionary<string, TextureAtlasConstantColor>(
                            StringComparer.OrdinalIgnoreCase))
                    .Keys
                    .ToArray();
            }
            catch
            {
                keepAlpha = [];
            }

            return new SharedAtlasMaterial(
                materialPath,
                null,
                textures.ToArray(),
                keepAlpha);
        }

        // Mesh-level rules (no source user, shared mesh part, gameplay
        // consumer, rigid used outside the pack) only matter when the atlas
        // rewrites the source rigids; a generated payload never does.
        internal static string? ResolveCrossRigidSharedAtlasMeshLevelReason(
            bool applyMeshBlocks,
            bool hasSourceUser,
            string? meshBlock)
        {
            if (!applyMeshBlocks)
                return null;
            return hasSourceUser ? meshBlock : SharedAtlasReasonNoUser;
        }

        private static SharedAtlasCostInput BuildCrossRigidSharedAtlasCostInput(
            BatchState state,
            IReadOnlyList<CrossRigidIdealMergePayload> acceptedPayloads,
            bool farMode = false,
            int mipShift = 0)
        {
            var index = GetCrossRigidSharedAtlasIndex(state);
            var coverage = state.VanillaTextureCoverage;
            return new SharedAtlasCostInput(
                acceptedPayloads
                    .Select(payload => farMode
                        ? new SharedAtlasPayload(
                            payload.Key,
                            GetCrossRigidFarAtlasSaving(
                                payload.ScenarioLodIndices,
                                payload.SavingByScenarioLod,
                                payload.T0SavingByScenarioLod),
                            payload.FarGeometryBytes,
                            GetCrossRigidFarGroups(
                                payload.ScenarioLodIndices,
                                payload.QualifyingMaterialGroupsByScenarioLod))
                        : new SharedAtlasPayload(
                            payload.Key,
                            payload.Saving,
                            payload.GeometryBytes,
                            payload.QualifyingMaterialGroups))
                    .ToArray(),
                material =>
                {
                    var cache = farMode ? index.FarMaterials : index.Materials;
                    if (!cache.TryGetValue(material, out var info))
                    {
                        info = BuildCrossRigidSharedAtlasMaterial(
                            state,
                            index,
                            material,
                            !farMode);
                        if (farMode && info.IneligibleReason == null)
                        {
                            var basePath = info.Textures
                                .FirstOrDefault(texture => texture.Slot.Equals(
                                    "t_xml_base_colour",
                                    StringComparison.OrdinalIgnoreCase))
                                ?.Path;
                            if (basePath != null &&
                                coverage != null &&
                                coverage.Entries.TryGetValue(
                                    basePath,
                                    out var entry) &&
                                entry.Finalized &&
                                entry.Tiled)
                            {
                                info = new SharedAtlasMaterial(
                                    material,
                                    SharedAtlasReasonTiledUv,
                                    [],
                                    []);
                            }
                        }

                        cache[material] = info;
                    }

                    return info;
                },
                texture =>
                {
                    var info = GetCrossRigidIdealMergeTextureInfo(
                        state,
                        texture);
                    if (info == null)
                        return null;
                    double? crop = null;
                    if (coverage != null &&
                        coverage.Entries.TryGetValue(texture, out var entry) &&
                        entry.Finalized &&
                        !entry.Tiled &&
                        entry.AssessedParts != 0 &&
                        entry.BoundingRectangleFraction > 0)
                    {
                        crop = entry.BoundingRectangleFraction;
                    }

                    return new SharedAtlasTexture(
                        info.BcnBytes,
                        info.Width,
                        info.Height,
                        crop);
                },
                index.TextureReferrers,
                texture => !state.Source.ContainsFile(texture)
                    ? SharedAtlasBlockVanilla
                    : index.BlockedTextures.Contains(texture)
                        ? SharedAtlasBlockExternal
                        : null,
                material => index.RigidsByMaterial.TryGetValue(
                    Normalize(material),
                    out var rigids)
                    ? rigids
                    : Array.Empty<string>(),
                index.RepackTextures,
                mipShift,
                farMode);
        }

        // ---- Report lines ----

        internal static List<string> BuildCrossRigidSharedAtlasCostLines(
            SharedAtlasCostResult result,
            double baselineTotal)
        {
            double Share(double value)
                => baselineTotal > AtlasValueGateExpectedDrawEpsilon
                    ? value / baselineTotal
                    : 0;
            string Draws(double value)
                => $"{value:0.###} ({Share(value):P1})";
            var lines = new List<string>();

            var reasons = result.IneligibleMaterialsByReason
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry =>
                    $"{entry.Key} {entry.Value:N0} material(s) / " +
                    $"{result.LostSavingByReason.GetValueOrDefault(entry.Key):0.###} draws")
                .ToArray();
            lines.Add(
                $"        shared-atlas eligibility (TryCreateCandidate subset): " +
                $"{result.EligibleMaterialCount:N0} of {result.MaterialCount:N0} " +
                $"material(s) eligible; payloads lost to an ineligible member " +
                $"{Draws(result.LostIneligibleSaving)} of " +
                $"{result.AtlasPayloadSaving:0.###} atlas-dependent" +
                (reasons.Length == 0
                    ? string.Empty
                    : "; by first reason: " + string.Join(", ", reasons)));
            lines.Add(
                $"        shared-atlas dry run (CreatePlan {PackAtlasMaxSize} page, " +
                $"padding {TextureAtlasBuilder.DefaultPadding}, base-colour " +
                $"layout shared by all channels, measured UV0 bounding crops): " +
                $"{result.ComponentCount:N0} eligible component(s), " +
                $"{result.FitComponentCount:N0} fit one page ({result.FitFullRectComponentCount:N0} " +
                $"with full-rect sources), largest page " +
                $"{result.LargestPageWidth}x{result.LargestPageHeight}; payloads " +
                $"lost to page fit {Draws(result.LostFitSaving)}");
            lines.Add(
                $"        shared-atlas selected: {result.SelectedComponentCount:N0} " +
                $"component(s), {result.SelectedMaterialCount:N0} material(s), " +
                $"draws {Draws(result.EligibleSaving)}, atlas " +
                $"{FormatMiB(result.AtlasBytes)} BCn (full-rect " +
                $"{FormatMiB(result.AtlasBytesFullRect)}), retired " +
                $"{FormatMiB(result.RetiredBytes)} ({result.RetiredTextureCount:N0} " +
                $"texture(s); kept: {result.KeptVanillaTextureCount:N0} vanilla, " +
                $"{result.KeptExternalTextureCount:N0} used outside), net " +
                $"{FormatMiB(result.NetBytes)}, lost repack " +
                $"{(result.LostRepackBytes.HasValue ? FormatMiB(result.LostRepackBytes.Value) : "n/a")}, " +
                $"geometry {FormatMiB(result.GeometryBytes)}");
            var curveLine = BuildCrossRigidSharedAtlasCurveLine(
                result,
                "shared-atlas");
            if (curveLine != null)
                lines.Add(curveLine);
            lines.Add(BuildCrossRigidSharedAtlasReachableLine(
                result,
                "shared-atlas"));
            return lines;
        }

        private static string? BuildCrossRigidSharedAtlasCurveLine(
            SharedAtlasCostResult result,
            string label)
        {
            if (result.Curve.Count == 0)
                return null;
            var points = new List<SharedAtlasCurvePoint>();
            foreach (var rank in CrossRigidSharedAtlasCurveRanks)
            {
                if (rank < result.Curve.Count)
                    points.Add(result.Curve[rank - 1]);
            }

            points.Add(result.Curve[^1]);
            return
                $"        {label} curve (components by net per saved draw; " +
                "cumulative rank: draws / net / geometry): " +
                string.Join("; ", points.Select(point =>
                    $"#{point.Rank} {point.Draws:0.###} / " +
                    $"{FormatMiB(point.NetBytes)} / " +
                    $"{FormatMiB(point.GeometryBytes)}"));
        }

        private static string BuildCrossRigidSharedAtlasReachableLine(
            SharedAtlasCostResult result,
            string label)
            => $"        {label} draws reachable at net texture per draw " +
               "(avg = best cumulative prefix, marg = components whose own " +
               "marginal price fits): " +
               string.Join("; ", result.Reachable.Select(item =>
                   $"<= {item.LimitMiB:0} MiB avg {item.AverageDraws:0.###} " +
                   $"(#{item.AverageRank}), marg {item.MarginalDraws:0.###} " +
                   $"({item.MarginalComponents:N0} comp)"));
    }
}
