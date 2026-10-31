using System.Reflection;
using System.Text;
using System.Xml;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    public partial class PackTextureAtlasCrossRigidTests
    {
        [Test]
        public void XmlMinifier_PreservesTextNamespacesCDataAndXmlSpace_AndWritesUtf8()
        {
            const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n" +
                               "<!-- author note -->\r\n" +
                               "<r xmlns=\"urn:test\">\r\n" +
                               "  <leaf>   </leaf>\r\n" +
                               "  <mixed>left <b> middle </b> right</mixed>\r\n" +
                               "  <refs attr=\"a&#xA;b\">left&#xD;right</refs>\r\n" +
                               "  <raw><![CDATA[ <raw> & untouched ]]></raw>\r\n" +
                               "  <preserve xml:space=\"preserve\">\r\n" +
                               "    <line> keep\r\n      this </line>\r\n" +
                               "    <default xml:space=\"default\">\r\n" +
                               "      <x />\r\n      <y />\r\n" +
                               "    </default>\r\n" +
                               "  </preserve>\r\n" +
                               "</r>\r\n";
            var unicode = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            var source = unicode.GetPreamble().Concat(unicode.GetBytes(xml)).ToArray();

            var attempt = XmlContentMinifier.TryMinify(source);

            Assert.That(attempt.Outcome, Is.EqualTo(XmlFileMinificationOutcome.Minified));
            var minified = attempt.MinifiedData!;
            Assert.That(minified.Length, Is.LessThan(source.Length));
            var text = Encoding.UTF8.GetString(minified);
            Assert.That(text, Does.StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
            Assert.That(text, Does.Not.Contain("author note"));
            Assert.That(text, Does.Contain("<![CDATA[ <raw> & untouched ]]>").And.Contain("xmlns=\"urn:test\""));

            // XML parsers normalize literal CRLFs to LF before the infoset is exposed.
            var originalDocument = LoadXml(xml.Replace("\r\n", "\n", StringComparison.Ordinal));
            var minifiedDocument = LoadXml(text);
            Assert.That(minifiedDocument.DocumentElement!.NamespaceURI, Is.EqualTo("urn:test"));
            Assert.That(TextAt(originalDocument, "leaf"), Is.EqualTo("   "));
            Assert.That(TextAt(minifiedDocument, "leaf"), Is.EqualTo("   "));
            Assert.That(TextAt(minifiedDocument, "mixed"), Is.EqualTo(TextAt(originalDocument, "mixed")));
            Assert.That(TextAt(minifiedDocument, "refs"), Is.EqualTo(TextAt(originalDocument, "refs")));
            Assert.That(minifiedDocument.SelectSingleNode("//*[local-name()='refs']")!.Attributes!["attr"]!.Value,
                Is.EqualTo(originalDocument.SelectSingleNode("//*[local-name()='refs']")!.Attributes!["attr"]!.Value));
            Assert.That(TextAt(minifiedDocument, "line"), Is.EqualTo(TextAt(originalDocument, "line")));
            Assert.That(minifiedDocument.SelectSingleNode("//*[local-name()='raw']")!.FirstChild,
                Is.TypeOf<XmlCDataSection>());
            var resetSpace = minifiedDocument.SelectSingleNode("//*[local-name()='default']")!;
            Assert.That(resetSpace.ChildNodes.Cast<XmlNode>().Any(node =>
                node.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace), Is.False,
                "xml:space=default resumes removal of element indentation inside a preserved subtree");
        }

        [Test]
        public void XmlMinifier_LeavesMalformedDtdAndNonReducibleXmlUnchanged()
        {
            var malformed = Encoding.UTF8.GetBytes("<root>\n  <child></root>");
            var dtd = Encoding.UTF8.GetBytes("<!DOCTYPE root [<!ENTITY x 'bad'>]><root>&x;</root>");
            var compact = Encoding.UTF8.GetBytes("<root/>");

            Assert.That(XmlContentMinifier.TryMinify(malformed).Outcome,
                Is.EqualTo(XmlFileMinificationOutcome.InvalidOrUnsupported));
            Assert.That(XmlContentMinifier.TryMinify(dtd).Outcome,
                Is.EqualTo(XmlFileMinificationOutcome.InvalidOrUnsupported));
            Assert.That(XmlContentMinifier.TryMinify(compact).Outcome,
                Is.EqualTo(XmlFileMinificationOutcome.NoSizeReduction));
        }

        [Test]
        public void XmlMinificationPass_MinifiesExistingAndGeneratedXmlAcrossExtensions_AndSniffsUnknownExtensions()
        {
            const string pretty = "<root>\n    <child>value</child>\n</root>";
            var fixture = new DownscaleFixture();
            var existingPaths = new[]
            {
                "data\\old.xml",
                "data\\old.xml.material",
                "data\\old.wsmodel",
                "data\\old.variantmeshdefinition",
                "data\\old.customxml",
                "data\\sniffed.txt",
            };
            foreach (var path in existingPaths)
                fixture.AddText(path, pretty);

            var generatedPath = "generated\\created.xml";
            fixture.Output[generatedPath] = Encoding.UTF8.GetBytes(pretty);
            var malformedPath = "data\\malformed.xml";
            fixture.AddText(malformedPath, "<root>\n  <child></root>");
            var compactPath = "data\\compact.xml";
            fixture.AddText(compactPath, "<root/>");
            var binaryPath = "textures\\image.dds";
            fixture.Add(binaryPath, [0x44, 0x44, 0x53, 0x20, 0x01, 0x02, 0x03, 0x04]);

            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            GetServiceType()
                .GetMethod("MinifyXmlFiles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);

            var expectedMinified = Encoding.UTF8.GetBytes("<root><child>value</child></root>");
            foreach (var path in existingPaths.Append(generatedPath))
                Assert.That(fixture.Output[path], Is.EqualTo(expectedMinified), path);
            Assert.That(fixture.Output[malformedPath], Is.EqualTo(Encoding.UTF8.GetBytes("<root>\n  <child></root>")));
            Assert.That(fixture.Output[compactPath], Is.EqualTo(Encoding.UTF8.GetBytes("<root/>")));
            Assert.That(fixture.Output[binaryPath], Is.EqualTo(new byte[] { 0x44, 0x44, 0x53, 0x20, 0x01, 0x02, 0x03, 0x04 }));

            var summary = Get<XmlMinificationSummary>(state, "XmlMinification");
            Assert.That(summary.FilesMinified, Is.EqualTo(7));
            Assert.That(summary.InvalidOrUnsupportedFiles, Is.EqualTo(1));
            Assert.That(summary.NoSizeReductionFiles, Is.EqualTo(1));
            Assert.That(summary.BytesSaved, Is.GreaterThan(0));

            var firstPassBytes = fixture.Output[generatedPath].ToArray();
            GetServiceType()
                .GetMethod("MinifyXmlFiles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);
            Assert.That(fixture.Output[generatedPath], Is.EqualTo(firstPassBytes));
            Assert.That(Get<XmlMinificationSummary>(state, "XmlMinification").FilesMinified, Is.Zero,
                "a second pass must not rewrite already-minified XML");
        }

        [Test]
        public void XmlMinificationPass_ConsumerProtectedMaterialIsNotReplaced()
        {
            const string pretty = "<material>\n    <name>shared</name>\n</material>";
            var fixture = new DownscaleFixture();
            fixture.AddText(ConsumerMaterial, pretty);
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            InstallConsumers(
                state,
                0,
                ("OtherMod", ConsumerFiles((
                    "variantmeshes\\mod\\other.wsmodel",
                    $"<model><geometry>variantmeshes\\mod\\other.rigid_model_v2</geometry>" +
                    $"<materials><material lod_index=\"0\" part_index=\"0\">{ConsumerMaterial}</material></materials></model>"))));
            var before = fixture.Output[ConsumerMaterial].ToArray();

            GetServiceType()
                .GetMethod("MinifyXmlFiles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [state, CancellationToken.None, null]);

            Assert.That(fixture.Output[ConsumerMaterial], Is.EqualTo(before));
            Assert.That(Get<XmlMinificationSummary>(state, "XmlMinification").ConsumerBlockedFiles, Is.EqualTo(1));
        }

        private static XmlDocument LoadXml(string xml)
        {
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.LoadXml(xml);
            return document;
        }

        private static string TextAt(XmlDocument document, string localName)
            => document.SelectSingleNode($"//*[local-name()='{localName}']")!.InnerText;
    }
}
