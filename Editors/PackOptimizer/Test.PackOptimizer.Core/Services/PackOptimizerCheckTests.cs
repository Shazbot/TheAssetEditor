using System.Text;
using Editors.PackOptimizer.Core;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Models.FileSources;
using Shared.Core.Settings;

namespace Test.PackOptimizer.Core
{
    public class PackOptimizerCheckTests
    {
        private const string Vmd = "variantmeshes\\variantmeshdefinitions\\unit.variantmeshdefinition";

        private static void Add(PackFileContainer container, string path, string content)
        {
            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            container.AddFiles([new NewPackFileEntry(directory, new PackFile(name, new MemorySource(Encoding.UTF8.GetBytes(content))))]);
        }

        private static PackOptimizerCheckRequest Request(PackFileContainer source)
        {
            source.IsReadOnly = true;
            var vanilla = PackFileContainer.CreateCaPackFile("vanilla");
            return new PackOptimizerCheckRequest("C:\\mods\\synthetic.pack", source, [vanilla], GameTypeEnum.Warhammer3);
        }

        private static PackFileContainer BrokenPack()
        {
            var pack = PackFileContainer.CreatePackFile("synthetic");
            Add(pack, Vmd,
                "<VARIANT_MESH><SLOT name=\"a\" attach_point=\"be_head\" probability=\"1\">" +
                "<VARIANT_MESH model=\"variantmeshes\\ok.wsmodel\" />" +
                "<VARIANT_MESH model=\"variantmeshes\\gone.wsmodel\" />" +
                "<VARIANT_MESH_REFERENCE definition=\"variantmeshes\\variantmeshdefinitions\\missing_child.variantmeshdefinition\" />" +
                "</SLOT></VARIANT_MESH>");
            Add(pack, "variantmeshes\\ok.wsmodel",
                "<model><geometry>variantmeshes\\gone.rigid_model_v2</geometry><materials>" +
                "<material lod_index=\"0\" part_index=\"0\">variantmeshes\\materials\\a.xml.material</material></materials></model>");
            Add(pack, "variantmeshes\\broken.wsmodel", "<model><geometry>");
            Add(pack, "variantmeshes\\variantmeshdefinitions\\bad.variantmeshdefinition", "this is not xml");
            return pack;
        }

        [Test]
        public void Check_FindsBrokenReferencesAndMalformedXml_WithoutWritingAnything()
        {
            var progress = new List<PackOptimizerProgress>();
            var result = Editors.PackOptimizer.Core.PackOptimizer.Check(Request(BrokenPack()), new ListProgress<PackOptimizerProgress>(progress));

            Assert.That(result.Status, Is.EqualTo(PackOptimizerStatus.Succeeded));
            var codes = result.Findings.Select(finding => finding.Code).ToList();
            Assert.That(codes, Does.Contain(PackCheckCodes.MissingReference));
            Assert.That(codes, Does.Contain(PackCheckCodes.MalformedVariantMesh));
            Assert.That(codes, Does.Contain(PackCheckCodes.MalformedWsModel));

            var missing = result.Findings.Where(finding => finding.Code == PackCheckCodes.MissingReference).ToList();
            Assert.That(missing.Select(finding => finding.Path), Does.Contain("variantmeshes\\gone.wsmodel"));
            Assert.That(missing.Select(finding => finding.Path), Does.Contain("variantmeshes\\gone.rigid_model_v2"));
            Assert.That(
                missing.Select(finding => finding.Path),
                Does.Contain("variantmeshes\\variantmeshdefinitions\\missing_child.variantmeshdefinition"));
            Assert.That(missing, Has.All.Matches<PackCheckFinding>(finding => finding.Severity == PackCheckSeverity.Error));
            Assert.That(
                missing.Single(finding => finding.Path == "variantmeshes\\gone.wsmodel").RelatedPaths,
                Is.EqualTo([Vmd]));

            var malformed = result.Findings.Single(finding => finding.Code == PackCheckCodes.MalformedVariantMesh);
            Assert.That(malformed.Path, Is.EqualTo("variantmeshes\\variantmeshdefinitions\\bad.variantmeshdefinition"));
            Assert.That(malformed.Category, Is.EqualTo(PackCheckCategories.Xml));
            Assert.That(
                result.Findings.Single(finding => finding.Code == PackCheckCodes.MalformedWsModel).Path,
                Is.EqualTo("variantmeshes\\broken.wsmodel"));

            // Summary matches the findings; findings are ordered most severe first.
            Assert.That(result.Summary.Errors, Is.EqualTo(result.Findings.Count(finding => finding.Severity == PackCheckSeverity.Error)));
            Assert.That(result.Summary.Total, Is.EqualTo(result.Findings.Count));
            Assert.That(result.Summary.ByCode[PackCheckCodes.MissingReference], Is.EqualTo(missing.Count));
            Assert.That(result.Findings.Select(finding => finding.Severity), Is.Ordered.Descending);
            Assert.That(result.VmdCount, Is.EqualTo(1));

            // The text report is rendered from the same findings.
            Assert.That(result.ReportText, Does.Contain("Visual Asset Check Report"));
            Assert.That(result.ReportText, Does.Contain($"[{PackCheckCodes.MissingReference}]"));
            Assert.That(result.ReportText, Does.Contain("variantmeshes\\gone.wsmodel"));
            Assert.That(progress.Select(item => item.Stage), Does.Contain("Validating VMD roots"));
            Assert.That(progress.Select(item => item.Stage), Does.Contain("Auditing LODs"));
        }

