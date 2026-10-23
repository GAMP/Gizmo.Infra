using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class CallerOwnedPublishingActionContractTests
{
    private static string Root => Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions");
    private static string Read(string name) => File.ReadAllText(Path.Combine(Root, name, "action.yml"));

    [Fact]
    public void Actions_ExposeOnlyImplementedCapabilities()
    {
        Assert.Equal(new[] { "nuget", "preflight", "tag" }, Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var name in new[] { "nuget", "preflight", "tag" })
        {
            var content = Read(name);
            var yaml = new YamlStream();
            using var reader = new StringReader(content);
            yaml.Load(reader);
            Assert.Single(yaml.Documents);
            Assert.DoesNotContain("${{ needs.", content, StringComparison.Ordinal);
            Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        }

        var nuget = Read("nuget");
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_URL", nuget, StringComparison.Ordinal);
        Assert.Contains("--expect-publisher nuget", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", nuget, StringComparison.OrdinalIgnoreCase);

        var tag = Read("tag");
        Assert.Contains("--expect-publisher publishable", tag, StringComparison.Ordinal);
        Assert.Contains("GIZMO_PUBLISHER" , tag, StringComparison.Ordinal);
        Assert.Contains("[[ \"$GIZMO_PUBLISHER\" == nuget ]]", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void Actions_UsePinnedDependenciesAndSharedNuspecParser()
    {
        var pin = new Regex(@"^\s*uses:\s+[^\s@]+@[0-9a-f]{40}\s+#\s+v[0-9][^\s]*\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        foreach (var action in new[] { "nuget", "preflight", "tag" })
        {
            var content = Read(action);
            Assert.Equal(Regex.Matches(content, @"^\s*uses:", RegexOptions.Multiline).Count, pin.Matches(content).Count);
        }

        var nuget = Read("nuget");
        Assert.Contains("${{ github.action_path }}/../../package/nuspec.py", nuget, StringComparison.Ordinal);
        Assert.Contains("python3 \"$NUSPEC_PARSER\"", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -oE 'commit=", nuget, StringComparison.Ordinal);
        Assert.Contains("does not contain exactly one nuspec", nuget, StringComparison.Ordinal);
        Assert.Contains("state.py", Read("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("branch-role:", Read("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("release-tag:", Read("tag"), StringComparison.Ordinal);
    }

    [Fact]
    public void NugetPublisher_RejectsPullRequestsAndUnprotectedRefs()
    {
        var root = YamlWorkflowReader.Parse(Read("nuget"));
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps")
            .Single(item => YamlWorkflowReader.ScalarChild(item, "name") == "Validate NuGet publisher invocation");
        var script = YamlWorkflowReader.ScalarChild(step, "run");

        var pullRequest = WorkflowShell.RunBash(script, Path.GetTempPath(), GuardEnvironment("pull_request", "true", "refs/heads/release"));
        Assert.NotEqual(0, pullRequest.ExitCode);
        Assert.Contains("Publishing is allowed only from push.", pullRequest.StandardError, StringComparison.Ordinal);

        var unprotected = WorkflowShell.RunBash(script, Path.GetTempPath(), GuardEnvironment("push", "false", "refs/heads/release"));
        Assert.NotEqual(0, unprotected.ExitCode);
        Assert.Contains("Publishing requires a protected branch ref.", unprotected.StandardError, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> GuardEnvironment(string eventName, string protectedRef, string reference) => new(StringComparer.Ordinal)
    {
        ["EVENT_NAME"] = eventName,
        ["REF_PROTECTED"] = protectedRef,
        ["REF_NAME"] = reference,
        ["NUGET_USER"] = "valid-user",
    };
}
