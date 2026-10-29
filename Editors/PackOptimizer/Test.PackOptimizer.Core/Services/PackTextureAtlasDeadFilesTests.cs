using System.Collections;
using System.Reflection;
using System.Text;
using Shared.GameFormats.RigidModel.Types;

namespace Test.PackOptimizer.Core
{
    // Opt-in dead-file pruning (PackTextureAtlasBatchService.DeadFiles.cs).
    public partial class PackTextureAtlasCrossRigidTests
    {
        private static byte[] DfBc1() => BuildBcDds(64, 64, 7, "BC1", dx10: true, srgb: true);

        private static void DfRun(
            DownscaleFixture fixture,
            object state,
            object service,
            params string[] reachable)
        {
            SetStateProperty(state, "PruneDeadFiles", true);
            GetServiceType()
                .GetMethod("PruneDeadFiles", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(
                    service,
                    [state, new HashSet<string>(reachable, StringComparer.OrdinalIgnoreCase), CancellationToken.None, null]);
        }

        private static object DfResult(object state) => Get<object>(state, "DeadFiles");

        private static List<string> DfPaths(object state, string list)
            => Get<IEnumerable>(DfResult(state), list).Cast<object>().Select(item => Get<string>(item, "Path")).ToList();

        private static string DfKeptReason(object state, string path)
            => Get<IEnumerable>(DfResult(state), "Kept").Cast<object>()
                .Single(item => Get<string>(item, "Path") == path) is { } kept
                ? Get<string>(kept, "KeptReason") + " | " + Get<string>(kept, "KeptDetail")
                : string.Empty;

        [Test]
        public void DeadFiles_UnreferencedDdsPsdAndRigidAreRemoved_ReachableAndGeneratedStay()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\df_used.dds", DfBc1());
            fixture.Add("textures\\df_leftover.dds", DfBc1());
            fixture.Add("textures\\remove___df_old.dds", DfBc1());
            fixture.Add("ui\\flags\\df_flag.psd", [1, 2, 3, 4]);
            fixture.Add("art\\thumbs.db", [1]);
            fixture.Add("variantmeshes\\wh_main\\backup_df_body.rigid_model_v2", CxRigidBytes());
            fixture.AddText("variantmeshes\\wh_main\\df_live.variantmeshdefinition", "<VARIANT_MESH/>");
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service, "textures\\df_used.dds");

            var removed = DfPaths(state, "Removed");
            Assert.That(removed, Is.EquivalentTo(new[]
            {
                "textures\\df_leftover.dds",
                "textures\\remove___df_old.dds",
                "ui\\flags\\df_flag.psd",
                "art\\thumbs.db",
                "variantmeshes\\wh_main\\backup_df_body.rigid_model_v2",
            }));
            Assert.That(fixture.Output.Keys, Does.Contain("textures\\df_used.dds"));
            Assert.That(fixture.Output.Keys, Does.Contain("variantmeshes\\wh_main\\df_live.variantmeshdefinition"));
            Assert.That(fixture.Output.Keys, Does.Not.Contain("textures\\df_leftover.dds"));
            Assert.That(fixture.Output.Keys, Does.Not.Contain("ui\\flags\\df_flag.psd"));
            Assert.That(Get<IEnumerable<string>>(state, "RemovedFiles"), Is.SupersetOf(removed));
            Assert.That(Get<string?>(DfResult(state), "Failure"), Is.Null);
        }

