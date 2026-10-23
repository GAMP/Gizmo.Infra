using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class ReusableWorkflowContractTests
{
    private const string WorkflowFile = "package.yml";
    private static readonly Regex PinnedUse = new(@"^\s*uses:\s+[^\s@]+@[0-9a-f]{40}\s+#\s+v[0-9][^\s]*\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void PublicWorkflow_IsOneReadOnlyReusableEntrypointWithMinimalContract()
    {
        var directory = Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "workflows");
        Assert.Equal(new[] { WorkflowFile }, Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.False(System.IO.File.Exists(Path.Combine(directory, "package-validation.yml")));
        Assert.False(System.IO.File.Exists(Path.Combine(directory, "package-publish.yml")));

        var content = WorkflowShell.ReadWorkflow(WorkflowFile);
        var root = YamlWorkflowReader.Parse(content);
        var call = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "on"), "workflow_call");
        Assert.Equal(new[] { "prev", "prod" }, Keys(YamlWorkflowReader.MappingChild(call, "inputs")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "infra-sha", "publisher", "state", "tag" }, Keys(YamlWorkflowReader.MappingChild(call, "outputs")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "plan" }, Keys(YamlWorkflowReader.MappingChild(root, "jobs")));
        var plan = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), "plan");
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(plan, "permissions"), "contents"));
        Assert.DoesNotContain("id-token", content, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
        Assert.Contains("PREV_BRANCH: ${{ inputs.prev }}", content, StringComparison.Ordinal);
        Assert.Contains("--arg prev \"$PREV_BRANCH\"", content, StringComparison.Ordinal);
        Assert.Contains("prev: $prev", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanWorkflow_IsGatedCheaplyAndPinsImmutableSourcesWithoutCheckoutCredentials()
    {
        var content = WorkflowShell.ReadWorkflow(WorkflowFile);
        var declaredUses = Regex.Matches(content, @"^\s*uses:", RegexOptions.Multiline).Count;
        var localUses = Regex.Matches(content, @"^\s*uses:\s+\./", RegexOptions.Multiline).Count;
        Assert.Equal(declaredUses, PinnedUse.Matches(content).Count + localUses);
        Assert.Contains("persist-credentials: false", content, StringComparison.Ordinal);
        Assert.Contains("job.workflow_sha", content, StringComparison.Ordinal);
        Assert.Contains("WORKFLOW_REF: ${{ job.workflow_ref }}", content, StringComparison.Ordinal);
        Assert.Contains("WORKFLOW_SHA: ${{ job.workflow_sha }}", content, StringComparison.Ordinal);
        Assert.Contains("WORKFLOW_REPOSITORY: ${{ job.workflow_repository }}", content, StringComparison.Ordinal);
        Assert.Contains("WORKFLOW_FILE_PATH: ${{ job.workflow_file_path }}", content, StringComparison.Ordinal);
        Assert.Contains("infra-sha: ${{ steps.trusted-identity.outputs.infra-sha }}", content, StringComparison.Ordinal);
        var steps = PlanSteps();
        var gateIndex = steps.ToList().FindIndex(step => YamlWorkflowReader.ScalarChild(step, "id") == "gate");
        var identityIndex = steps.ToList().FindIndex(step => YamlWorkflowReader.ScalarChild(step, "id") == "trusted-identity");
        Assert.Equal(0, gateIndex);
        Assert.True(identityIndex > gateIndex);
        var infraCheckout = steps.Single(step => YamlWorkflowReader.ScalarChild(step, "name") == "Checkout immutable Gizmo.Infra action source");
        var identity = steps[identityIndex];
        Assert.False(YamlWorkflowReader.HasChild(identity, "if"));
        var checkoutIndexes = steps.Select((step, index) => (step, index))
            .Where(item => YamlWorkflowReader.HasChild(item.step, "uses")
                && YamlWorkflowReader.ScalarChild(item.step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal))
            .Select(item => item.index);
        Assert.NotEmpty(checkoutIndexes);
        Assert.All(checkoutIndexes, checkoutIndex => Assert.True(identityIndex < checkoutIndex));
        var checkoutInputs = YamlWorkflowReader.MappingChild(infraCheckout, "with");
        Assert.Equal("GAMP/Gizmo.Infra", YamlWorkflowReader.ScalarChild(checkoutInputs, "repository"));
        Assert.Equal("${{ steps.trusted-identity.outputs.infra-sha }}", YamlWorkflowReader.ScalarChild(checkoutInputs, "ref"));
        Assert.Equal(".gizmo-infra", YamlWorkflowReader.ScalarChild(checkoutInputs, "path"));
        Assert.Equal("false", YamlWorkflowReader.ScalarChild(checkoutInputs, "persist-credentials"));
        var preflight = steps.Single(step => YamlWorkflowReader.ScalarChild(step, "name") == "Discover caller package metadata");
        Assert.Equal("./.gizmo-infra/.github/actions/preflight", YamlWorkflowReader.ScalarChild(preflight, "uses"));
        Assert.Contains("steps.gate.outputs.role != 'none'", content, StringComparison.Ordinal);
        Assert.Contains("STATE_PY=\"$GITHUB_WORKSPACE/.gizmo-infra/.github/package/state.py\"", content, StringComparison.Ordinal);
        Assert.Contains("actions/setup-dotnet@", content, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 11.0.x", content, StringComparison.Ordinal);
        Assert.Contains("nuget-package-${{ github.run_id }}-${{ github.run_attempt }}", content, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package-validation.yml", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package-publish.yml", content, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.package-id", content, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.repository-visibility", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_ResolvesAnnotatedPackageTagObjectsBeforeFingerprinting()
    {
        var content = WorkflowShell.ReadWorkflow(WorkflowFile);
        Assert.Contains("resolve_commit() {", content, StringComparison.Ordinal);
        Assert.Contains("case \"$object_type\" in", content, StringComparison.Ordinal);
        Assert.Contains("tag)", content, StringComparison.Ordinal);
        Assert.Contains("/git/tags/$object_sha", content, StringComparison.Ordinal);
        Assert.Contains("depth > 16", content, StringComparison.Ordinal);
        Assert.Contains("commit)", content, StringComparison.Ordinal);
        Assert.Contains("Package tag ref does not resolve to a commit.", content, StringComparison.Ordinal);
        Assert.Contains("{ref: .[0], objectSha: .[1], commit: .[2]}", content, StringComparison.Ordinal);
        Assert.Contains("python3 \"$STATE_PY\" plan", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@stable", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "other/repository/.github/workflows/package.yml@refs/tags/stable", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0)]
    [InlineData("gamp/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/other.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@main", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/v1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 1)]
    [InlineData("GAMP/Gizmo.Infra", ".github/workflows/package.yml", "GAMP/Gizmo.Infra/.github/workflows/package.yml@refs/tags/stable", "not-a-sha", 1)]
    public void TrustedIdentityScript_UsesOnlyTheResolvedWorkflowIdentity(
        string repository, string path, string reference, string resolvedSha, int expectedExitCode)
    {
        var steps = PlanSteps();
        var identity = steps.Single(step => YamlWorkflowReader.HasChild(step, "id")
            && YamlWorkflowReader.ScalarChild(step, "id") == "trusted-identity");
        var output = Path.GetTempFileName();
        try
        {
            var result = WorkflowShell.RunBash(
                YamlWorkflowReader.ScalarChild(identity, "run"),
                InfraRepositoryLocator.ResolveRoot(),
                new Dictionary<string, string>
                {
                    ["WORKFLOW_REPOSITORY"] = repository,
                    ["WORKFLOW_FILE_PATH"] = path,
                    ["WORKFLOW_REF"] = reference,
                    ["WORKFLOW_SHA"] = resolvedSha,
                    ["CALLER_SHA"] = "cccccccccccccccccccccccccccccccccccccccc",
                    ["GITHUB_OUTPUT"] = output,
                });

            Assert.Equal(expectedExitCode, result.ExitCode);
            var outputText = File.ReadAllText(output);
            if (expectedExitCode == 0)
            {
                Assert.Contains($"infra-sha={resolvedSha}", outputText, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("infra-sha=", outputText, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static IReadOnlyList<YamlMappingNode> PlanSteps()
    {
        var root = YamlWorkflowReader.Parse(WorkflowShell.ReadWorkflow(WorkflowFile));
        return YamlWorkflowReader.MappingSequence(
                YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), "plan"), "steps")
            .Select(Assert.IsType<YamlMappingNode>).ToArray();
    }

    private static IEnumerable<string> Keys(YamlMappingNode mapping) =>
        mapping.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);
}
