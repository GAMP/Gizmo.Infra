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
        Assert.Equal(new[] { "dev", "prod" }, Keys(YamlWorkflowReader.MappingChild(call, "inputs")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "publisher", "state", "tag" }, Keys(YamlWorkflowReader.MappingChild(call, "outputs")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "plan" }, Keys(YamlWorkflowReader.MappingChild(root, "jobs")));
        var plan = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), "plan");
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(plan, "permissions"), "contents"));
        Assert.DoesNotContain("id-token", content, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
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
        Assert.Contains("workflow_ref_sha", content, StringComparison.Ordinal);
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

    private static IEnumerable<string> Keys(YamlMappingNode mapping) =>
        mapping.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);
}
