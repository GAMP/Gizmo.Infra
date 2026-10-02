using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract coverage for the canonical single caller-owned package workflow at
/// <c>.github/templates/package.yml</c>. The template is the only caller file;
/// there is no separate <c>package-validation.yml</c>, <c>package-publish.yml</c>,
/// or <c>.github/package.yml</c> descriptor. Physical branch names are declared
/// exactly once through YAML anchors and passed to the reusable workflows.
/// </summary>
public sealed class CallerTemplateContractTests
{
    private const string TemplatePath = ".github/templates/package.yml";

    // Callers replace this one marker with the same immutable 40-character Gizmo.Infra commit SHA in every reference.
    private const string InfraShaPlaceholder = "<40-character-infra-commit-sha>";
    private const string ValidationWorkflowRef =
        "GAMP/Gizmo.Infra/.github/workflows/package-validation.yml@" + InfraShaPlaceholder;
    private const string PublishWorkflowRef =
        "GAMP/Gizmo.Infra/.github/workflows/package-publish.yml@" + InfraShaPlaceholder;

    // Mutually exclusive publishing jobs use the prepared visibility as the sole routing signal.
    private const string PublicIf =
        "${{ github.event_name != 'pull_request' && needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'public' }}";
    private const string PrivateIf =
        "${{ github.event_name != 'pull_request' && needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility == 'private' }}";
    private const string RejectIf =
        "${{ github.event_name != 'pull_request' && needs.prepare.outputs.branch-role != 'none' && needs.prepare.outputs.repository-visibility != 'public' && needs.prepare.outputs.repository-visibility != 'private' }}";
    private const string TagIf =
        "${{ always() && github.event_name != 'pull_request' && needs.prepare.outputs.branch-role == 'production' && (needs.publish-public.result == 'success' || needs.publish-private.result == 'success') }}";

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
        Template_TriggersOnlyOnPullRequestAndPushAndIsNotReusable();
        Template_PrepareJobIsPushOnlyAndNeverReachableFromDispatch();
        Template_DeclaresOnlyReadRootPermissions();
        Template_OwnsTheSingleNonCancellingCallerRepositoryLock();
        Template_DeclaresExactlyTheSixJobs();
    }

    private void Template_TriggersOnlyOnPullRequestAndPushAndIsNotReusable()
    {
        var triggers = YamlWorkflowReader.MappingChild(Parse(), "on");

        // Canonical caller triggers are exactly pull_request and push; the
        // canonical workflow never starts from a manual dispatch, so a same-SHA
        // recovery re-runs an existing push workflow run instead.
        Assert.True(YamlWorkflowReader.HasChild(triggers, "pull_request"));
        Assert.True(YamlWorkflowReader.HasChild(triggers, "push"));
        Assert.False(YamlWorkflowReader.HasChild(triggers, "workflow_dispatch"));
        Assert.False(YamlWorkflowReader.HasChild(triggers, "workflow_call"));

        // The trigger filter must not duplicate configured branch names; role
        // resolution belongs to the reusable preflight.
        Assert.DoesNotContain("branches:", Content(), StringComparison.Ordinal);
        Assert.DoesNotContain("pre-release", Content(), StringComparison.Ordinal);
    }

    private void Template_PrepareJobIsPushOnlyAndNeverReachableFromDispatch()
    {
        // Preparation runs only on a pushed branch; a manually dispatched run,
        // which the template cannot start anyway, would never reach the reusable
        // publish workflow. Same-SHA recovery is a re-run of an existing push
        // workflow run, not a new dispatch.
        Assert.Equal("${{ github.event_name == 'push' }}", JobIf("prepare"));
        Assert.DoesNotContain("workflow_dispatch", JobIf("prepare"), StringComparison.Ordinal);
        Assert.DoesNotContain("'push' || github.event_name == 'workflow_dispatch'", JobIf("prepare"), StringComparison.Ordinal);
    }

    private void Template_DeclaresOnlyReadRootPermissions()
    {
        var permissions = YamlWorkflowReader.MappingChild(Parse(), "permissions");

        Assert.Single(permissions.Children);
        Assert.Equal("read", YamlWorkflowReader.ScalarChild(permissions, "contents"));
    }

    private void Template_OwnsTheSingleNonCancellingCallerRepositoryLock()
    {
        var concurrency = YamlWorkflowReader.MappingChild(Parse(), "concurrency");

        Assert.Equal("nuget-${{ github.repository }}", YamlWorkflowReader.ScalarChild(concurrency, "group"));
        Assert.Equal("false", YamlWorkflowReader.ScalarChild(concurrency, "cancel-in-progress"));
    }

    private void Template_DeclaresExactlyTheSixJobs()
    {
        Assert.Equal(
            new[] { "prepare", "publish-private", "publish-public", "reject-unsupported-visibility", "tag", "validate" },
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

        // Two reusable workflows (validation + publish preparation) plus the
        // three caller-owned composite actions (public, private, release-tag).
        Assert.Equal(5, shaReferences.Length);
        Assert.All(shaReferences, sha => Assert.Equal(InfraShaPlaceholder, sha));
        Assert.Contains($"uses: {ValidationWorkflowRef}", content, StringComparison.Ordinal);
        Assert.Contains($"uses: {PublishWorkflowRef}", content, StringComparison.Ordinal);
        Assert.Contains(
            $"GAMP/Gizmo.Infra/.github/actions/public@{InfraShaPlaceholder}",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            $"GAMP/Gizmo.Infra/.github/actions/private@{InfraShaPlaceholder}",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            $"GAMP/Gizmo.Infra/.github/actions/tag@{InfraShaPlaceholder}",
            content,
            StringComparison.Ordinal);
        Template_DeclaresDevelopmentAndProductionBranchesOnceAndForwardsBoth();
        Template_ValidateJobIsPullRequestOnly();
        Template_PublisherAndTagJobsDoNotReachPullRequestAndAreGuardedByActionsForDispatch();
    }

    private void Template_DeclaresDevelopmentAndProductionBranchesOnceAndForwardsBoth()
    {
        var content = Content();

        // The two branch names live in env with YAML anchors, then flow to the
        // reusable workflows through the required development-branch and
        // production-branch inputs. No duplicates anywhere in the template.
        Assert.Contains("DEVELOPMENT_BRANCH: &development_branch", content, StringComparison.Ordinal);
        Assert.Contains("PRODUCTION_BRANCH: &production_branch", content, StringComparison.Ordinal);
        Assert.Contains("development-branch: *development_branch", content, StringComparison.Ordinal);
        Assert.Contains("production-branch: *production_branch", content, StringComparison.Ordinal);

        // The branch names must not be duplicated in trigger filters or job conditions.
        Assert.DoesNotContain("branches:", content, StringComparison.Ordinal);

        // The reusable workflows consume the configured branches as inputs.
        Assert.Contains(
            $"uses: {ValidationWorkflowRef}",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            $"uses: {PublishWorkflowRef}",
            content,
            StringComparison.Ordinal);
    }

    private void Template_ValidateJobIsPullRequestOnly()
    {
        var content = Content();

        // A pull request only validates; the if guard skips it on every other event.
        Assert.Contains(
            "github.event_name == 'pull_request'",
            JobIf("validate"),
            StringComparison.Ordinal);

        Assert.DoesNotContain("publish", JobIf("validate"), StringComparison.Ordinal);

        // No publisher or tag job may run for a pull request.
        foreach (var publishingJob in new[] { "publish-public", "publish-private", "reject-unsupported-visibility", "tag" })
        {
            Assert.Contains(
                "github.event_name != 'pull_request'",
                JobIf(publishingJob),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Template_PublishersAreMutuallyExclusiveAndReceiveExactPreparationVisibility()
    {
        Assert.Equal(PublicIf, JobIf("publish-public"));
        Assert.Equal(PrivateIf, JobIf("publish-private"));

        Assert.Equal(VisibilityInput, StepWithValue("publish-public", "repository-visibility"));
        Assert.Equal(VisibilityInput, StepWithValue("publish-private", "repository-visibility"));

        // Routing consumes the authenticated preparation output; nothing may
        // re-derive visibility from the event, an input, or a caller config file.
        Assert.DoesNotContain("github.event.repository.visibility", Content(), StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.", Content(), StringComparison.Ordinal);
        Assert.DoesNotContain(".github/package.yml", Content(), StringComparison.Ordinal);
        Assert.DoesNotContain("branches.release", Content(), StringComparison.Ordinal);
        Template_PublicJobIsOidcOnlyAndPrivateJobIsTokenOnly();
        Template_UnsupportedVisibilityFailsClosedWithoutPublishOrTag();
        Template_TaggingRequiresProductionAndASuccessfulSelectedPublisher();
        Template_PassesThePreparedBranchRoleToTheReleaseTagAction();
        Template_RejectStep_FailsClosedAndIsValidBash();
        Template_NeverInheritsSecretsOrStoresAPermanentKey();
    }

    private void Template_PublicJobIsOidcOnlyAndPrivateJobIsTokenOnly()
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
            $"GAMP/Gizmo.Infra/.github/actions/public@{InfraShaPlaceholder}",
            StepUses("publish-public"));
        Assert.Equal(
            $"GAMP/Gizmo.Infra/.github/actions/private@{InfraShaPlaceholder}",
            StepUses("publish-private"));

        // The public job is OIDC-only and the private job is caller-token-only.
        Assert.DoesNotContain("packages", StepUses("publish-public"), StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", StepUses("publish-private"), StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", StepUses("publish-private"), StringComparison.Ordinal);
    }

    private void Template_UnsupportedVisibilityFailsClosedWithoutPublishOrTag()
    {
        var job = Job("reject-unsupported-visibility");

        Assert.Equal(RejectIf, JobIf("reject-unsupported-visibility"));
        Assert.Empty(Permissions("reject-unsupported-visibility").Children);

        var step = YamlWorkflowReader.MappingSequence(job, "steps").Single();
        Assert.False(YamlWorkflowReader.HasChild(step, "uses"));
        Assert.DoesNotContain("contents: write", YamlWorkflowReader.ScalarChild(step, "run"), StringComparison.Ordinal);
    }

    private void Template_TaggingRequiresProductionAndASuccessfulSelectedPublisher()
    {
        Assert.Equal(TagIf, JobIf("tag"));
        Assert.Contains("branch-role == 'production'", JobIf("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("development", JobIf("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("'release'", JobIf("tag"), StringComparison.Ordinal);

        // Two mutually exclusive publishers yield at most one success, so the OR engages only after the selected publisher succeeded.
        Assert.Contains("needs.publish-public.result == 'success'", JobIf("tag"), StringComparison.Ordinal);
        Assert.Contains("needs.publish-private.result == 'success'", JobIf("tag"), StringComparison.Ordinal);

        var permissions = Permissions("tag");
        Assert.Single(permissions.Children);
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(permissions, "contents"));
    }

    private void Template_PassesThePreparedBranchRoleToTheReleaseTagAction()
    {
        Assert.Equal(
            $"GAMP/Gizmo.Infra/.github/actions/tag@{InfraShaPlaceholder}",
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

    private void Template_RejectStep_FailsClosedAndIsValidBash()
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

    private void Template_NeverInheritsSecretsOrStoresAPermanentKey()
    {
        var content = Content();

        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
    }

    private void Template_PublisherAndTagJobsDoNotReachPullRequestAndAreGuardedByActionsForDispatch()
    {
        // The template gates publishing and tagging jobs on event_name != 'pull_request',
        // so a pull request never reaches them. The prepare job is itself push-only,
        // so a manually dispatched run (which the template cannot start anyway) never
        // reaches them either. The publisher and tag actions also refuse any event
        // other than push as defense in depth.
        foreach (var publishingJob in new[] { "publish-public", "publish-private", "reject-unsupported-visibility", "tag" })
        {
            Assert.Contains(
                "github.event_name != 'pull_request'",
                JobIf(publishingJob),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "workflow_dispatch",
                JobIf(publishingJob),
                StringComparison.Ordinal);
        }
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
