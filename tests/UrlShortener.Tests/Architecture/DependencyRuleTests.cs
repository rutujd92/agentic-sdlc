using System.Xml.Linq;

namespace UrlShortener.Tests.Architecture;

// Enforces the CLAUDE.md rule: dependencies point inward (Api -> Infrastructure -> Core).
// Reads <ProjectReference> items from the .csproj files, because the compiler omits
// referenced assemblies from metadata when none of their types are used yet.
public class DependencyRuleTests
{
    private const string Core = "UrlShortener.Core";
    private const string Infrastructure = "UrlShortener.Infrastructure";
    private const string Api = "UrlShortener.Api";

    [Fact]
    public void Core_references_no_other_solution_project()
    {
        Assert.Empty(ProjectReferences(Core));
    }

    [Fact]
    public void Infrastructure_references_only_core()
    {
        Assert.Equal([Core], ProjectReferences(Infrastructure));
    }

    [Fact]
    public void Api_references_infrastructure_and_core()
    {
        Assert.Equal([Core, Infrastructure], ProjectReferences(Api));
    }

    private static string[] ProjectReferences(string project)
    {
        var csproj = Path.Combine(RepositoryRoot(), "src", project, $"{project}.csproj");
        Assert.True(File.Exists(csproj), $"Project file not found: {csproj}");

        return XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UrlShortener.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("UrlShortener.sln not found above test output.");
    }
}
