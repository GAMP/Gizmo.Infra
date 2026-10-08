using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class CallerOwnedPublishingActionContractTests
{
    private static readonly string[] Actions = ["internal", "nuget", "preflight", "tag"];
    private static string Root => Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions");
    private static string Read(string name) => File.ReadAllText(Path.Combine(Root, name, "action.yml"));

    [Fact]
    public void Actions_ExposeOnlyNewCapabilitiesAndShareTheOneParser()
    {
        Assert.Equal(Actions, Directory.EnumerateDirectories(Root).Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal));
        foreach (var name in Actions)
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(Read(name));
            yaml.Load(reader);
            Assert.Single(yaml.Documents);
            Assert.DoesNotContain("${{ needs.", Read(name), StringComparison.Ordinal);
            Assert.DoesNotContain("secrets:", Read(name), StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_API_KEY", Read(name), StringComparison.Ordinal);
        }

        foreach (var publisher in new[] { "nuget", "internal" })
        {
            var root = YamlWorkflowReader.Parse(Read(publisher));
            var inputs = YamlWorkflowReader.MappingChild(root, "inputs");
            var expected = publisher == "nuget" ? new[] { "state", "user" } : new[] { "state" };
            Assert.Equal(expected, Keys(inputs).OrderBy(value => value, StringComparer.Ordinal));
            Assert.Contains("${{ github.action_path }}/../../package/state.py", Read(publisher), StringComparison.Ordinal);
            Assert.Contains("${{ github.action_path }}/../../package/nuspec.py", Read(publisher), StringComparison.Ordinal);
            Assert.Contains("GIZMO_ARTIFACT_DIGEST", Read(publisher), StringComparison.Ordinal);
            Assert.Contains("sha256sum", Read(publisher), StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet build", Read(publisher), StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet pack", Read(publisher), StringComparison.Ordinal);
        }
        Assert.Contains("scripts/validate.mjs", Read("internal"), StringComparison.Ordinal);
        Assert.Contains("state.py", Read("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("branch-role:", Read("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("release-tag:", Read("tag"), StringComparison.Ordinal);
    }

    [Fact]
    public void PrivilegedActions_RejectUnsafeContextsBeforeAnyCredentialOrMutationUse()
    {
        foreach (var directory in new[] { "nuget", "internal" })
        {
            var content = Read(directory);
            Assert.Contains("EVENT_NAME: ${{ github.event_name }}", content, StringComparison.Ordinal);
            Assert.Contains("REF_PROTECTED: ${{ github.ref_protected }}", content, StringComparison.Ordinal);
            Assert.Contains("if [[ \"$EVENT_NAME\" != push ]]", content, StringComparison.Ordinal);
            Assert.Contains("if [[ \"$REF_PROTECTED\" != true || \"$REF_NAME\" != refs/heads/* ]]", content, StringComparison.Ordinal);
            Assert.Contains("Parse state and revalidate destination and artifact", content, StringComparison.Ordinal);
            Assert.Contains("The package state is missing, malformed, tampered, or unknown-version", content, StringComparison.Ordinal);
            Assert.Contains("actions/download-artifact@", content, StringComparison.Ordinal);
            Assert.Contains("nuget-package-${{ github.run_id }}-${{ github.run_attempt }}", content, StringComparison.Ordinal);
        }

        var nuget = Read("nuget");
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_URL", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", nuget, StringComparison.Ordinal);
        var internalAction = Read("internal");
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", internalAction, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", internalAction, StringComparison.Ordinal);
        var tag = Read("tag");
        Assert.Contains("Parse state and revalidate production policy role", tag, StringComparison.Ordinal);
        Assert.Contains("Tagging requires the production policy role", tag, StringComparison.Ordinal);
        Assert.Contains("The planned state did not request a tag", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", tag, StringComparison.Ordinal);
        Assert.DoesNotContain("--request PATCH", tag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--request PUT", tag, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--request DELETE", tag, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Actions_UsePinnedDependenciesAndNuspecProvenanceIsStructural()
    {
        var pin = new Regex(@"^\s*uses:\s+[^\s@]+@[0-9a-f]{40}\s+#\s+v[0-9][^\s]*\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        foreach (var directory in Actions)
        {
            Assert.Equal(Regex.Matches(Read(directory), @"^\s*uses:", RegexOptions.Multiline).Count, pin.Matches(Read(directory)).Count);
        }
        var nuspec = File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "package", "nuspec.py"));
        Assert.Contains("expat.ParserCreate", nuspec, StringComparison.Ordinal);
        Assert.Contains("StartDoctypeDeclHandler", nuspec, StringComparison.Ordinal);
        Assert.Contains("EntityDeclHandler", nuspec, StringComparison.Ordinal);
        Assert.Contains("_sole_child", nuspec, StringComparison.Ordinal);
        Assert.Contains("casefold", nuspec, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -oE", Read("internal"), StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherRecoveryAndCollisionPaths_RemainFailClosedAndSameShaIdempotent()
    {
        var nuget = Read("nuget");
        var internalAction = Read("internal");
        foreach (var content in new[] { nuget, internalAction })
        {
            Assert.Contains("Calculated package state drifted before publication; refusing to publish.", content, StringComparison.Ordinal);
            Assert.Contains("no authenticated provenance for this caller SHA", content, StringComparison.Ordinal);
            Assert.Contains("package-state=published", content, StringComparison.Ordinal);
            Assert.Contains("package-state=unpublished", content, StringComparison.Ordinal);
            Assert.Contains("does not contain exactly one nuspec", content, StringComparison.Ordinal);
            Assert.Contains("nuspec ID does not match the expected package ID", content, StringComparison.Ordinal);
            Assert.Contains("nuspec version does not match the expected package version", content, StringComparison.Ordinal);
            Assert.Contains("has no authenticated provenance", content, StringComparison.Ordinal);
            Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", content, StringComparison.Ordinal);
        }

        Assert.Contains("2*) echo \"NuGet.org accepted public package", nuget, StringComparison.Ordinal);
        Assert.Contains("409) echo \"NuGet.org already has public package", nuget, StringComparison.Ordinal);
        Assert.Contains("readback_attempts=13", nuget, StringComparison.Ordinal);
        Assert.Contains("readback_interval_seconds=10", nuget, StringComparison.Ordinal);
        Assert.Contains("readback_max_delay_seconds=120", nuget, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", nuget, StringComparison.Ordinal);
        var authenticatedPut = nuget.Split('\n').Single(line => line.Contains("--request PUT", StringComparison.Ordinal));
        Assert.Contains("--connect-timeout 10 --max-time 60", authenticatedPut, StringComparison.Ordinal);
        Assert.DoesNotContain("--location", authenticatedPut, StringComparison.Ordinal);
        Assert.Contains("--connect-timeout 5 --max-time 15", nuget, StringComparison.Ordinal);

        var tag = Read("tag");
        Assert.Contains("if [[ \"$tag_sha\" != \"$GIZMO_SHA\" ]]", tag, StringComparison.Ordinal);
        Assert.Contains("Release tag already exists for a different commit; refusing to move it.", tag, StringComparison.Ordinal);
        Assert.Contains("200) tag_sha=$(jq -er '.object.sha | strings'", tag, StringComparison.Ordinal);
        Assert.Contains("git/ref/tags/$GIZMO_RELEASE_TAG", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherEntryGuardsRejectPullRequestsAndUnprotectedRefs()
    {
        foreach (var directory in new[] { "nuget", "internal" })
        {
            var step = GetStep(directory, "Validate " + (directory == "nuget" ? "NuGet" : "internal") + " publisher invocation");
            var pullRequest = WorkflowShell.RunBash(step, Path.GetTempPath(), GuardEnvironment("pull_request", "true", "refs/heads/release"));
            Assert.NotEqual(0, pullRequest.ExitCode);
            Assert.Contains("Publishing is allowed only from push.", pullRequest.StandardError, StringComparison.Ordinal);

            var unprotected = WorkflowShell.RunBash(step, Path.GetTempPath(), GuardEnvironment("push", "false", "refs/heads/release"));
            Assert.NotEqual(0, unprotected.ExitCode);
            Assert.Contains("Publishing requires a protected branch ref.", unprotected.StandardError, StringComparison.Ordinal);
        }
        var tagGuard = GetStep("tag", "Validate trusted tag invocation");
        var prTag = WorkflowShell.RunBash(tagGuard, Path.GetTempPath(), GuardEnvironment("pull_request", "true", "refs/heads/release"));
        Assert.NotEqual(0, prTag.ExitCode);
        Assert.Contains("Tagging is allowed only from push.", prTag.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherEntryGuards_AcceptProtectedPushAndNugetRejectsMalformedProfileIdentity()
    {
        foreach (var action in new[] { "nuget", "internal" })
        {
            var stepName = action == "nuget" ? "Validate NuGet publisher invocation" : "Validate internal publisher invocation";
            var step = GetStep(action, stepName);
            var accepted = WorkflowShell.RunBash(step, Path.GetTempPath(), GuardEnvironment("push", "true", "refs/heads/pre-release"));
            Assert.Equal(0, accepted.ExitCode);
        }

        var nugetGuard = GetStep("nuget", "Validate NuGet publisher invocation");
        foreach (var invalid in new[] { "", "contains space", "bad/identity", new string('x', 129) })
        {
            var environment = GuardEnvironment("push", "true", "refs/heads/pre-release");
            environment["NUGET_USER"] = invalid;
            var result = WorkflowShell.RunBash(nugetGuard, Path.GetTempPath(), environment);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("user is missing or invalid", result.StandardError, StringComparison.Ordinal);
        }

        var tagGuard = GetStep("tag", "Validate trusted tag invocation");
        var acceptedTag = WorkflowShell.RunBash(tagGuard, Path.GetTempPath(), GuardEnvironment("push", "true", "refs/heads/release"));
        Assert.Equal(0, acceptedTag.ExitCode);
    }

    private static string GetStep(string action, string name)
    {
        var root = YamlWorkflowReader.Parse(Read(action));
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(item => YamlWorkflowReader.ScalarChild(item, "name") == name);
        return YamlWorkflowReader.ScalarChild(step, "run");
    }

    private static Dictionary<string, string> GuardEnvironment(string eventName, string protectedRef, string reference) => new(StringComparer.Ordinal)
    {
        ["EVENT_NAME"] = eventName, ["REF_PROTECTED"] = protectedRef, ["REF_NAME"] = reference,
        ["NUGET_USER"] = "valid-user",
    };

    private static IEnumerable<string> Keys(YamlMappingNode mapping) => mapping.Children.Keys
        .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);
}
