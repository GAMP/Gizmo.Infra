using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;
using Xunit.Sdk;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Static contract coverage for the three direct reusable <c>workflow_call</c>
/// workflows Gizmo.Infra publishes. The tests read the committed YAML and never
/// render or execute it, so they hold without GitHub, NuGet, or remote setup.
/// </summary>
public sealed class ReusableWorkflowContractTests
{
    private const string ValidationFile = "package-validation.yml";
    private const string DevelopmentFile = "package-development.yml";
    private const string ReleaseFile = "package-release.yml";

    private static readonly string[] ContractFiles = [ValidationFile, DevelopmentFile, ReleaseFile];
    private static readonly string[] PublishingFiles = [DevelopmentFile, ReleaseFile];

    // These publishing/tag jobs are hard-disabled pending the routing contract,
    // but their retained bodies still repeat tag discovery and state validation;
    // each is a distinct attack surface for pagination, prefix, malformed-tag, and
    // annotated-tag handling regressions.
    private static readonly (string File, string Job, string Step, string PackageVariable)[] RecheckJobs =
    [
        (DevelopmentFile, "publish-public", "Recheck calculated tag state before publication", "$EXPECTED_PACKAGE_ID"),
        (DevelopmentFile, "publish-private", "Recheck calculated tag state before publication", "$EXPECTED_PACKAGE_ID"),
        (ReleaseFile, "publish-public", "Recheck calculated state and public collision before publication", "$EXPECTED_PACKAGE_ID"),
        (ReleaseFile, "publish-private", "Recheck calculated state and private collision before publication", "$EXPECTED_PACKAGE_ID"),
        (ReleaseFile, "tag", "Refetch and recheck calculated state before tagging", "$package_id"),
    ];

    // Reruns of the same workflow run must not collide on the uploaded artifact
    // name, so the attempt discriminator is part of the contract.
    private const string ArtifactName = "nuget-package-${{ github.run_id }}-${{ github.run_attempt }}";

    // Every operation shares one caller-repository lock; the preflight derives
    // the package from the workspace, so the group is not per-package.
    private const string ConcurrencyGroup = "nuget-${{ github.repository }}";

    // Finalized Node 24 action releases. Every workflow that declares one of
    // these actions must use exactly this commit and matching release comment, so
    // a partial upgrade that leaves any action on a stale major fails here.
    // download-artifact is declared only by the publishing workflows.
    private const string CheckoutPin =
        "uses: actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0";
    private const string SetupDotnetPin =
        "uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5.4.0";
    private const string UploadArtifactPin =
        "uses: actions/upload-artifact@b7c566a772e6b6bfb58ed0dc250532a479d7789f # v6.0.0";
    private const string DownloadArtifactPin =
        "uses: actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1";

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
    public void WorkflowDirectory_ContainsExactlyTheThreeContractFiles()
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
        // E15: the preflight discovers the project, package ID, compatibility
        // line, and visibility, and no publisher input remains, so every workflow
        // exposes a body-less `workflow_call:` (a null scalar, not a mapping).
        foreach (var file in ContractFiles)
        {
            var root = Parse(file);
            var workflowCall = YamlWorkflowReader.Child(
                YamlWorkflowReader.MappingChild(root, "on"), "workflow_call");

            Assert.IsType<YamlScalarNode>(workflowCall);
            Assert.Null(WorkflowCallInputs(root));

            // The compatibility line comes from the caller project; no descriptor,
            // patch, project path, package ID, visibility, nuget user, or workflow
            // version input may exist.
            foreach (var forbidden in new[]
                     {
                         "version", "package-version", "version-override", "patch", "prerelease",
                         "project-path", "package-id", "package-visibility", "nuget-user",
                         "require-nuget-user",
                     })
            {
                Assert.DoesNotContain($"inputs.{forbidden}", Read(file), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Visibility_IsDiagnosticOnlyAndNeverRoutesAPublisher()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);

            // The preflight resolves visibility, but the workflow must not read it
            // to pick a registry, gate a job, or branch a step; the caller input is
            // gone entirely.
            Assert.DoesNotContain("package-visibility", content, StringComparison.Ordinal);
            Assert.DoesNotContain("github.event.repository.visibility", content, StringComparison.Ordinal);
        }

        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);
            var build = BuildJob(root, file);

