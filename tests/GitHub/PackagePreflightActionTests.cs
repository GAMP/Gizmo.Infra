using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract and executable coverage for the bundled <c>package-preflight</c>
/// composite action: deterministic SDK-style packable project discovery, MSBuild
/// metadata reads, <c>.github/package.yml</c> branch configuration and role
/// resolution, authenticated caller-repository visibility, and the guarantee that
/// the preflight adds no publishing or registry-routing behavior.
///
/// Executable tests run the action's own extracted bash and Node blocks against a
/// real working tree, so the committed source, not a hand copy, is under test.
/// Network and GitHub are unavailable locally, so the visibility transport is
/// exercised through its exact command text plus a curl test double, and the
/// visibility parsing block runs its committed jq filter through a Node jq shim.
/// </summary>
public sealed class PackagePreflightActionTests
{
    private const string PreflightAction = "package-preflight";
    private const string ValidationFile = "package-validation.yml";
    private const string DevelopmentFile = "package-development.yml";
    private const string ReleaseFile = "package-release.yml";

    private static readonly string[] ContractFiles = [ValidationFile, DevelopmentFile, ReleaseFile];

    private static readonly string Action = WorkflowShell.ReadAction(PreflightAction);

    private const string PackableProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>Gizmo.Widget</PackageId>
            <Version>3.4</Version>
          </PropertyGroup>
        </Project>
        """;

    private const string UnpackableProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <IsPackable>false</IsPackable>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Discovery_SelectsTheSinglePackableSdkProjectAndPrunesRepositoryTrees()
    {
        using var repository = new TempRepository();
        repository.WriteFile("Widget.csproj", PackableProject);
        repository.WriteFile("Samples.csproj", UnpackableProject);

        // Checked-out metadata and the immutable Gizmo.Infra source tree may hold
        // their own projects; neither is part of the caller's packable surface.
        repository.WriteFile(".git/objects/Historical.csproj", PackableProject);
        repository.WriteFile(".gizmo-infra/action/Packed.csproj", PackableProject);

        var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("count=1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("project_path=Widget.csproj", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_FailsClosedWhenNoProjectIsAPackableCandidate()
    {
        using var repository = new TempRepository();
        repository.WriteFile("Samples.csproj", UnpackableProject);

        var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("found none", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_FailsClosedWhenMultipleProjectsArePackableCandidates()
    {
        using var repository = new TempRepository();
        repository.WriteFile("Alpha.csproj", PackableProject);
        repository.WriteFile("Beta.csproj", PackableProject);

        var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("found multiple candidates", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_UsesDeterministicNulTerminatedOrderingAndAPackablePredicate()
    {
        Assert.Contains(
            "-path ./.git -prune -o -path ./.gizmo-infra -prune -o -type f -name '*.csproj' -print0",
            Action,
            StringComparison.Ordinal);
        Assert.Contains("| LC_ALL=C sort -z)", Action, StringComparison.Ordinal);
        Assert.Contains("[[ \"$is_sdk_style\" == true && \"$is_packable\" == true ]]", Action, StringComparison.Ordinal);
        Assert.Contains("candidates+=(\"$project\")", Action, StringComparison.Ordinal);
        Assert.Contains("case ${#candidates[@]} in", Action, StringComparison.Ordinal);
        Assert.Contains("1) project_path=${candidates[0]} ;;", Action, StringComparison.Ordinal);
        Assert.Contains("0) fail \"Expected exactly one SDK-style packable .csproj; found none.\" ;;", Action, StringComparison.Ordinal);
        Assert.Contains("*) fail \"Expected exactly one SDK-style packable .csproj; found multiple candidates.\" ;;", Action, StringComparison.Ordinal);

        // A discovered path that carries a newline cannot be forwarded safely.
        Assert.Contains("[[ \"$project\" != *$'\\n'* && \"$project\" != *$'\\r'* ]]", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_ReadsPackageIdVersionAndPackabilityThroughMsbuild()
    {
        Assert.Contains("-getProperty:UsingMicrosoftNETSdk", Action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:IsPackable", Action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:PackageId", Action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:Version", Action, StringComparison.Ordinal);
        Assert.Contains(
            "[[ \"$is_packable\" == true ]] || fail \"The discovered project is not packable.\"",
            Action,
            StringComparison.Ordinal);
        Assert.Contains(
            "[[ \"$package_id\" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || fail \"The discovered project PackageId has an invalid format.\"",
            Action,
            StringComparison.Ordinal);

        // The evaluated project Version is the 3.X compatibility line only.
        Assert.Contains(
            "[[ \"$compatibility_line\" =~ ^3\\.(0|[1-9][0-9]*)$ ]] || fail \"The discovered project Version must be exactly 3.X, with numeric X and no leading zero.\"",
            Action,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("branches:\n  development: develop\n  release: main", "{\"development\":\"develop\",\"release\":\"main\"}")]
    [InlineData("# comment\nbranches:\n\n  development: version-3\n  release: release", "{\"development\":\"version-3\",\"release\":\"release\"}")]
    [InlineData("branches:\n  development: 'release candidate'\n  release: \"main\"", "{\"development\":\"release candidate\",\"release\":\"main\"}")]
    public void BranchConfig_ParsesExactlyTwoStringBranches(string yaml, string expectedJson)
    {
        var result = RunConfig(yaml);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedJson, result.StandardOutput);
    }

    [Theory]
    [InlineData("branches:\n  development: develop")]                                    // missing release
    [InlineData("branches:\n  release: main")]                                            // missing development
    [InlineData("branches:\n  development: a\n  development: b\n  release: c")]           // duplicate key
    [InlineData("branches:\n  development: a\n  release: b\n  extra: c")]                 // unknown key
    [InlineData("branches:\n  development: 3\n  release: main")]                          // numeric scalar
    [InlineData("branches:\n  development: true\n  release: main")]                       // boolean scalar
    [InlineData("branches:\n  development: ~\n  release: main")]                          // null scalar
    [InlineData("branches:\n  development: develop \n  release: main")]                   // trailing whitespace
    [InlineData("branches:\n  development:\n  release: main")]                            // empty value
    [InlineData("branches:\n  development: [a]\n  release: main")]                        // flow sequence
    [InlineData("branches:\n  development: 'unterminated\n  release: main")]              // malformed quoting
    [InlineData("development: a\nrelease: b")]                                            // missing root
    [InlineData("branches:\r\n  development: a\r\n  release: b\r\n  repeated: c")]        // CRLF with unknown key
    public void BranchConfig_FailsClosedOnMalformedOrIncompleteSchemas(string yaml)
    {
        var result = RunConfig(yaml);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void BranchConfig_RequiresDistinctValidGitBranchNamesAndEmitsEveryOutput()
    {
        Assert.Contains("[[ \"$development_branch\" != \"$release_branch\" ]] || fail", Action, StringComparison.Ordinal);
        Assert.Contains("git check-ref-format --branch \"$development_branch\"", Action, StringComparison.Ordinal);
        Assert.Contains("git check-ref-format --branch \"$release_branch\"", Action, StringComparison.Ordinal);
        Assert.Contains(
            @"printf 'project-path=%s\npackage-id=%s\ncompatibility-line=%s\nrepository-visibility=%s\nbranch-role=%s\n'",
            Action,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refs/heads/develop", "development")]
    [InlineData("refs/heads/main", "release")]
    [InlineData("refs/heads/feature", "none")]
    [InlineData("refs/tags/v3.0.0", "none")]
    public void BranchRole_ResolvesDevelopmentReleaseOrNone(string currentRef, string expected)
    {
        var result = RunBranchRole(currentRef);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput);
    }

    [Fact]
    public void VisibilityRequest_UsesCallerRepositoryAndWorkflowTokenAuthenticatedEndpoint()
    {
        Assert.Contains(
            "[[ \"$CALLER_REPOSITORY\" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail \"github.repository is malformed.\"",
            Action,
            StringComparison.Ordinal);
        Assert.Contains(
            "[[ -n \"$GH_TOKEN\" ]] || fail \"The caller GITHUB_TOKEN is required to read repository metadata.\"",
            Action,
            StringComparison.Ordinal);
        Assert.Contains("\"$GITHUB_API_URL/repos/$CALLER_REPOSITORY\"", Action, StringComparison.Ordinal);
        Assert.Contains("--header \"Authorization: Bearer $GH_TOKEN\"", Action, StringComparison.Ordinal);
        Assert.Contains("--header 'Accept: application/vnd.github+json'", Action, StringComparison.Ordinal);
        Assert.Contains("--write-out '%{http_code}'", Action, StringComparison.Ordinal);
        Assert.Contains("CALLER_REPOSITORY: ${{ inputs.caller-repository }}", Action, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ inputs.github-token }}", Action, StringComparison.Ordinal);

        // The authenticated lookup must be time-bounded so a stalled GitHub
        // response cannot hang the preflight indefinitely.
        Assert.Contains("--connect-timeout 10", Action, StringComparison.Ordinal);
        Assert.Contains("--max-time 30", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Visibility_DeclaresTheFailClosedStringTypeFilter()
    {
        // The executable tests below run this filter through the jq shim; pinning
        // the committed text keeps a weakened filter from being masked by it.
        Assert.Equal(
            "if type == \"object\" and (.visibility | type == \"string\") then .visibility else error(\"missing visibility\") end",
            VisibilityJqFilter());
        Assert.Contains("fail \"GitHub returned malformed caller repository metadata.\"", Action, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "500", "Could not fetch authenticated caller repository metadata.")]
    [InlineData(false, "500", "GitHub returned HTTP 500 for caller repository metadata.")]
    [InlineData(false, "404", "GitHub returned HTTP 404 for caller repository metadata.")]
    public void Visibility_FailsClosedOnTransportAndNonSuccessStatus(
        bool failTransport,
        string httpStatus,
        string expectedError)
    {
        var result = RunVisibilityRequest(failTransport, httpStatus);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Visibility_AcceptsAnAuthenticatedHttp200Response()
    {
        var result = RunVisibilityRequest(failTransport: false, httpStatus: "200");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void VisibilityRequest_ExtractsTheCompleteRequestBlockNotAnInnerSubstring()
    {
        var transport = WorkflowShell.ExtractBlock(Action, "if ! status=$(curl", "fi");

        // Regression: the 'fi' inside 'response_file' must not close the block.
        Assert.Contains("--output \"$response_file\"", transport, StringComparison.Ordinal);
        Assert.Contains(
            "Could not fetch authenticated caller repository metadata.",
            transport,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("internal")]
    public void Visibility_AcceptsOnlyKnownVisibilityFromParsedMetadata(string value)
    {
        var result = RunVisibilityParsing($"{{\"visibility\":\"{value}\"}}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("accepted", result.StandardOutput);
    }

    [Theory]
    [InlineData("gist")]
    [InlineData("PUBLIC")]
    [InlineData("")]
    public void Visibility_FailsClosedOnUnknownVisibilityValues(string value)
    {
        var result = RunVisibilityParsing($"{{\"visibility\":\"{value}\"}}");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "GitHub returned an unsupported caller repository visibility.",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]                              // object without visibility
    [InlineData("{\"visibility\":null}")]           // explicit null
    [InlineData("{\"visibility\":123}")]            // non-string number
    [InlineData("{\"visibility\":true}")]           // non-string boolean
    [InlineData("{\"visibility\":[\"public\"]}")]   // non-string array
    [InlineData("[]")]                              // non-object array
    [InlineData("\"public\"")]                      // non-object string
    [InlineData("null")]                            // non-object null
    [InlineData("{")]                               // malformed JSON
    [InlineData("not json")]                        // malformed JSON
    public void Visibility_FailsClosedOnMissingNonStringOrNonObjectMetadata(string responseBody)
    {
        var result = RunVisibilityParsing(responseBody);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "GitHub returned malformed caller repository metadata.",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void Preflight_NeverReadsEventSpecificRepositoryContext()
    {
        Assert.DoesNotContain("github.event.repository", Action, StringComparison.Ordinal);

        foreach (var file in ContractFiles)
        {
            Assert.DoesNotContain("github.event.repository.visibility", Read(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Preflight_IntroducesNoPublishingOrRegistryRouting()
    {
        foreach (var forbidden in new[]
                 {
                     "dotnet nuget push", "dotnet pack", "dotnet build", "dotnet restore",
                     "ACTIONS_ID_TOKEN", "id-token", "api.nuget.org", "nuget.pkg.github.com",
                     "NUGET_API_KEY", "packages: write", "secrets:", "GITHUB_REPOSITORY_OWNER",
                     "--source", "registry",
                 })
        {
            Assert.DoesNotContain(forbidden, Action, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Preflight_DeclaresNoPublisherIdentityOrProfileValidation()
    {
        // E15 leaves the publisher and tag jobs disabled and authorizes no
        // routing contract, so the preflight must carry no publisher identity and
        // perform no public-profile validation. Reintroducing either would let the
        // preflight branch on a publisher credential.
        Assert.DoesNotContain("NUGET_USER", Action, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget-user", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Action_DeclaresOnlyThePreflightInputsOutputsAndCompositeStep()
    {
        var root = YamlWorkflowReader.Parse(Action);

        Assert.Equal(
            new[] { "caller-repository", "current-ref", "github-token" },
            MappingKeys(YamlWorkflowReader.MappingChild(root, "inputs"))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            new[] { "branch-role", "compatibility-line", "package-id", "project-path", "repository-visibility" },
            MappingKeys(YamlWorkflowReader.MappingChild(root, "outputs"))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        Assert.Equal("composite", YamlWorkflowReader.ScalarChild(runs, "using"));

        var step = Assert.Single(YamlWorkflowReader.MappingSequence(runs, "steps"));
        Assert.Equal("discover", YamlWorkflowReader.ScalarChild(step, "id"));
        Assert.Equal("bash", YamlWorkflowReader.ScalarChild(step, "shell"));
        Assert.False(YamlWorkflowReader.HasChild(step, "uses"));
    }

    [Fact]
    public void Workflows_PassCallerIdentityAndConsumeOnlyPreflightMetadataOutputs()
    {
        foreach (var file in ContractFiles)
        {
            var build = BuildJob(Parse(file), file);
            var metadata = StepById(build, "metadata");

            Assert.Equal(
                "./.gizmo-infra/.github/actions/package-preflight",
                YamlWorkflowReader.ScalarChild(metadata, "uses"));

            var with = YamlWorkflowReader.MappingChild(metadata, "with");
            Assert.Equal("${{ github.repository }}", YamlWorkflowReader.ScalarChild(with, "caller-repository"));
            Assert.Equal("${{ github.token }}", YamlWorkflowReader.ScalarChild(with, "github-token"));
            Assert.Equal("${{ github.ref }}", YamlWorkflowReader.ScalarChild(with, "current-ref"));

            // The preflight discovers these; the workflow must not resupply them.
            // E15 also removes every publisher input, so no nuget-user or
            // require-nuget-user may reappear before a routing contract exists.
            foreach (var forbidden in new[]
                     {
                         "project-path", "package-id", "package-visibility", "repository-visibility",
                         "nuget-user", "require-nuget-user",
                     })
            {
                Assert.False(YamlWorkflowReader.HasChild(with, forbidden), $"{file} must not pass '{forbidden}'.");
            }

            // A reusable workflow with no declared inputs must not read the inputs
            // context at all.
            Assert.DoesNotContain("inputs.", Read(file), StringComparison.Ordinal);
        }
    }

    private static ShellResult RunConfig(string yaml)
    {
        using var repository = new TempRepository();
        var configPath = repository.WriteFile(".github/package.yml", yaml);
        return WorkflowShell.RunNode(NodeProgram(), configPath);
    }

    private static ShellResult RunBranchRole(string currentRef)
    {
        var block = WorkflowShell.ExtractBlock(Action, "branch_role=none", "fi");
        var script =
            "development_branch=develop\n"
            + "release_branch=main\n"
            + $"CURRENT_REF='{currentRef}'\n"
            + block
            + "\nprintf '%s' \"$branch_role\"\n";
        return WorkflowShell.RunBash(script, Path.GetTempPath());
    }

    private static ShellResult RunVisibilityRequest(bool failTransport, string httpStatus)
    {
        var transport = WorkflowShell.ExtractBlock(Action, "if ! status=$(curl", "fi");
        var statusGuard = WorkflowShell.ExtractBlock(Action, "[[ \"$status\" == 200 ]]", "\n");
        var stub = "curl() { " + (failTransport ? "return 7;" : $"printf '{httpStatus}';") + " }\n";
        var script =
            "set -euo pipefail\n"
            + "fail() { echo \"$1\" >&2; exit 1; }\n"
            + stub
            + "CALLER_REPOSITORY=owner/repo\n"
            + "GH_TOKEN=token\n"
            + "GITHUB_API_URL=https://api.github.com\n"
            + "response_file=/dev/null\n"
            + transport
            + "\n"
            + statusGuard
            + "\nprintf 'ok'\n";
        return WorkflowShell.RunBash(script, Path.GetTempPath());
    }

    private static ShellResult RunVisibilityParsing(string responseBody)
    {
        using var repository = new TempRepository();
        repository.WriteFile("response.json", responseBody);
        var shim = JqShim.BashFunction(repository.WriteFile("jq.js", JqShim.JavaScript));
        var parsing = WorkflowShell.ExtractBlock(Action, "if ! repository_visibility=$(jq", "esac");
        var script =
            "set -euo pipefail\n"
            + "fail() { echo \"$1\" >&2; exit 1; }\n"
            + shim
            + "\nresponse_file=response.json\n"
            + parsing
            + "\nprintf 'accepted'\n";
        return WorkflowShell.RunBash(script, repository.Root);
    }

    private static string VisibilityJqFilter()
    {
        var block = WorkflowShell.ExtractBlock(Action, "if ! repository_visibility=$(jq", "esac");
        var match = Regex.Match(block, @"jq -er '(?<filter>[^']*)'", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "the visibility block has no jq -er filter.");
        return match.Groups["filter"].Value;
    }

    private static string DiscoveryScript()
    {
        var block = WorkflowShell.ExtractBlock(Action, "candidates=()", "esac");
        return $$"""
            set -euo pipefail
            fail() { echo "$1" >&2; exit 1; }
            {{block}}
            printf 'count=%s\n' "${#candidates[@]}"
            printf 'project_path=%s\n' "${project_path:-}"
            """;
    }

    private static string NodeProgram()
    {
        var match = Regex.Match(
            Action,
            "<<'NODE'\\r?\\n(?<program>.*?)\\r?\\n\\s*NODE",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(match.Success, "action.yml has no embedded Node config parser.");
        return match.Groups["program"].Value;
    }

    private static IEnumerable<string> MappingKeys(YamlMappingNode mapping) =>
        mapping.Children.Keys.Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);

    private static string Read(string fileName) => WorkflowShell.ReadWorkflow(fileName);

    private static YamlMappingNode Parse(string fileName) => YamlWorkflowReader.Parse(Read(fileName));

    private static YamlMappingNode Jobs(YamlMappingNode root) => YamlWorkflowReader.MappingChild(root, "jobs");

    private static YamlMappingNode Job(YamlMappingNode root, string name) =>
        YamlWorkflowReader.MappingChild(Jobs(root), name);

    private static YamlMappingNode BuildJob(YamlMappingNode root, string file) =>
        Job(root, file == ValidationFile ? "validate" : "build");

    private static YamlMappingNode StepById(YamlMappingNode job, string id) =>
        YamlWorkflowReader.MappingSequence(job, "steps").Single(step =>
            YamlWorkflowReader.HasChild(step, "id")
            && YamlWorkflowReader.ScalarChild(step, "id") == id);
}
