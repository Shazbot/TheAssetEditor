using System.Xml;
using Shared.GameFormats;

namespace Test.KitbashEditor.Services
{
    public class XmlCompatibilityParserTests
    {
        [Test]
        public void Parse_LeavesValidXmlUntouched()
        {
            var document = XmlCompatibilityParser.Parse(
                "<model><geometry>foo</geometry></model>",
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("model"));
                Assert.That(repairs, Is.Empty);
            });
        }

        [Test]
        public void Parse_IgnoresTrailingDashPseudoComments()
        {
            const string xml =
                "<model><geometry>foo</geometry></model>\r\n" +
                "-- replace with wsmodels when CBA or CA fixes generic FCM materials\r\n" +
                "-- second note\r\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("model"));
                Assert.That(repairs, Has.Count.EqualTo(1));
                Assert.That(repairs[0], Does.Contain("2 trailing"));
            });
        }

        [Test]
        public void Parse_RepairsMissingClosingTagTerminator()
        {
            const string xml =
                "<VARIANT_MESH><SLOT></SLOT></VARIANT_MESH";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("VARIANT_MESH"));
                Assert.That(repairs, Has.Count.EqualTo(1));
                Assert.That(repairs[0], Does.Contain("</VARIANT_MESH>"));
            });
        }

        [Test]
        public void Parse_AppliesTrailingCommentAndClosingTagRepairsTogether()
        {
            const string xml =
                "<VARIANT_MESH><SLOT></SLOT></VARIANT_MESH\n" +
                "-- temporary workaround\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("VARIANT_MESH"));
                Assert.That(repairs, Has.Count.EqualTo(2));
                Assert.That(repairs[0], Does.Contain("pseudo-comment"));
                Assert.That(repairs[1], Does.Contain("</VARIANT_MESH>"));
            });
        }

        [Test]
        public void Parse_DoesNotGuessBrokenXmlStructure()
        {
            const string xml = "<model><broken</model>";

            Assert.Throws<XmlException>(() =>
                XmlCompatibilityParser.Parse(
                    xml,
                    ParseDocument,
                    out _));
        }

        private static XmlDocument ParseDocument(string xml)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document;
        }
    }
}
