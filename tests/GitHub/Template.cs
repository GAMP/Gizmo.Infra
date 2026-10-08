using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class CallerTemplateContractTests
{
    private const string TemplatePath = ".github/templates/package.yml";
    private const string Sha = "<40-character-infra-commit-sha>";

    private static string Content() => File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), TemplatePath));
    private static YamlMappingNode Parse() => YamlWorkflowReader.Parse(Content());

    [Fact]
    public void CanonicalTemplate_UsesMinimalCallerContractAndOnlyPinnedInfraCapabilities()
    {
        var content = Content();
        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(reader);
        Assert.Single(yaml.Documents);

        var root = Parse();
        var jobs = YamlWorkflowReader.MappingChild(root, "jobs");
        Assert.Equal(new[] { "internal", "nuget", "plan", "tag" }, Keys(jobs).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(root, "permissions"), "contents"));
        Assert.Equal("nuget-${{ github.repository }}", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(root, "concurrency"), "group"));
        Assert.Equal("false", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(root, "concurrency"), "cancel-in-progress"));

        Assert.Contains("DEV: &dev", content, StringComparison.Ordinal);
        Assert.Contains("PROD: &prod", content, StringComparison.Ordinal);
        Assert.Contains("dev: *dev", content, StringComparison.Ordinal);
        Assert.Contains("prod: *prod", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package-id:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package-version:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("release-tag:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("calculated-state:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("tag-state-fingerprint:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("repository-visibility:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", content, StringComparison.OrdinalIgnoreCase);

        var pins = Regex.Matches(content, @"GAMP/Gizmo\.Infra/[^\s@]+@(?<sha>[^\s]+)")
            .Cast<Match>().Select(match => match.Groups["sha"].Value).ToArray();
        Assert.Equal(4, pins.Length);
        Assert.All(pins, pin => Assert.Equal(Sha, pin));
        Assert.Contains(".github/workflows/package.yml@" + Sha, content, StringComparison.Ordinal);
        Assert.Contains(".github/actions/nuget@" + Sha, content, StringComparison.Ordinal);
        Assert.Contains(".github/actions/internal@" + Sha, content, StringComparison.Ordinal);
        Assert.Contains(".github/actions/tag@" + Sha, content, StringComparison.Ordinal);
    }

    [Fact]
    public void CallerJobs_KeepPublicationAndTagPermissionsSeparated()
    {
        var jobs = YamlWorkflowReader.MappingChild(Parse(), "jobs");
        Assert.Equal("${{ needs.plan.outputs.publisher == 'nuget' }}", JobIf(jobs, "nuget"));
        Assert.Equal("${{ needs.plan.outputs.publisher == 'internal' }}", JobIf(jobs, "internal"));

        var nuget = Permissions(jobs, "nuget");
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(nuget, "contents"));
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(nuget, "id-token"));
        Assert.False(YamlWorkflowReader.HasChild(nuget, "packages"));

        var internalPermissions = Permissions(jobs, "internal");
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(internalPermissions, "contents"));
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(internalPermissions, "packages"));
        Assert.False(YamlWorkflowReader.HasChild(internalPermissions, "id-token"));

        var tagPermissions = Permissions(jobs, "tag");
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(tagPermissions, "contents"));
        Assert.False(YamlWorkflowReader.HasChild(tagPermissions, "id-token"));
        Assert.False(YamlWorkflowReader.HasChild(tagPermissions, "packages"));
        Assert.Contains("needs.plan.outputs.tag == 'true'", JobIf(jobs, "tag"), StringComparison.Ordinal);
        Assert.Contains("needs.nuget.result == 'success'", JobIf(jobs, "tag"), StringComparison.Ordinal);
        Assert.Contains("needs.internal.result == 'success'", JobIf(jobs, "tag"), StringComparison.Ordinal);

        Assert.Equal("${{ needs.plan.outputs.state }}", StepInput(jobs, "nuget", "state"));
        Assert.Equal("${{ needs.plan.outputs.state }}", StepInput(jobs, "internal", "state"));
        Assert.Equal("${{ needs.plan.outputs.state }}", StepInput(jobs, "tag", "state"));
        Assert.Equal("${{ vars.NUGET_USER }}", StepInput(jobs, "nuget", "user"));
    }

    [Fact]
    public void UnsupportedInternalRepositoryVisibilityFailsClosedInPlanner()
    {
        var plan = File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "package", "state.py"));
        Assert.Contains("def _route(visibility):", plan, StringComparison.Ordinal);
        Assert.Contains("raise StateError(\"unsupported-routing\")", plan, StringComparison.Ordinal);
        Assert.Contains("raise StateError(\"unsupported-routing\")", plan, StringComparison.Ordinal);
    }

    private static string[] Keys(YamlMappingNode mapping) => mapping.Children.Keys
        .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty).ToArray();

    private static YamlMappingNode Job(YamlMappingNode jobs, string name) => YamlWorkflowReader.MappingChild(jobs, name);
    private static string JobIf(YamlMappingNode jobs, string name) => YamlWorkflowReader.ScalarChild(Job(jobs, name), "if");
    private static YamlMappingNode Permissions(YamlMappingNode jobs, string name) => YamlWorkflowReader.MappingChild(Job(jobs, name), "permissions");
    private static string StepInput(YamlMappingNode jobs, string name, string input)
    {
        var step = YamlWorkflowReader.MappingSequence(Job(jobs, name), "steps").Single();
        return YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(step, "with"), input);
    }
}
