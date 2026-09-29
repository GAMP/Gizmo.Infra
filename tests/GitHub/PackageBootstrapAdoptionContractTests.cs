using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract and executable coverage for the one-time existing-package
/// bootstrap/adoption boundary. Natural bootstrap (no stable registry package
/// <em>and</em> no matching line tag) and legacy adoption (existing stable line
/// versions without complete tag history) are documented operator situations, not
/// steady-state workflow features. The reusable workflows only derive the next
/// patch from the complete compatibility-line tag state and never query a
/// registry, and a publisher accepts an already-published calculated version only
/// when the package carries the exact caller commit as <c>RepositoryCommit</c>.
/// Every absent, malformed, foreign, conflicting, or partially proven state fails
/// closed, so adopting an existing stable package stays a separate deliberate
/// operator action documented outside the workflow.
/// </summary>
public sealed class PackageBootstrapAdoptionContractTests
{
    private static readonly string[] WorkflowFiles = ["package-publish.yml", "package-validation.yml"];

    private static readonly string[] ActionDirectories =
    [
        "package-preflight",
        "package-private-publish",
        "package-public-publish",
        "package-release-tag",
    ];

    // Legacy bootstrap/adoption or registry-migration branches must never enter
    // the steady-state workflow or actions; the generic empty-tag next-patch
    // derivation is the only bootstrap, and adoption stays an operator contract.
    private static readonly string[] LegacyTokens =
    [
        "adopt", "bootstrap", "migrat", "legacy", "synthetic", "backfill", "pre-existing", "seed",
    ];

