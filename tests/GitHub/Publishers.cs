using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>Read-only contract coverage for the committed caller-owned composite action YAML; never renders or executes a GitHub job.</summary>
public sealed class CallerOwnedPublishingActionContractTests
{
    private static readonly string[] ActionDirectories =
    [
        "preflight",
        "private",
        "public",
        "tag",
    ];

    private static readonly string[] PublisherActions =
    [
        "public",
        "private",
    ];

    private static string ActionsRoot() =>
        Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions");

    private static string ActionPath(string directory) =>
        Path.Combine(ActionsRoot(), directory, "action.yml");

    private static string Read(string directory) => File.ReadAllText(ActionPath(directory));

    private static string ReadPrivatePublisherValidator() => File.ReadAllText(
        Path.Combine(ActionsRoot(), "private", "scripts", "validate.mjs"));

    private static string ReadPublicPublisherProvenanceValidator() => File.ReadAllText(
        Path.Combine(ActionsRoot(), "public", "scripts", "nuspec_provenance.py"));

    [Fact]
    public void DeclaredActions_AreExactlyThePreflightPublishersAndTag()
    {
        var actions = Directory.EnumerateDirectories(ActionsRoot())
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ActionDirectories.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actions);
        EveryDeclaredAction_ExistsAndParsesAsYaml();
        CompositeActions_DoNotReachIntoCallerNeedsContext();
        EveryNestedActionReference_IsPinnedToAFullSha();
        NoAction_InheritsSecretsOrStoresAPermanentKey();
    }

    private void EveryDeclaredAction_ExistsAndParsesAsYaml()
    {
        foreach (var directory in ActionDirectories)
        {
            var yaml = new YamlStream();
            using var reader = new StringReader(Read(directory));
            yaml.Load(reader);
            Assert.Single(yaml.Documents);
        }
    }

    private void CompositeActions_DoNotReachIntoCallerNeedsContext()
    {
        foreach (var directory in ActionDirectories)
        {
            Assert.DoesNotContain("${{ needs.", Read(directory), StringComparison.Ordinal);
        }
    }

    private void EveryNestedActionReference_IsPinnedToAFullSha()
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
        var content = Read("public");

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("refs/heads/", content, StringComparison.Ordinal);
        Assert.Contains("github.event_name", content, StringComparison.Ordinal);
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_URL", content, StringComparison.Ordinal);
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_TOKEN", content, StringComparison.Ordinal);
        Assert.Contains("audience=https%3A%2F%2Fwww.nuget.org", content, StringComparison.Ordinal);
        Assert.Contains("https://www.nuget.org/api/v2/token", content, StringComparison.Ordinal);
        Assert.Contains("--request PUT", content, StringComparison.Ordinal);
        Assert.Contains("X-NuGet-ApiKey: $nuget_api_key", content, StringComparison.Ordinal);
        Assert.Contains("Calculated package state drifted before publication", content, StringComparison.Ordinal);
        Assert.Contains("::add-mask::", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);

        Assert.DoesNotContain("packages: write", content, StringComparison.Ordinal);
        Assert.DoesNotContain(@"--api-key ""$GH_TOKEN""", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", content, StringComparison.Ordinal);
        Assert.Contains("REPOSITORY_VISIBILITY: ${{ inputs.repository-visibility }}", content, StringComparison.Ordinal);
        Assert.Contains("if [[ \"$REPOSITORY_VISIBILITY\" != public ]]; then", content, StringComparison.Ordinal);
        PrivatePublisher_UsesCallerTokenWithoutOidcAndRejectsNonPrivateVisibility();
        Publishers_RecheckCollisionAndProvenanceImmediatelyBeforePublishing();
        Publishers_UseTheExactArtifactAndNeverRebuild();
    }

    [Fact]
    public void PublicPublisher_DiscriminatesDuplicateByStructuredStatusAndBoundsReadback()
    {
        var content = Read("public");

        // A new package is accepted on 2xx and needs no readback; only a duplicate 409 continues.
        Assert.Contains("2*) echo \"NuGet.org accepted public package $PACKAGE_VERSION.\"; exit 0 ;;", content, StringComparison.Ordinal);
        Assert.Contains("409) echo \"NuGet.org already has public package $PACKAGE_VERSION; reconciling the existing package provenance.\" ;;", content, StringComparison.Ordinal);
        Assert.Contains("*) echo \"NuGet.org returned HTTP $push_status for the push; failing closed.\" >&2; exit 1 ;;", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", content, StringComparison.Ordinal);

        // Every request carries explicit connect/total budgets; the key-bearing PUT never follows a redirect.
        var pushLine = content.Split('\n').Single(line => line.Contains("--request PUT", StringComparison.Ordinal));
        Assert.Contains("--connect-timeout 10 --max-time 60", pushLine, StringComparison.Ordinal);
        Assert.DoesNotContain("--location", pushLine, StringComparison.Ordinal);
        var boundedReads = Regex.Matches(
            content,
            Regex.Escape("--location --connect-timeout 5 --max-time 15")).Count;
        Assert.Equal(3, boundedReads); // precheck index, precheck package, duplicate readback
        Assert.Contains(
            "--connect-timeout 5 --max-time 15 --header \"Authorization: Bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN\"",
            content,
            StringComparison.Ordinal);
        Assert.Contains("--connect-timeout 5 --max-time 15 --request POST", content, StringComparison.Ordinal);

        // The untrusted nuspec is parsed by the checked-in structural module, not a regex.
        Assert.Contains(
            "python3 \"$GITHUB_ACTION_PATH/scripts/nuspec_provenance.py\"",
            content,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(content, Regex.Escape("scripts/nuspec_provenance.py")).Count);
        Assert.DoesNotContain("grep -oE 'commit=", content, StringComparison.Ordinal);

        // The finite duplicate readback policy is centralized in the publish step.
        Assert.Contains("readback_attempts=13", content, StringComparison.Ordinal);
        Assert.Contains("readback_interval_seconds=10", content, StringComparison.Ordinal);
        Assert.Contains("readback_max_delay_seconds=120", content, StringComparison.Ordinal);
        Assert.Contains(
            "package_download_url=\"https://api.nuget.org/v3-flatcontainer/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg\"",
            content,
            StringComparison.Ordinal);

        // Readable but foreign or malformed provenance is terminal; only an unreadable package is retried and then fails closed.
        Assert.Contains(
            "The published public package has divergent provenance for this caller SHA; failing closed.",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "The published public package has missing or malformed provenance; failing closed.",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "Could not read back the published public package for $PACKAGE_VERSION after $readback_attempts attempts; failing closed.",
            content,
            StringComparison.Ordinal);

        // The readback runs only after a duplicate status; an accepted push exits before it.
        var pushStatusIndex = content.IndexOf("push_status=$(curl", StringComparison.Ordinal);
        var duplicateIndex = content.IndexOf("409) echo", StringComparison.Ordinal);
        var readbackIndex = content.IndexOf("Readback attempt", StringComparison.Ordinal);
        Assert.True(pushStatusIndex >= 0 && duplicateIndex > pushStatusIndex && readbackIndex > duplicateIndex);

        // The private publisher's immediate push is unchanged.
        Assert.DoesNotContain("readback_attempts", Read("private"), StringComparison.Ordinal);
    }

    private void PrivatePublisher_UsesCallerTokenWithoutOidcAndRejectsNonPrivateVisibility()
    {
        var content = Read("private");

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
        PrivatePublisher_ResolvesPackageBaseAddressFromTheAuthenticatedServiceIndex();
        PrivatePublisher_ValidatesTheTrustedOriginBeforeAuthenticatingDerivedRequests();
        PrivatePublisher_DeclaresTheServiceIndexDiscoveryFilter();
        Assert.DoesNotContain("nuget-user", content, StringComparison.Ordinal);
    }

    private void PrivatePublisher_ResolvesPackageBaseAddressFromTheAuthenticatedServiceIndex()
    {
        var content = Read("private");

        // The collision recheck discovers the flat-container base from the authenticated service index; no path shape is assumed.
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

        // The discovered @id is validated by the checked-in named module, pinned to the exact origin, protocol, authority, and empty query/fragment before any URL is derived.
        // The untrusted candidate travels on stdin, never on the interpreter command line.
        Assert.Contains(
            "printf '%s' \"$package_base_address\" | node \"$GITHUB_ACTION_PATH/scripts/validate.mjs\"",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "validate.mjs\" \"$package_base_address\"",
            content,
            StringComparison.Ordinal);

        var validator = ReadPrivatePublisherValidator();
        Assert.Contains("readFileSync(0, \"utf8\")", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("argv[2]", validator, StringComparison.Ordinal);
        Assert.Contains("new URL(candidate)", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.protocol !== \"https:\"", validator, StringComparison.Ordinal);
        Assert.Contains("const TRUSTED_HOSTNAME = \"nuget.pkg.github.com\"", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.hostname !== TRUSTED_HOSTNAME", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.port !== \"\"", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.username !== \"\" || parsed.password !== \"\"", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.search !== \"\" || parsed.hash !== \"\"", validator, StringComparison.Ordinal);
        Assert.Contains("parsed.href !== candidate", validator, StringComparison.Ordinal);

        // Derived URLs use only the discovered base and lower-cased ids.
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

    private void PrivatePublisher_ValidatesTheTrustedOriginBeforeAuthenticatingDerivedRequests()
    {
        var content = Read("private");

        // Ordering proof: validation and base normalization must precede derived authenticated requests.
        var validationIndex = content.IndexOf(
            "scripts/validate.mjs",
            StringComparison.Ordinal);
        var normalizeIndex = content.IndexOf(
            "package_base_address=${package_base_address%/}",
            StringComparison.Ordinal);
        var indexUrlIndex = content.IndexOf("package_index_url=", StringComparison.Ordinal);
        var downloadUrlIndex = content.IndexOf("package_download_url=", StringComparison.Ordinal);
        var indexRequestIndex = content.IndexOf(
            "--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_index_url\"",
            StringComparison.Ordinal);
        var downloadRequestIndex = content.IndexOf(
            "--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_download_url\"",
            StringComparison.Ordinal);

        Assert.True(validationIndex >= 0, "the checked-in structural validator call is missing.");
        Assert.True(normalizeIndex > validationIndex, "the base is normalized before structural validation.");
        Assert.True(indexUrlIndex > normalizeIndex, "the package index URL is derived before validation.");
        Assert.True(downloadUrlIndex > indexUrlIndex, "the download URL is derived before the index URL.");
        Assert.True(indexRequestIndex > indexUrlIndex, "the package index is requested before it is derived.");
        Assert.True(downloadRequestIndex > downloadUrlIndex, "the package download is requested before it is derived.");

        // Only the known service-index URL and the two URLs derived from the validated base are authenticated.
        var authenticatedVariables = Regex
            .Matches(content, @"--user ""\$GITHUB_ACTOR:\$GH_TOKEN"" ""\$(?<variable>[A-Za-z_]+)""")
            .Cast<Match>()
            .Select(match => match.Groups["variable"].Value)
            .ToArray();
        Assert.Equal(
            new[] { "service_index_url", "package_index_url", "package_download_url" },
            authenticatedVariables);
    }

    private void PrivatePublisher_DeclaresTheServiceIndexDiscoveryFilter()
    {
        // jq is stubbed in the executable tests, so the committed discovery filter is pinned here instead of by behavior.
        var match = Regex.Match(
            Read("private"),
            @"if ! raw_package_base_address=\$\(jq -er '(?<filter>.*?)' ""\$response_file"" && printf 'x'\); then",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "the private publisher has no service-index discovery filter.");

        var normalized = Regex.Replace(match.Groups["filter"].Value, @"\s+", " ").Trim();
        var expected =
            "if type == \"object\" and (.resources | type == \"array\") then "
            + "[.resources[] | select( (.[\"@type\"] | type == \"string\" and . == \"PackageBaseAddress/3.0.0\") "
            + "or (.[\"@type\"] | type == \"array\" and (index(\"PackageBaseAddress/3.0.0\") != null)) ) "
            + "| .[\"@id\"] ] "
            + "| if length == 1 and (.[0] | type == \"string\") and (.[0] | length > 0) and (.[0] | test(\"[[:cntrl:]]\") | not) then .[0] else empty end "
            + "else error(\"malformed service index\") end";
        Assert.Equal(expected, normalized);
    }

    private void Publishers_RecheckCollisionAndProvenanceImmediatelyBeforePublishing()
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

            // The existing version must carry the caller commit, and a matching version short-circuits the push.
            Assert.Contains("no authenticated provenance for this caller SHA", content, StringComparison.Ordinal);
            Assert.Contains("package-state=published", content, StringComparison.Ordinal);
            Assert.Contains("package-state=unpublished", content, StringComparison.Ordinal);
            Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", content, StringComparison.Ordinal);

            if (directory == "public")
            {
                // The published signal is emitted only after the structural parse succeeds.
                PublicPublisher_RequiresStructuralNuspecProvenance(content);
            }
            else
            {
                // The private path is unchanged: broad commit extraction immediately before the published signal.
                Assert.Contains(provenanceComparison, content, StringComparison.Ordinal);
                Assert.Contains("nuspec=$(unzip -p \"$package_file\" '*.nuspec' 2>/dev/null || true)", content, StringComparison.Ordinal);
                var comparisonIndex = content.IndexOf("repository_commit", StringComparison.Ordinal);
                var publishedIndex = content.IndexOf("package-state=published", StringComparison.Ordinal);
                Assert.True(comparisonIndex >= 0 && publishedIndex > comparisonIndex);
            }
        }
    }

    private static void PublicPublisher_RequiresStructuralNuspecProvenance(string content)
    {
        // NuGet.org repository-signs the stored archive, so bytes are not compared; the untrusted nuspec is parsed structurally.
        Assert.DoesNotContain("sha256sum < \"$package_file\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256sum < \"$PACKAGE_ARTIFACT\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("byte-identical", content, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -oE 'commit=", content, StringComparison.Ordinal);

        Assert.Contains(
            @"nuspec_entries=$(unzip -Z1 ""$package_file"" 2>/dev/null | grep -i '\.nuspec$' || true)",
            content,
            StringComparison.Ordinal);
        Assert.Contains("nuspec_count=$(printf '%s\\n' \"$nuspec_entries\" | grep -c '[^[:space:]]' || true)", content, StringComparison.Ordinal);
        Assert.Contains("does not contain exactly one nuspec", content, StringComparison.Ordinal);
        Assert.Contains("nuspec_name=$(printf '%s\\n' \"$nuspec_entries\" | grep '[^[:space:]]')", content, StringComparison.Ordinal);
        Assert.Contains("nuspec is not the expected package nuspec", content, StringComparison.Ordinal);
        Assert.Contains("nuspec=$(unzip -p \"$package_file\" \"$nuspec_name\" 2>/dev/null || true)", content, StringComparison.Ordinal);

        // Both the precheck and the duplicate readback delegate to the same structural module.
        var invocation = "python3 \"$GITHUB_ACTION_PATH/scripts/nuspec_provenance.py\"";
        Assert.Contains("| " + invocation, content, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(content, Regex.Escape(invocation)).Count);
        Assert.Contains("The existing public package version nuspec ID does not match the expected package ID", content, StringComparison.Ordinal);
        Assert.Contains("The published public package nuspec ID does not match the expected package ID", content, StringComparison.Ordinal);

        // The published signal is emitted only after the structural parse succeeds.
        var parseIndex = content.IndexOf("scripts/nuspec_provenance.py", StringComparison.Ordinal);
        var publishedIndex = content.IndexOf("package-state=published", StringComparison.Ordinal);
        Assert.True(parseIndex >= 0 && publishedIndex > parseIndex);

        // The module must be namespace-aware, structural, and DTD/entity-hostile.
        var module = ReadPublicPublisherProvenanceValidator();
        Assert.Contains("xml.parsers.expat", module, StringComparison.Ordinal);
        Assert.Contains("XML_PARAM_ENTITY_PARSING_NEVER", module, StringComparison.Ordinal);
        Assert.Contains("StartDoctypeDeclHandler", module, StringComparison.Ordinal);
        Assert.Contains("EntityDeclHandler", module, StringComparison.Ordinal);
        Assert.Contains("ExternalEntityRefHandler", module, StringComparison.Ordinal);
        Assert.Contains("_NUSPEC_NAMESPACE", module, StringComparison.Ordinal);
        Assert.Contains("_sole_child", module, StringComparison.Ordinal);
        Assert.Contains("casefold", module, StringComparison.Ordinal);
        Assert.Contains("GITHUB_SHA", module, StringComparison.Ordinal);
    }

    private void Publishers_UseTheExactArtifactAndNeverRebuild()
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

    private void NoAction_InheritsSecretsOrStoresAPermanentKey()
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
        var content = Read("tag");

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
        ReleaseTagAction_DeclaresARequiredReleaseOnlyBranchRoleInput();
    }

    private void ReleaseTagAction_DeclaresARequiredReleaseOnlyBranchRoleInput()
    {
        var root = YamlWorkflowReader.Parse(Read("tag"));
        var branchRole = YamlWorkflowReader.MappingChild(
            YamlWorkflowReader.MappingChild(root, "inputs"), "branch-role");

        Assert.Equal("true", YamlWorkflowReader.ScalarChild(branchRole, "required"));
        Assert.Contains(
            "BRANCH_ROLE: ${{ inputs.branch-role }}",
            Read("tag"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseTagAction_AcceptsOnlyTheExactProductionBranchRole()
    {
        var result = RunReleaseTagValidation("production");

        Assert.Equal(0, result.ExitCode);
        ReleaseTagAction_FailsClosedForAnyNonProductionBranchRole();
        ReleaseTagAction_RejectsAnyEventOtherThanPushAndPreservesProtectedBranchAndProductionRoleGuards();
    }

    private void ReleaseTagAction_FailsClosedForAnyNonProductionBranchRole()
    {
        foreach (var branchRole in new[] { "development", "none", "Production", "production ", "release", "" })
        {
            var result = RunReleaseTagValidation(branchRole);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Tagging requires the preparation branch role 'production'", result.StandardError, StringComparison.Ordinal);
        }
    }

    private void ReleaseTagAction_RejectsAnyEventOtherThanPushAndPreservesProtectedBranchAndProductionRoleGuards()
    {
        // The event-name guard runs first so a workflow_dispatch or pull_request
        // event is rejected before the protected-branch and production-role
        // guards can be silently skipped; the protected-branch and branch-role
        // guards still trip after the event-name passes.
        foreach (var eventName in new[] { "workflow_dispatch", "pull_request", "schedule", "" })
        {
            var rejectedByEvent = RunReleaseTagValidation("production", eventName);
            Assert.NotEqual(0, rejectedByEvent.ExitCode);
            Assert.Contains("Tagging is allowed only from push.", rejectedByEvent.StandardError, StringComparison.Ordinal);
        }
        var rejectedByBranch = RunReleaseTagValidation("production", refProtected: "false");
        var rejectedByRole = RunReleaseTagValidation("development");
        Assert.NotEqual(0, rejectedByBranch.ExitCode);
        Assert.NotEqual(0, rejectedByRole.ExitCode);
    }

    [Fact]
    public void Publishers_RejectAnyEventOtherThanPush()
    {
        // The publisher guards run as defense in depth: the caller workflow
        // never starts a workflow_dispatch and the prepare job is push-only, so
        // dispatch cannot reach the publisher. The action still rejects any
        // non-push event, including a pull_request, as the second line of defense.
        foreach (var actionDirectory in PublisherActions)
        {
            foreach (var eventName in new[] { "workflow_dispatch", "pull_request", "schedule" })
            {
                var result = RunPublisherValidation(
                    actionDirectory,
                    eventName,
                    refProtected: "true",
                    refName: "refs/heads/main",
                    visibility: actionDirectory == "public" ? "public" : "private");
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("Publishing is allowed only from push.", result.StandardError, StringComparison.Ordinal);
            }
            Publishers_StillRejectUnprotectedRefOnPush(actionDirectory);
        }
        PublicPublisher_StillRejectsNonPublicVisibilityOnPush();
        PrivatePublisher_StillRejectsNonPrivateVisibilityOnPush();
        foreach (var actionDirectory in PublisherActions)
        {
            Publishers_AcceptPushOnProtectedBranchWithMatchingVisibility(actionDirectory);
        }
    }

    private void Publishers_StillRejectUnprotectedRefOnPush(string actionDirectory)
    {
        // The event-name guard must not silently short-circuit the protected-branch
        // guard; an unprotected branch ref still fails closed on a push.
        var visibility = actionDirectory == "public" ? "public" : "private";

        var unprotectedRef = RunPublisherValidation(
            actionDirectory,
            "push",
            refProtected: "false",
            refName: "refs/heads/main",
            visibility: visibility);

        Assert.NotEqual(0, unprotectedRef.ExitCode);
        Assert.Contains(
            "Publishing requires a protected branch ref.",
            unprotectedRef.StandardError,
            StringComparison.Ordinal);

        var tagRef = RunPublisherValidation(
            actionDirectory,
            "push",
            refProtected: "true",
            refName: "refs/tags/v3.0.0",
            visibility: visibility);

        Assert.NotEqual(0, tagRef.ExitCode);
        Assert.Contains(
            "Publishing requires a protected branch ref.",
            tagRef.StandardError,
            StringComparison.Ordinal);
    }

    private void PublicPublisher_StillRejectsNonPublicVisibilityOnPush()
    {
        var result = RunPublisherValidation(
            "public",
            "push",
            refProtected: "true",
            refName: "refs/heads/main",
            visibility: "private");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The public publisher requires a public caller repository",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private void PrivatePublisher_StillRejectsNonPrivateVisibilityOnPush()
    {
        var result = RunPublisherValidation(
            "private",
            "push",
            refProtected: "true",
            refName: "refs/heads/main",
            visibility: "public");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The private publisher requires a private caller repository",
            result.StandardError,
            StringComparison.Ordinal);
    }

    private void Publishers_AcceptPushOnProtectedBranchWithMatchingVisibility(string actionDirectory)
    {
        // The positive control: a push on a protected branch with the matching
        // visibility passes the publisher guard, so a production re-run keeps
        // the same inputs and the action reaches the existing package check.
        var visibility = actionDirectory == "public" ? "public" : "private";

        var result = RunPublisherValidation(
            actionDirectory,
            "push",
            refProtected: "true",
            refName: "refs/heads/main",
            visibility: visibility);

        Assert.Equal(0, result.ExitCode);
    }

    private static ShellResult RunReleaseTagValidation(
        string branchRole,
        string eventName = "push",
        string refProtected = "true",
        string refName = "refs/heads/main")
    {
        var root = YamlWorkflowReader.Parse(Read("tag"));
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(step =>
            YamlWorkflowReader.ScalarChild(step, "name") == "Validate trusted tag invocation");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EVENT_NAME"] = eventName,
            ["REF_NAME"] = refName,
            ["REF_PROTECTED"] = refProtected,
            ["BRANCH_ROLE"] = branchRole,
        };

        return WorkflowShell.RunBash(
            YamlWorkflowReader.ScalarChild(step, "run"),
            Path.GetTempPath(),
            environment);
    }

    private static ShellResult RunPublisherValidation(
        string actionDirectory,
        string eventName,
        string refProtected,
        string refName,
        string visibility)
    {
        var root = YamlWorkflowReader.Parse(Read(actionDirectory));
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var stepName = actionDirectory == "public"
            ? "Validate public publisher invocation"
            : "Validate private publisher invocation";
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(step =>
            YamlWorkflowReader.ScalarChild(step, "name") == stepName);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EVENT_NAME"] = eventName,
            ["REF_NAME"] = refName,
            ["REF_PROTECTED"] = refProtected,
            ["REPOSITORY_VISIBILITY"] = visibility,
            ["NUGET_USER"] = "test-nuget-user",
        };

        return WorkflowShell.RunBash(
            YamlWorkflowReader.ScalarChild(step, "run"),
            Path.GetTempPath(),
            environment);
    }
}
