using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Static contract coverage for the two direct reusable <c>workflow_call</c>
/// workflows Gizmo.Infra publishes: the validation workflow and the non-mutating
/// publish preparation workflow. The tests read the committed YAML and never
/// render or execute it, so they hold without GitHub, NuGet, or remote setup.
/// </summary>
public sealed class ReusableWorkflowContractTests
{
    private const string ValidationFile = "package-validation.yml";
    private const string PublishFile = "package-publish.yml";

    private static readonly string[] ContractFiles = [ValidationFile, PublishFile];

    // The publish workflow is a preparation authority only: it must export the
    // caller-routing outputs and never request a publication credential, contact a
    // package feed, publish, or mutate a tag.
    private static readonly string[] CallerRoutingOutputs =
    [
        "package-artifact",
        "package-version",
        "release-tag",
        "release-tag-state",
        "calculated-state",
        "tag-state-fingerprint",
        "package-id",
        "branch-role",
        "repository-visibility",
    ];

    // Reruns of the same workflow run must not collide on the uploaded artifact
    // name, so the attempt discriminator is part of the contract.
    private const string ArtifactName = "nuget-package-${{ github.run_id }}-${{ github.run_attempt }}";

    // The caller template and the direct validation workflow share one
    // caller-repository lock; the preflight derives the package from the
    // workspace, so the group is not per-package.
    private const string ConcurrencyGroup = "nuget-${{ github.repository }}";

    // The preflight action is the only authority for the publish branch role; the
    // expensive steps all skip when it resolves to none.
    private const string PublishableBranchRole = "${{ steps.metadata.outputs.branch-role != 'none' }}";

    // Finalized Node 24 action releases. Every workflow that declares one of
    // these actions must use exactly this commit and matching release comment, so
    // a partial upgrade that leaves any action on a stale major fails here.
    private const string CheckoutPin =
        "uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0";
    private const string SetupDotnetPin =
        "uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0";
    private const string UploadArtifactPin =
        "uses: actions/upload-artifact@b7c566a772e6b6bfb58ed0dc250532a479d7789f # v6.0.0";

    // Validation run 35636544410 failed in setup because actions/setup-dotnet
    // resolved an invalid v4.0.0 commit; the corrected pin above must hold and
    // the rejected commit must never come back.
    private const string RejectedSetupDotnetSha = "d4c94342e560b34958e1a5f7d17e66c4b9131d1f";

    private static readonly Regex UsesLine = new(
        @"^\s*uses:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex PinnedUsesLine = new(
        @"^\s*uses:\s+[^\s@]+@[0-9a-f]{40}\s+#\s+v[0-9][^\s]*\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // The bundled preflight is the only permitted non-remote action reference.
    private static readonly Regex LocalUsesLine = new(
        @"^\s*uses:\s+\./[^\s]+\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void WorkflowDirectory_ContainsExactlyTheTwoContractFiles()
    {
        var directory = Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "workflows");

        // Both .yml and .yaml are workflow files, so enumerating only *.yml would
        // silently accept an extra workflow spelled with the other extension.
        var files = Directory.EnumerateFiles(directory)
            .Where(path =>
                string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ContractFiles.OrderBy(name => name, StringComparer.Ordinal).ToArray(), files);
    }

    [Fact]
    public void EachFile_IsADirectWorkflowCallWorkflow()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);
            var root = YamlWorkflowReader.Parse(content);
            var triggers = YamlWorkflowReader.MappingChild(root, "on");

            Assert.True(YamlWorkflowReader.HasChild(triggers, "workflow_call"), $"{file} must be reusable.");
            Assert.False(YamlWorkflowReader.HasChild(triggers, "push"), $"{file} must not run on push.");
            Assert.False(YamlWorkflowReader.HasChild(triggers, "pull_request"), $"{file} must not run on pull_request.");
            Assert.False(YamlWorkflowReader.HasChild(triggers, "workflow_dispatch"), $"{file} must not run on dispatch.");

