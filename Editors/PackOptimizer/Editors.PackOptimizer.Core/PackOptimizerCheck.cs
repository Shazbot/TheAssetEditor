using System.Globalization;
using System.Text;
using Shared.Core.PackFiles.Models;
using Shared.Core.Settings;

namespace Editors.PackOptimizer.Core
{
    /// <summary>How serious a visual-asset check finding is.</summary>
    public enum PackCheckSeverity
    {
        /// <summary>Worth knowing, nothing is broken (duplicate textures, short LOD chains).</summary>
        Info = 0,
        /// <summary>Probably unintended or wasteful, the asset still works (repaired XML, opaque alpha-tested material).</summary>
        Warning = 1,
        /// <summary>A reference or file that is broken: the game will not render it as authored.</summary>
        Error = 2,
    }

    /// <summary>The stable finding codes. Callers may key UI text and filters on them.</summary>
    public static class PackCheckCodes
    {
        // References
        public const string MissingReference = "MissingReference";
        public const string InvalidReference = "InvalidReference";
        public const string UnparsableAsset = "UnparsableAsset";
        public const string UnknownReferenceType = "UnknownReferenceType";
        // XML
        public const string MalformedVariantMesh = "MalformedVariantMesh";
        public const string MalformedWsModel = "MalformedWsModel";
        public const string XmlRepaired = "XmlRepaired";
        // Materials
        public const string MalformedMaterial = "MalformedMaterial";
        public const string MissingMaterial = "MissingMaterial";
        public const string OpaqueAlphaTested = "OpaqueAlphaTested";
        public const string MostlyOpaqueAlphaTested = "MostlyOpaqueAlphaTested";
        // Textures
        public const string MissingTexture = "MissingTexture";
        public const string UnreadableTexture = "UnreadableTexture";
        public const string DuplicateTexture = "DuplicateTexture";
        // Models
        public const string UnusableWsModel = "UnusableWsModel";
        public const string UnusableVariantMesh = "UnusableVariantMesh";
        public const string UnreadableRigid = "UnreadableRigid";
        public const string ComponentDiagnostic = "ComponentDiagnostic";
        // Skinning
        public const string InvalidBoneWeights = "InvalidBoneWeights";
        // LODs
        public const string LodNotLighter = "LodNotLighter";
        // The check itself
        public const string CheckIncomplete = "CheckIncomplete";
        public const string ProvidedByDependency = "ProvidedByDependency";
        public const string NoVisualAssets = "NoVisualAssets";
    }

    /// <summary>The finding categories.</summary>
    public static class PackCheckCategories
    {
        public const string References = "References";
        public const string Xml = "Xml";
        public const string Materials = "Materials";
        public const string Textures = "Textures";
        public const string Models = "Models";
        public const string Skinning = "Skinning";
        public const string Lods = "Lods";
        public const string Audit = "Audit";
    }

    /// <summary>One thing the visual-asset check found.</summary>
    /// <param name="Code">Stable identifier of the kind of finding (see <see cref="PackCheckCodes"/>).</param>
    /// <param name="Message">One human-readable line.</param>
    /// <param name="Path">The primary path: the pack file the finding is about (a material, a WSModel, a rigid,
    /// a texture) or, for a broken reference, the path that cannot be resolved. Empty for pack-wide findings.</param>
    /// <param name="RelatedPaths">Other paths involved (what references it, what duplicates it), capped at
    /// <see cref="PackOptimizerCheck.RelatedPathLimit"/>; <paramref name="RelatedCount"/> is the real number.</param>
    /// <param name="Details">Extra detail (parser message, measured values), or null.</param>
    /// <param name="UsedByGame">True when a unit in the game data (the pack's own DB plus the game's) reaches the
    /// asset through a variant mesh definition; false when only unused ones do (the severity is then one level
    /// lower and <paramref name="Details"/> says so); null when unknown (no DB schema, resolver unhealthy, or not
    /// attributable to a variant mesh).</param>
    public sealed record PackCheckFinding(
        PackCheckSeverity Severity,
        string Code,
        string Category,
        string Message,
        string Path,
        IReadOnlyList<string> RelatedPaths,
        int RelatedCount,
        string? Details = null,
        bool? UsedByGame = null);

    public sealed record PackCheckSummary(
        int Errors,
        int Warnings,
        int Infos,
        IReadOnlyDictionary<string, int> ByCode,
        IReadOnlyDictionary<string, int> ByCategory,
        int FindingsUsed = 0,
        int FindingsUnused = 0,
        int FindingsUnknown = 0,
        bool UsageKnown = false,
        int VmdsUsed = 0,
        int VmdsUnused = 0)
    {
        public int Total => Errors + Warnings + Infos;
    }

    /// <summary>
    /// What a check needs, passed explicitly. The check reads the source pack and the game packs; it writes
    /// nothing (no output pack) and ignores other mods.
    /// </summary>
    /// <param name="SourcePackPath">Path of the pack being checked; used for naming and the report.</param>
    /// <param name="SourceContainer">The already loaded (read-only) source pack.</param>
    /// <param name="ConsumerContainers">Read-only game packs the source may reference, in precedence order, later wins.</param>
    /// <param name="CacheDirectory">Directory for the vanilla consumer cache; null disables the cache.</param>
    /// <param name="DbSchema">With a schema, the unit-category resolver finds which variant meshes the game data
    /// reaches and findings get <see cref="PackCheckFinding.UsedByGame"/>; without one it stays null.</param>
    /// <param name="DependencyContainers">Other enabled mods (read-only). A reference one of them provides is not
    /// reported as missing; one info finding per providing pack names it.</param>
    public sealed record PackOptimizerCheckRequest(
        string SourcePackPath,
        IPackFileContainer SourceContainer,
        IReadOnlyList<IPackFileContainer> ConsumerContainers,
        GameTypeEnum Game,
        string? CacheDirectory = null,
        PackOptimizerDbSchema? DbSchema = null,
        IReadOnlyList<IPackFileContainer>? DependencyContainers = null);

