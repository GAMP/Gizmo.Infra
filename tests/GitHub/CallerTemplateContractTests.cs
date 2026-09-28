using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract coverage for the canonical caller-owned publish template at
/// <c>.github/templates/package-publish.yml</c>. The template is the versioned
/// source callers copy, so the tests parse it and execute its fail-closed step
/// to verify the exact mutually exclusive registry routing without GitHub, NuGet,
/// or remote setup.
/// </summary>
public sealed class CallerTemplateContractTests
{
    private const string TemplatePath = ".github/templates/package-publish.yml";

    // Callers replace this one marker with the same immutable 40-character
    // Gizmo.Infra commit SHA in every Infra workflow and action reference.
    private const string InfraShaPlaceholder = "<40-character-infra-commit-sha>";
    private const string InfraWorkflowRef =
        "GAMP/Gizmo.Infra/.github/workflows/package-publish.yml@" + InfraShaPlaceholder;

    private const string PublicIf =
        "${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'public' }}";
    private const string PrivateIf =
        "${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'private' }}";
    private const string RejectIf =
        "${{ needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility != 'public' && needs.prepare.outputs.repository-visibility != 'private' }}";
    private const string TagIf =
        "${{ always() && needs.prepare.outputs.branch-role == 'release' && (needs.publish-public.result == 'success' || needs.publish-private.result == 'success') }}";

    private const string VisibilityInput = "${{ needs.prepare.outputs.repository-visibility }}";