            // The public version check, the private collision job, and both
            // publishers remain declared but are hard-disabled.
            Assert.Equal(
                "${{ false }}",
                YamlWorkflowReader.ScalarChild(Step(build, PublicCollisionStepName(file)), "if"));
            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "verify-private-collision"), "if"));
            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-public"), "if"));
            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-private"), "if"));
        }

        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(Parse(ReleaseFile), "tag"), "if"));
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
    public void Concurrency_UsesANonCancellingCallerRepositoryGroup()
    {
        foreach (var file in ContractFiles)
        {
            var concurrency = YamlWorkflowReader.MappingChild(Parse(file), "concurrency");
            var group = YamlWorkflowReader.ScalarChild(concurrency, "group");

            Assert.Equal(ConcurrencyGroup, group);
            Assert.Contains("github.repository", group, StringComparison.Ordinal);
            Assert.DoesNotContain("package-id", group, StringComparison.Ordinal);
            Assert.Equal("false", YamlWorkflowReader.ScalarChild(concurrency, "cancel-in-progress"));
        }

        // The doc promises the latest pending run may replace an earlier pending
        // run without cancelling an active one; that is exactly this setting.
        Assert.DoesNotContain("cancel-in-progress: true", Read(ValidationFile), StringComparison.Ordinal);
        Assert.DoesNotContain("cancel-in-progress: true", Read(DevelopmentFile), StringComparison.Ordinal);
        Assert.DoesNotContain("cancel-in-progress: true", Read(ReleaseFile), StringComparison.Ordinal);
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
    public void PublishingJobs_GrantOnlyTheCredentialTheyNeed()
    {
        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);

            var publicPermissions = Permissions(Job(root, "publish-public"));
            Assert.Equal("read", YamlWorkflowReader.ScalarChild(publicPermissions, "contents"));
            Assert.Equal("write", YamlWorkflowReader.ScalarChild(publicPermissions, "id-token"));
            Assert.False(YamlWorkflowReader.HasChild(publicPermissions, "packages"));
            Assert.Equal(2, publicPermissions.Children.Count);

            var privatePermissions = Permissions(Job(root, "publish-private"));
            Assert.Equal("read", YamlWorkflowReader.ScalarChild(privatePermissions, "contents"));
            Assert.Equal("write", YamlWorkflowReader.ScalarChild(privatePermissions, "packages"));
            Assert.False(YamlWorkflowReader.HasChild(privatePermissions, "id-token"));
            Assert.Equal(2, privatePermissions.Children.Count);

            var collisionPermissions = Permissions(Job(root, "verify-private-collision"));
            Assert.Equal("read", YamlWorkflowReader.ScalarChild(collisionPermissions, "packages"));
            Assert.Single(collisionPermissions.Children);
        }

        var tagPermissions = Permissions(Job(Parse(ReleaseFile), "tag"));
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(tagPermissions, "contents"));
        Assert.Single(tagPermissions.Children);
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
    public void DevelopmentWorkflow_HasNoTagJob()
    {
        Assert.False(YamlWorkflowReader.HasChild(Jobs(Parse(DevelopmentFile)), "tag"));
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

        foreach (var file in PublishingFiles)
        {
            Assert.Matches("actions/download-artifact@[0-9a-f]{40} # v", Read(file));
        }
    }

    [Theory]
    [InlineData(ValidationFile, "actions/checkout", CheckoutPin)]
    [InlineData(DevelopmentFile, "actions/checkout", CheckoutPin)]
    [InlineData(ReleaseFile, "actions/checkout", CheckoutPin)]
    [InlineData(ValidationFile, "actions/setup-dotnet", SetupDotnetPin)]
    [InlineData(DevelopmentFile, "actions/setup-dotnet", SetupDotnetPin)]
    [InlineData(ReleaseFile, "actions/setup-dotnet", SetupDotnetPin)]
    [InlineData(ValidationFile, "actions/upload-artifact", UploadArtifactPin)]
    [InlineData(DevelopmentFile, "actions/upload-artifact", UploadArtifactPin)]
    [InlineData(ReleaseFile, "actions/upload-artifact", UploadArtifactPin)]
    [InlineData(DevelopmentFile, "actions/download-artifact", DownloadArtifactPin)]
    [InlineData(ReleaseFile, "actions/download-artifact", DownloadArtifactPin)]
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

        // Release packaging must build the caller's exact commit, not a mutable ref.
        var releaseWith = YamlWorkflowReader.MappingChild(
            Step(Job(Parse(ReleaseFile), "build"), "Checkout caller commit"), "with");
        Assert.Equal("${{ github.sha }}", YamlWorkflowReader.ScalarChild(releaseWith, "ref"));
    }

    [Fact]
    public void ImmutableInfraSource_IsResolvedFromCallerIndependentJobContexts()
    {
        foreach (var file in ContractFiles)
        {
            var content = Read(file);
            var build = BuildJob(Parse(file), file);

            // github.workflow_ref describes the caller's workflow, not this
            // reusable workflow, so it cannot pin the Gizmo.Infra source commit.
            Assert.DoesNotContain("github.workflow_ref", content, StringComparison.Ordinal);
            Assert.DoesNotContain("github.workflow_sha", content, StringComparison.Ordinal);

            var guard = StepById(build, "infra-source");
            var env = YamlWorkflowReader.MappingChild(guard, "env");
            Assert.Equal("${{ job.workflow_repository }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_REPOSITORY"));
            Assert.Equal("${{ job.workflow_sha }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_SHA"));
            Assert.Equal("${{ job.workflow_file_path }}", YamlWorkflowReader.ScalarChild(env, "WORKFLOW_FILE_PATH"));

            var run = YamlWorkflowReader.ScalarChild(guard, "run");
            Assert.Contains("\"$WORKFLOW_REPOSITORY\" != GAMP/Gizmo.Infra", run, StringComparison.Ordinal);
            Assert.Contains($"\"$WORKFLOW_FILE_PATH\" != .github/workflows/{file}", run, StringComparison.Ordinal);
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
            [DevelopmentFile] = "Pack calculated development version",
            [ReleaseFile] = "Pack calculated release version",
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
        Assert.Contains(
            "package_version=\"${base_version}-dev.${GITHUB_RUN_NUMBER}\"",
            VersionStateScript(DevelopmentFile),
            StringComparison.Ordinal);

        var release = VersionStateScript(ReleaseFile);
        Assert.Contains("package_version=$base_version", release, StringComparison.Ordinal);
        Assert.Contains("release_tag=\"${PACKAGE_ID}/v${base_version}\"", release, StringComparison.Ordinal);
        Assert.DoesNotContain("-pr.", release, StringComparison.Ordinal);
        Assert.DoesNotContain("-dev.", release, StringComparison.Ordinal);

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
            VersionStateScript(ReleaseFile),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishAndTag_RefetchAndRecheckStateBeforeActing()
    {
        var development = Parse(DevelopmentFile);
        foreach (var jobName in new[] { "publish-public", "publish-private" })
        {
            var script = Run(Job(development, jobName), "Recheck calculated tag state before publication");

            Assert.Contains("EXPECTED_STATE", script, StringComparison.Ordinal);
            Assert.Contains("EXPECTED_FINGERPRINT", script, StringComparison.Ordinal);
            Assert.Contains("Calculated package state drifted before publication; refusing to publish.", script, StringComparison.Ordinal);
            Assert.Contains("Could not refetch caller-repository package tag refs.", script, StringComparison.Ordinal);
            Assert.Contains(
                "git/matching-refs/tags/$EXPECTED_PACKAGE_ID/?per_page=100&page=$page",
                script,
                StringComparison.Ordinal);
            Assert.Contains("Malformed tag under the exact package prefix: $ref", script, StringComparison.Ordinal);
        }

        var release = Parse(ReleaseFile);
        var publishSteps = new (string Job, string Step)[]
        {
            ("publish-public", "Recheck calculated state and public collision before publication"),
            ("publish-private", "Recheck calculated state and private collision before publication"),
        };
        foreach (var (jobName, stepName) in publishSteps)
        {
            var script = Run(Job(release, jobName), stepName);

            Assert.Contains("EXPECTED_FINGERPRINT", script, StringComparison.Ordinal);
            Assert.Contains("Calculated package state drifted before publication; refusing to publish.", script, StringComparison.Ordinal);
            Assert.Contains("resolve_commit() {", script, StringComparison.Ordinal);
            Assert.Contains("Package version collision appeared before publication.", script, StringComparison.Ordinal);
        }

        var tagScript = Run(Job(release, "tag"), "Refetch and recheck calculated state before tagging");
        Assert.Contains("EXPECTED_FINGERPRINT", tagScript, StringComparison.Ordinal);
        Assert.Contains("Calculated package state drifted before tagging; refusing to create a tag.", tagScript, StringComparison.Ordinal);
        Assert.Contains("resolve_commit() {", tagScript, StringComparison.Ordinal);
    }

    [Fact]
    public void RecheckJobs_PaginateTheCompleteExactPrefixAndTerminate()
    {
        foreach (var (file, job, step, packageVariable) in RecheckJobs)
        {
            var script = Run(Job(Parse(file), job), step);

            Assert.Contains("page=1", script, StringComparison.Ordinal);
            Assert.Contains(
                $"git/matching-refs/tags/{packageVariable}/?per_page=100&page=$page",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                @"all(.[]; (.ref | type == ""string"") and (.object | type == ""object"") and (.object.type | type == ""string"") and (.object.sha | type == ""string""))",
                script,
                StringComparison.Ordinal);
            Assert.Contains("jq -r '.[] | [.ref, .object.type, .object.sha] | @tsv' \"$response_file\"", script, StringComparison.Ordinal);
            Assert.Contains("count=$(jq -er 'length' \"$response_file\")", script, StringComparison.Ordinal);
            Assert.Contains("if (( count < 100 )); then break; fi", script, StringComparison.Ordinal);
            Assert.Contains("((page += 1))", script, StringComparison.Ordinal);

            // A malformed page body or non-200 must fail closed rather than read as
            // an empty (unpublished) tag set.
            Assert.Contains(
                job == "tag"
                    ? "GitHub returned a malformed package tag-ref response during tag recheck."
                    : "GitHub returned a malformed package tag-ref response during publication recheck.",
                script,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RecheckJobs_RejectForeignAndMalformedTagsUnderTheExactPrefix()
    {
        foreach (var (file, job, step, _) in RecheckJobs)
        {
            var script = Run(Job(Parse(file), job), step);
            var expectedPrefix = job == "tag"
                ? "tag_prefix=\"refs/tags/${package_id}/\""
                : "tag_prefix=\"refs/tags/${EXPECTED_PACKAGE_ID}/\"";

            Assert.Contains(expectedPrefix, script, StringComparison.Ordinal);

            // A ref outside the requested package prefix is never silently ignored.
            Assert.Contains(
                "if [[ \"$ref\" != \"$tag_prefix\"* ]]; then echo \"GitHub returned a tag outside the requested package prefix.\" >&2; exit 1; fi",
                script,
                StringComparison.Ordinal);

            // A malformed leaf under the exact package prefix fails closed, so a
            // bogus tag cannot be skipped or attributed to another package.
            Assert.Contains("leaf=${ref#\"$tag_prefix\"}", script, StringComparison.Ordinal);
            Assert.Contains(
                "if [[ ! \"$leaf\" =~ ^v3\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$ ]]; then echo \"Malformed tag under the exact package prefix: $ref\" >&2; exit 1; fi",
                script,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RecheckJobs_ResolveAnnotatedTagsToCommitsBeforeSnapshotting()
    {
        foreach (var (file, job, step, _) in RecheckJobs)
        {
            var script = Run(Job(Parse(file), job), step);

            Assert.Contains("resolve_commit() {", script, StringComparison.Ordinal);
            Assert.Contains("git/tags/$object_sha", script, StringComparison.Ordinal);
            Assert.Contains("case \"$object_type\" in", script, StringComparison.Ordinal);
            Assert.Contains("commit) printf '%s' \"$object_sha\"; return 0 ;;", script, StringComparison.Ordinal);
            Assert.Contains("if (( depth > 16 )) ||", script, StringComparison.Ordinal);
            Assert.Contains("*) echo \"Package tag ref does not resolve to a commit.\" >&2; return 1 ;;", script, StringComparison.Ordinal);

            // Each page resolves every entry and aborts instead of snapshotting an
            // annotated tag whose chain could not be walked back to a commit.
            Assert.Contains("resolve_commit \"$object_type\" \"$object_sha\" > /dev/null || exit 1", script, StringComparison.Ordinal);
            Assert.Contains("snapshot+=\"${ref}=${object_sha}\"$'\\n'", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(DevelopmentFile, "publish-public")]
    [InlineData(DevelopmentFile, "publish-private")]
    [InlineData(ReleaseFile, "publish-public")]
    [InlineData(ReleaseFile, "publish-private")]
    [InlineData(ReleaseFile, "tag")]
    public void PublisherAndTagJobs_AreDisabledPendingTheRoutingContract(string file, string jobName)
    {
        // GH-4 accepts repository visibility as diagnostic-only. Until a separate
        // routing contract is authorized, no job may choose a registry or mutate a
        // tag, so every publisher and tag job is hard-disabled rather than gated on
        // a visibility or protected-branch condition.
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(Parse(file), jobName), "if"));
    }

    [Fact]
    public void ReleaseCollisionProvenance_BindsTheExistingPackageToTheCallerSha()
    {
        AssertProvenanceFailsClosed(
            PublicCollisionScript(ReleaseFile),
            packageLabel: "public",
            packageDownload: "https://api.nuget.org/v3-flatcontainer/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg",
            transport: "NuGet.org");
        AssertProvenanceFailsClosed(
            PrivateCollisionScript(ReleaseFile),
            packageLabel: "private",
            packageDownload: "https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/flatcontainer/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg",
            transport: "GitHub Packages");

        // The retained provenance rejection cannot lead to a tag: the tag job is
        // hard-disabled until a routing contract restores tagging.
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(Parse(ReleaseFile), "tag"), "if"));
    }

    private static void AssertProvenanceFailsClosed(string script, string packageLabel, string packageDownload, string transport)
    {
        Assert.Contains(packageDownload, script, StringComparison.Ordinal);
        Assert.Contains("nuspec=$(unzip -p \"$package_file\" '*.nuspec' 2>/dev/null || true)", script, StringComparison.Ordinal);
        Assert.Contains(
            "repository_commit=$(printf '%s' \"$nuspec\" | grep -oE 'commit=\"[0-9a-fA-F]{40}\"' | head -n 1 | sed -E 's/.*\"([0-9a-fA-F]{40})\".*/\\1/' | tr 'A-F' 'a-f' || true)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{transport} returned HTTP $status for the published {packageLabel} package; treating the existing version as an unverifiable collision.",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"if [[ \"$repository_commit\" != \"$GITHUB_SHA\" ]]; then echo \"The existing {packageLabel} package version has no authenticated provenance for this caller SHA; failing closed instead of creating a recovery tag.\" >&2; exit 1; fi",
            script,
            StringComparison.Ordinal);

        // The published signal is the only fallback that lets recovery tagging run
        // without a successful publish, so it can only be emitted after the commit
        // comparison succeeds.
        var comparisonIndex = script.IndexOf("if [[ \"$repository_commit\" != \"$GITHUB_SHA\" ]]", StringComparison.Ordinal);
        var publishedIndex = script.IndexOf("package-state=published", StringComparison.Ordinal);
        Assert.True(comparisonIndex >= 0, $"{packageLabel} release collision must compare the embedded commit.");
        Assert.True(publishedIndex > comparisonIndex, $"{packageLabel} release collision must publish state only after provenance matches.");
    }

    [Fact]
    public void CollisionChecks_QueryTheOperationSpecificFeed()
    {
        foreach (var file in PublishingFiles)
        {
            Assert.Contains(
                "https://api.nuget.org/v3-flatcontainer/${package_id_lower}/index.json",
                PublicCollisionScript(file),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PrivateCollisionCheck_UsesTheCallerOwnerFeedWithTheCallerToken()
    {
        foreach (var file in PublishingFiles)
        {
            var job = Job(Parse(file), "verify-private-collision");
            var script = PrivateCollisionScript(file);

            // The caller-owner GitHub Packages NuGet feed is the only endpoint this
            // token can read for the caller repository; the management REST
            // /user/packages path is not a valid caller-token precheck.
            Assert.Contains(
                "https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/flatcontainer/",
                script,
                StringComparison.Ordinal);
            Assert.Contains("${package_id_lower}/index.json", script, StringComparison.Ordinal);
            Assert.Contains(@"--user ""$GITHUB_ACTOR:$GH_TOKEN""", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/user/packages", script, StringComparison.Ordinal);
            Assert.DoesNotContain("api.github.com", script, StringComparison.Ordinal);

            var env = YamlWorkflowReader.MappingChild(job, "env");
            Assert.Equal("${{ github.token }}", YamlWorkflowReader.ScalarChild(env, "GH_TOKEN"));
            Assert.Equal("${{ needs.build.outputs.package-id }}", YamlWorkflowReader.ScalarChild(env, "EXPECTED_PACKAGE_ID"));
            Assert.Equal(
                "${{ needs.build.outputs.package-version }}",
                YamlWorkflowReader.ScalarChild(env, "PACKAGE_VERSION"));
        }
    }

    [Fact]
    public void CollisionChecks_TreatTransportAndUnexpectedStatusesAsFailure()
    {
        foreach (var script in CollisionChecks())
        {
            Assert.Contains("--write-out '%{http_code}'", script, StringComparison.Ordinal);

            // Transport failure or a failed curl is a hard stop, never "unpublished".
            Assert.Contains("if ! status=$(curl", script, StringComparison.Ordinal);
            Assert.Matches("Could not (re)?check whether the", script);
            Assert.Contains("exit 1", script, StringComparison.Ordinal);

            // A malformed or empty HTTP 200 body is not a valid version index and
            // must fail closed rather than read as "no collision".
            Assert.Contains(
                "jq -e 'type == \"object\" and (.versions | type == \"array\") and all(.versions[]; type == \"string\")'",
                script,
                StringComparison.Ordinal);
            Assert.Contains("malformed package-version response.", script, StringComparison.Ordinal);

            // Any other HTTP status is unexpected and fails closed.
            Assert.Matches(@"returned HTTP \$status while (re)?checking", script);
        }
    }

    [Fact]
    public void CollisionChecks_RefuseAnExistingVersionAndRecordPublishedState()
    {
        // Development fails the run when the version is already published.
        Assert.Contains(
            "Package version already exists; refusing to overwrite.",
            PublicCollisionScript(DevelopmentFile),
            StringComparison.Ordinal);
        Assert.Contains(
            "Package version already exists; refusing to overwrite.",
            PrivateCollisionScript(DevelopmentFile),
            StringComparison.Ordinal);

        // Release records the claim and gates every publish job on it, so a rerun
        // cannot overwrite an already-published version.
        Assert.Contains("package-state=published", PublicCollisionScript(ReleaseFile), StringComparison.Ordinal);
        Assert.Contains("package-state=unpublished", PublicCollisionScript(ReleaseFile), StringComparison.Ordinal);
        Assert.Contains("package-state=published", PrivateCollisionScript(ReleaseFile), StringComparison.Ordinal);
        Assert.Contains("package-state=unpublished", PrivateCollisionScript(ReleaseFile), StringComparison.Ordinal);
    }

    [Fact]
    public void PublicCollisionStep_RetainsItsOrderButNeverRuns()
    {
        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);
            var build = BuildJob(root, file);
            var names = StepNames(build);

            var collisionIndex = names.IndexOf(PublicCollisionStepName(file));
            var packIndex = names.FindIndex(name => name.StartsWith("Pack calculated", StringComparison.Ordinal));

            Assert.True(collisionIndex >= 0, $"{file} must retain the public version check.");
            Assert.True(
                collisionIndex < names.IndexOf("Restore with NuGet audit"),
                $"{file} must retain the public version check before restoring.");
            Assert.True(
                collisionIndex < packIndex,
                $"{file} must retain the public version check before packing.");

            // The retained check is inert and cannot gate anything: routing on the
            // discovered visibility is disabled pending a separate contract.
            Assert.Equal(
                "${{ false }}",
                YamlWorkflowReader.ScalarChild(Step(build, PublicCollisionStepName(file)), "if"));
            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-public"), "if"));

            // The publish job remains wired after the build job.
            Assert.Equal("build", YamlWorkflowReader.ScalarChild(Job(root, "publish-public"), "needs"));

            if (file == ReleaseFile)
            {
                // Identity/state resolution still precedes the inert version check.
                Assert.True(names.IndexOf("Calculate release version and tag state") < collisionIndex);
            }
        }
    }

    [Fact]
    public void PrivateCollisionJob_IsDisabledAndDoesNotRouteOnVisibility()
    {
        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);
            var collision = Job(root, "verify-private-collision");

            Assert.Equal("build", YamlWorkflowReader.ScalarChild(collision, "needs"));
            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(collision, "if"));

            var needs = Needs(Job(root, "publish-private"));
            Assert.Contains("build", needs);
            Assert.Contains("verify-private-collision", needs);

            // The retained check still records or rejects state, but it cannot run
            // and cannot select a registry from the discovered visibility.
            var check = PrivateCollisionScript(file);
            if (file == ReleaseFile)
            {
                Assert.Contains("package-state=published", check, StringComparison.Ordinal);
                Assert.Contains("package-state=unpublished", check, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("refusing to overwrite.", check, StringComparison.Ordinal);
                Assert.Contains("exit 1", check, StringComparison.Ordinal);
            }

            Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-private"), "if"));
        }
    }

    [Fact]
    public void PublicPublication_UsesCallerScopedOidcInsteadOfAStoredKey()
    {
        foreach (var file in PublishingFiles)
        {
            var publish = Job(Parse(file), "publish-public");
            var permissions = Permissions(publish);

            Assert.Equal("read", YamlWorkflowReader.ScalarChild(permissions, "contents"));
            Assert.Equal("write", YamlWorkflowReader.ScalarChild(permissions, "id-token"));
            Assert.False(YamlWorkflowReader.HasChild(permissions, "packages"));

            var exchange = Run(publish, "Exchange OIDC identity for temporary NuGet API key");
            Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_URL", exchange, StringComparison.Ordinal);
            Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_TOKEN", exchange, StringComparison.Ordinal);
            Assert.Contains("audience=https%3A%2F%2Fwww.nuget.org", exchange, StringComparison.Ordinal);
            Assert.Contains("https://www.nuget.org/api/v2/token", exchange, StringComparison.Ordinal);
            Assert.Contains("::add-mask::", exchange, StringComparison.Ordinal);
            Assert.Contains(@"--api-key ""$nuget_api_key""", exchange, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_API_KEY", exchange, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetadataPreflight_PrecedesRestoreCollisionAndPack()
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

        // Only the publishing workflows retain a public collision step, which must
        // still run after discovery.
        foreach (var file in PublishingFiles)
        {
            var build = BuildJob(Parse(file), file);
            var names = StepNames(build);
            var metadataIndex = names.IndexOf(
                YamlWorkflowReader.ScalarChild(StepById(build, "metadata"), "name"));
            Assert.True(
                metadataIndex < names.IndexOf(PublicCollisionStepName(file)),
                $"{file} must discover caller metadata before checking publication state.");
        }
    }

    [Fact]
    public void ReleaseRerun_ReusesExactlyOneCurrentShaTagAndFailsClosedOnAmbiguity()
    {
        var build = Job(Parse(ReleaseFile), "build");
        var outputs = Outputs(build);
        Assert.Equal(
            "${{ steps.version-state.outputs.release-tag-state }}",
            YamlWorkflowReader.ScalarChild(outputs, "release-tag-state"));
        Assert.Equal(
            "${{ steps.version-state.outputs.release-tag }}",
            YamlWorkflowReader.ScalarChild(outputs, "release-tag"));

        var script = VersionStateScript(ReleaseFile);
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
    public void ReleaseTagCreation_IsIdempotentAndNeverMovesOrOverwrites()
    {
        var finalize = Run(Job(Parse(ReleaseFile), "tag"), "Create or reconcile immutable release tag");

        Assert.Contains("git/ref/tags/$RELEASE_TAG", finalize, StringComparison.Ordinal);
        Assert.Contains("Release tag already exists for a different commit; refusing to move it.", finalize, StringComparison.Ordinal);
        Assert.Contains("the tag was not moved or overwritten.", finalize, StringComparison.Ordinal);
        Assert.Contains("git/refs", finalize, StringComparison.Ordinal);
        Assert.Contains("--request POST", finalize, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$status\" != 201 ]]", finalize, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", finalize, StringComparison.Ordinal);
        Assert.DoesNotContain("PATCH", finalize, StringComparison.Ordinal);
        Assert.DoesNotContain("--request PUT", finalize, StringComparison.Ordinal);
        Assert.DoesNotContain("--request DELETE", finalize, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseTagJob_IsDisabledAndRetainsImmutableTagPermissions()
    {
        var root = Parse(ReleaseFile);
        var tag = Job(root, "tag");
        var needs = Needs(tag);

        Assert.Contains("build", needs);
        Assert.Contains("verify-private-collision", needs);
        Assert.Contains("publish-public", needs);
        Assert.Contains("publish-private", needs);

        // No condition can enable the tag job until routing is authorized.
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(tag, "if"));

        var permissions = Permissions(tag);
        Assert.Equal("write", YamlWorkflowReader.ScalarChild(permissions, "contents"));
        Assert.Single(permissions.Children);
    }

    [Fact]
    public void ReleaseTagRecovery_IsDisabledAndRetainsIdempotentLogic()
    {
        var root = Parse(ReleaseFile);

        // The build no longer advertises a package state: with routing disabled no
        // downstream job consumes it.
        Assert.False(YamlWorkflowReader.HasChild(Outputs(Job(root, "build")), "package-state"));

        // Every job that could recover a tag is hard-disabled pending routing.
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-public"), "if"));
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "publish-private"), "if"));
        Assert.Equal("${{ false }}", YamlWorkflowReader.ScalarChild(Job(root, "tag"), "if"));

        // Same-commit tag is a success and an absent tag is created; a tag on
        // another commit fails closed without mutation.
        var finalize = Run(Job(root, "tag"), "Create or reconcile immutable release tag");
        Assert.Contains("if [[ \"$tag_sha\" != \"$GITHUB_SHA\" ]]", finalize, StringComparison.Ordinal);
        Assert.Contains("404)", finalize, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBuild_CalculatesStateAndAppliesTagRulesBeforePackaging()
    {
        var names = StepNames(Job(Parse(ReleaseFile), "build"));

        var stateIndex = names.IndexOf("Calculate release version and tag state");
        Assert.True(stateIndex >= 0);
        Assert.True(stateIndex < names.IndexOf("Resolve public package version state"));
        Assert.True(stateIndex < names.IndexOf("Restore with NuGet audit"));
        Assert.True(stateIndex < names.FindIndex(name => name.StartsWith("Pack calculated", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildAndPublish_HandOffTheExactPackageArtifact()
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
        }

        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);
            var outputs = Outputs(Job(root, "build"));
            Assert.True(YamlWorkflowReader.HasChild(outputs, "package-artifact"));
            Assert.True(YamlWorkflowReader.HasChild(outputs, "package-version"));

            foreach (var jobName in new[] { "publish-public", "publish-private" })
            {
                var publish = Job(root, jobName);
                var download = Step(publish, "Download exact package artifact");
                var downloadWith = YamlWorkflowReader.MappingChild(download, "with");
                Assert.Equal(ArtifactName, YamlWorkflowReader.ScalarChild(downloadWith, "name"));
                Assert.Equal("artifacts", YamlWorkflowReader.ScalarChild(downloadWith, "path"));

                // Publishing must consume the transferred artifact, never rebuild it.
                foreach (var script in Scripts(publish))
                {
                    Assert.DoesNotContain("dotnet pack", script, StringComparison.Ordinal);
                    Assert.DoesNotContain("dotnet build", script, StringComparison.Ordinal);
                    Assert.DoesNotContain("dotnet restore", script, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void ArtifactNames_AreRerunSafeAndIdenticalAcrossEveryHandoffStep()
    {
        foreach (var file in ContractFiles)
        {
            var upload = Step(BuildJob(Parse(file), file), "Upload exact package artifact");
            var uploadWith = YamlWorkflowReader.MappingChild(upload, "with");
            var name = YamlWorkflowReader.ScalarChild(uploadWith, "name");

            Assert.Contains("github.run_id", name, StringComparison.Ordinal);
            Assert.Contains("github.run_attempt", name, StringComparison.Ordinal);
        }

        foreach (var file in PublishingFiles)
        {
            var root = Parse(file);
            var uploaded = YamlWorkflowReader.ScalarChild(
                YamlWorkflowReader.MappingChild(Step(BuildJob(root, file), "Upload exact package artifact"), "with"),
                "name");

            foreach (var jobName in new[] { "publish-public", "publish-private" })
            {
                var download = Step(Job(root, jobName), "Download exact package artifact");
                Assert.Equal(
                    uploaded,
                    YamlWorkflowReader.ScalarChild(YamlWorkflowReader.MappingChild(download, "with"), "name"));
            }
        }
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

        foreach (var file in PublishingFiles)
        {
            var content = Read(file);

            Assert.Contains("EXPECTED_PACKAGE_ID: ${{ needs.build.outputs.package-id }}", content, StringComparison.Ordinal);
            Assert.Contains(
                "package_id_lower=$(printf '%s' \"$EXPECTED_PACKAGE_ID\" | tr '[:upper:]' '[:lower:]')",
                content,
                StringComparison.Ordinal);
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

    private static string PublicCollisionStepName(string file) =>
        file == ReleaseFile
            ? "Resolve public package version state"
            : "Verify public package version is unpublished";

    private static string PublicCollisionScript(string file) =>
        Run(BuildJob(Parse(file), file), PublicCollisionStepName(file));

    private static string PrivateCollisionScript(string file) =>
        Run(
            Job(Parse(file), "verify-private-collision"),
            file == ReleaseFile
                ? "Resolve private package version state"
                : "Verify private package version is unpublished");

    private static string RecheckScript(string file, bool publicCheck)
    {
        if (file == ReleaseFile)
        {
            return Run(
                Job(Parse(file), publicCheck ? "publish-public" : "publish-private"),
                publicCheck
                    ? "Recheck calculated state and public collision before publication"
                    : "Recheck calculated state and private collision before publication");
        }

        return Run(Job(Parse(file), publicCheck ? "publish-public" : "publish-private"), "Recheck calculated tag state before publication");
    }

    private static IEnumerable<string> CollisionChecks()
    {
        foreach (var file in PublishingFiles)
        {
            yield return PublicCollisionScript(file);
            yield return PrivateCollisionScript(file);
            yield return RecheckScript(file, publicCheck: true);
            yield return RecheckScript(file, publicCheck: false);
        }
    }

    private static string Run(YamlMappingNode job, string name) =>
        YamlWorkflowReader.ScalarChild(Step(job, name), "run");

    private static List<string> StepNames(YamlMappingNode job) =>
        Steps(job).Select(step => YamlWorkflowReader.ScalarChild(step, "name")).ToList();

    private static IEnumerable<string> Scripts(YamlMappingNode job) =>
        Steps(job)
            .Where(step => YamlWorkflowReader.HasChild(step, "run"))
            .Select(step => YamlWorkflowReader.ScalarChild(step, "run"));

    private static IReadOnlyList<YamlMappingNode> CheckoutSteps(YamlMappingNode job) =>
        Steps(job)
            .Where(step =>
                YamlWorkflowReader.HasChild(step, "uses")
                && YamlWorkflowReader.ScalarChild(step, "uses").StartsWith("actions/checkout@", StringComparison.Ordinal))
            .ToArray();

    private static IReadOnlyList<string> Needs(YamlMappingNode job) =>
        YamlWorkflowReader.Child(job, "needs") switch
        {
            YamlScalarNode scalar => (IReadOnlyList<string>)new[] { scalar.Value ?? string.Empty },
            YamlSequenceNode sequence => YamlWorkflowReader.ScalarSequence(sequence),
            _ => throw new XunitException($"Job 'needs' in '{YamlWorkflowReader.ScalarChild(job, "name")}' is not a scalar or sequence."),
        };

    private static YamlMappingNode? WorkflowCallInputs(YamlMappingNode root) =>
        YamlWorkflowReader.Child(YamlWorkflowReader.MappingChild(root, "on"), "workflow_call") is YamlMappingNode call
        && YamlWorkflowReader.HasChild(call, "inputs")
            ? YamlWorkflowReader.MappingChild(call, "inputs")
            : null;
}