            // Direct files replace the retired renderer output; a generated header would
            // mean a consumer-installed tool still owns the workflow.
            Assert.DoesNotContain("<gizmo-infra-generated>", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet tool install Gizmo.Infra", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachFile_DeclaresNoCallerSuppliedInputs()
    {
        // The preflight discovers the project, package ID, compatibility line,
        // branch role, and visibility; the publish workflow adds only outputs, so
        // no workflow declares any caller-supplied input.
        foreach (var file in ContractFiles)
        {
            var root = Parse(file);
            var workflowCall = YamlWorkflowReader.Child(
                YamlWorkflowReader.MappingChild(root, "on"), "workflow_call");

            if (workflowCall is YamlMappingNode mapping)
            {
                Assert.False(
                    YamlWorkflowReader.HasChild(mapping, "inputs"),
                    $"{file} must not declare workflow_call inputs.");
            }

            Assert.Null(WorkflowCallInputs(root));

            // The compatibility line comes from the caller project; no descriptor,
            // patch, project path, package ID, visibility, nuget user, or workflow
            // version input may exist.
            foreach (var forbidden in new[]
                     {
                         "version", "package-version", "version-override", "patch", "prerelease",
                         "project-path", "package-id", "package-visibility", "repository-visibility",
                         "nuget-user", "require-nuget-user",
                     })
            {
                Assert.DoesNotContain($"inputs.{forbidden}", Read(file), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PublishWorkflow_ExportsCallerRoutingOutputs()
    {
        var root = Parse(PublishFile);
        var call = YamlWorkflowReader.MappingChild(
            YamlWorkflowReader.MappingChild(root, "on"), "workflow_call");
        var workflowOutputs = YamlWorkflowReader.MappingChild(call, "outputs");
        var jobOutputs = Outputs(Job(root, "build"));

        foreach (var output in CallerRoutingOutputs)
        {
            var workflowOutput = YamlWorkflowReader.MappingChild(workflowOutputs, output);
            Assert.Contains(
                $"jobs.build.outputs.{output}",
                YamlWorkflowReader.ScalarChild(workflowOutput, "value"),
                StringComparison.Ordinal);
            Assert.True(
                YamlWorkflowReader.HasChild(jobOutputs, output),
                $"the build job must expose the '{output}' output.");
        }

        // Identity and routing outputs come from the always-running discovery step,
        // not from a step that skips when the branch role is none.
        Assert.Equal(
            "${{ steps.metadata.outputs.branch-role }}",
            YamlWorkflowReader.ScalarChild(jobOutputs, "branch-role"));
        Assert.Equal(
            "${{ steps.metadata.outputs.repository-visibility }}",
            YamlWorkflowReader.ScalarChild(jobOutputs, "repository-visibility"));

        // The exported visibility is routing data only; the workflow must not
        // branch on it or read an event-specific visibility.
        Assert.DoesNotContain(
            "github.event.repository.visibility",
            Read(PublishFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishWorkflow_IsANonMutatingPreparationAuthority()
    {
        var content = Read(PublishFile);

        foreach (var forbidden in new[]
                 {
                     "dotnet nuget push", "id-token", "ACTIONS_ID_TOKEN",
                     "api.nuget.org", "nuget.pkg.github.com", "git/refs",
                     "contents: write", "packages: write", "packages: read",
                     "actions/download-artifact", "NUGET_API_KEY", "NUGET_TOKEN", "secrets:",
                 })
        {
            Assert.DoesNotContain(forbidden, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublishWorkflow_DeclaresOnlyThePreparationJob()
    {
        var jobs = Jobs(Parse(PublishFile));

        var names = jobs.Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "build" }, names);
    }

    [Fact]
    public void EveryWorkflowRunBlock_IsSyntacticallyValidBash()
    {
        foreach (var file in ContractFiles)
        {
            foreach (var job in Jobs(Parse(file)).Children.Values.OfType<YamlMappingNode>())
            {
                if (!YamlWorkflowReader.HasChild(job, "steps"))
                {
                    continue;
                }

                foreach (var step in Steps(job))
                {
                    if (!YamlWorkflowReader.HasChild(step, "run"))
                    {
                        continue;
                    }

                    var result = WorkflowShell.CheckBashSyntax(YamlWorkflowReader.ScalarChild(step, "run"));
                    Assert.True(
                        result.ExitCode == 0,
                        $"{file}: '{YamlWorkflowReader.ScalarChild(step, "name")}' is not valid bash: {result.StandardError}");
                }
            }
        }
    }

    [Fact]
    public void RootPermissions_AreEmptySoCallersMustGrantEveryPrivilege()
    {
        foreach (var file in ContractFiles)
        {
            Assert.Empty(Permissions(Parse(file)).Children);
        }
    }

    [Fact]
    public void ValidationWorkflow_UsesANonCancellingCallerRepositoryGroup()
    {
        // Validation is called directly and owns its own caller-repository lock.
        var concurrency = YamlWorkflowReader.MappingChild(Parse(ValidationFile), "concurrency");
        var group = YamlWorkflowReader.ScalarChild(concurrency, "group");

        Assert.Equal(ConcurrencyGroup, group);
        Assert.Contains("github.repository", group, StringComparison.Ordinal);
        Assert.DoesNotContain("package-id", group, StringComparison.Ordinal);
        Assert.Equal("false", YamlWorkflowReader.ScalarChild(concurrency, "cancel-in-progress"));

        Assert.DoesNotContain("cancel-in-progress: true", Read(ValidationFile), StringComparison.Ordinal);
    }

    [Fact]
    public void PublishPreparationWorkflow_DeclaresNoConcurrency()
    {
        // The canonical caller template owns the caller-repository lock around
        // prepare -> publish -> tag. The reusable preparation workflow must not
        // redeclare the same group, because a nested evaluation of the same lock
        // can deadlock the run against itself.
        var content = Read(PublishFile);

        Assert.DoesNotContain("concurrency", content, StringComparison.Ordinal);
        Assert.DoesNotContain(ConcurrencyGroup, content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJobs_AreReadOnly()
    {
        foreach (var file in ContractFiles)
        {
            var permissions = Permissions(BuildJob(Parse(file), file));

            Assert.Equal("read", YamlWorkflowReader.ScalarChild(permissions, "contents"));
            Assert.Single(permissions.Children);
        }
    }

    [Fact]
    public void ValidationWorkflow_CannotPublishTagOrElevate()
    {
        var content = Read(ValidationFile);

        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", content, StringComparison.Ordinal);
        Assert.DoesNotContain("packages", content, StringComparison.Ordinal);
        Assert.DoesNotContain("git/refs", content, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", content, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWorkflow_InheritsSecretsOrStoresAPermanentKey()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);

            Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_TOKEN", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryAction_IsPinnedToAFullCommitShaWithAReleaseComment()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);
            var declared = UsesLine.Matches(content).Cast<Match>().ToArray();

            Assert.NotEmpty(declared);

            // The bundled preflight is a local action reference; every remote
            // action must be pinned to a full commit SHA with a release comment.
            Assert.Equal(
                declared.Length,
                PinnedUsesLine.Matches(content).Count + LocalUsesLine.Matches(content).Count);
            Assert.Contains(
                "uses: ./.gizmo-infra/.github/actions/package-preflight",
                content,
                StringComparison.Ordinal);
            Assert.DoesNotContain("uses: actions/checkout@v", content, StringComparison.Ordinal);
            Assert.DoesNotContain("@main", content, StringComparison.Ordinal);
            Assert.DoesNotContain("@master", content, StringComparison.Ordinal);
        }

        foreach (var file in ContractFiles)
        {
            var content = Read(file);
            Assert.Matches("actions/checkout@[0-9a-f]{40} # v", content);
            Assert.Matches("actions/setup-dotnet@[0-9a-f]{40} # v", content);
            Assert.Matches("actions/upload-artifact@[0-9a-f]{40} # v", content);
        }
    }

    [Theory]
    [InlineData(ValidationFile, "actions/checkout", CheckoutPin)]
    [InlineData(PublishFile, "actions/checkout", CheckoutPin)]
    [InlineData(ValidationFile, "actions/setup-dotnet", SetupDotnetPin)]
    [InlineData(PublishFile, "actions/setup-dotnet", SetupDotnetPin)]
    [InlineData(ValidationFile, "actions/upload-artifact", UploadArtifactPin)]
    [InlineData(PublishFile, "actions/upload-artifact", UploadArtifactPin)]
    public void Actions_ArePinnedToTheFinalNode24Release(string file, string action, string expectedPin)
    {
        var uses = ActionUses(Read(file), action);

        Assert.NotEmpty(uses);
        Assert.All(uses, use => Assert.Equal(expectedPin, use));
    }

    [Fact]
    public void SetupDotnet_NeverReintroducesTheRejectedCommit()
    {
        foreach (var file in ContractFiles)
        {
            Assert.DoesNotContain(RejectedSetupDotnetSha, Read(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Builds_UseTheNet11Sdk()
    {
        foreach (var file in ContractFiles)
        {
            // The direct workflows intentionally moved to the user-configured
            // .NET 11 SDK; a reintroduced 10.x pin must fail here.
            Assert.Contains("dotnet-version: 11.0.x", Read(file), StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet-version: 10.", Read(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CheckoutSteps_DisablePersistedCredentials()
    {
        foreach (var file in ContractFiles)
        {
            var checkouts = CheckoutSteps(BuildJob(Parse(file), file));

            // Both the caller checkout and the immutable Gizmo.Infra source checkout
            // must run without persisted credentials.
            Assert.Equal(2, checkouts.Count);
            Assert.All(checkouts, checkout =>
                Assert.Equal(
                    "false",
                    YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(checkout, "with"), "persist-credentials")));
        }

        // Publishing must build the caller's exact commit, not a mutable ref.
        var publishWith = YamlWorkflowReader.MappingChild(
            Step(Job(Parse(PublishFile), "build"), "Checkout caller commit"), "with");
        Assert.Equal("${{ github.sha }}", YamlWorkflowReader.ScalarChild(publishWith, "ref"));
    }

    [Fact]
    public void ImmutableInfraSource_ValidatesJobWorkflowRefAndShaAlignment()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);
            var build = BuildJob(Parse(file), file);

            // github.workflow_* describes the caller's workflow, not this called
            // reusable workflow, so only job.workflow_* can pin the source commit.
            Assert.DoesNotContain("github.workflow_ref", content, StringComparison.Ordinal);
            Assert.DoesNotContain("github.workflow_sha", content, StringComparison.Ordinal);

            var guard = StepById(build, "infra-source");
            var env = YamlWorkflowReader.MappingChild(guard, "env");
            Assert.Equal("${{ job.workflow_repository }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_REPOSITORY"));
            Assert.Equal("${{ job.workflow_ref }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_REF"));
            Assert.Equal("${{ job.workflow_sha }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_SHA"));
            Assert.Equal("${{ job.workflow_file_path }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_FILE_PATH"));

            var run = YamlWorkflowReader.ScalarChild(guard, "run");
            Assert.Contains("\"$WORKFLOW_REPOSITORY\" != GAMP/Gizmo.Infra", run, StringComparison.Ordinal);
            Assert.Contains($"\"$WORKFLOW_FILE_PATH\" != .github/workflows/{file}", run, StringComparison.Ordinal);

            // The resolved job.workflow_sha is a commit, not proof the caller used
            // a full SHA; job.workflow_ref's path@sha must carry the same complete
            // 40-character commit before the source is trusted.
            Assert.Contains($"expected_workflow_ref=\"GAMP/Gizmo.Infra/.github/workflows/{file}@\"", run, StringComparison.Ordinal);
            Assert.Contains("workflow_ref_sha=${WORKFLOW_REF#\"$expected_workflow_ref\"}", run, StringComparison.Ordinal);
            Assert.Contains("\"$workflow_ref_sha\" == \"$WORKFLOW_REF\"", run, StringComparison.Ordinal);
            Assert.Contains("[[ ! \"$workflow_ref_sha\" =~ ^[0-9a-f]{40}$ ]]", run, StringComparison.Ordinal);
            Assert.Contains("\"$workflow_ref_sha\" != \"$WORKFLOW_SHA\"", run, StringComparison.Ordinal);
            Assert.Contains("[[ ! \"$WORKFLOW_SHA\" =~ ^[0-9a-f]{40}$ ]]", run, StringComparison.Ordinal);
            Assert.Contains("printf 'repository=%s\\nref=%s\\n'", run, StringComparison.Ordinal);

            // The immutable source checkout consumes only the validated identity.
            var checkout = Steps(build).Single(step =>
                YamlWorkflowReader.ScalarChild(step, "name") == "Checkout immutable Gizmo.Infra action source");
            var with = YamlWorkflowReader.MappingChild(checkout, "with");
            Assert.Equal(
                "${{ steps.infra-source.outputs.repository }}",
                YamlWorkflowReader.ScalarChild(with, "repository"));
            Assert.Equal("${{ steps.infra-source.outputs.ref }}", YamlWorkflowReader.ScalarChild(with, "ref"));
            Assert.Equal(".gizmo-infra", YamlWorkflowReader.ScalarChild(with, "path"));
            Assert.Equal("false", YamlWorkflowReader.ScalarChild(with, "persist-credentials"));
        }
    }

    [Fact]
    public void EveryWorkflow_DelegatesCallerValidationToThePreflightAction()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);

            Assert.Contains("set -euo pipefail", content, StringComparison.Ordinal);
            Assert.Contains(
                "uses: ./.gizmo-infra/.github/actions/package-preflight",
                content,
                StringComparison.Ordinal);

            // Caller package metadata is discovered and validated inside the
            // preflight, never accepted as an input and never read from an
            // event-specific repository context.
            Assert.DoesNotContain("github.event.repository.visibility", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CompatibilityLine_ComesFromThePreflightAndIsNeverOverriddenAtPackTime()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);

            // The evaluated project Version is a compatibility line only; the
            // workflow consumes the preflight's value rather than re-evaluating
            // MSBuild itself.
            Assert.Contains(
                "COMPATIBILITY_LINE: ${{ steps.metadata.outputs.compatibility-line }}",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "PACKAGE_ID: ${{ steps.metadata.outputs.package-id }}",
                content,
                StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet msbuild", content, StringComparison.Ordinal);
            Assert.DoesNotContain("-getProperty:", content, StringComparison.Ordinal);

            // The project Version is a compatibility line only; no pack-time project
            // version override is passed.
            Assert.DoesNotContain("-p:Version=", content, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:VersionPrefix=", content, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:VersionSuffix=", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--version ", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Packaging_PassesTheCalculatedVersionToPack()
    {
        var packStepNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ValidationFile] = "Pack calculated validation version",
            [PublishFile] = "Pack calculated package version",
        };

        foreach (var file in ContractFiles)
        {
            var pack = Step(BuildJob(Parse(file), file), packStepNames[file]);

            Assert.Equal(
                "${{ steps.version-state.outputs.package-version }}",
                YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(pack, "env"), "CALCULATED_VERSION"));
            Assert.Contains(
                @"dotnet pack ""$PROJECT_PATH"" --configuration Release --no-restore --output artifacts -p:RepositoryCommit=""$GITHUB_SHA"" -p:PackageVersion=""$CALCULATED_VERSION""",
                YamlWorkflowReader.ScalarChild(pack, "run"),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TagDiscovery_PaginatesTheCompleteExactPackagePrefix()
    {
        foreach (var file in ContractFiles)
        {
            var build = VersionStateScript(file);

            Assert.Contains(
                "git/matching-refs/tags/$PACKAGE_ID/?per_page=100&page=$page",
                build,
                StringComparison.Ordinal);
            Assert.Contains("count=$(jq -er 'length' \"$response_file\")", build, StringComparison.Ordinal);
            Assert.Contains("if (( count < 100 )); then", build, StringComparison.Ordinal);
            Assert.Contains("tag_prefix=\"refs/tags/${PACKAGE_ID}/\"", build, StringComparison.Ordinal);
            Assert.Contains(
                @"all(.[]; (.ref | type == ""string"") and (.object | type == ""object"") and (.object.type | type == ""string"") and (.object.sha | type == ""string""))",
                build,
                StringComparison.Ordinal);

            // Malformed tags under the exact package prefix are a hard failure,
            // never silently ignored or treated as another package's tag.
            Assert.Contains("GitHub returned a tag outside the requested package prefix.", build, StringComparison.Ordinal);
            Assert.Contains("Malformed tag under the exact package prefix: $ref", build, StringComparison.Ordinal);
            Assert.Contains("exit 1", build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TagResolution_ResolvesAnnotatedTagsToTheirCommit()
    {
        foreach (var file in ContractFiles)
        {
            var build = VersionStateScript(file);

            Assert.Contains("resolve_commit() {", build, StringComparison.Ordinal);
            Assert.Contains("git/tags/$object_sha", build, StringComparison.Ordinal);
            Assert.Contains("case \"$object_type\" in", build, StringComparison.Ordinal);
            Assert.Contains("commit) printf '%s' \"$object_sha\"; return 0 ;;", build, StringComparison.Ordinal);
            Assert.Contains("depth > 16", build, StringComparison.Ordinal);
            Assert.Contains("Could not resolve an annotated package tag.", build, StringComparison.Ordinal);
            Assert.Contains("GitHub returned a malformed annotated package-tag response.", build, StringComparison.Ordinal);
            Assert.Contains("Package tag ref does not resolve to a commit.", build, StringComparison.Ordinal);
            Assert.Contains("tag_commit=$(resolve_commit \"$object_type\" \"$object_sha\")", build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TagGrammar_IsPackageQualifiedThreeXStable()
    {
        foreach (var file in ContractFiles)
        {
            var build = VersionStateScript(file);

            Assert.Contains(@"^v3\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", build, StringComparison.Ordinal);
            Assert.Contains(@"tag_line=""3.${BASH_REMATCH[1]}""", build, StringComparison.Ordinal);
            Assert.Contains("tag_patch=${BASH_REMATCH[2]}", build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PatchSelection_BootstrapsAtZeroAndIncrementsTheLineMaximum()
    {
        foreach (var file in ContractFiles)
        {
            var build = VersionStateScript(file);

            Assert.Contains("max_patch=''", build, StringComparison.Ordinal);
            Assert.Contains("[[ \"$tag_line\" == \"$COMPATIBILITY_LINE\" ]]", build, StringComparison.Ordinal);
            Assert.Contains("(( ${#tag_patch} > ${#max_patch} ))", build, StringComparison.Ordinal);
            Assert.Contains("[[ \"$tag_patch\" > \"$max_patch\" ]]", build, StringComparison.Ordinal);
            Assert.Contains("patch=0", build, StringComparison.Ordinal);
            Assert.Contains("patch=$(increment_decimal \"$max_patch\")", build, StringComparison.Ordinal);
            Assert.Contains("increment_decimal() {", build, StringComparison.Ordinal);
            Assert.Contains("base_version=\"${COMPATIBILITY_LINE}.${patch}\"", build, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VersionDerivation_IsOperationSpecific()
    {
        Assert.Contains(
            "package_version=\"${base_version}-pr.${GITHUB_RUN_NUMBER}\"",
            VersionStateScript(ValidationFile),
            StringComparison.Ordinal);

        var publish = VersionStateScript(PublishFile);
        Assert.Contains(
            "package_version=\"${base_version}-dev.${GITHUB_RUN_NUMBER}\"",
            publish,
            StringComparison.Ordinal);
        Assert.Contains("package_version=$base_version", publish, StringComparison.Ordinal);
        Assert.Contains("release_tag=\"${PACKAGE_ID}/v${base_version}\"", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("-pr.", publish, StringComparison.Ordinal);

        foreach (var file in ContractFiles)
        {
            Assert.Contains(
                "package_artifact=\"artifacts/${PACKAGE_ID}.${package_version}.nupkg\"",
                Read(file),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildEmitsFullCalculatedStateAndAFingerprint()
    {
        foreach (var file in ContractFiles)
        {
            var outputs = Outputs(BuildJob(Parse(file), file));
            Assert.Equal(
                "${{ steps.version-state.outputs.calculated-state }}",
                YamlWorkflowReader.ScalarChild(outputs, "calculated-state"));
            Assert.Equal(
                "${{ steps.version-state.outputs.tag-state-fingerprint }}",
                YamlWorkflowReader.ScalarChild(outputs, "tag-state-fingerprint"));

            var build = VersionStateScript(file);
            Assert.Contains(
                "tag_state_fingerprint=$(printf '%s' \"$tag_snapshot\" | sha256sum | cut -d ' ' -f1)",
                build,
                StringComparison.Ordinal);
            Assert.Contains(
                ";compatibility-line=${COMPATIBILITY_LINE};base-version=${base_version};package-version=${package_version};",
                build,
                StringComparison.Ordinal);
            Assert.Contains("tag-state=${tag_state}", build, StringComparison.Ordinal);
        }

        Assert.Contains(
            "release-tag=${release_tag};release-tag-state=${release_tag_state};current-sha-tags=${current_tag_list};tag-state=${tag_state}",
            VersionStateScript(PublishFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishWorkflow_UsesOnlyThePreflightBranchRoleForMode()
    {
        var content = Read(PublishFile);

        // The preflight resolver output is the sole mode authority for the
        // preparation workflow; the workflow must not reparse the caller config.
        Assert.Contains(
            "BRANCH_ROLE: ${{ steps.metadata.outputs.branch-role }}",
            content,
            StringComparison.Ordinal);
        Assert.Contains("case \"$BRANCH_ROLE\" in", content, StringComparison.Ordinal);
        Assert.Contains("development|release) ;;", content, StringComparison.Ordinal);
        Assert.Contains(
            "The package preflight did not resolve a publishable branch role.",
            content,
            StringComparison.Ordinal);

        // No duplicate branch-name interpretation: the caller config is never
        // parsed here and no static branch filter shadows the resolver.
        Assert.DoesNotContain(".github/package.yml", content, StringComparison.Ordinal);
        Assert.DoesNotContain("package.yml", content, StringComparison.Ordinal);
        Assert.DoesNotContain("refs/heads/", content, StringComparison.Ordinal);
        Assert.DoesNotContain("github.ref_name", content, StringComparison.Ordinal);
        Assert.DoesNotContain("github.base_ref", content, StringComparison.Ordinal);
        Assert.DoesNotContain("github.head_ref", content, StringComparison.Ordinal);
    }

    [Fact]
    public void NoneMode_SkipsEveryExpensiveStep()
    {
        var build = BuildJob(Parse(PublishFile), PublishFile);

        // The version calculation performs the tag lookup, and the restore, build,
        // pack, and upload all cost work; every one must skip for none while the
        // cheap preflight still resolves the role.
        foreach (var step in new[]
                 {
                     "Calculate package version and tag state",
                     "Restore with NuGet audit",
                     "Build",
                     "Pack calculated package version",
                     "Upload exact package artifact",
                 })
        {
            Assert.Equal(PublishableBranchRole, YamlWorkflowReader.ScalarChild(Step(build, step), "if"));
        }

        // The mode guard rejects any unexpected role rather than silently doing work.
        Assert.Contains("*) echo \"The package preflight did not resolve a publishable branch role.\" >&2; exit 1 ;;", VersionStateScript(PublishFile), StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataPreflight_PrecedesRestoreAndPack()
    {
        foreach (var file in ContractFiles)
        {
            var build = BuildJob(Parse(file), file);
            var metadata = StepById(build, "metadata");

            // The bundled preflight is the single discovery step; every later step
            // consumes its outputs instead of re-discovering caller metadata.
            Assert.Equal(
                "./.gizmo-infra/.github/actions/package-preflight",
                YamlWorkflowReader.ScalarChild(metadata, "uses"));

            var names = StepNames(build);
            var metadataIndex = names.IndexOf(YamlWorkflowReader.ScalarChild(metadata, "name"));
            Assert.True(metadataIndex >= 0, $"{file} must declare the metadata preflight step.");
            Assert.True(metadataIndex < names.IndexOf("Restore with NuGet audit"));
            Assert.True(
                metadataIndex < names.FindIndex(name => name.StartsWith("Pack calculated", StringComparison.Ordinal)),
                $"{file} must discover caller metadata before packing.");
        }
    }

    [Fact]
    public void ReleaseBuild_CalculatesStateAndAppliesTagRulesBeforePackaging()
    {
        var names = StepNames(Job(Parse(PublishFile), "build"));

        var stateIndex = names.IndexOf("Calculate package version and tag state");
        Assert.True(stateIndex >= 0);
        Assert.True(stateIndex < names.IndexOf("Restore with NuGet audit"));
        Assert.True(stateIndex < names.FindIndex(name => name.StartsWith("Pack calculated", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReleaseRerun_ReusesExactlyOneCurrentShaTagAndFailsClosedOnAmbiguity()
    {
        var build = Job(Parse(PublishFile), "build");
        var outputs = Outputs(build);
        Assert.Equal(
            "${{ steps.version-state.outputs.release-tag-state }}",
            YamlWorkflowReader.ScalarChild(outputs, "release-tag-state"));
        Assert.Equal(
            "${{ steps.version-state.outputs.release-tag }}",
            YamlWorkflowReader.ScalarChild(outputs, "release-tag"));

        var script = VersionStateScript(PublishFile);
        Assert.Contains("current_tags=()", script, StringComparison.Ordinal);
        Assert.Contains(
            "if [[ \"$tag_commit\" == \"$GITHUB_SHA\" ]]; then current_tags+=(\"$tag_leaf\"); fi",
            script,
            StringComparison.Ordinal);
        Assert.Contains("case ${#current_tags[@]} in", script, StringComparison.Ordinal);
        Assert.Contains("release_tag_state=missing", script, StringComparison.Ordinal);
        Assert.Contains("release_tag_state=present", script, StringComparison.Ordinal);
        Assert.Contains(
            "Multiple package/compatibility-line tags point to the caller commit; refusing ambiguous release rerun.",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Current-SHA package tag is malformed.", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndUpload_HandOffTheExactPackageArtifact()
    {
        foreach (var file in ContractFiles)
        {
            var buildJob = BuildJob(Parse(file), file);
            Assert.Contains(
                "package_artifact=\"artifacts/${PACKAGE_ID}.${package_version}.nupkg\"",
                Read(file),
                StringComparison.Ordinal);

            var upload = Step(buildJob, "Upload exact package artifact");
            var uploadWith = YamlWorkflowReader.MappingChild(upload, "with");
            Assert.Equal(ArtifactName, YamlWorkflowReader.ScalarChild(uploadWith, "name"));
            Assert.Contains("github.run_id", ArtifactName, StringComparison.Ordinal);
            Assert.Contains("github.run_attempt", ArtifactName, StringComparison.Ordinal);
            Assert.Equal("${{ steps.version-state.outputs.package-artifact }}", YamlWorkflowReader.ScalarChild(uploadWith, "path"));
            Assert.Equal("error", YamlWorkflowReader.ScalarChild(uploadWith, "if-no-files-found"));

            var outputs = Outputs(buildJob);
            Assert.True(YamlWorkflowReader.HasChild(outputs, "package-version"));
        }

        // The publish preparation advertises the exact artifact path its
        // caller-owned publishers download; validation only retains it.
        Assert.True(
            YamlWorkflowReader.HasChild(Outputs(Job(Parse(PublishFile), "build")), "package-artifact"));
    }

    [Fact]
    public void Restore_AppliesTheNuGetAuditPolicy()
    {
        foreach (var file in ContractFiles)
        {
            var script = Run(BuildJob(Parse(file), file), "Restore with NuGet audit");

            Assert.Contains("-p:NuGetAudit=true", script, StringComparison.Ordinal);
            Assert.Contains("-p:NuGetAuditMode=all", script, StringComparison.Ordinal);
            Assert.Contains("-p:NuGetAuditLevel=low", script, StringComparison.Ordinal);

            // MSBuild parses a '-p:' value as semicolon-separated name=value
            // pairs, so an unescaped 'NU1904;NU1903' splits 'NU1903' into its
            // own switch and fails the Bash restore with MSB1006. Only the %3B
            // escape survives, with or without shell quoting around the value.
            Assert.Contains("-p:WarningsAsErrors=NU1904%3BNU1903", script, StringComparison.Ordinal);
            Assert.Contains("-p:WarningsNotAsErrors=NU1901%3BNU1902", script, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"-p:Warnings(?:Not)?AsErrors=(?:""[^""\r\n]*;|NU\d+;)", script);
            Assert.DoesNotContain("-p:WarningsAsErrors=\"NU1904;NU1903\"", script, StringComparison.Ordinal);
            Assert.DoesNotContain("-p:WarningsNotAsErrors=\"NU1901;NU1902\"", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Packages_AreDiscoveredAndVersionedIndependently()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);

            // The preflight discovers the package identity and publishes it as an
            // evaluated output; it is never a caller-supplied input.
            Assert.Contains("PACKAGE_ID: ${{ steps.metadata.outputs.package-id }}", content, StringComparison.Ordinal);
            Assert.DoesNotContain("inputs.package-id", content, StringComparison.Ordinal);
            Assert.Contains("tag_prefix=\"refs/tags/${PACKAGE_ID}/\"", content, StringComparison.Ordinal);
            Assert.Contains("package_artifact=\"artifacts/${PACKAGE_ID}.${package_version}.nupkg\"", content, StringComparison.Ordinal);
        }
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(
            InfraRepositoryLocator.ResolveRoot(), ".github", "workflows", fileName));

    private static string[] ActionUses(string content, string action) =>
        Regex.Matches(
                content,
                $@"^\s*uses:\s+{Regex.Escape(action)}@[^\s#]+(?:\s+#[^\r\n]*)?\s*$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Value.Trim())
            .ToArray();

    private static YamlMappingNode Parse(string fileName) => YamlWorkflowReader.Parse(Read(fileName));

    private static YamlMappingNode Jobs(YamlMappingNode root) => YamlWorkflowReader.MappingChild(root, "jobs");

    private static YamlMappingNode Job(YamlMappingNode root, string name) =>
        YamlWorkflowReader.MappingChild(Jobs(root), name);

    private static YamlMappingNode BuildJob(YamlMappingNode root, string file) =>
        Job(root, file == ValidationFile ? "validate" : "build");

    private static YamlMappingNode Outputs(YamlMappingNode job) =>
        YamlWorkflowReader.MappingChild(job, "outputs");

    private static YamlMappingNode Permissions(YamlMappingNode node) =>
        YamlWorkflowReader.MappingChild(node, "permissions");

    private static IReadOnlyList<YamlMappingNode> Steps(YamlMappingNode job) =>
        YamlWorkflowReader.MappingSequence(job, "steps");

    private static YamlMappingNode Step(YamlMappingNode job, string name) =>
        Steps(job).Single(step => YamlWorkflowReader.ScalarChild(step, "name") == name);

    private static YamlMappingNode StepById(YamlMappingNode job, string id) =>
        Steps(job).Single(step =>
            YamlWorkflowReader.HasChild(step, "id")
            && YamlWorkflowReader.ScalarChild(step, "id") == id);

    private static string VersionStateScript(string file)
    {
        var step = StepById(BuildJob(Parse(file), file), "version-state");
        return YamlWorkflowReader.ScalarChild(step, "run");
    }

    private static string Run(YamlMappingNode job, string name) =>
        YamlWorkflowReader.ScalarChild(Step(job, name), "run");

    private static List<string> StepNames(YamlMappingNode job) =>
        Steps(job).Select(step => YamlWorkflowReader.ScalarChild(step, "name")).ToList();

    private static IReadOnlyList<YamlMappingNode> CheckoutSteps(YamlMappingNode job) =>
        Steps(job)
            .Where(step =>
                YamlWorkflowReader.HasChild(step, "uses")
                && YamlWorkflowReader.ScalarChild(step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal))
            .ToArray();

    private static YamlMappingNode? WorkflowCallInputs(YamlMappingNode root) =>
        YamlWorkflowReader.Child(YamlWorkflowReader.MappingChild(root, "on"), "workflow_call") is YamlMappingNode call
        && YamlWorkflowReader.HasChild(call, "inputs")
            ? YamlWorkflowReader.MappingChild(call, "inputs")
            : null;
}
