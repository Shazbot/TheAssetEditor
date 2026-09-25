using System.Reflection;

namespace Test.KitbashEditor.Services
{
    public class Wh3UnitCategoryResolverTests
    {
        private static string Classify(string caste, string landCategory, string uiGroupKey = "")
        {
            var assembly = Assembly.Load("Editors.KitbasherEditor");
            var resolverType = assembly.GetType(
                "Editors.KitbasherEditor.Services.Wh3UnitCategoryResolver",
                throwOnError: true)!;
            var method = resolverType.GetMethod(
                "Classify",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Wh3UnitCategoryResolver.Classify was not found.");

            return method.Invoke(null, [caste, landCategory, uiGroupKey])?.ToString()
                ?? throw new InvalidOperationException("Wh3UnitCategoryResolver.Classify returned null.");
        }

        [TestCase("melee_cavalry", "war_beast", "CavalryChariot")]
        [TestCase("missile_cavalry", "inf_ranged", "CavalryChariot")]
        [TestCase("chariot", "war_machine", "CavalryChariot")]
        [TestCase("monster", "inf_melee", "MonsterBeast")]
        [TestCase("monstrous_infantry", "inf_melee", "MonsterBeast")]
        [TestCase("war_beast", "inf_melee", "MonsterBeast")]
        [TestCase("warmachine", "artillery", "ArtilleryWarMachine")]
        [TestCase("melee_infantry", "war_beast", "InfantryMissile")]
        public void CasteTakesPrecedenceOverLandCategory(
            string caste,
            string landCategory,
            string expected)
        {
            Assert.That(Classify(caste, landCategory), Is.EqualTo(expected));
        }

        [TestCase("missile_infantry", "inf_ranged", "monster_beasts", "MonsterBeast")]
        [TestCase("melee_infantry", "inf_melee", "cavalry_chariots", "CavalryChariot")]
        [TestCase("monster", "inf_melee", "artillery_war_machines", "ArtilleryWarMachine")]
        public void UnitViewerUiGroupTakesPrecedence(
            string caste,
            string landCategory,
            string uiGroupKey,
            string expected)
        {
            Assert.That(Classify(caste, landCategory, uiGroupKey), Is.EqualTo(expected));
        }

        [TestCase("lord", "war_machine", "Lord")]
        [TestCase("hero", "cavalry", "Hero")]
        public void CharacterCasteAlwaysWins(
            string caste,
            string landCategory,
            string expected)
        {
            Assert.That(Classify(caste, landCategory), Is.EqualTo(expected));
        }
    }
}
