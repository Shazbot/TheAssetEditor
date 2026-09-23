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
    [Test]
    public void MaterialRewrite_NeutralizesFactionMaskWhenBaseColourIsPainted()
    {
        var exporterType = typeof(AssetHostProtocol).Assembly.GetType(
            "WH3AssetHost.PaintedVariantExporter",
            throwOnError: true)!;
        var rewrite = exporterType.GetMethod(
            "RewriteMaterialTextureReferences",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("RewriteMaterialTextureReferences was not found.");

        var document = new XmlDocument();
        document.LoadXml("""
            <material>
              <textures>
                <texture>
                  <slot version="2">t_xml_base_colour</slot>
                  <source>variantmeshes/source_base.dds</source>
                </texture>
                <texture>
                  <slot version="2">t_xml_mask</slot>
                  <source>variantmeshes/source_mask.dds</source>
                </texture>
                <texture>
                  <slot version="2">t_xml_normal</slot>
                  <source>variantmeshes/source_normal.dds</source>
                </texture>
              </textures>
            </material>
            """);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"variantmeshes\source_base.dds"] = @"variantmeshes\painted_base.dds"
        };
        var changed = (bool)(rewrite.Invoke(null, [document, replacements])
            ?? throw new AssertionException("RewriteMaterialTextureReferences returned null."));

        Assert.That(changed, Is.True);
        Assert.That(
            document.SelectSingleNode("//texture[slot='t_xml_base_colour']/source")!.InnerText,
            Is.EqualTo(@"variantmeshes\painted_base.dds"));
        Assert.That(
            document.SelectSingleNode("//texture[slot='t_xml_mask']/source")!.InnerText,
            Is.EqualTo("commontextures/default_black.dds"));
        Assert.That(
            document.SelectSingleNode("//texture[slot='t_xml_normal']/source")!.InnerText,
            Is.EqualTo("variantmeshes/source_normal.dds"));
    }

    [Test]
    public void MaterialRewrite_PreservesFactionMaskWhenOnlyNormalIsPainted()
    {
        var exporterType = typeof(AssetHostProtocol).Assembly.GetType(
            "WH3AssetHost.PaintedVariantExporter",
            throwOnError: true)!;
        var rewrite = exporterType.GetMethod(
            "RewriteMaterialTextureReferences",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("RewriteMaterialTextureReferences was not found.");

        var document = new XmlDocument();
        document.LoadXml("""
            <material>
              <textures>
                <texture>
                  <slot version="2">t_xml_base_colour</slot>
                  <source>variantmeshes/source_base.dds</source>
                </texture>
                <texture>
                  <slot version="2">t_xml_mask</slot>
                  <source>variantmeshes/source_mask.dds</source>
                </texture>
                <texture>
                  <slot version="2">t_xml_normal</slot>
                  <source>variantmeshes/source_normal.dds</source>
                </texture>
              </textures>
            </material>
            """);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"variantmeshes\source_normal.dds"] = @"variantmeshes\painted_normal.dds"
        };
        var changed = (bool)(rewrite.Invoke(null, [document, replacements])
            ?? throw new AssertionException("RewriteMaterialTextureReferences returned null."));

        Assert.That(changed, Is.True);
        Assert.That(
            document.SelectSingleNode("//texture[slot='t_xml_normal']/source")!.InnerText,
            Is.EqualTo(@"variantmeshes\painted_normal.dds"));
        Assert.That(
            document.SelectSingleNode("//texture[slot='t_xml_mask']/source")!.InnerText,
            Is.EqualTo("variantmeshes/source_mask.dds"));
    }

    [Test]
    public void VariantMeshRewrite_PreservesAllVariantsStumpsMetadataAndAttachPoints()
    {
        var exporterType = typeof(AssetHostProtocol).Assembly.GetType(
            "WH3AssetHost.PaintedVariantExporter",
            throwOnError: true)!;
        var rewrite = exporterType.GetMethod(
            "RewriteVariantMeshDocument",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertionException("RewriteVariantMeshDocument was not found.");

        var document = new XmlDocument();
        document.LoadXml("""
            <VARIANT_MESH>
              <SLOT name="body">
                <VARIANT_MESH_REFERENCE definition="VariantMeshes/VariantMeshDefinitions/emp_state_troops_base.VariantMeshDefinition" />
              </SLOT>
              <SLOT name="weapon_1" attach_point="be_prop_0" />
              <SLOT name="shield" attach_point="be_prop_2">
                <VARIANT_MESH_REFERENCE definition="VariantMeshes/VariantMeshDefinitions/emp_state_troops_shields_set1.VariantMeshDefinition" />
              </SLOT>
              <SLOT name="torso">
                <VARIANT_MESH model="VariantMeshes/torso_01.wsmodel">
                  <META_DATA>audio_armour_type:plate</META_DATA>
                </VARIANT_MESH>
                <VARIANT_MESH model="VariantMeshes/torso_02.wsmodel" />
                <VARIANT_MESH model="VariantMeshes/torso_03.wsmodel" />
              </SLOT>
              <SLOT attach_point="spine_2" name="stump_neck">
                <VARIANT_MESH model="VariantMeshes/stump_neck.rigid_model_v2">
                  <META_DATA>equipment</META_DATA>
                  <META_DATA>is_stump</META_DATA>
                </VARIANT_MESH>
              </SLOT>
            </VARIANT_MESH>
            """);

        string RewriteModel(string path)
            => path.EndsWith("torso_01.wsmodel", StringComparison.OrdinalIgnoreCase)
                ? @"variantmeshes\whmm_unit_painter\painted\models\torso_01.wsmodel"
                : path;
        string RewriteDefinition(string path)
            => path.Contains("emp_state_troops_base", StringComparison.OrdinalIgnoreCase)
                ? @"variantmeshes\whmm_unit_painter\painted\variantmeshdefinitions\emp_state_troops_base.variantmeshdefinition"
                : path;

        var changed = (bool)(rewrite.Invoke(
            null,
            [document, (Func<string, string>)RewriteModel, (Func<string, string>)RewriteDefinition])
            ?? throw new AssertionException("RewriteVariantMeshDocument returned null."));

        Assert.That(changed, Is.True);
        Assert.That(document.SelectNodes("//SLOT[@name='torso']/VARIANT_MESH")!.Count, Is.EqualTo(3));
        Assert.That(
            document.SelectSingleNode("//SLOT[@name='torso']/VARIANT_MESH[1]")!.Attributes!["model"]!.Value,
            Is.EqualTo(@"variantmeshes\whmm_unit_painter\painted\models\torso_01.wsmodel"));
        Assert.That(
            document.SelectSingleNode("//SLOT[@name='torso']/VARIANT_MESH[2]")!.Attributes!["model"]!.Value,
            Is.EqualTo("VariantMeshes/torso_02.wsmodel"));
        Assert.That(
            document.SelectSingleNode("//SLOT[@name='torso']/VARIANT_MESH[1]/META_DATA")!.InnerText,
            Is.EqualTo("audio_armour_type:plate"));

        var stumpSlot = (XmlElement)document.SelectSingleNode("//SLOT[@name='stump_neck']")!;
        Assert.That(stumpSlot.GetAttribute("attach_point"), Is.EqualTo("spine_2"));
        Assert.That(stumpSlot.SelectNodes("VARIANT_MESH/META_DATA")!.Count, Is.EqualTo(2));
        Assert.That(
            stumpSlot.SelectSingleNode("VARIANT_MESH")!.Attributes!["model"]!.Value,
            Is.EqualTo("VariantMeshes/stump_neck.rigid_model_v2"));

        var weaponSlot = (XmlElement)document.SelectSingleNode("//SLOT[@name='weapon_1']")!;
        Assert.That(weaponSlot.GetAttribute("attach_point"), Is.EqualTo("be_prop_0"));
        Assert.That(weaponSlot.ChildNodes.Count, Is.EqualTo(0));

        Assert.That(
            document.SelectSingleNode("//SLOT[@name='body']/VARIANT_MESH_REFERENCE")!.Attributes!["definition"]!.Value,
            Is.EqualTo(@"variantmeshes\whmm_unit_painter\painted\variantmeshdefinitions\emp_state_troops_base.variantmeshdefinition"));
        Assert.That(
            document.SelectSingleNode("//SLOT[@name='shield']/VARIANT_MESH_REFERENCE")!.Attributes!["definition"]!.Value,
            Is.EqualTo("VariantMeshes/VariantMeshDefinitions/emp_state_troops_shields_set1.VariantMeshDefinition"));
    }


}
