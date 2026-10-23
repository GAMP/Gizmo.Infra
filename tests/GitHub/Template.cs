using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class CallerTemplateContractTests
{
    private const string TemplatePath = ".github/templates/package.yml";
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

        Assert.Contains("PREV: &prev", content, StringComparison.Ordinal);
        Assert.Contains("PROD: &prod", content, StringComparison.Ordinal);
        Assert.Contains("prev: *prev", content, StringComparison.Ordinal);
        Assert.Contains("prod: *prod", content, StringComparison.Ordinal);
        var callerEnv = YamlWorkflowReader.MappingChild(root, "env");
        var planInputs = YamlWorkflowReader.MappingChild(Job(jobs, "plan"), "with");
        Assert.Equal(YamlWorkflowReader.ScalarChild(callerEnv, "PREV"), YamlWorkflowReader.ScalarChild(planInputs, "prev"));
        Assert.DoesNotContain("package-id:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package-version:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("release-tag:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("calculated-state:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("tag-state-fingerprint:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("repository-visibility:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", content, StringComparison.OrdinalIgnoreCase);

        var pins = Regex.Matches(content, @"GAMP/Gizmo\.Infra/[^\s@]+@(?<ref>[^\s]+)")
            .Cast<Match>().Select(match => match.Groups["ref"].Value).ToArray();
        Assert.Equal(new[] { "stable" }, pins);
        Assert.Contains(".github/workflows/package.yml@stable", content, StringComparison.Ordinal);
        Assert.DoesNotContain("GAMP/Gizmo.Infra/.github/actions/", content, StringComparison.Ordinal);
        Assert.Contains(".github/actions/nuget", content, StringComparison.Ordinal);
        Assert.DoesNotContain(".github/actions/internal", content, StringComparison.Ordinal);
        Assert.Contains(".github/actions/tag", content, StringComparison.Ordinal);
        Assert.Contains("${{ needs.plan.outputs.infra-sha }}", content, StringComparison.Ordinal);
        var externalUses = Regex.Matches(content, @"^\s*uses:\s+(?<reference>[^\s]+)", RegexOptions.Multiline)
            .Cast<Match>().Select(match => match.Groups["reference"].Value)
            .Where(reference => !reference.StartsWith("./", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1, externalUses.Count(reference => reference == "GAMP/Gizmo.Infra/.github/workflows/package.yml@stable"));
        Assert.All(externalUses.Where(reference => reference != "GAMP/Gizmo.Infra/.github/workflows/package.yml@stable"), reference =>
            Assert.Matches(@"^[^@]+@[0-9a-f]{40}$", reference));
        var parsedJobs = YamlWorkflowReader.MappingChild(Parse(), "jobs");
        foreach (var jobName in new[] { "nuget", "tag" })
        {
            var checkout = YamlWorkflowReader.MappingSequence(Job(parsedJobs, jobName), "steps")
                .Single(step => YamlWorkflowReader.HasChild(step, "uses")
                    && YamlWorkflowReader.ScalarChild(step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal));
            Assert.Matches(@"^actions/checkout@[0-9a-f]{40}$", YamlWorkflowReader.ScalarChild(checkout, "uses"));
            Assert.Equal("${{ needs.plan.outputs.infra-sha }}", YamlWorkflowReader.ScalarChild(
                YamlWorkflowReader.MappingChild(checkout, "with"), "ref"));
        }
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

        var internalJob = Job(jobs, "internal");
        var internalPermissions = Permissions(jobs, "internal");
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(internalPermissions, "contents"));
        Assert.Equal(new[] { "contents" }, Keys(internalPermissions));
        var internalSteps = YamlWorkflowReader.MappingSequence(internalJob, "steps");
        var reject = Assert.Single(internalSteps);
        Assert.Equal("Reject unsupported internal registry publishing", YamlWorkflowReader.ScalarChild(reject, "name"));
        Assert.False(YamlWorkflowReader.HasChild(reject, "uses"));
        Assert.Contains("Internal registry publishing is not implemented", YamlWorkflowReader.ScalarChild(reject, "run"), StringComparison.Ordinal);
        Assert.Contains("exit 1", YamlWorkflowReader.ScalarChild(reject, "run"), StringComparison.Ordinal);
        var placeholder = WorkflowShell.RunBash(YamlWorkflowReader.ScalarChild(reject, "run"), InfraRepositoryLocator.ResolveRoot());
        Assert.NotEqual(0, placeholder.ExitCode);
        Assert.Contains("Internal registry publishing is not implemented", placeholder.StandardError, StringComparison.Ordinal);

        var tagPermissions = Permissions(jobs, "tag");
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(tagPermissions, "contents"));
        Assert.False(YamlWorkflowReader.HasChild(tagPermissions, "id-token"));
        Assert.False(YamlWorkflowReader.HasChild(tagPermissions, "packages"));
        Assert.Contains("needs.plan.outputs.tag == 'true'", JobIf(jobs, "tag"), StringComparison.Ordinal);
        Assert.Contains("needs.nuget.result == 'success'", JobIf(jobs, "tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("needs.internal", JobIf(jobs, "tag"), StringComparison.Ordinal);

        foreach (var job in new[] { "nuget", "tag" })
        {
            var steps = YamlWorkflowReader.MappingSequence(Job(jobs, job), "steps");
            var checkout = steps.Single(step => YamlWorkflowReader.HasChild(step, "uses")
                && YamlWorkflowReader.ScalarChild(step, "uses") == "actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09");
            Assert.Equal("${{ needs.plan.outputs.infra-sha }}", YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(checkout, "with"), "ref"));

            var localAction = steps.Single(step => YamlWorkflowReader.HasChild(step, "uses")
                && YamlWorkflowReader.ScalarChild(step, "uses").StartsWith("./.gizmo-infra/.github/actions/", StringComparison.Ordinal));
            var localWith = YamlWorkflowReader.MappingChild(localAction, "with");
            Assert.Equal("${{ needs.plan.outputs.state }}", YamlWorkflowReader.ScalarChild(localWith, "state"));
            Assert.Equal(job, YamlWorkflowReader.ScalarChild(localAction, "uses").Split('/').Last());
        }
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

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    [InlineData("", 1)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 1)]
    [InlineData("not-a-sha", 1)]
    public void PublisherAndTagJobs_RejectMissingOrMalformedPlannedSha(string plannedSha, int expectedExitCode)
    {
        var jobs = YamlWorkflowReader.MappingChild(Parse(), "jobs");
        foreach (var jobName in new[] { "nuget", "tag" })
        {
            var steps = YamlWorkflowReader.MappingSequence(Job(jobs, jobName), "steps");
            var guard = steps.First();
            var result = WorkflowShell.RunBash(
                YamlWorkflowReader.ScalarChild(guard, "run"),
                InfraRepositoryLocator.ResolveRoot(),
                new Dictionary<string, string> { ["INFRA_SHA"] = plannedSha });

            Assert.Equal(expectedExitCode, result.ExitCode);
        }
    }

    [Fact]
    public void PublisherAndTagJobs_RejectCheckoutThatDoesNotMatchPlannedSha()
    {
        var jobs = YamlWorkflowReader.MappingChild(Parse(), "jobs");
        foreach (var jobName in new[] { "nuget", "tag" })
        {
            var steps = YamlWorkflowReader.MappingSequence(Job(jobs, jobName), "steps");
            var verify = steps.Single(step => YamlWorkflowReader.ScalarChild(step, "name") == "Verify the checked-out Gizmo.Infra commit");
            var result = WorkflowShell.RunBash(
                "git() { printf '%s\\n' 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; }\n" + YamlWorkflowReader.ScalarChild(verify, "run"),
                InfraRepositoryLocator.ResolveRoot(),
                new Dictionary<string, string> { ["INFRA_SHA"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not match the planned commit", result.StandardError, StringComparison.Ordinal);
        }
    }

    private static string[] Keys(YamlMappingNode mapping) => mapping.Children.Keys
        .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty).ToArray();

    private static YamlMappingNode Job(YamlMappingNode jobs, string name) => YamlWorkflowReader.MappingChild(jobs, name);
    private static string JobIf(YamlMappingNode jobs, string name) => YamlWorkflowReader.ScalarChild(Job(jobs, name), "if");
    private static YamlMappingNode Permissions(YamlMappingNode jobs, string name) => YamlWorkflowReader.MappingChild(Job(jobs, name), "permissions");
    private static string StepInput(YamlMappingNode jobs, string name, string input)
    {
        var step = YamlWorkflowReader.MappingSequence(Job(jobs, name), "steps")
            .Single(step => YamlWorkflowReader.HasChild(step, "uses")
                && YamlWorkflowReader.ScalarChild(step, "uses") == "./.gizmo-infra/.github/actions/nuget");
        return YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(step, "with"), input);
    }
}