    public sealed record PackOptimizerCheckResult(
        PackOptimizerStatus Status,
        IReadOnlyList<PackCheckFinding> Findings,
        PackCheckSummary Summary,
        string ReportText,
        int VmdCount,
        int WsModelCount,
        int RigidCount,
        int MaterialCount,
        TimeSpan Elapsed);

    /// <summary>Shared pieces of the check: limits and the text report.</summary>
    public static class PackOptimizerCheck
    {
        /// <summary>The most related paths one finding lists.</summary>
        public const int RelatedPathLimit = 25;

        public static PackCheckSummary Summarize(
            IReadOnlyCollection<PackCheckFinding> findings,
            bool usageKnown = false,
            int vmdsUsed = 0,
            int vmdsUnused = 0)
            => new(
                findings.Count(finding => finding.Severity == PackCheckSeverity.Error),
                findings.Count(finding => finding.Severity == PackCheckSeverity.Warning),
                findings.Count(finding => finding.Severity == PackCheckSeverity.Info),
                findings.GroupBy(finding => finding.Code, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                findings.GroupBy(finding => finding.Category, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                findings.Count(finding => finding.UsedByGame == true),
                findings.Count(finding => finding.UsedByGame == false),
                findings.Count(finding => finding.UsedByGame == null),
                usageKnown,
                vmdsUsed,
                vmdsUnused);

        public static string SeverityName(PackCheckSeverity severity) => severity switch
        {
            PackCheckSeverity.Error => "error",
            PackCheckSeverity.Warning => "warning",
            _ => "info",
        };

        /// <summary>The human-readable report: a summary, then the findings grouped by severity and category.</summary>
        public static string RenderReport(
            string sourcePackPath,
            IReadOnlyList<PackCheckFinding> findings,
            PackCheckSummary summary,
            int vmdCount,
            int wsModelCount,
            int rigidCount,
            int materialCount,
            TimeSpan elapsed,
            bool cancelled = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Visual Asset Check Report");
            sb.AppendLine("=========================");
            sb.AppendLine($"Source: {sourcePackPath}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (cancelled)
                sb.AppendLine("Status: CANCELLED (the findings below are partial)");
            sb.AppendLine();

            sb.AppendLine("Summary");
            sb.AppendLine("-------");
            sb.AppendLine($"Errors: {summary.Errors:N0}");
            sb.AppendLine($"Warnings: {summary.Warnings:N0}");
            sb.AppendLine($"Info: {summary.Infos:N0}");
            if (summary.UsageKnown)
            {
                sb.AppendLine(
                    $"Used by game data: {summary.FindingsUsed:N0} finding(s) used, {summary.FindingsUnused:N0} unused " +
                    $"(shown one level lower), {summary.FindingsUnknown:N0} unknown; " +
                    $"{summary.VmdsUsed:N0} variant mesh(es) reached by a unit, {summary.VmdsUnused:N0} not reached.");
            }
            else
            {
                sb.AppendLine("Used by game data: unknown (no DB schema given or the unit data could not be resolved).");
            }
            sb.AppendLine(
                $"Checked: {vmdCount:N0} variant mesh definition(s), {wsModelCount:N0} WSModel(s) reached, " +
                $"{rigidCount:N0} rigid model(s) reached, {materialCount:N0} material(s) reached.");
            sb.AppendLine($"Time: {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s");
            if (summary.ByCode.Count != 0)
            {
                sb.AppendLine("By code:");
                foreach (var (code, count) in summary.ByCode
                             .OrderByDescending(item => item.Value)
                             .ThenBy(item => item.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine($"  {code}: {count:N0}");
                }
            }

            sb.AppendLine();
            if (findings.Count == 0)
            {
                sb.AppendLine("No findings.");
                return sb.ToString();
            }

            foreach (var severity in new[] { PackCheckSeverity.Error, PackCheckSeverity.Warning, PackCheckSeverity.Info })
            {
                var ofSeverity = findings.Where(finding => finding.Severity == severity).ToList();
                if (ofSeverity.Count == 0)
                    continue;

                var title = $"{char.ToUpperInvariant(SeverityName(severity)[0])}{SeverityName(severity)[1..]}s ({ofSeverity.Count:N0})";
                sb.AppendLine(title);
                sb.AppendLine(new string('=', title.Length));
                foreach (var category in ofSeverity
                             .GroupBy(finding => finding.Category, StringComparer.Ordinal)
                             .OrderBy(group => group.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine();
                    sb.AppendLine($"{category.Key} ({category.Count():N0})");
                    sb.AppendLine(new string('-', category.Key.Length + $" ({category.Count():N0})".Length));
                    foreach (var finding in category)
                        AppendFinding(sb, finding);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void AppendFinding(StringBuilder sb, PackCheckFinding finding)
        {
            sb.AppendLine($"[{finding.Code}] {finding.Message}");
            if (finding.Path.Length != 0)
                sb.AppendLine($"    path: {finding.Path}");
            if (!string.IsNullOrWhiteSpace(finding.Details))
                sb.AppendLine($"    details: {finding.Details}");
            if (finding.RelatedPaths.Count != 0)
            {
                sb.AppendLine($"    related ({finding.RelatedCount:N0}):");
                foreach (var related in finding.RelatedPaths)
                    sb.AppendLine($"      {related}");
                if (finding.RelatedCount > finding.RelatedPaths.Count)
                    sb.AppendLine($"      ... {finding.RelatedCount - finding.RelatedPaths.Count:N0} more");
            }
        }
    }
}
