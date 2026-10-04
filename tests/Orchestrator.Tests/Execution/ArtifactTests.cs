using Orchestrator.Core.Execution;

namespace Orchestrator.Tests.Execution;

public class ArtifactTests
{
    [Fact]
    public void Hash_is_stable_sha256_of_content()
    {
        Assert.Equal(Artifact.Create("a", "hello").Hash, Artifact.Create("b", "hello").Hash);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", Artifact.Create("a", "hello").Hash);
        Assert.NotEqual(Artifact.Create("a", "hello").Hash, Artifact.Create("a", "hello!").Hash);
    }
}
