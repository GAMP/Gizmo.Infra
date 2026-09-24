using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.Docs;

/// <summary>
/// The provider documentation and repository README are the operator contract for
/// the reusable-workflow caller integration: automatic 3.X versioning, complete
/// per-package tag discovery, publication rechecks, immutable revision pinning,
/// permissions, OIDC, and recovery behavior. These assertions keep that
/// documented contract from silently drifting.
/// </summary>
public sealed class GitHubNugetProviderDocsTests
{
    // Prose wraps across source lines; collapse whitespace so assertions match the
    // rendered sentence rather than the file's line wrapping.
    private static string ProviderDoc() => Collapse(File.ReadAllText(
        InfraRepositoryLocator.DocsPath(InfraRepositoryLocator.ResolveRoot(), "GITHUB_NUGET_PROVIDER.md")));

    private static string Readme() => Collapse(File.ReadAllText(
        Path.Combine(InfraRepositoryLocator.ResolveRoot(), "README.md")));

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

    [Fact]
    public void ProviderDoc_NamesTheThreeDirectReusableWorkflows()
    {
        var doc = ProviderDoc();

        Assert.Contains("three distinct direct `workflow_call` workflows", doc, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/package-validation.yml", doc, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/package-development.yml", doc, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/package-release.yml", doc, StringComparison.Ordinal);
        Assert.Contains("no mode, descriptor version, workflow version, or equivalent version input", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_RequiresFullInfraCommitShaPinning()
    {
        var doc = ProviderDoc();

        Assert.Contains("immutable, complete 40-character Gizmo.Infra commit SHA", doc, StringComparison.Ordinal);
        Assert.Contains(
            "A branch, tag, abbreviated SHA, or expression is not an acceptable workflow reference",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "GAMP/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsAutomaticThreeXVersioning()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "The evaluated project `<Version>` is a compatibility-line input and must be exactly `3.X`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("generation is fixed at `3`", doc, StringComparison.Ordinal);
        Assert.Contains("Callers supply no patch or prerelease version.", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Project descriptor values and workflow version inputs are not authoritative.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("3.X.Y-pr.${{ github.run_number }}", doc, StringComparison.Ordinal);
        Assert.Contains("3.X.Y-dev.${{ github.run_number }}", doc, StringComparison.Ordinal);
        Assert.Contains("`3.X.Y` and `<package-id>/v3.X.Y`", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsCompletePerPackageTagDiscovery()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "paginate the complete caller-repository tag set under the exact prefix `<package-id>/`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("Each tag there must be exactly `<package-id>/v3.X.Y`", doc, StringComparison.Ordinal);
        Assert.Contains("malformed prefix tags fail closed", doc, StringComparison.Ordinal);
        Assert.Contains("selects `Y=0` when it has no tags, otherwise numeric `max(Y)+1`", doc, StringComparison.Ordinal);
        Assert.Contains("The GitHub run number supplies `N`", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsReleaseRerunAndWrongCommitSafety()
    {
        var doc = ProviderDoc();

        Assert.Contains("Release first resolves every matching line tag to its commit.", doc, StringComparison.Ordinal);
        Assert.Contains(
            "A rerun reuses a base only if exactly one package/line tag resolves to the caller SHA.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("Multiple current-SHA tags are ambiguous and fail closed.", doc, StringComparison.Ordinal);
        Assert.Contains("the workflow never moves or overwrites a tag.", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsStateFingerprintAndInactiveRechecks()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "The build job emits package version, complete calculated state, and a tag-state fingerprint.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "The inactive publisher and tag jobs retain their fail-closed rechecks but do not run until a routing contract authorizes them.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsNonCancellingCallerRepositoryConcurrency()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "use a shared caller-repository concurrency group `nuget-${{ github.repository }}` with `cancel-in-progress: false`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("It never cancels running work.", doc, StringComparison.Ordinal);
        Assert.Contains("the latest pending run may replace an earlier pending run", doc, StringComparison.Ordinal);
        Assert.Contains("this is not a durable queue", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsPreflightDiscoveryBranchConfigAndVisibility()
    {
        var doc = ProviderDoc();

        Assert.Contains("`.github/package.yml` with exactly its branch deployment configuration", doc, StringComparison.Ordinal);
        Assert.Contains("The branch names must be distinct valid Git branch names.", doc, StringComparison.Ordinal);
        Assert.Contains("resolves the caller ref to `development`, `release`, or `none`", doc, StringComparison.Ordinal);
        Assert.Contains("deterministically discover exactly one SDK-style packable `.csproj`", doc, StringComparison.Ordinal);
        Assert.Contains("read `PackageId`, `Version`, and `IsPackable` through MSBuild", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Zero or multiple candidates, non-packable or non-SDK-style projects, invalid project metadata, or invalid package configuration fail closed.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "callers do not supply a project path, package ID, version, or package visibility",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "uses the caller `GITHUB_TOKEN` and `github.repository` to read authenticated repository metadata",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("then emits the `repository-visibility` output", doc, StringComparison.Ordinal);
        Assert.Contains("Only `public`, `private`, and `internal` visibility values are accepted", doc, StringComparison.Ordinal);
        Assert.Contains(
            "transport failures, timeouts, non-success responses, malformed metadata, and unknown values fail closed",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("It never reads `github.event.repository.visibility`.", doc, StringComparison.Ordinal);
        Assert.Contains("does not introduce registry-routing behavior", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Consequently, the public/private collision, publication, and release-tag jobs are disabled until a separate routing contract is authorized.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsImmutableJobWorkflowSourceIdentity()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "validates its own repository and file path through the caller-independent `job.workflow_*` contexts",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "validates `job.workflow_ref` as the expected workflow identity ending in the same complete 40-character commit SHA reported by `job.workflow_sha`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("checks out that exact commit into `.gizmo-infra`", doc, StringComparison.Ordinal);
        Assert.Contains(
            "never assumes a caller-local `./.github/actions` path belongs to Gizmo.Infra",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "`job.workflow_sha` alone is a resolved commit and cannot establish that a caller used a full SHA",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("the original `job.workflow_ref` check enforces that invariant", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Repository visibility discovery is diagnostic and fail-closed only; it does not select a collision check or publishing registry.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsArtifactHandoffAndDisabledPublishers()
    {
        var doc = ProviderDoc();

        Assert.Contains("packs the calculated version with the caller commit as repository metadata", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Artifact names include both `github.run_id` and `github.run_attempt`.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "No active job downloads the artifact, queries a package feed, publishes, or creates a release tag.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsTheDisabledPublisherTrustBoundary()
    {
        var doc = ProviderDoc();

        Assert.Contains("The public/private collision, publisher, and release-tag jobs remain disabled.", doc, StringComparison.Ordinal);
        Assert.Contains(
            "They do not obtain OIDC or package credentials, download artifacts, contact a package feed, or create tags.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "A separate routing contract must restore an operation-specific publisher path and its protected-branch trust boundary.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_ForbidsInheritedSecretsAndPermanentKeys()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "Do not use `secrets: inherit`, pass an API key, or create a `NUGET_API_KEY` secret.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "The active workflows do not request publication credentials or publish to either registry.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_ForbidsTrustedPublishingForDisabledPublishers()
    {
        var doc = ProviderDoc();

        Assert.Contains("Do not configure NuGet.org Trusted Publishing for these inactive publishers.", doc, StringComparison.Ordinal);
        Assert.Contains("Any future publisher is a confirmation-gated operator action", doc, StringComparison.Ordinal);
        Assert.Contains(
            "bind the exact caller repository and approved immutable Gizmo.Infra revision without wildcards",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_PinsActionsAndDisablesCheckoutCredentials()
    {
        var doc = ProviderDoc();

        Assert.Contains("All action references are pinned to full commit SHAs", doc, StringComparison.Ordinal);
        Assert.Contains("checkout credentials are disabled", doc, StringComparison.Ordinal);
        Assert.Contains("No third-party GitHub Action is used.", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsConsumerDevelopmentRangeOnly()
    {
        var doc = ProviderDoc();

        Assert.Contains("floating development range `3.X.*-dev.*`", doc, StringComparison.Ordinal);
        Assert.Contains(
            "This is consumer documentation only: Gizmo.Infra does not migrate consumers or enable CPM floating-version behavior.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_PointsAtTheReusableWorkflowContract()
    {
        var readme = Readme();

        Assert.Contains("three callable workflow contracts", readme, StringComparison.Ordinal);
        Assert.Contains("automatic GitHub-calculated versioning", readme, StringComparison.Ordinal);
        Assert.Contains("immutable full-SHA invocation", readme, StringComparison.Ordinal);
        Assert.Contains("OIDC trusted-publishing requirements", readme, StringComparison.Ordinal);
        Assert.Contains("set only the `3.X` compatibility line", readme, StringComparison.Ordinal);
    }
}
