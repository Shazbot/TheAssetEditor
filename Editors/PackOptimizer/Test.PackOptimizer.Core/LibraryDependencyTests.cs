using System.Reflection;
using Editors.PackOptimizer.Core;

namespace Test.PackOptimizer.Core
{
    /// <summary>
    /// The optimizer is meant to run in the headless asset host, so nothing it references,
    /// directly or transitively, may be a UI or renderer assembly.
    /// </summary>
    public class LibraryDependencyTests
    {
        private static readonly string[] ForbiddenPrefixes =
        [
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase",
            "System.Windows.Forms",
            "GameWorld.Core",
            "Shared.Ui",
            "Shared.Core,",
            "MonoGame",
            "SharpDX",
            "AvalonEdit",
        ];

        [Test]
        public void Library_DoesNotReferenceUiOrRendererAssemblies()
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offenders = new List<string>();
            var queue = new Queue<Assembly>();
            queue.Enqueue(typeof(Editors.PackOptimizer.Core.PackOptimizer).Assembly);

            while (queue.Count > 0)
            {
                var assembly = queue.Dequeue();
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    var fullName = reference.FullName;
                    if (!visited.Add(fullName))
                        continue;

                    if (ForbiddenPrefixes.Any(prefix => fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        offenders.Add($"{assembly.GetName().Name} -> {reference.Name}");
                        continue;
                    }

                    // Only follow our own assemblies; framework references are checked by name above.
                    if (reference.Name != null &&
                        (reference.Name.StartsWith("Editors.", StringComparison.Ordinal) ||
                         reference.Name.StartsWith("Shared.", StringComparison.Ordinal) ||
                         reference.Name.StartsWith("GameWorld.", StringComparison.Ordinal)))
                    {
                        try
                        {
                            queue.Enqueue(Assembly.Load(reference));
                        }
                        catch (FileNotFoundException)
                        {
                        }
                    }
                }
            }

            Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
        }

        [Test]
        public void RunningTheLibrary_DoesNotLoadWpf()
        {
            // Touch the public surface so the library and its dependencies are loaded.
            _ = typeof(PackOptimizerRequest).Assembly;
            _ = new PackOptimizerOptions();

            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetName().Name ?? string.Empty)
                .Where(name => name.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith("PresentationCore", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.That(loaded, Is.Empty);
        }
    }
}
