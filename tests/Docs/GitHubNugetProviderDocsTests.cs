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
            "gizmo/Gizmo.Infra/.github/workflows/package-validation.yml@<40-character-infra-commit-sha>",
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
    public void ProviderDoc_DocumentsStateFingerprintAndRechecks()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "The build job emits package version, complete calculated state, and a tag-state fingerprint.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("resolve annotated tags to their commit", doc, StringComparison.Ordinal);
        Assert.Contains(
            "A fingerprint/state drift or a package/tag collision fails closed.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsNonCancellingPerPackageConcurrency()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "use native per-package concurrency `nuget-${{ github.repository }}-${{ inputs.package-id }}` with `cancel-in-progress: false`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("It never cancels running work.", doc, StringComparison.Ordinal);
        Assert.Contains("the latest pending run may replace an earlier pending run", doc, StringComparison.Ordinal);
        Assert.Contains("this is not a durable queue", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsArtifactHandoffAndCollisionChecks()
    {
        var doc = ProviderDoc();

        Assert.Contains("packs the calculated version with the caller commit as repository metadata", doc, StringComparison.Ordinal);
        Assert.Contains(
            "Artifact names include both `github.run_id` and `github.run_attempt`; publishers download that exact name, never a wildcard, and never rebuild from source.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains(
            "Private packages check the caller-owner GitHub Packages NuGet feed at `GITHUB_REPOSITORY_OWNER`",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("they do not use GitHub's package-management REST endpoints.", doc, StringComparison.Ordinal);
        Assert.Contains(
            "malformed or empty successful responses, and a collision detected during the final recheck fail closed.",
            doc,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_DocumentsReleaseRecoveryWithoutMutation()
    {
        var doc = ProviderDoc();

        Assert.Contains("may create only the missing tag", doc, StringComparison.Ordinal);
        Assert.Contains("If the tag is already at the caller SHA, it is an idempotent result.", doc, StringComparison.Ordinal);
        Assert.Contains("The workflow never force-updates, deletes, or moves a tag.", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_ForbidsInheritedSecretsAndPermanentKeys()
    {
        var doc = ProviderDoc();

        Assert.Contains(
            "Do not use `secrets: inherit`, pass an API key, or create a `NUGET_API_KEY` secret.",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("Public publishing uses caller-bound GitHub OIDC", doc, StringComparison.Ordinal);
        Assert.Contains("Private publishing uses only the calling job's `GITHUB_TOKEN`", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderDoc_RequiresCallerBoundOidcTrustedPublishing()
    {
        var doc = ProviderDoc();

        Assert.Contains("configure NuGet.org Trusted Publishing", doc, StringComparison.Ordinal);
        Assert.Contains(
            "caller repository and each applicable Gizmo.Infra reusable workflow identity",
            doc,
            StringComparison.Ordinal);
        Assert.Contains("do not use wildcard repository or workflow rules", doc, StringComparison.Ordinal);
        Assert.Contains("confirmation-gated operator action", doc, StringComparison.Ordinal);
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