    private static readonly string CurrentSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);

    private const string ProvenanceFailure = "no authenticated provenance for this caller SHA";

    private const string NuspecWithoutRepositoryCommit =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Gizmo.Widget</id>
            <version>3.0.0</version>
            <repository type="git" url="https://github.com/owner/repository" />
          </metadata>
        </package>
        """;

    [Theory]
    [InlineData("package-public-publish", "different")]
    [InlineData("package-public-publish", "absent")]
    [InlineData("package-private-publish", "different")]
    [InlineData("package-private-publish", "absent")]
    public void ExistingPackageWithoutCallerRepositoryCommitProvenance_FailsClosed(
        string actionDirectory,
        string kind)
    {
        var action = WorkflowShell.ReadAction(actionDirectory);
        Assert.Contains(ProvenanceFailure, action, StringComparison.Ordinal);

        // The committed guard refuses to publish or synthesize a recovery tag, so
        // an existing package can never be adopted as a side effect of a run.
        Assert.Contains("creating a recovery tag", action, StringComparison.Ordinal);

        var nuspec = kind == "absent" ? NuspecWithoutRepositoryCommit : Nuspec(OtherSha);
        var run = RunProvenanceGuard(actionDirectory, nuspec, CurrentSha);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(ProvenanceFailure, run.Result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("39-hex")]
    [InlineData("non-hex")]
    public void ExistingPackageWithMalformedRepositoryCommitProvenance_FailsClosed(string kind)
    {
        var commit = kind == "39-hex" ? new string('a', 39) : new string('z', 40);
        var run = RunProvenanceGuard("package-public-publish", Nuspec(commit), CurrentSha);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(ProvenanceFailure, run.Result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package-public-publish")]
    [InlineData("package-private-publish")]
    public void ExistingPackageWithExactCallerCommitProvenance_IsAcceptedAndNormalized(string actionDirectory)
    {
        // Uppercase hex must normalize to the lowercase caller SHA; this is the
        // exact same-SHA recovery the publisher relies on, and nothing else.
        var run = RunProvenanceGuard(actionDirectory, Nuspec(CurrentSha.ToUpperInvariant()), CurrentSha);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Equal(CurrentSha, run.RepositoryCommit);
    }

    [Fact]
    public void SteadyStateSurface_CarriesNoLegacyBootstrapOrAdoptionLogic()
    {
        foreach (var file in WorkflowFiles)
        {
            AssertNoLegacyTokens(WorkflowShell.ReadWorkflow(file));
        }

        foreach (var directory in ActionDirectories)
        {
            AssertNoLegacyTokens(WorkflowShell.ReadAction(directory));
        }
    }

    [Fact]
    public void PreparationWorkflows_DoNotCreateTags()
    {
        // "No synthetic tag": only the release-only tag action may write a ref.
        foreach (var file in WorkflowFiles)
        {
            var content = WorkflowShell.ReadWorkflow(file);
            Assert.DoesNotContain("git/refs", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--request POST", content, StringComparison.Ordinal);
            Assert.DoesNotContain("package-release-tag", content, StringComparison.Ordinal);
        }

        Assert.Contains("git/refs", WorkflowShell.ReadAction("package-release-tag"), StringComparison.Ordinal);
    }

    [Fact]
    public void NaturalBootstrap_RequiresNoStableRegistryPackageAndNoLineTag()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        // Bootstrap is the conjunction: the line starts at its first 3.X.0 only
        // when the registry has no stable package for the line *and* no matching
        // tag exists. "No tag" alone is not bootstrap.
        Assert.Contains(
            "the selected registry *and* no tag under `<package-id>/` for that line",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("derives the first `3.X.0`", caller, StringComparison.Ordinal);
        Assert.Contains(
            "the selected registry *and* no matching stable tag",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdoption_SelectsTheHighestStableLineVersion()
    {
        var caller = CallerDoc();

        // Adoption is not bootstrap: when the line already has published stable
        // versions, the candidate is the highest of them, never the first 3.X.0.
        Assert.Contains("The candidate is the highest stable", caller, StringComparison.Ordinal);
        Assert.Contains(
            "registry versions `3.X.2`, `3.X.4`, and `3.X.5`",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("line yield candidate `3.X.5`", caller, StringComparison.Ordinal);
        Assert.Contains("never the first `3.X.0`", caller, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdoption_EstablishesTheProvenHighestTagThenAdvancesToTheNextPatch()
    {
        var caller = CallerDoc();

        // A proven candidate 3.X.5 is tagged outside the workflow,
        // and the next release resumes one patch later at 3.X.6.
        Assert.Contains("Create the immutable package-qualified tag", caller, StringComparison.Ordinal);
        Assert.Contains(
            "outside the workflow. This is the one-time adoption.",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("next release claims `Y+1`", caller, StringComparison.Ordinal);
        Assert.Contains("resumes at the next stable `3.X.6`", caller, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdoption_UnprovenProvenanceFailsClosed()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        // Missing, malformed, or foreign provenance can never be adopted.
        Assert.Contains(
            "Missing, malformed, or foreign provenance fails closed",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("git cat-file -e <sha>^{commit}", caller, StringComparison.Ordinal);
        Assert.Contains(
            "conclusive `RepositoryCommit` provenance tied to a real caller commit",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("fails closed", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdoption_TagPackageConflictFailsClosed()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        // A tag and a package that disagree about the same version adopt nothing.
        Assert.Contains("tag/package conflict", caller, StringComparison.Ordinal);
        Assert.Contains("adopts nothing", caller, StringComparison.Ordinal);
        Assert.Contains("a tag/package version conflict", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdoption_LowerValidCandidateBelowUnprovenHigherVersionFailsClosed()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        // A valid lower candidate must not be tagged while a higher line version
        // remains unproven; the whole adoption fails closed instead.
        Assert.Contains(
            "valid lower candidate that sits below an unproven higher line version",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "lower candidate under an unproven higher line version",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapAndAdoption_ExcludeOtherCompatibilityLines()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        Assert.Contains(
            "Versions for another compatibility line are excluded",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "package or another compatibility line never",
            provider,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SteadyStatePublishing_IsTagDerivedAndAdoptsNothing()
    {
        var caller = CallerDoc();
        var provider = ProviderDoc();

        // The runtime is tag-derived and carries no registry query for candidate
        // selection, so bootstrap and adoption stay an operator contract. The
        // provider also states the same-SHA recovery and no-synthetic-tag rules.
        Assert.Contains("Steady-state publishing is tag-derived", caller, StringComparison.Ordinal);
        Assert.Contains("it never queries a registry", caller, StringComparison.Ordinal);
        Assert.Contains(
            "separate, deliberate, one-time caller action, outside the workflow",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("do not create a synthetic tag", caller, StringComparison.Ordinal);
        Assert.Contains(
            "The steady-state workflow does not adopt an existing package",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("same-SHA recovery is the only automatic success", provider, StringComparison.Ordinal);
        Assert.Contains("never creates a synthetic tag", provider, StringComparison.Ordinal);
        Assert.Contains(
            "or action carries registry migration or adoption code",
            provider,
            StringComparison.Ordinal);
    }

    private sealed record ProvenanceRun(ShellResult Result, string RepositoryCommit);

    /// <summary>
    /// Runs the committed nuspec provenance extraction and guard from a publisher
    /// action against a stubbed <c>unzip</c>, so the actual comparison decides
    /// rather than a hand copy.
    /// </summary>
    private static ProvenanceRun RunProvenanceGuard(string actionDirectory, string nuspec, string githubSha)
    {
        var guard = WorkflowShell.ExtractBlock(
            WorkflowShell.ReadAction(actionDirectory),
            "nuspec=$(unzip",
            "fi");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STUB_NUSPEC"] = nuspec,
            ["GITHUB_SHA"] = githubSha,
        };

        var script =
            "set -euo pipefail\n"
            + "package_file=/dev/null\n"
            + "unzip() { printf '%s' \"$STUB_NUSPEC\"; }\n"
            + guard
            + "\nprintf 'repository_commit=%s\\n' \"$repository_commit\"\n";

        var result = WorkflowShell.RunBash(script, Path.GetTempPath(), environment);
        var outputs = ParsePrinted(result.StandardOutput);
        var commit = outputs.TryGetValue("repository_commit", out var value) ? value : string.Empty;
        return new ProvenanceRun(result, commit);
    }

    private static void AssertNoLegacyTokens(string content)
    {
        foreach (var token in LegacyTokens)
        {
            Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Nuspec(string commit) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Gizmo.Widget</id>
            <version>3.0.0</version>
            <repository type="git" url="https://github.com/owner/repository" commit="{commit}" />
          </metadata>
        </package>
        """;

    private static IReadOnlyDictionary<string, string> ParsePrinted(string standardOutput)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        return values;
    }

    private static string CallerDoc() => Read("docs", "CALLER_OWNED_NUGET_PUBLISHING.md");

    private static string ProviderDoc() => Read("docs", "GITHUB_NUGET_PROVIDER.md");

    private static string Read(string directory, string file) =>
        File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), directory, file));
}