    private static string Content() =>
        File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), TemplatePath));

    private static YamlMappingNode Parse() => YamlWorkflowReader.Parse(Content());

    [Fact]
    public void Template_ExistsAndParsesAsYaml()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(Content());
        yaml.Load(reader);

        Assert.Single(yaml.Documents);
    }

    [Fact]
    public void Template_RunsOnPushAndDispatchAndIsNotReusable()
    {
        var triggers = YamlWorkflowReader.MappingChild(Parse(), "on");

        Assert.True(YamlWorkflowReader.HasChild(triggers, "push"));
        Assert.True(YamlWorkflowReader.HasChild(triggers, "workflow_dispatch"));
        Assert.False(YamlWorkflowReader.HasChild(triggers, "workflow_call"));
        Assert.False(YamlWorkflowReader.HasChild(triggers, "pull_request"));
    }

    [Fact]
    public void Template_DeclaresOnlyReadRootPermissions()
    {
        var permissions = YamlWorkflowReader.MappingChild(Parse(), "permissions");

        Assert.Single(permissions.Children);
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(permissions, "contents"));
    }

    [Fact]
    public void Template_OwnsTheSingleNonCancellingCallerRepositoryLock()
    {
        var concurrency = YamlWorkflowReader.MappingChild(Parse(), "concurrency");

        Assert.Equal("nuget-${{ github.repository }}", YamlWorkflowReader.ScalarChild(concurrency, "group"));
        Assert.Equal("false", YamlWorkflowReader.ScalarChild(concurrency, "cancel-in-progress"));
    }

    [Fact]
    public void Template_DeclaresExactlyThePreparationRoutingAndTagJobs()
    {
        Assert.Equal(
            new[] { "prepare", "publish-private", "publish-public", "reject-unsupported-visibility", "tag" },
            JobNames());
    }

    [Fact]
    public void Template_PinsEveryInfraReferenceToTheSameImmutableShaPlaceholder()
    {
        var content = Content();
        var shaReferences = Regex
            .Matches(content, @"GAMP/Gizmo\.Infra/[^\s@]+@(?<sha>\S+)", RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Groups["sha"].Value)
            .ToArray();

        // One reusable workflow plus the public, private, and release-tag actions.
        Assert.Equal(4, shaReferences.Length);
        Assert.All(shaReferences, sha => Assert.Equal(InfraShaPlaceholder, sha));
        Assert.Contains($"uses: {InfraWorkflowRef}", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_PublishersAreMutuallyExclusiveAndReceiveExactPreparationVisibility()
    {
        Assert.Equal(PublicIf, JobIf("publish-public"));
        Assert.Equal(PrivateIf, JobIf("publish-private"));

        Assert.Equal(VisibilityInput, StepWithValue("publish-public", "repository-visibility"));
        Assert.Equal(VisibilityInput, StepWithValue("publish-private", "repository-visibility"));

        // Routing consumes the single authenticated preparation output; nothing
        // may re-derive visibility from the event payload or a caller input.
        Assert.DoesNotContain("github.event.repository.visibility", Content(), StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.", Content(), StringComparison.Ordinal);
    }

    [Fact]
    public void Template_PublicJobIsOidcOnlyAndPrivateJobIsTokenOnly()
    {
        var publicPermissions = Permissions("publish-public");
        Assert.Equal(new[] { "contents", "id-token" }, PermissionNames("publish-public"));
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(publicPermissions, "contents"));
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(publicPermissions, "id-token"));

        var privatePermissions = Permissions("publish-private");
        Assert.Equal(new[] { "contents", "packages" }, PermissionNames("publish-private"));
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(privatePermissions, "contents"));
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(privatePermissions, "packages"));

        Assert.Equal(
            $"GAMP/Gizmo.Infra/.github/actions/package-public-publish@{InfraShaPlaceholder}",
            StepUses("publish-public"));
        Assert.Equal(
            $"GAMP/Gizmo.Infra/.github/actions/package-private-publish@{InfraShaPlaceholder}",
            StepUses("publish-private"));

        // The public job is OIDC-only and the private job is caller-token-only.
        Assert.DoesNotContain("packages", StepUses("publish-public"), StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", StepUses("publish-private"), StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", StepUses("publish-private"), StringComparison.Ordinal);
    }

    [Fact]
    public void Template_UnsupportedVisibilityFailsClosedWithoutPublishOrTag()
    {
        var job = Job("reject-unsupported-visibility");

        Assert.Equal(RejectIf, JobIf("reject-unsupported-visibility"));
        Assert.Empty(Permissions("reject-unsupported-visibility").Children);

        var step = YamlWorkflowReader.MappingSequence(job, "steps").Single();
        Assert.False(YamlWorkflowReader.HasChild(step, "uses"));
        Assert.DoesNotContain("contents: write", YamlWorkflowReader.ScalarChild(step, "run"), StringComparison.Ordinal);
    }

    [Fact]
    public void Template_TaggingRequiresReleaseAndASuccessfulSelectedPublisher()
    {
        Assert.Equal(TagIf, JobIf("tag"));
        Assert.Contains("branch-role == 'release'", JobIf("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("development", JobIf("tag"), StringComparison.Ordinal);

        // Two mutually exclusive publishers publish at most one success, so the OR
        // engages only after the selected publisher actually succeeded.
        Assert.Contains("needs.publish-public.result == 'success'", JobIf("tag"), StringComparison.Ordinal);
        Assert.Contains("needs.publish-private.result == 'success'", JobIf("tag"), StringComparison.Ordinal);

        var permissions = Permissions("tag");
        Assert.Single(permissions.Children);
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(permissions, "contents"));
    }

    [Fact]
    public void Template_PassesThePreparedBranchRoleToTheReleaseTagAction()
    {
        Assert.Equal(
            $"GAMP/Gizmo.Infra/.github/actions/package-release-tag@{InfraShaPlaceholder}",
            StepUses("tag"));
        Assert.Equal("${{ needs.prepare.outputs.branch-role }}", StepWithValue("tag", "branch-role"));
        Assert.Equal("${{ needs.prepare.outputs.release-tag }}", StepWithValue("tag", "release-tag"));
        Assert.Equal(
            "${{ needs.prepare.outputs.calculated-state }}",
            StepWithValue("tag", "calculated-state"));
        Assert.Equal(
            "${{ needs.prepare.outputs.tag-state-fingerprint }}",
            StepWithValue("tag", "tag-state-fingerprint"));
    }

    [Fact]
    public void Template_RejectStep_FailsClosedAndIsValidBash()
    {
        var run = RejectRun();

        var syntax = WorkflowShell.CheckBashSyntax(run);
        Assert.True(syntax.ExitCode == 0, $"reject step is not valid bash: {syntax.StandardError}");

        var result = WorkflowShell.RunBash(
            run,
            Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["REPOSITORY_VISIBILITY"] = "internal" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported caller repository visibility 'internal'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_NeverInheritsSecretsOrStoresAPermanentKey()
    {
        var content = Content();

        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
    }

    private static string[] JobNames() =>
        Jobs(Parse()).Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static YamlMappingNode Jobs(YamlMappingNode root) => YamlWorkflowReader.MappingChild(root, "jobs");

    private static YamlMappingNode Job(string name) => YamlWorkflowReader.MappingChild(Jobs(Parse()), name);

    private static YamlMappingNode Permissions(string job) => YamlWorkflowReader.MappingChild(Job(job), "permissions");

    private static string[] PermissionNames(string job) =>
        Permissions(job).Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string JobIf(string job) => YamlWorkflowReader.ScalarChild(Job(job), "if");

    private static YamlMappingNode SingleStep(string job) =>
        YamlWorkflowReader.MappingSequence(Job(job), "steps").Single();

    private static string StepUses(string job) => YamlWorkflowReader.ScalarChild(SingleStep(job), "uses");

    private static string RejectRun() => YamlWorkflowReader.ScalarChild(SingleStep("reject-unsupported-visibility"), "run");

    private static YamlMappingNode StepWith(string job) =>
        YamlWorkflowReader.MappingChild(SingleStep(job), "with");

    private static string StepWithValue(string job, string key) =>
        YamlWorkflowReader.ScalarChild(StepWith(job), key);
}
