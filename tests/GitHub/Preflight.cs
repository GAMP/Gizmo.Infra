using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class PackagePreflightActionTests
{
    private static string Action => WorkflowShell.ReadAction("preflight");

    [Fact]
    public void Preflight_DeclaresOnlyDiscoveryInputsAndDoesNotSelectPublisher()
    {
        var root = YamlWorkflowReader.Parse(Action);
        Assert.Equal(new[] { "caller-repository", "current-ref", "dev", "github-token", "prod" },
            Keys(YamlWorkflowReader.MappingChild(root, "inputs")).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal(new[] { "branch-role", "compatibility-line", "package-id", "project-path", "repository-visibility" },
            Keys(YamlWorkflowReader.MappingChild(root, "outputs")).OrderBy(value => value, StringComparer.Ordinal));
        foreach (var forbidden in new[] { "publisher", "ACTIONS_ID_TOKEN", "api.nuget.org", "nuget.pkg.github.com", "packages: write", "dotnet nuget push" })
            Assert.DoesNotContain(forbidden, Action, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchRole_ResolvesDevProductionAndCheapNoOp()
    {
        var block = WorkflowShell.ExtractBlock(Action, "branch_role=none", "fi");
        foreach (var (reference, expected) in new[]
                 {
                     ("refs/heads/pre-release", "development"),
                     ("refs/heads/release", "production"),
                     ("refs/heads/topic", "none"),
                     ("refs/tags/v3.0.0", "none"),
                 })
        {
            var script = "set -euo pipefail\nDEV_BRANCH=pre-release\nPROD_BRANCH=release\nCURRENT_REF='" + reference + "'\n" + block + "\nprintf '%s' \"$branch_role\"\n";
            var result = WorkflowShell.RunBash(script, Path.GetTempPath());
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, result.StandardOutput);
        }
        Assert.Contains("if [[ \"$branch_role\" != none ]]", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchInputs_RejectMissingEqualAndMalformedNames()
    {
        Assert.Contains("git check-ref-format --branch \"$DEV_BRANCH\"", Action, StringComparison.Ordinal);
        Assert.Contains("git check-ref-format --branch \"$PROD_BRANCH\"", Action, StringComparison.Ordinal);
        foreach (var (dev, prod, expected) in new[]
                 {
                     ("", "release", "both required"),
                     ("release", "release", "must differ"),
                     ("bad..branch", "release", "valid branch name"),
                 })
        {
            var script = "set -euo pipefail\nfail() { echo \"$1\" >&2; exit 1; }\n"
                + "DEV_BRANCH='" + dev + "'\nPROD_BRANCH='" + prod + "'\n"
                + WorkflowShell.ExtractBlock(Action, "[[ -n \"$DEV_BRANCH\"", "git check-ref-format --branch \"$PROD_BRANCH\"");
            var result = WorkflowShell.RunBash(script, Path.GetTempPath());
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expected, result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Preflight_ValidatesProjectMetadataAndAuthenticatedVisibilityFailClosed()
    {
        Assert.Contains("find . -path ./.git -prune -o -path ./.gizmo-infra -prune", Action, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one SDK-style packable .csproj", Action, StringComparison.Ordinal);
        Assert.Contains("compatibility_line=", Action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:PackageId", Action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:Version", Action, StringComparison.Ordinal);
        Assert.Contains("PackageId has an invalid format", Action, StringComparison.Ordinal);
        Assert.Contains("Version must be exactly <major>.<minor>", Action, StringComparison.Ordinal);
        Assert.Contains("GITHUB_API_URL/repos/$CALLER_REPOSITORY", Action, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $GH_TOKEN", Action, StringComparison.Ordinal);
        Assert.Contains("public|private|internal", Action, StringComparison.Ordinal);
        Assert.Contains("unsupported caller repository visibility", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_ExecutesNoMultiplePrunedAndNewlineCandidateCases()
    {
        using (var repository = new TempRepository())
        {
            repository.WriteFile("Widget.csproj", "sdk");
            repository.WriteFile(".git/objects/Hidden.csproj", "sdk");
            repository.WriteFile(".gizmo-infra/Hidden.csproj", "sdk");
            repository.WriteFile("Samples-not-sdk.csproj", "not-sdk");
            var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("count=1", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("project_path=Widget.csproj", result.StandardOutput, StringComparison.Ordinal);
        }

        using (var repository = new TempRepository())
        {
            repository.WriteFile("Samples-not-sdk.csproj", "not-sdk");
            var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("found none", result.StandardError, StringComparison.Ordinal);
        }

        using (var repository = new TempRepository())
        {
            repository.WriteFile("Alpha.csproj", "sdk");
            repository.WriteFile("Beta.csproj", "sdk");
            var result = WorkflowShell.RunBash(DiscoveryScript(), repository.Root);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("found multiple candidates", result.StandardError, StringComparison.Ordinal);
        }

        using (var repository = new TempRepository())
        {
            var result = WorkflowShell.RunBash(DiscoveryScript(newlineCandidate: true), repository.Root);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("contains a newline", result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VisibilityRequestAndParsing_ExecuteTransportStatusAndMetadataFailures()
    {
        foreach (var (transportFail, status, expected) in new[]
                 {
                     (true, "200", "Could not fetch authenticated caller repository metadata."),
                     (false, "500", "GitHub returned HTTP 500"),
                     (false, "404", "GitHub returned HTTP 404"),
                 })
        {
            var result = RunVisibilityRequest(transportFail, status);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expected, result.StandardError, StringComparison.Ordinal);
        }
        Assert.Equal(0, RunVisibilityRequest(transportFail: false, status: "200").ExitCode);

        foreach (var visibility in new[] { "public", "private", "internal" })
        {
            Assert.Equal(0, RunVisibilityParsing($"{{\"visibility\":\"{visibility}\"}}").ExitCode);
        }
        foreach (var visibility in new[] { "gist", "PUBLIC" })
        {
            var result = RunVisibilityParsing($"{{\"visibility\":\"{visibility}\"}}");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("unsupported caller repository visibility", result.StandardError, StringComparison.Ordinal);
        }
        foreach (var body in new[] { "{}", "{\"visibility\":12}", "[]", "not json" })
        {
            var result = RunVisibilityParsing(body);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("malformed caller repository metadata", result.StandardError, StringComparison.Ordinal);
            Assert.Empty(result.StandardOutput);
        }
    }

    private static IEnumerable<string> Keys(YamlMappingNode mapping) => mapping.Children.Keys
        .Select(key => Assert.IsType<YamlScalarNode>(key).Value ?? string.Empty);

    private static string DiscoveryScript(bool newlineCandidate = false)
    {
        var block = WorkflowShell.ExtractBlock(Action, "candidates=()", "esac");
        var findStub = newlineCandidate ? "find() { printf './bad\\nname.csproj\\0'; }\n" : string.Empty;
        return $$"""
            set -euo pipefail
            fail() { echo "$1" >&2; exit 1; }
            {{findStub}}
            dotnet() {
              case "$*" in
                *-getProperty:UsingMicrosoftNETSdk*) [[ "$*" == *not-sdk* ]] && printf false || printf true ;;
                *-getProperty:IsPackable*) printf true ;;
                *) echo "unexpected dotnet arguments: $*" >&2; return 7 ;;
              esac
            }
            {{block}}
            printf 'count=%s\nproject_path=%s\n' "${#candidates[@]}" "${project_path:-}"
            """;
    }

    private static ShellResult RunVisibilityRequest(bool transportFail, string status)
    {
        var request = WorkflowShell.ExtractBlock(Action, "if ! status=$(curl", "fi");
        var statusGuard = WorkflowShell.ExtractBlock(Action, "[[ \"$status\" == 200 ]]", "\n");
        var curl = transportFail
            ? "curl() { return 7; }"
            : "curl() { local output=''; while (( $# )); do case \"$1\" in --output) output=$2; shift 2 ;; *) shift ;; esac; done; printf '{\"visibility\":\"public\"}' > \"$output\"; printf '%s' \"$STUB_STATUS\"; }";
        var script = "set -euo pipefail\nfail() { echo \"$1\" >&2; exit 1; }\n" + curl + "\n"
            + "CALLER_REPOSITORY=owner/repo\nGH_TOKEN=mock\nGITHUB_API_URL=https://api.github.com\nresponse_file=\"$STUB_RESPONSE\"\n"
            + request + "\n" + statusGuard + "\n";
        using var repository = new TempRepository();
        var environment = new Dictionary<string, string> { ["STUB_STATUS"] = status, ["STUB_RESPONSE"] = repository.AbsolutePath("response.json") };
        return WorkflowShell.RunBash(script, repository.Root, environment);
    }

    private static ShellResult RunVisibilityParsing(string body)
    {
        using var repository = new TempRepository();
        repository.WriteFile("response.json", body);
        var jq = JqShim.BashFunction(repository.WriteFile("jq.js", JqShim.JavaScript));
        var block = WorkflowShell.ExtractBlock(Action, "if ! repository_visibility=$(jq", "esac");
        var script = "set -euo pipefail\nfail() { echo \"$1\" >&2; exit 1; }\n" + jq + "\nresponse_file=response.json\n" + block;
        return WorkflowShell.RunBash(script, repository.Root);
    }
}
