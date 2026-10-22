using System.Reflection;
using System.Text;
using Editors.PackOptimizer.Core;
using Shared.Core.PackFiles.Models;
using Shared.Core.PackFiles.Models.Containers;
using Shared.Core.PackFiles.Models.FileSources;

namespace Test.PackOptimizer.Core
{
    // A mod split over several packs: files the source references but does not contain are
    // resolved from the packs that have a dependency relationship with it (and only those).
    public class ResolutionProviderTests
    {
        private const string Rigid = "variantmeshes\\wh_variantmodels\\hu1\\gone.rigid_model_v2";
        private const string Texture = "variantmeshes\\textures\\only_in_dep.dds";

        private static Type ServiceType => Assembly.Load("Editors.PackOptimizer.Core")
            .GetType("Editors.PackOptimizer.Core.PackTextureAtlasBatchService", throwOnError: true)!;

        private static Type Nested(string name)
            => ServiceType.GetNestedType(name, BindingFlags.NonPublic)
               ?? throw new InvalidOperationException(name);

        private static PackFileContainer Pack(string name, params (string Path, string Content)[] files)
        {
            var pack = PackFileContainer.CreatePackFile(name);
            foreach (var (path, content) in files)
            {
                pack.AddFiles([new NewPackFileEntry(
                    Path.GetDirectoryName(path)!,
                    new PackFile(Path.GetFileName(path), new MemorySource(Encoding.UTF8.GetBytes(content))))]);
            }

            return pack;
        }

        private static object Providers(params IPackFileContainer[] containers)
            => Activator.CreateInstance(
                Nested("ResolutionProviderSet"),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [(IReadOnlyList<IPackFileContainer>)containers],
                null)!;

        private static bool Supplied(object providers, IPackFileContainer container)
            => (bool)providers.GetType().GetMethod("Supplied")!.Invoke(providers, [container])!;

        private static IPackFileContainer? Resolve(
            IPackFileContainer source,
            IReadOnlyList<IPackFileContainer> ca,
            string path,
            object? providers)
            => (IPackFileContainer?)ServiceType
                .GetMethod(
                    "ResolveGameplayTraversalContainer",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    [typeof(IPackFileContainer), typeof(IReadOnlyList<IPackFileContainer>), typeof(string), Nested("ResolutionProviderSet")],
                    null)!
                .Invoke(null, [source, ca, path, providers]);

        [Test]
        public void Resolve_FileOnlyInDependencyPack_ComesFromTheDependency_AndMarksItSupplied()
        {
            var source = Pack("source", ("variantmeshes\\a.wsmodel", "<model />"));
            var dependency = Pack("dependency", (Rigid, "rigid"));
            var providers = Providers(dependency);
            var ca = Pack("ca");

            Assert.That(Supplied(providers, dependency), Is.False);
            Assert.That(Resolve(source, [ca], Rigid, providers), Is.SameAs(dependency));
            Assert.That(Supplied(providers, dependency), Is.True);
        }

        [Test]
        public void Resolve_SourceWinsOverProviders_AndLastProviderWins()
        {
            var source = Pack("source", (Rigid, "source"));
            var first = Pack("first", (Rigid, "first"), (Texture, "first"));
            var last = Pack("last", (Texture, "last"));
            var providers = Providers(first, last);
            var ca = Pack("ca");

            Assert.That(Resolve(source, [ca], Rigid, providers), Is.SameAs(source));
            Assert.That(Supplied(providers, first), Is.False, "a file the source owns is not provided");
            Assert.That(Resolve(source, [ca], Texture, providers), Is.SameAs(last));
            Assert.That(Supplied(providers, last), Is.True);
            Assert.That(Supplied(providers, first), Is.False);
        }

        [Test]
        public void Resolve_FileInNonDependencyPack_IsNotUsed()
        {
            var source = Pack("source");
            var unrelated = Pack("unrelated", (Rigid, "other mod"));
            var dependency = Pack("dependency", (Texture, "dep"));
            var providers = Providers(dependency);
            var ca = Pack("ca");

            Assert.That(Resolve(source, [ca], Rigid, providers), Is.Null);
            Assert.That(Resolve(source, [ca], Rigid, null), Is.Null);
            Assert.That(unrelated.ContainsFile(Rigid), Is.True);
            Assert.That(Supplied(providers, dependency), Is.False);
        }

        [Test]
        public void FindForRead_ConsultsProvidersAfterSourceBeforeGamePacks()
        {
            var source = Pack("source");
            var output = Pack("output");
            var dependency = Pack("dependency", (Texture, "dep"));
            var state = NewState(source, output);
            state.GetType().GetProperty("ResolutionProviders")!.SetValue(state, Providers(dependency));

            var found = (PackFile?)ServiceType
                .GetMethod("FindForReadStatic", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [state, Texture]);

            Assert.That(found, Is.Not.Null);
            Assert.That(Encoding.UTF8.GetString(found!.DataSource.ReadData()), Is.EqualTo("dep"));
        }

        [Test]
        public void DescribeConsumerPacks_ProviderIsRelevantOnlyWhenItSuppliedAFile()
        {
            var source = Pack("source");
            var supplier = Pack("supplier", (Texture, "dep"));
            var idleDependency = Pack("idle_dependency", ("text\\readme.txt", "x"));
            var unrelated = Pack("unrelated", ("text\\readme2.txt", "x"));
            IReadOnlyList<IPackFileContainer> consumers = [supplier, idleDependency, unrelated];
            var index = ServiceType.GetMethod("BuildConsumerReferenceIndexFor", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [source, consumers, 0, CancellationToken.None, null])!;
            var providers = Providers(supplier, idleDependency);
            Resolve(source, [Pack("ca")], Texture, providers);

            var described = ((IEnumerable<PackOptimizerConsumerPack>)ServiceType
                .GetMethod("DescribeConsumerPacks", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [index, providers])!).ToList();

            Assert.That(described.Select(pack => (pack.Name, pack.Relevant, pack.ResolutionProvider)), Is.EqualTo(new[]
            {
                ("supplier", true, true),
                ("idle_dependency", false, false),
                ("unrelated", false, false),
            }));
        }

        private static object NewState(IPackFileContainer source, IPackFileContainer output)
            => Nested("BatchState").GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single()
                .Invoke([source, output, new Moq.Mock<IPackOptimizerPackFiles>().Object, string.Empty, string.Empty, string.Empty, false, false, false]);
    }
}