        [Test]
        public void DeadFiles_StemMentionedInADbBlob_InUtf8OrUtf16_KeepsTheFile()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\df_utf8_tex.dds", DfBc1());
            fixture.Add("textures\\df_utf16_tex.dds", DfBc1());
            fixture.Add("textures\\df_nobody.dds", DfBc1());
            fixture.Add("db\\units_tables\\df_table", Encoding.UTF8.GetBytes("\0\0row VariantMeshes/Textures/DF_UTF8_TEX.dds \0"));
            fixture.Add("db\\other_tables\\df_table", Encoding.Unicode.GetBytes("row textures\\df_utf16_tex.dds end"));
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[] { "textures\\df_nobody.dds" }));
            Assert.That(fixture.Output.Keys, Does.Contain("textures\\df_utf8_tex.dds"));
            Assert.That(fixture.Output.Keys, Does.Contain("textures\\df_utf16_tex.dds"));
            Assert.That(DfKeptReason(state, "textures\\df_utf8_tex.dds"), Does.Contain("db\\units_tables\\df_table"));
            Assert.That(DfKeptReason(state, "textures\\df_utf16_tex.dds"), Does.Contain("db\\other_tables\\df_table"));
        }

        [Test]
        public void DeadFiles_VanillaOverride_IsKept_AndWithoutVanillaDataEverythingIsKept()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\df_override.dds", DfBc1());
            fixture.Vanilla["textures\\df_override.dds"] = [1];
            fixture.Add("textures\\df_modonly.dds", DfBc1());
            fixture.Add("art\\df_model.blend", [1]);
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[] { "textures\\df_modonly.dds", "art\\df_model.blend" }));
            Assert.That(DfKeptReason(state, "textures\\df_override.dds"), Does.Contain("overrides a vanilla file"));

            var other = new DownscaleFixture();
            other.Add("textures\\df_modonly.dds", DfBc1());
            var (otherService, otherState) = CreateDownscaleState(other, downscale: false);
            SetStateProperty(otherState, "GameplayTraversalContainers", new List<Shared.Core.PackFiles.Models.IPackFileContainer>());
            DfRun(other, otherState, otherService);
            Assert.That(DfPaths(otherState, "Removed"), Is.Empty);
            Assert.That(DfKeptReason(otherState, "textures\\df_modonly.dds"), Does.Contain("not loaded"));
        }

        [Test]
        public void DeadFiles_AnotherModsPathOrMention_KeepsTheFile()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\df_used_by_mod.dds", DfBc1());
            fixture.Add("textures\\df_named_in_db.dds", DfBc1());
            fixture.Add("textures\\df_free.dds", DfBc1());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);
            InstallConsumers(
                state,
                0,
                ("OtherMod", ConsumerFiles(
                    ("materials\\mod.xml.material", "<material><textures><texture><source>textures\\df_used_by_mod.dds</source></texture></textures></material>"),
                    ("db\\tbl_tables\\data", "df_named_in_db"))));

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[] { "textures\\df_free.dds" }));
            Assert.That(DfKeptReason(state, "textures\\df_used_by_mod.dds"), Does.Contain("another mod"));
            Assert.That(DfKeptReason(state, "textures\\df_named_in_db.dds"), Does.Contain("another mod's file").And.Contain("OtherMod"));
        }

        [Test]
        public void DeadFiles_FilesInFoldersLoadedByName_AreOnlyReported()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("ui\\loading_screens\\df_screen.dds", DfBc1());
            fixture.Add("textures\\df_dead.dds", DfBc1());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[] { "textures\\df_dead.dds" }));
            Assert.That(DfPaths(state, "ReportOnly"), Is.EquivalentTo(new[] { "ui\\loading_screens\\df_screen.dds" }));
            Assert.That(fixture.Output.Keys, Does.Contain("ui\\loading_screens\\df_screen.dds"));
            var builder = new StringBuilder();
            GetStaticMethod("AppendDeadFilesSection").Invoke(null, [builder, state]);
            Assert.That(builder.ToString(), Does.Contain("Dead files").And.Contain("ui\\loading_screens\\df_screen.dds"));
        }

        [Test]
        public void DeadFiles_ChainWsModelRigidMaterialTexture_IsRemovedTogether()
        {
            var fixture = new DownscaleFixture();
            fixture.AddText(
                "variantmeshes\\dead\\dc_ws.wsmodel",
                "<model><geometry>variantmeshes\\dead\\dc_geo.rigid_model_v2</geometry><materials><material lod_index=\"0\" part_index=\"0\">materials\\dc_mat.xml.material</material></materials></model>");
            fixture.Add("variantmeshes\\dead\\dc_geo.rigid_model_v2", CxRigidBytes((TextureType.Diffuse, "textures\\dc_tex.dds")));
            fixture.AddMaterial("materials\\dc_mat.xml.material", ("t_xml_base_colour", "textures\\dc_tex.dds"));
            fixture.Add("textures\\dc_tex.dds", DfBc1());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[]
            {
                "variantmeshes\\dead\\dc_ws.wsmodel",
                "variantmeshes\\dead\\dc_geo.rigid_model_v2",
                "materials\\dc_mat.xml.material",
                "textures\\dc_tex.dds",
            }));
            Assert.That(DfPaths(state, "Kept"), Is.Empty);
        }

        [Test]
        public void DeadFiles_TextureNamedByAKeptRigidsEmbeddedMaterial_IsKept_AlsoAcrossRounds()
        {
            var fixture = new DownscaleFixture();
            fixture.AddText(
                "variantmeshes\\live\\kr_ws.wsmodel",
                "<model><geometry>variantmeshes\\live\\kr_geo.rigid_model_v2</geometry></model>");
            // Reachable rigid: its embedded texture is named in the material the rigid carries.
            fixture.Add("variantmeshes\\live\\kr_live.rigid_model_v2", CxRigidBytes((TextureType.Diffuse, "textures\\kr_embedded.dds")));
            fixture.Add("textures\\kr_embedded.dds", DfBc1());
            // Not reachable by the VMD walk, but the kept WSModel names the rigid; the rigid then names a texture.
            fixture.Add("variantmeshes\\live\\kr_geo.rigid_model_v2", CxRigidBytes((TextureType.Diffuse, "textures\\kr_second_round.dds")));
            fixture.Add("textures\\kr_second_round.dds", DfBc1());
            fixture.Add("textures\\kr_dead.dds", DfBc1());
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service, "variantmeshes\\live\\kr_ws.wsmodel", "variantmeshes\\live\\kr_live.rigid_model_v2");

            Assert.That(DfPaths(state, "Removed"), Is.EquivalentTo(new[] { "textures\\kr_dead.dds" }));
            Assert.That(DfKeptReason(state, "textures\\kr_embedded.dds"), Does.Contain("kr_live.rigid_model_v2"));
            Assert.That(DfKeptReason(state, "variantmeshes\\live\\kr_geo.rigid_model_v2"), Does.Contain("kr_ws.wsmodel"));
            Assert.That(DfKeptReason(state, "textures\\kr_second_round.dds"), Does.Contain("kr_geo.rigid_model_v2"));
            Assert.That(Get<int>(DfResult(state), "Rounds"), Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void DeadFiles_DamagedDdsCandidate_IsFlaggedInTheReport()
        {
            var fixture = new DownscaleFixture();
            fixture.Add("textures\\df_truncated.dds", DfBc1()[..200]);
            fixture.Add("textures\\df_garbage.dds", [9, 9, 9, 9, 9]);
            var (service, state) = CreateDownscaleState(fixture, downscale: false);

            DfRun(fixture, state, service);

            Assert.That(DfPaths(state, "Removed"), Has.Count.EqualTo(2));
            var problems = Get<IEnumerable>(DfResult(state), "DamagedDds").Cast<object>().Count();
            Assert.That(problems, Is.EqualTo(2));
            var builder = new StringBuilder();
            GetStaticMethod("AppendDeadFilesSection").Invoke(null, [builder, state]);
            Assert.That(builder.ToString(), Does.Contain("Damaged DDS among the candidates: 2"));
        }
    }
}
