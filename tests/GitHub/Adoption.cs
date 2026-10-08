using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class SharedPublisherProvenanceContractTests
{
    [Fact]
    public void Publishers_UseOneStructuralNuspecParserAndNoRegexCommitExtraction()
    {
        var root = InfraRepositoryLocator.ResolveRoot();
        var sharedParser = Path.Combine(root, ".github", "package", "nuspec.py");
        Assert.True(File.Exists(sharedParser));
        foreach (var action in new[] { "nuget", "internal" })
        {
            var content = WorkflowShell.ReadAction(action);
            Assert.Contains("${{ github.action_path }}/../../package/nuspec.py", content, StringComparison.Ordinal);
            Assert.Contains("python3 \"$NUSPEC_PARSER\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("grep -oE 'commit=", content, StringComparison.Ordinal);
            Assert.Contains("does not contain exactly one nuspec", content, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(Path.Combine(root, ".github", "actions", "public")));
        Assert.False(Directory.Exists(Path.Combine(root, ".github", "actions", "private")));
    }

    [Fact]
    public void PublicDocumentation_HidesConcreteInternalRegistryBackend()
    {
        var root = InfraRepositoryLocator.ResolveRoot();
        foreach (var path in new[] { "README.md", Path.Combine("docs", "caller.md"), Path.Combine("docs", "provider.md"), Path.Combine(".github", "templates", "package.yml") })
        {
            var content = File.ReadAllText(Path.Combine(root, path));
            Assert.DoesNotContain("nuget.pkg.github.com", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GitHub Packages", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BootstrapAndAdoptionPolicy_RemainsDocumentedForBothConsumerAndProvider()
    {
        var root = InfraRepositoryLocator.ResolveRoot();
        var caller = Flatten(File.ReadAllText(Path.Combine(root, "docs", "caller.md")));
        var provider = Flatten(File.ReadAllText(Path.Combine(root, "docs", "provider.md")));

        Assert.Contains("gated on a migration operator inspecting every stable registry version", caller, StringComparison.Ordinal);
        Assert.Contains("Natural bootstrap — no stable line package and no matching line tag", caller, StringComparison.Ordinal);
        Assert.Contains("Adopt the highest proven version, then advance one patch", caller, StringComparison.Ordinal);
        Assert.Contains("Unprovable highest — migration stays disabled", caller, StringComparison.Ordinal);
        Assert.Contains("RepositoryCommit", caller, StringComparison.Ordinal);
        Assert.Contains("Gizmo.Shared/v1.0.13", caller, StringComparison.Ordinal);
        Assert.Contains("1.0.14-dev.N", caller, StringComparison.Ordinal);
        Assert.Contains("Gizmo.Shared/v1.0.14", caller, StringComparison.Ordinal);
        Assert.Contains("separate, deliberate, one-time caller action, outside the workflow", caller, StringComparison.Ordinal);

        Assert.Contains("gated on a migration operator inspecting all stable registry versions", provider, StringComparison.Ordinal);
        Assert.Contains("Natural bootstrap", provider, StringComparison.Ordinal);
        Assert.Contains("Migration stays disabled", provider, StringComparison.Ordinal);
        Assert.Contains("Gizmo.Shared/v1.0.13", provider, StringComparison.Ordinal);
        Assert.Contains("1.0.14-dev.N", provider, StringComparison.Ordinal);
        Assert.Contains("same-SHA recovery is the only automatic success", provider, StringComparison.Ordinal);
        Assert.Contains("at most 13 flat-container `GET` requests", provider, StringComparison.Ordinal);
        Assert.Contains("120-second total-delay cap", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void SteadyStateWorkflowAndActions_ContainNoMigrationOrAdoptionExecutionPath()
    {
        var root = InfraRepositoryLocator.ResolveRoot();
        var files = new[] { Path.Combine(".github", "workflows", "package.yml") }
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "actions"), "action.yml", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            var content = File.ReadAllText(Path.IsPathRooted(file) ? file : Path.Combine(root, file));
            foreach (var token in new[] { "adopt", "bootstrap", "migrat", "backfill", "synthetic" })
            {
                Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string Flatten(string content) =>
        string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
