using System.Reflection;
using System.Text;
using System.Xml;
using Shared.GameFormats.Vmd;
using WH3AssetHost;

namespace Test.ImportExport.Exporting;

public sealed class PaintedVariantExporterTests
{
    [Test]
    public void FlattenedVmd_PreservesSelectedSlotNamesPathsAndOptionalAttributes()
    {
        var exporterType = typeof(AssetHostProtocol).Assembly.GetType(
            "WH3AssetHost.PaintedVariantExporter",
            throwOnError: true)!;
        var componentType = exporterType.GetNestedType(
            "ExportedComponent",
            BindingFlags.NonPublic)
            ?? throw new AssertionException("PaintedVariantExporter.ExportedComponent was not found.");
        var constructor = componentType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 6);

        object CreateComponent(
            string modelPath,
            string? slotName,
            string attachmentPoint,
            string? sourceAttachmentPoint = null,
            string? probability = null,
            string? useDifferentAttachPointParts = null)
            => constructor.Invoke(
            [
                modelPath,
                slotName,
                attachmentPoint,
                sourceAttachmentPoint,
                probability,
                useDifferentAttachPointParts
            ]);

        var components = Array.CreateInstance(componentType, 4);
        components.SetValue(
            CreateComponent(
                @"variantmeshes\wh_variantmodels\hu3\dwf\dwf_belegar\dwf_belegar_head_01.wsmodel",
                null,
                string.Empty),
            0);
        components.SetValue(
            CreateComponent(
                @"variantmeshes\whmm_unit_painter\painted\models\001_torso.wsmodel",
                "torso",
                string.Empty),
            1);
        components.SetValue(
            CreateComponent(
                @"variantmeshes\wh_variantmodels\hu3\dwf\dwf_belegar\dwf_belegar_legs_01.wsmodel",
                "legs",
                string.Empty),
            2);
        components.SetValue(
            CreateComponent(
                @"variantmeshes\wh_variantmodels\hu3\dwf\dwf_belegar\dwf_belegar_hammer_1h_01.wsmodel",
                "weapon_1",
                "be_prop_1",
                "be_prop_1",
                "0.75",
                "true"),
            3);

        var sourceDefinition = new VariantMeshDefinition.VariantMesh
        {
            MetaDataList = [new VariantMeshDefinition.MetaData { Value = "equipment" }]
        };

        var build = exporterType.GetMethod(
            "BuildFlattenedVariantMesh",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("BuildFlattenedVariantMesh was not found.");
        var bytes = (byte[])(build.Invoke(null, [components, sourceDefinition])
            ?? throw new AssertionException("BuildFlattenedVariantMesh returned null."));

        var document = new XmlDocument();
        document.LoadXml(Encoding.UTF8.GetString(bytes));
        var root = document.DocumentElement!;

        Assert.That(
            root.GetAttribute("model"),
            Is.EqualTo(@"variantmeshes\wh_variantmodels\hu3\dwf\dwf_belegar\dwf_belegar_head_01.wsmodel"));

        var slots = root.SelectNodes("SLOT")!.Cast<XmlElement>().ToArray();
        Assert.That(slots.Select(slot => slot.GetAttribute("name")), Is.EqualTo(new[] { "torso", "legs", "weapon_1" }));
        Assert.That(slots[0].HasAttribute("attach_point"), Is.False);
        Assert.That(slots[0].HasAttribute("probability"), Is.False);
        Assert.That(slots[1].HasAttribute("attach_point"), Is.False);
        Assert.That(slots[1].HasAttribute("probability"), Is.False);
        Assert.That(
            slots[1].SelectSingleNode("VARIANT_MESH")!.Attributes!["model"]!.Value,
            Is.EqualTo(@"variantmeshes\wh_variantmodels\hu3\dwf\dwf_belegar\dwf_belegar_legs_01.wsmodel"));

        Assert.That(slots[2].GetAttribute("attach_point"), Is.EqualTo("be_prop_1"));
        Assert.That(slots[2].GetAttribute("probability"), Is.EqualTo("0.75"));
        Assert.That(slots[2].GetAttribute("use_different_attach_point_parts"), Is.EqualTo("true"));

        var xml = Encoding.UTF8.GetString(bytes);
        Assert.That(xml, Does.Not.Contain("whmm_painted_"));
        Assert.That(xml, Does.Not.Contain("probability=\"1\""));
    }
}
