using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract coverage for the caller-owned composite actions: the bundled
/// preflight, the unified public and private publishers, and the immutable
/// release-tag reconciler. The tests read the committed action YAML; they never
/// render or execute a GitHub job.
/// </summary>
public sealed class CallerOwnedPublishingActionContractTests
{
    private static readonly string[] ActionDirectories =
    [
        "package-preflight",
        "package-private-publish",
        "package-public-publish",
        "package-release-tag",
    ];

    private static readonly string[] PublisherActions =
    [
        "package-public-publish",
        "package-private-publish",
    ];

    private static string ActionsRoot() =>
        Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions");

    private static string ActionPath(string directory) =>
        Path.Combine(ActionsRoot(), directory, "action.yml");

    private static string Read(string directory) => File.ReadAllText(ActionPath(directory));

    [Fact]
    public void DeclaredActions_AreExactlyThePreflightPublishersAndTag()
    {
        var actions = Directory.EnumerateDirectories(ActionsRoot())
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ActionDirectories.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actions);
    }

    [Fact]
    public void EveryDeclaredAction_ExistsAndParsesAsYaml()
    {
        foreach (var directory in ActionDirectories)
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(Read(directory));
            yaml.Load(reader);
            Assert.Single(yaml.Documents);
        }
    }

    [Fact]
    public void CompositeActions_DoNotReachIntoCallerNeedsContext()
    {
        foreach (var directory in ActionDirectories)
        {
            Assert.DoesNotContain("${{ needs.", Read(directory), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryNestedActionReference_IsPinnedToAFullSha()
    {
        var usesLine = new Regex(
            @"^\s*uses:\s+([^\s@]+)@([0-9a-f]{40})\s+#\s+v[0-9][^\s]*\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        foreach (var directory in ActionDirectories)
        {
            var content = Read(directory);
            var declared = Regex.Matches(content, @"^\s*uses:", RegexOptions.Multiline);
            Assert.Equal(declared.Count, usesLine.Matches(content).Count);
            Assert.DoesNotContain("@main", content, StringComparison.Ordinal);
            Assert.DoesNotContain("@master", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicPublisher_IsCallerOwnedOidcAndRejectsNonPublicVisibility()
    {
        var content = Read("package-public-publish");

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("refs/heads/", content, StringComparison.Ordinal);
        Assert.Contains("github.event_name", content, StringComparison.Ordinal);
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_URL", content, StringComparison.Ordinal);
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_TOKEN", content, StringComparison.Ordinal);
        Assert.Contains("audience=https%3A%2F%2Fwww.nuget.org", content, StringComparison.Ordinal);
        Assert.Contains("https://www.nuget.org/api/v2/token", content, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.Contains("Calculated package state drifted before publication", content, StringComparison.Ordinal);
        Assert.Contains("::add-mask::", content, StringComparison.Ordinal);
        Assert.Contains(@"--api-key ""$nuget_api_key""", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);

        Assert.DoesNotContain("packages: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain(@"--api-key ""$GH_TOKEN""", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", content, StringComparison.Ordinal);
        Assert.Contains("REPOSITORY_VISIBILITY: ${{ inputs.repository-visibility }}", content, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$REPOSITORY_VISIBILITY\" != public ]]; then", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivatePublisher_UsesCallerTokenWithoutOidcAndRejectsNonPrivateVisibility()
    {
        var content = Read("package-private-publish");

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("github.event_name", content, StringComparison.Ordinal);
        Assert.Contains("service_index_url=\"https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/index.json\"", content, StringComparison.Ordinal);
        Assert.Contains(@"--user ""$GITHUB_ACTOR:$GH_TOKEN""", content, StringComparison.Ordinal);
        Assert.Contains(
            @"dotnet nuget push ""$PACKAGE_ARTIFACT"" --source ""https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/index.json"" --api-key ""$GH_TOKEN""",
            content,
            StringComparison.Ordinal);
        Assert.Contains("Calculated package state drifted before publication", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);

        Assert.Contains("REPOSITORY_VISIBILITY: ${{ inputs.repository-visibility }}", content, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$REPOSITORY_VISIBILITY\" != private ]]; then", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget-user", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivatePublisher_ResolvesPackageBaseAddressFromTheAuthenticatedServiceIndex()
    {
        var content = Read("package-private-publish");

        // The collision recheck discovers the flat-container base from the
        // authenticated NuGet V3 service index instead of assuming a path shape.
        Assert.Contains(
            "service_index_url=\"https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/index.json\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$service_index_url\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("\"PackageBaseAddress/3.0.0\"", content, StringComparison.Ordinal);
        Assert.Contains(".[\"@id\"]", content, StringComparison.Ordinal);
        Assert.Contains("if length == 1 and (.[0] | type == \"string\")", content, StringComparison.Ordinal);
        Assert.Contains(
            "GitHub Packages returned HTTP $status for the NuGet service index during publication recheck.",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "malformed, missing, or ambiguous PackageBaseAddress resource",
            content,
            StringComparison.Ordinal);
        Assert.Contains("malformed PackageBaseAddress @id", content, StringComparison.Ordinal);

        // The package, version, and provenance URLs derive only from the
        // discovered base plus the lower-cased ids.
        Assert.Contains(
            "package_index_url=\"$package_base_address/${package_id_lower}/index.json\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "package_download_url=\"$package_base_address/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_index_url\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_download_url\"",
            content,
            StringComparison.Ordinal);

        // No flat-container or download path may be hardcoded.
        Assert.DoesNotContain("flatcontainer", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/download/", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivatePublisher_DeclaresTheServiceIndexDiscoveryFilter()
    {
        // The executable tests stub jq, so the committed discovery filter is
        // pinned here rather than by behavior. It must accept a string or an array
        // @type, require exactly one PackageBaseAddress/3.0.0 resource, and reject
        // anything else.
        var match = Regex.Match(
            Read("package-private-publish"),
            @"if ! package_base_address=\$\(jq -er '(?<filter>.*?)' ""\$response_file""\); then",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "the private publisher has no service-index discovery filter.");

        var normalized = Regex.Replace(match.Groups["filter"].Value, @"\s+", " ").Trim();
        var expected =
            "if type == \"object\" and (.resources | type == \"array\") then "
            + "[.resources[] | select( (.[\"@type\"] | type == \"string\" and . == \"PackageBaseAddress/3.0.0\") "
            + "or (.[\"@type\"] | type == \"array\" and (index(\"PackageBaseAddress/3.0.0\") != null)) ) "
            + "| .[\"@id\"] ] "
            + "| if length == 1 and (.[0] | type == \"string\") and (.[0] | length > 0) then .[0] else empty end "
            + "else error(\"malformed service index\") end";
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Publishers_RecheckCollisionAndProvenanceImmediatelyBeforePublishing()
    {
        var provenanceComparison =
            "repository_commit=$(printf '%s' \"$nuspec\" | grep -oE 'commit=\"[0-9a-fA-F]{40}\"' | head -n 1 | sed -E 's/.*\"([0-9a-fA-F]{40})\".*/\\1/' | tr 'A-F' 'a-f' || true)";

        foreach (var directory in PublisherActions)
        {
            var content = Read(directory);

            Assert.Contains("EXPECTED_STATE", content, StringComparison.Ordinal);
            Assert.Contains("EXPECTED_FINGERPRINT", content, StringComparison.Ordinal);
            Assert.Contains("Calculated package state drifted before publication; refusing to publish.", content, StringComparison.Ordinal);
            Assert.Contains("Could not refetch caller-repository package tag refs.", content, StringComparison.Ordinal);
            Assert.Contains("resolve_commit() {", content, StringComparison.Ordinal);
            Assert.Contains("Package tag ref does not resolve to a commit.", content, StringComparison.Ordinal);
            Assert.Contains("git/matching-refs/tags/$EXPECTED_PACKAGE_ID/?per_page=100&page=$page", content, StringComparison.Ordinal);
            Assert.Contains("Malformed tag under the exact package prefix: $ref", content, StringComparison.Ordinal);
            Assert.Contains("Could not resolve an annotated package tag during publication recheck.", content, StringComparison.Ordinal);

            // The existing version must carry the caller commit as provenance, and
            // a matching version short-circuits the push so same-SHA recovery works.
            Assert.Contains("nuspec=$(unzip -p \"$package_file\" '*.nuspec' 2>/dev/null || true)", content, StringComparison.Ordinal);
            Assert.Contains(provenanceComparison, content, StringComparison.Ordinal);
            Assert.Contains("no authenticated provenance for this caller SHA", content, StringComparison.Ordinal);
            Assert.Contains("package-state=published", content, StringComparison.Ordinal);
            Assert.Contains("package-state=unpublished", content, StringComparison.Ordinal);
            Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", content, StringComparison.Ordinal);

            // A version collision with matching provenance is a success, so the
            // recheck emits the published signal after the commit comparison.
            var comparisonIndex = content.IndexOf("repository_commit", StringComparison.Ordinal);
            var publishedIndex = content.IndexOf("package-state=published", StringComparison.Ordinal);
            Assert.True(comparisonIndex >= 0 && publishedIndex > comparisonIndex);
        }
    }

    [Fact]
    public void Publishers_UseTheExactArtifactAndNeverRebuild()
    {
        foreach (var directory in PublisherActions)
        {
            var content = Read(directory);

            Assert.Contains(
                "name: nuget-package-${{ github.run_id }}-${{ github.run_attempt }}",
                content,
                StringComparison.Ordinal);
            Assert.Contains("path: artifacts", content, StringComparison.Ordinal);
            Assert.Contains("PACKAGE_ARTIFACT", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet pack", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet build", content, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet restore", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoAction_InheritsSecretsOrStoresAPermanentKey()
    {
        foreach (var directory in ActionDirectories)
        {
            var content = Read(directory);

            Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("NUGET_TOKEN", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReleaseTagAction_IsOidcFreeAndNeverMovesATag()
    {
        var content = Read("package-release-tag");

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("Refetch and recheck calculated state before tagging", content, StringComparison.Ordinal);
        Assert.Contains("Create or reconcile immutable release tag", content, StringComparison.Ordinal);
        Assert.Contains("git/ref/tags/$RELEASE_TAG", content, StringComparison.Ordinal);
        Assert.Contains("--request POST", content, StringComparison.Ordinal);
        Assert.Contains("git/refs", content, StringComparison.Ordinal);
        Assert.Contains("Release tag already exists for a different commit; refusing to move it.", content, StringComparison.Ordinal);
        Assert.Contains("the tag was not moved or overwritten.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget-user", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--request PATCH", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--request PUT", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--request DELETE", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("force", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseTagAction_DeclaresARequiredReleaseOnlyBranchRoleInput()
    {
        var root = YamlWorkflowReader.Parse(Read("package-release-tag"));
        var branchRole = YamlWorkflowReader.MappingChild(
            YamlWorkflowReader.MappingChild(root, "inputs"), "branch-role");

        Assert.Equal("true", YamlWorkflowReader.ScalarChild(branchRole, "required"));
        Assert.Contains(
            "BRANCH_ROLE: ${{ inputs.branch-role }}",
            Read("package-release-tag"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseTagAction_AcceptsOnlyTheExactReleaseBranchRole()
    {
        var result = RunReleaseTagValidation("release");

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("development")]
    [InlineData("none")]
    [InlineData("Release")]
    [InlineData("release ")]
    [InlineData("")]
    public void ReleaseTagAction_FailsClosedForAnyNonReleaseBranchRole(string branchRole)
    {
        // A caller wiring mistake must not be able to tag from a development or
        // unresolved preparation run; the action repeats the release-only guard.
        var result = RunReleaseTagValidation(branchRole);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Tagging requires the preparation branch role 'release'",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private static ShellResult RunReleaseTagValidation(string branchRole)
    {
        var root = YamlWorkflowReader.Parse(Read("package-release-tag"));
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(step =>
            YamlWorkflowReader.ScalarChild(step, "name") == "Validate trusted tag invocation");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EVENT_NAME"] = "push",
            ["REF_NAME"] = "refs/heads/main",
            ["REF_PROTECTED"] = "true",
            ["BRANCH_ROLE"] = branchRole,
        };

        return WorkflowShell.RunBash(
            YamlWorkflowReader.ScalarChild(step, "run"),
            Path.GetTempPath(),
            environment);
    }
}
