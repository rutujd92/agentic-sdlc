using Orchestrator.Core.Governance;

namespace Orchestrator.Tests.Governance;

public class PolicyEngineTests
{
    private static readonly PolicyRules Rules = new()
    {
        AllowedPaths = ["src/**", "tests/**", "docs/**", "README.md"],
        ApprovalPaths = ["**/Migrations/**"],
        BlockedPaths = [".github/**", "**/*.pem"],
        SecretPatterns = ["(?i)password\\s*=\\s*[^;\\s\"]{4,}", "AKIA[0-9A-Z]{16}"],
        AllowedLicenses = ["MIT", "Apache-2.0"],
        PackageLicenses = new Dictionary<string, string> { ["QRCoder"] = "MIT", ["GplThing"] = "GPL-3.0" },
    };

    // Assembled at runtime so the repository itself never contains a credential-shaped literal (keeps gitleaks strict).
    private static readonly string FakeAwsKey = "AKIA" + "ABCDEFGHIJKLMNOP";

    private readonly PolicyEngine _engine = new(Rules);

    [Fact]
    public void Allows_change_within_allowed_paths()
    {
        var decision = _engine.Evaluate(Diffs.File("src/UrlShortener.Api/Program.cs", "var x = 1;"));

        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.Empty(decision.Findings);
    }

    [Fact]
    public void Allows_non_diff_artifacts()
    {
        Assert.Equal(PolicyOutcome.Allow, _engine.Evaluate("# Requirements\n- acceptance criteria").Outcome);
    }

    [Fact]
    public void Blocks_change_outside_allowed_paths()
    {
        var decision = _engine.Evaluate(Diffs.File("deploy/prod.sh", "rm -rf /"));

        Assert.Equal(PolicyOutcome.Block, decision.Outcome);
        Assert.Contains(decision.Findings, f => f.Rule == "allowed-paths" && f.Message.Contains("deploy/prod.sh", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocks_change_to_blocked_path_even_if_otherwise_allowed()
    {
        Assert.Equal(PolicyOutcome.Block, _engine.Evaluate(Diffs.File(".github/workflows/ci.yml", "on: push")).Outcome);
        Assert.Equal(PolicyOutcome.Block, _engine.Evaluate(Diffs.File("src/certs/key.pem", "x")).Outcome);
    }

    [Fact]
    public void Requires_approval_for_database_migrations()
    {
        var decision = _engine.Evaluate(Diffs.File("src/UrlShortener.Infrastructure/Persistence/Migrations/20261004_AddClicks.cs", "CreateTable"));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(decision.Findings, f => f.Rule == "approval-paths");
    }

    [Fact]
    public void Blocks_secrets_in_added_lines_only()
    {
        var added = _engine.Evaluate(Diffs.File("src/appsettings.json", "\"Db\": \"Host=x;Password=hunter22\""));
        var removed = _engine.Evaluate("diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n-Password=hunter22\n+// cleaned\n");

        Assert.Equal(PolicyOutcome.Block, added.Outcome);
        Assert.Contains(added.Findings, f => f.Rule == "secrets");
        Assert.Equal(PolicyOutcome.Allow, removed.Outcome);
    }

    [Fact]
    public void Secret_finding_does_not_echo_the_secret()
    {
        var decision = _engine.Evaluate(Diffs.File("src/a.cs", $"var key = \"{FakeAwsKey}\";"));

        Assert.Contains(decision.Findings, f => f.Rule == "secrets");
        Assert.DoesNotContain(decision.Findings, f => f.Message.Contains(FakeAwsKey, StringComparison.Ordinal));
    }

    [Fact]
    public void New_package_with_allowed_license_requires_approval()
    {
        var decision = _engine.Evaluate(Diffs.File("src/UrlShortener.Api/UrlShortener.Api.csproj", "<PackageReference Include=\"QRCoder\" Version=\"1.6.0\" />"));

        Assert.Equal(PolicyOutcome.RequireApproval, decision.Outcome);
        Assert.Contains(decision.Findings, f => f.Rule == "new-dependency" && f.Message.Contains("QRCoder (MIT)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GplThing")]
    [InlineData("UnknownPkg")]
    public void Blocks_package_with_disallowed_or_unknown_license(string package)
    {
        var decision = _engine.Evaluate(Diffs.File("src/a/a.csproj", $"<PackageReference Include=\"{package}\" Version=\"1.0.0\" />"));

        Assert.Equal(PolicyOutcome.Block, decision.Outcome);
        Assert.Contains(decision.Findings, f => f.Rule == "licenses");
    }

    [Fact]
    public void Block_outranks_approval_and_all_findings_are_reported()
    {
        var decision = _engine.Evaluate(Diffs.Combine(
            Diffs.File("src/x/Migrations/m.cs", "CreateTable"),
            Diffs.File("deploy/x.sh", "echo")));

        Assert.Equal(PolicyOutcome.Block, decision.Outcome);
        Assert.Equal(2, decision.Findings.Count);
    }

    [Theory]
    [InlineData("src/**", "src/a/b/c.cs", true)]
    [InlineData("src/**", "srcx/a.cs", false)]
    [InlineData("**/Migrations/**", "src/Infra/Migrations/x.cs", true)]
    [InlineData("**/Migrations/**", "Migrations/x.cs", true)]
    [InlineData("**/*.pem", "key.pem", true)]
    [InlineData("README.md", "docs/README.md", false)]
    [InlineData("docs/*.md", "docs/a/b.md", false)]
    public void Glob_matching(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(pattern, path));
    }
}
