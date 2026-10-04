using System.Xml.Linq;

namespace Orchestrator.Tests.Architecture;

// Same inward rule as the shortener: Cli -> Infrastructure -> Core, Core references nothing.
public class DependencyRuleTests
{
    [Fact]
    public void Core_references_no_other_solution_project() =>
        Assert.Empty(ProjectReferences("Orchestrator.Core"));

    [Fact]
    public void Infrastructure_references_only_core() =>
        Assert.Equal(["Orchestrator.Core"], ProjectReferences("Orchestrator.Infrastructure"));

    [Fact]
    public void Cli_references_infrastructure_and_core() =>
        Assert.Equal(["Orchestrator.Core", "Orchestrator.Infrastructure"], ProjectReferences("Orchestrator.Cli"));

    private static string[] ProjectReferences(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UrlShortener.sln")))
        {
            directory = directory.Parent;
        }

        var csproj = Path.Combine(directory!.FullName, "src", project, $"{project}.csproj");
        return XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
