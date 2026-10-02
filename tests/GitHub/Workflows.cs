using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class ReusableWorkflowContractTests
{
    private static readonly string[] Files = ["package-validation.yml", "package-publish.yml"];
    private static readonly Regex RemotePin = new(@"^\s*uses:\s+[^\s@]+@[0-9a-f]{40}\s+#\s+v[0-9][^\s]*\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void Workflows_AreExactlyTheTwoDirectReusableCallersAndAcceptOnlyBranchInputs()
    {
        var directory = Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "workflows");
        Assert.Equal(Files.OrderBy(value => value, StringComparer.Ordinal), Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml").Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal));

        foreach (var file in Files)
        {
            var content = Read(file);
            var root = YamlWorkflowReader.Parse(content);
            var triggers = YamlWorkflowReader.MappingChild(root, "on");
            Assert.True(YamlWorkflowReader.HasChild(triggers, "workflow_call"));
            Assert.False(YamlWorkflowReader.HasChild(triggers, "push"));
            Assert.False(YamlWorkflowReader.HasChild(triggers, "pull_request"));
            Assert.False(YamlWorkflowReader.HasChild(triggers, "workflow_dispatch"));
            Assert.Empty(YamlWorkflowReader.MappingChild(root, "permissions").Children);

            var call = YamlWorkflowReader.MappingChild(triggers, "workflow_call");
            var inputs = YamlWorkflowReader.MappingChild(call, "inputs");
            Assert.Equal(new[] { "development-branch", "production-branch" }, Keys(inputs).OrderBy(value => value, StringComparer.Ordinal));
            Assert.DoesNotContain("inputs.package-id", content, StringComparison.Ordinal);
            Assert.DoesNotContain("inputs.project-path", content, StringComparison.Ordinal);
            Assert.DoesNotContain("inputs.repository-visibility", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublishPreparation_ExportsRoutingStateWithoutPublishingOrMutating()
    {
        var root = YamlWorkflowReader.Parse(Read("package-publish.yml"));
        var jobs = YamlWorkflowReader.MappingChild(root, "jobs");
        Assert.Equal(new[] { "build" }, Keys(jobs));
        var job = YamlWorkflowReader.MappingChild(jobs, "build");
        var outputs = YamlWorkflowReader.MappingChild(job, "outputs");
        foreach (var name in new[] { "package-artifact", "package-version", "release-tag", "release-tag-state", "calculated-state", "tag-state-fingerprint", "package-id", "branch-role", "repository-visibility" })
        {
            Assert.True(YamlWorkflowReader.HasChild(outputs, name));
        }

        var content = Read("package-publish.yml");
        foreach (var forbidden in new[] { "dotnet nuget push", "id-token", "ACTIONS_ID_TOKEN", "api.nuget.org", "nuget.pkg.github.com", "git/refs", "contents: write", "packages: write", "packages: read", "NUGET_API_KEY", "NUGET_TOKEN", "secrets:" })
        {
            Assert.DoesNotContain(forbidden, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Workflows_PinDependenciesAndBuildTheImmutableCallerCommitWithoutCredentials()
    {
        foreach (var file in Files)
        {
            var content = Read(file);
            var declaredUses = Regex.Matches(content, @"^\s*uses:", RegexOptions.Multiline).Count;
            var remotePins = RemotePin.Matches(content).Count;
            var localPreflight = Regex.Matches(content, @"^\s*uses:\s+\./[^\s]+\s*$", RegexOptions.Multiline).Count;
            Assert.Equal(declaredUses, remotePins + localPreflight);
            Assert.Contains("actions/checkout@", content, StringComparison.Ordinal);
            Assert.Contains("actions/setup-dotnet@", content, StringComparison.Ordinal);
            Assert.Contains("actions/upload-artifact@", content, StringComparison.Ordinal);
            Assert.DoesNotContain("d4c94342e560b34958e1a5f7d17e66c4b9131d1f", content, StringComparison.Ordinal);
            Assert.Contains("dotnet-version: 11.0.x", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet-version: 10.", content, StringComparison.Ordinal);
            Assert.Contains("persist-credentials: false", content, StringComparison.Ordinal);
            Assert.Contains("uses: ./.gizmo-infra/.github/actions/preflight", content, StringComparison.Ordinal);
            Assert.Contains("COMPATIBILITY_LINE: ${{ steps.metadata.outputs.compatibility-line }}", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet msbuild", content, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:Version=", content, StringComparison.Ordinal);
        }

        var publish = Read("package-publish.yml");
        Assert.Contains("ref: ${{ github.sha }}", publish, StringComparison.Ordinal);
        Assert.Contains("job.workflow_sha", publish, StringComparison.Ordinal);
        Assert.Contains("workflow_ref_sha", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightRoleIsAuthoritativeAndNoneSkipsEveryExpensiveStep()
    {
        foreach (var file in Files)
        {
            var content = Read(file);
            Assert.Contains("github.event_name == 'pull_request'", content, StringComparison.Ordinal);
            Assert.DoesNotContain("github.ref_name", content, StringComparison.Ordinal);
            Assert.DoesNotContain("github.head_ref", content, StringComparison.Ordinal);

            var expectedGate = "${{ steps.metadata.outputs.branch-role != 'none' }}";
            var root = YamlWorkflowReader.Parse(content);
            var jobName = file == "package-validation.yml" ? "validate" : "build";
            var job = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), jobName);
            var steps = YamlWorkflowReader.MappingSequence(job, "steps");
            var metadata = steps.Single(step =>
                YamlWorkflowReader.HasChild(step, "id")
                && YamlWorkflowReader.ScalarChild(step, "id") == "metadata");
            Assert.False(YamlWorkflowReader.HasChild(metadata, "if"), "metadata discovery must run for the none role.");

            var expensiveStepNames = new[]
            {
                "Calculate package version and tag state",
                "Restore with NuGet audit",
                "Build",
                file == "package-validation.yml" ? "Pack calculated validation version" : "Pack calculated package version",
                "Upload exact package artifact",
            };
            foreach (var stepName in expensiveStepNames)
            {
                var step = steps.Single(candidate =>
                    YamlWorkflowReader.HasChild(candidate, "name")
                    && YamlWorkflowReader.ScalarChild(candidate, "name") == stepName);
                Assert.Equal(expectedGate, YamlWorkflowReader.ScalarChild(step, "if"));
            }
        }

        var publish = Read("package-publish.yml");
        Assert.Contains("BRANCH_ROLE: ${{ steps.metadata.outputs.branch-role }}", publish, StringComparison.Ordinal);
        Assert.Contains("development|production) ;;", publish, StringComparison.Ordinal);
        Assert.Contains("The package preflight did not resolve a publishable branch role.", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("development|release)", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void VersioningAndArtifactContractsCarryExactCalculatedState()
    {
        foreach (var file in Files)
        {
            var content = Read(file);
            Assert.Contains("COMPATIBILITY_LINE: ${{ steps.metadata.outputs.compatibility-line }}", content, StringComparison.Ordinal);
            Assert.Contains("PACKAGE_ID: ${{ steps.metadata.outputs.package-id }}", content, StringComparison.Ordinal);
            Assert.Contains("package_artifact=\"artifacts/${PACKAGE_ID}.${package_version}.nupkg\"", content, StringComparison.Ordinal);
            Assert.Contains("-p:PackageVersion=\"$CALCULATED_VERSION\"", content, StringComparison.Ordinal);
            Assert.Contains("tag_state_fingerprint=$(printf '%s' \"$tag_snapshot\" | sha256sum", content, StringComparison.Ordinal);
            Assert.Contains("refs/tags/${PACKAGE_ID}/", content, StringComparison.Ordinal);
            Assert.Contains("increment_decimal()", content, StringComparison.Ordinal);
        }

        var validation = Read("package-validation.yml");
        Assert.Contains("package_version=\"${base_version}-pr.${GITHUB_RUN_NUMBER}\"", validation, StringComparison.Ordinal);
        var publish = Read("package-publish.yml");
        Assert.Contains("package_version=\"${base_version}-dev.${GITHUB_RUN_NUMBER}\"", publish, StringComparison.Ordinal);
        Assert.Contains("current-sha-tags=${current_tag_list}", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowBoundaries_KeepPermissionsAuditAndArtifactRetryIdentityExplicit()
    {
        foreach (var file in Files)
        {
            var content = Read(file);
            Assert.DoesNotContain("concurrency", content, StringComparison.Ordinal);
            Assert.DoesNotContain("packages: write", content, StringComparison.Ordinal);
            Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
            Assert.Contains("-p:NuGetAudit=true", content, StringComparison.Ordinal);
            Assert.Contains("-p:NuGetAuditMode=all", content, StringComparison.Ordinal);
            Assert.Contains("-p:NuGetAuditLevel=low", content, StringComparison.Ordinal);
            Assert.Contains("WarningsAsErrors=NU1904%3BNU1903", content, StringComparison.Ordinal);
            Assert.Contains("WarningsNotAsErrors=NU1901%3BNU1902", content, StringComparison.Ordinal);
            Assert.Contains("nuget-package-${{ github.run_id }}-${{ github.run_attempt }}", content, StringComparison.Ordinal);
            Assert.Contains("if-no-files-found: error", content, StringComparison.Ordinal);
        }
    }

    private static string Read(string file) => WorkflowShell.ReadWorkflow(file);

    private static IEnumerable<string> Keys(YamlMappingNode mapping) =>
        mapping.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);
}
