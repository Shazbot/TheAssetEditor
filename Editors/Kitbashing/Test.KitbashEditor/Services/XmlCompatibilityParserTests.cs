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
        public void Parse_IgnoresTrailingDashAnnotations()
        {
            const string xml =
                "<model><geometry>foo</geometry></model>\r\n" +
                "--11 is moustache+plume\r\n" +
                "--12 is beard stubble\r\n" +
                "--14 is goatee+band\r\n" +
                "--15 is alpha beard\r\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("model"));
                Assert.That(repairs, Has.Count.EqualTo(1));
                Assert.That(repairs[0], Does.Contain("trailing non-XML text"));
                Assert.That(repairs[0], Does.Contain("4 non-empty lines"));
            });
        }

        [Test]
        public void Parse_IgnoresTrailingEqualsAnnotation()
        {
            const string xml =
                "<model><geometry>foo</geometry></model>\n" +
                "===this goes well with demigryph knight and greatsword chests, except 01\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("model"));
                Assert.That(repairs, Has.Count.EqualTo(1));
                Assert.That(repairs[0], Does.Contain("1 non-empty line"));
            });
        }

        [Test]
        public void Parse_IgnoresTrailingFreeformMultilineNotes()
        {
            const string xml =
                "<model><geometry>foo</geometry></model>\n" +
                "01 & 02 masked (half split)\n" +
                "\n" +
                "03 brown cloth\n" +
                "\n" +
                "no hood and no filthy peasant either\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("model"));
                Assert.That(repairs, Has.Count.EqualTo(1));
                Assert.That(repairs[0], Does.Contain("3 non-empty lines"));
            });
        }

        [Test]
        public void Parse_RepairsMissingClosingTagTerminator()
        {
            const string xml =
                "<VARIANT_MESH>\n" +
                "  <SLOT></SLOT>\n" +
                "</VARIANT_MESH";

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
        public void Parse_AppliesClosingTagAndTrailingTextRepairsTogether()
        {
            const string xml =
                "<VARIANT_MESH>\n" +
                "  <SLOT></SLOT>\n" +
                "</VARIANT_MESH\n" +
                "01 & 02 masked (half split)\n";

            var document = XmlCompatibilityParser.Parse(
                xml,
                ParseDocument,
                out var repairs);

            Assert.Multiple(() =>
            {
                Assert.That(document.DocumentElement?.Name, Is.EqualTo("VARIANT_MESH"));
                Assert.That(repairs, Has.Count.EqualTo(2));
                Assert.That(repairs[0], Does.Contain("</VARIANT_MESH>"));
                Assert.That(repairs[1], Does.Contain("trailing non-XML text"));
            });
        }

        [Test]
        public void Parse_DoesNotIgnoreTextInsideTheRootElement()
        {
            const string xml =
                "<model>\n" +
                "  <broken\n" +
                "</model>";

            Assert.Throws<XmlException>(() =>
                XmlCompatibilityParser.Parse(
                    xml,
                    ParseDocument,
                    out _));
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