        [Test]
        public void Check_ReferenceProvidedByDependencyPack_IsNotMissing()
        {
            var dependency = PackFileContainer.CreatePackFile("dep_mod");
            Add(dependency, "variantmeshes\\gone.wsmodel", "<model />");
            var request = Request(BrokenPack()) with { DependencyContainers = [dependency] };

            var result = Editors.PackOptimizer.Core.PackOptimizer.Check(request);

            Assert.That(
                result.Findings.Where(finding => finding.Code == PackCheckCodes.MissingReference).Select(finding => finding.Path),
                Does.Not.Contain("variantmeshes\\gone.wsmodel"));
            var provided = result.Findings.Single(finding => finding.Code == PackCheckCodes.ProvidedByDependency);
            Assert.That(provided.Severity, Is.EqualTo(PackCheckSeverity.Info));
            Assert.That(provided.Message, Does.Contain("dep_mod"));
            Assert.That(provided.RelatedPaths, Is.EqualTo(["variantmeshes\\gone.wsmodel"]));
            Assert.That(result.Summary.UsageKnown, Is.False);
            Assert.That(result.Findings, Has.All.Matches<PackCheckFinding>(finding => finding.UsedByGame == null));
        }

        [Test]
        public void Summarize_CountsUsageAndRenderReport_ShowsTheSplit()
        {
            var used = new PackCheckFinding(PackCheckSeverity.Error, "A", "X", "m", "p", [], 0, null, true);
            var unused = new PackCheckFinding(PackCheckSeverity.Warning, "A", "X", "m", "p", [], 0, "d", false);
            var unknown = new PackCheckFinding(PackCheckSeverity.Info, "B", "X", "m", "", [], 0);
            var summary = PackOptimizerCheck.Summarize([used, unused, unknown], true, 3, 2);

            Assert.That((summary.FindingsUsed, summary.FindingsUnused, summary.FindingsUnknown), Is.EqualTo((1, 1, 1)));
            var text = PackOptimizerCheck.RenderReport("s.pack", [used, unused, unknown], summary, 5, 0, 0, 0, TimeSpan.Zero);
            Assert.That(text, Does.Contain("1 finding(s) used, 1 unused"));
            Assert.That(text, Does.Contain("3 variant mesh(es) reached by a unit, 2 not reached"));
        }

        [Test]
        public void Check_PackWithoutVariantMeshes_ReportsNothingToCheck()
        {
            var pack = PackFileContainer.CreatePackFile("empty");
            Add(pack, "text\\readme.txt", "hello");

            var result = Editors.PackOptimizer.Core.PackOptimizer.Check(Request(pack));

            Assert.That(result.Status, Is.EqualTo(PackOptimizerStatus.Succeeded));
            var finding = result.Findings.Single();
            Assert.That(finding.Code, Is.EqualTo(PackCheckCodes.NoVisualAssets));
            Assert.That(finding.Severity, Is.EqualTo(PackCheckSeverity.Info));
            Assert.That(result.Summary.Errors, Is.Zero);
        }

        [Test]
        public void Check_Cancelled_ReturnsCancelledWithoutFindings()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = Editors.PackOptimizer.Core.PackOptimizer.Check(Request(BrokenPack()), null, cancellation.Token);

            Assert.That(result.Status, Is.EqualTo(PackOptimizerStatus.Cancelled));
            Assert.That(result.Findings, Is.Empty);
        }

        [Test]
        public void Summarize_CountsBySeverityCodeAndCategory()
        {
            PackCheckFinding Finding(PackCheckSeverity severity, string code, string category)
                => new(severity, code, category, "m", "p", [], 0);
            var summary = PackOptimizerCheck.Summarize(
            [
                Finding(PackCheckSeverity.Error, "A", "X"),
                Finding(PackCheckSeverity.Error, "A", "X"),
                Finding(PackCheckSeverity.Warning, "B", "Y"),
                Finding(PackCheckSeverity.Info, "C", "Y"),
            ]);

            Assert.That((summary.Errors, summary.Warnings, summary.Infos), Is.EqualTo((2, 1, 1)));
            Assert.That(summary.ByCode["A"], Is.EqualTo(2));
            Assert.That(summary.ByCategory["Y"], Is.EqualTo(2));
        }

        [Test]
        public void RenderReport_ListsRelatedPathsAndTruncation()
        {
            var finding = new PackCheckFinding(
                PackCheckSeverity.Warning, "XmlRepaired", "Xml", "msg", "a\\b.xml", ["r1", "r2"], 5, "why");
            var summary = PackOptimizerCheck.Summarize([finding]);

            var text = PackOptimizerCheck.RenderReport("src.pack", [finding], summary, 1, 2, 3, 4, TimeSpan.FromSeconds(1.5));

            Assert.That(text, Does.Contain("Warnings: 1"));
            Assert.That(text, Does.Contain("[XmlRepaired] msg"));
            Assert.That(text, Does.Contain("details: why"));
            Assert.That(text, Does.Contain("related (5):"));
            Assert.That(text, Does.Contain("... 3 more"));
        }
    }

    internal sealed class ListProgress<T>(List<T> target) : IProgress<T>
    {
        public void Report(T value) => target.Add(value);
    }
}
