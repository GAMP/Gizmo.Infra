using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class InternalPublisherOriginValidationTests
{
    private static string Script => Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions", "internal", "scripts", "validate.mjs");
    private static string Action => WorkflowShell.ReadAction("internal");
    private const string BaseAddress = "https://nuget.pkg.github.com/owner/download";
    private const string PackageVersion = "3.0.0-dev.7";

    [Theory]
    [InlineData("https://nuget.pkg.github.com/owner/download")]
    [InlineData("https://nuget.pkg.github.com/")]
    public void TrustedCanonicalHttpsOrigin_IsAccepted(string address)
    {
        var result = WorkflowShell.RunNodeScriptWithStdin(Script, address);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(address, result.StandardOutput);
    }

    [Theory]
    [InlineData("http://nuget.pkg.github.com/owner")]
    [InlineData("https://evil.example/owner")]
    [InlineData("https://nuget.pkg.github.com.evil.example/owner")]
    [InlineData("https://user@nuget.pkg.github.com/owner")]
    [InlineData("https://:password@nuget.pkg.github.com/owner")]
    [InlineData("https://user:password@nuget.pkg.github.com/owner")]
    [InlineData("https://nuget.pkg.github.com:443/owner")]
    [InlineData("https://nuget.pkg.github.com/owner?x=1")]
    [InlineData("https://nuget.pkg.github.com/owner#fragment")]
    [InlineData("https://nuget.pkg.github.com/../owner")]
    public void UntrustedOrRewrittenOrigin_IsRejected(string address)
    {
        var result = WorkflowShell.RunNodeScriptWithStdin(Script, address);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public void ServiceIndexDiscovery_ExecutesAuthenticatedLookupAndDerivesPackageUrls()
    {
        using var repository = new TempRepository();
        var result = RunDiscovery(repository, "ok", BaseAddress, "200", false);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("base=" + BaseAddress, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index=" + BaseAddress + "/gizmo.widget/index.json", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("download=" + BaseAddress + "/gizmo.widget/" + PackageVersion + "/gizmo.widget." + PackageVersion + ".nupkg", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("actor:mock-token", File.ReadAllText(repository.AbsolutePath("auth.log")), StringComparison.Ordinal);

        using var slashRepository = new TempRepository();
        var slash = RunDiscovery(slashRepository, "ok", BaseAddress + "/", "200", false);
        Assert.Equal(0, slash.ExitCode);
        Assert.Contains("base=" + BaseAddress, slash.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("index=" + BaseAddress + "/gizmo.widget/index.json", slash.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty", "200", false)]
    [InlineData("error", "200", false)]
    [InlineData("ambiguous", "200", false)]
    [InlineData("ok", "500", false)]
    [InlineData("ok", "200", true)]
    public void ServiceIndexDiscovery_FailsClosedOnMissingMalformedAmbiguousStatusOrTransport(string jqMode, string status, bool transportFailure)
    {
        using var repository = new TempRepository();
        var result = RunDiscovery(repository, jqMode, BaseAddress, status, transportFailure);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("service index", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceIndexDiscovery_RejectsUntrustedBaseAndValidatesItBeforeDerivingRequests()
    {
        using var repository = new TempRepository();
        var candidate = "https://evil.example/owner/download";
        var result = RunDiscovery(repository, "ok", candidate, "200", false);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("malformed PackageBaseAddress @id", result.StandardError, StringComparison.Ordinal);

        var validation = Action.IndexOf("node \"$VALIDATE_ORIGIN\"", StringComparison.Ordinal);
        var indexUrl = Action.IndexOf("package_index_url=", validation, StringComparison.Ordinal);
        var downloadUrl = Action.IndexOf("package_download_url=", indexUrl, StringComparison.Ordinal);
        var indexRequest = Action.IndexOf("\"$package_index_url\"", downloadUrl, StringComparison.Ordinal);
        var downloadRequest = Action.IndexOf("\"$package_download_url\"", indexRequest, StringComparison.Ordinal);
        Assert.True(validation >= 0 && indexUrl > validation && downloadUrl > indexUrl && indexRequest > downloadUrl && downloadRequest > indexRequest);
        Assert.Contains("--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_index_url\"", Action, StringComparison.Ordinal);
        Assert.Contains("--user \"$GITHUB_ACTOR:$GH_TOKEN\" \"$package_download_url\"", Action, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceIndexDiscovery_KeepsUntrustedCandidateOffNodeArgumentsAndRejectsNewline()
    {
        const string marker = "untrusted-base-sentinel";
        var candidate = "https://evil.example/" + marker;
        using var repository = new TempRepository();
        var rejected = RunDiscovery(repository, "ok", candidate, "200", false);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("malformed PackageBaseAddress @id", rejected.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, File.ReadAllText(repository.AbsolutePath("node-argv.log")), StringComparison.Ordinal);
        Assert.Contains(candidate, File.ReadAllText(repository.AbsolutePath("node-stdin.log")), StringComparison.Ordinal);

        using var newline = new TempRepository();
        var trailingNewline = RunDiscovery(newline, "ok", BaseAddress + "\n", "200", false);
        Assert.NotEqual(0, trailingNewline.ExitCode);
        Assert.Contains("malformed PackageBaseAddress @id", trailingNewline.StandardError, StringComparison.Ordinal);
    }

    private static ShellResult RunDiscovery(TempRepository repository, string jqMode, string baseAddress, string status, bool transportFailure)
    {
        const string start = "service_index_url=\"https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/index.json\"";
        const string end = "package_download_url=\"$package_base_address/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg\"";
        var block = WorkflowShell.ExtractBlock(Action, start, end);
        var serviceIndex = repository.WriteFile("service-index.json", "{\"resources\":[]}");
        var script = $$"""
            set -euo pipefail
            GITHUB_ACTOR=actor
            GH_TOKEN=mock-token
            GITHUB_REPOSITORY_OWNER=owner
            GIZMO_PACKAGE_ID=Gizmo.Widget
            GIZMO_PACKAGE_VERSION={{PackageVersion}}
            package_id_lower=gizmo.widget
            version_lower={{PackageVersion}}
            response_file="$STUB_RESPONSE_FILE"
            curl() {
              local output=''
              while (( $# )); do case "$1" in --output) output=$2; shift 2 ;; --write-out|--user|--header) shift 2 ;; --silent|--show-error|--location) shift ;; *) shift ;; esac; done
              printf 'https://nuget.pkg.github.com/owner/index.json\n' >> "$STUB_REQUEST_LOG"
              printf 'actor:mock-token\n' >> "$STUB_AUTH_LOG"
              [[ -z "$STUB_TRANSPORT_FAIL" ]] || return 7
              cp "$STUB_SERVICE_INDEX" "$output"
              printf '%s' "$STUB_STATUS"
            }
            jq() {
              case "$STUB_JQ_MODE" in
                ok) printf '%s\n' "$STUB_BASE_ADDRESS" ;;
                empty|ambiguous) return 4 ;;
                error) echo 'malformed service index' >&2; return 5 ;;
              esac
            }
            node() { printf '%s\n' "$@" >> "$STUB_NODE_ARGV"; tee "$STUB_NODE_STDIN" | command node "$@"; }
            {{block}}
            printf 'base=%s\nindex=%s\ndownload=%s\n' "$package_base_address" "$package_index_url" "$package_download_url"
            """;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STUB_RESPONSE_FILE"] = repository.AbsolutePath("response.json"),
            ["STUB_REQUEST_LOG"] = repository.AbsolutePath("request.log"),
            ["STUB_AUTH_LOG"] = repository.AbsolutePath("auth.log"),
            ["STUB_SERVICE_INDEX"] = serviceIndex,
            ["STUB_JQ_MODE"] = jqMode,
            ["STUB_BASE_ADDRESS"] = baseAddress,
            ["STUB_STATUS"] = status,
            ["STUB_TRANSPORT_FAIL"] = transportFailure ? "true" : string.Empty,
            ["STUB_NODE_STDIN"] = repository.AbsolutePath("node-stdin.log"),
            ["STUB_NODE_ARGV"] = repository.AbsolutePath("node-argv.log"),
            ["VALIDATE_ORIGIN"] = Script.Replace('\\', '/'),
        };
        return WorkflowShell.RunBash(script, repository.Root, environment);
    }
}
