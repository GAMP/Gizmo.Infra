using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>Executable coverage for the private publisher's PackageBaseAddress discovery, origin validation, and fail-closed control flow.</summary>
public sealed class PrivatePublisherServiceIndexTests
{
    private const string Action = "package-private-publish";
    private const string PackageIdLower = "gizmo.widget";
    private const string VersionLower = "3.0.0-dev.7";

    private const string ServiceIndexUrl = "https://nuget.pkg.github.com/owner/index.json";
    private const string BaseAddress = "https://nuget.pkg.github.com/owner/download";
    private const string ExpectedIndexUrl = BaseAddress + "/" + PackageIdLower + "/index.json";
    private const string ExpectedDownloadUrl =
        BaseAddress + "/" + PackageIdLower + "/" + VersionLower + "/" + PackageIdLower + "." + VersionLower + ".nupkg";

    private const string ServiceIndex = """
        {
          "version": "3.0.0",
          "resources": [
            { "@id": "https://nuget.pkg.github.com/owner/index.json", "@type": "SearchQueryService" },
            { "@id": "https://nuget.pkg.github.com/owner/download", "@type": "PackageBaseAddress/3.0.0" },
            { "@id": "https://nuget.pkg.github.com/owner/index.json", "@type": "RegistrationsBaseUrl/3.6.0" }
          ]
        }
        """;

    private static readonly string ActionContent = WorkflowShell.ReadAction(Action);

    [Fact]
    public void Discovery_RequestsTheAuthenticatedServiceIndexAndDerivesUrlsFromTheDiscoveredBase()
    {
        using var repository = new TempRepository();
        var requestLog = repository.AbsolutePath("requests.log");
        var authLog = repository.AbsolutePath("auth.log");
        var responseFile = repository.AbsolutePath("response.json");

        var result = Run(repository, requestLog, authLog, responseFile, jqMode: "ok", baseAddress: BaseAddress);

        Assert.Equal(0, result.ExitCode);
        var printed = ParsePrinted(result.StandardOutput);
        Assert.Equal(BaseAddress, printed["base"]);
        Assert.Equal(ExpectedIndexUrl, printed["index"]);
        Assert.Equal(ExpectedDownloadUrl, printed["download"]);

        Assert.Equal(ServiceIndexUrl, File.ReadAllText(requestLog).Trim());
        Assert.Equal("actor:token", File.ReadAllText(authLog).Trim());
        Assert.Equal(ServiceIndex, File.ReadAllText(responseFile).Trim());
    }

    [Fact]
    public void Discovery_NormalizesATrailingSlashOnTheDiscoveredBase()
    {
        using var repository = new TempRepository();

        var result = Run(
            repository,
            repository.AbsolutePath("requests.log"),
            repository.AbsolutePath("auth.log"),
            repository.AbsolutePath("response.json"),
            jqMode: "ok",
            baseAddress: BaseAddress + "/");

        Assert.Equal(0, result.ExitCode);
        var printed = ParsePrinted(result.StandardOutput);
        Assert.Equal(BaseAddress, printed["base"]);
        Assert.Equal(ExpectedIndexUrl, printed["index"]);
        Assert.Equal(ExpectedDownloadUrl, printed["download"]);
    }

    [Fact]
    public void Discovery_FailsClosedWhenTheServiceIndexTransportFails()
    {
        var result = RunFailure(transportFail: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Could not read the GitHub Packages NuGet service index",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_FailsClosedOnAnUnexpectedServiceIndexStatus()
    {
        var result = RunFailure(httpStatus: "500");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "GitHub Packages returned HTTP 500 for the NuGet service index",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty")] // missing or ambiguous PackageBaseAddress resource
    [InlineData("error")] // malformed JSON or malformed service-index structure
    public void Discovery_FailsClosedOnMalformedMissingOrAmbiguousResources(string jqMode)
    {
        var result = RunFailure(jqMode: jqMode);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "malformed, missing, or ambiguous PackageBaseAddress resource",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://nuget.pkg.github.com/owner/download")]             // protocol is not https:
    [InlineData("https://evil.example.com/owner/download")]                // hostname is not the trusted origin
    [InlineData("https://nuget.pkg.github.com:443/owner/download")]        // explicit (default) port
    [InlineData("https://user:pass@nuget.pkg.github.com/owner/download")]  // embedded credentials
    [InlineData("https://nuget.pkg.github.com/owner/download?x=1")]        // query injection
    [InlineData("https://nuget.pkg.github.com/owner/download#frag")]       // fragment injection
    [InlineData("https://nuget.pkg.github.com/owner/download?")]           // empty query marker
    [InlineData("https://nuget.pkg.github.com/owner/download#")]           // empty fragment marker
    [InlineData("https://nuget.pkg.github.com/owner/../../evil")]          // path traversal
    [InlineData("https://nuget.pkg.github.com/owner/down load")]           // embedded whitespace
    [InlineData("not-a-url")]                                              // malformed / ambiguous
    public void Discovery_FailsClosedOnAnUntrustedDiscoveredBaseAddress(string baseAddress)
    {
        var result = RunFailure(baseAddress: baseAddress);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "malformed PackageBaseAddress @id",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_FailsClosedOnATerminalNewlineInTheDiscoveredBaseAddress()
    {
        var result = RunFailure(baseAddress: BaseAddress + "\n");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "malformed PackageBaseAddress @id",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BaseAddress)]
    [InlineData(BaseAddress + "/")]
    public void TrustedOriginValidator_AcceptsTheCanonicalPackageBaseAddress(string baseAddress)
    {
        var result = WorkflowShell.RunNode(TrustedOriginProgram(), baseAddress);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(baseAddress, result.StandardOutput);
    }

    [Theory]
    [InlineData("http://nuget.pkg.github.com/owner/download")]             // protocol is not https:
    [InlineData("https://evil.example.com/owner/download")]                // hostname is not the trusted origin
    [InlineData("https://nuget.pkg.github.com:443/owner/download")]        // explicit (default) port
    [InlineData("https://user:pass@nuget.pkg.github.com/owner/download")]  // embedded credentials
    [InlineData("https://nuget.pkg.github.com/owner/download?x=1")]        // query injection
    [InlineData("https://nuget.pkg.github.com/owner/download#frag")]       // fragment injection
    [InlineData("https://nuget.pkg.github.com/owner/download?")]           // WHATWG round-trips an empty query marker
    [InlineData("https://nuget.pkg.github.com/owner/download#")]           // WHATWG round-trips an empty fragment marker
    [InlineData("https://nuget.pkg.github.com/owner/../../evil")]          // path traversal
    [InlineData("https://nuget.pkg.github.com/owner/down load")]           // embedded whitespace
    [InlineData("not-a-url")]                                              // malformed / ambiguous
    public void TrustedOriginValidator_RejectsAnyUntrustedOrAmbiguousOrigin(string baseAddress)
    {
        var result = WorkflowShell.RunNode(TrustedOriginProgram(), baseAddress);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
    }

    private static ShellResult RunFailure(
        string jqMode = "ok",
        string? baseAddress = BaseAddress,
        string httpStatus = "200",
        bool transportFail = false)
    {
        using var repository = new TempRepository();
        return Run(
            repository,
            repository.AbsolutePath("requests.log"),
            repository.AbsolutePath("auth.log"),
            repository.AbsolutePath("response.json"),
            jqMode,
            baseAddress,
            httpStatus,
            transportFail);
    }

    private static ShellResult Run(
        TempRepository repository,
        string requestLog,
        string authLog,
        string responseFile,
        string jqMode = "ok",
        string? baseAddress = BaseAddress,
        string httpStatus = "200",
        bool transportFail = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STUB_RESPONSE_FILE"] = responseFile,
            ["STUB_REQUEST_LOG"] = requestLog,
            ["STUB_AUTH_LOG"] = authLog,
            ["STUB_JQ_MODE"] = jqMode,
            ["STUB_BASE_ADDRESS"] = baseAddress ?? string.Empty,
            ["STUB_HTTP_STATUS"] = httpStatus,
            ["STUB_TRANSPORT_FAIL"] = transportFail ? "1" : string.Empty,
            ["STUB_SERVICE_INDEX_FILE"] = repository.WriteFile("service-index.json", ServiceIndex),
        };

        return WorkflowShell.RunBash(DiscoveryScript(), repository.Root, environment);
    }

    private static IReadOnlyDictionary<string, string> ParsePrinted(string standardOutput)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        return values;
    }

    /// <summary>Extracts the action's embedded Node trusted-origin validator so the URL matrices exercise the committed parser.</summary>
    private static string TrustedOriginProgram()
    {
        var match = Regex.Match(
            ActionContent,
            @"node -e '(?<program>.*?)' ""\$package_base_address""",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "the private publisher has no embedded trusted-origin validator.");
        return match.Groups["program"].Value;
    }

    private static string DiscoveryScript()
    {
        var block = WorkflowShell.ExtractBlock(
            ActionContent,
            "service_index_url=\"https://nuget.pkg.github.com/$GITHUB_REPOSITORY_OWNER/index.json\"",
            "package_download_url=\"$package_base_address/${package_id_lower}/${version_lower}/${package_id_lower}.${version_lower}.nupkg\"");

        return $$"""
            set -euo pipefail
            GITHUB_ACTOR=actor
            GH_TOKEN=token
            GITHUB_REPOSITORY_OWNER=owner
            GITHUB_SHA={{new string('a', 40)}}
            EXPECTED_PACKAGE_ID=Gizmo.Widget
            PACKAGE_VERSION=3.0.0-dev.7
            package_id_lower=gizmo.widget
            version_lower=3.0.0-dev.7
            response_file="$STUB_RESPONSE_FILE"
            curl() {
              local arg output='' url='' auth=''
              while (( $# )); do
                arg=$1
                case "$arg" in
                  --output) output=$2; shift 2 ;;
                  --user) auth=$2; shift 2 ;;
                  --write-out) shift 2 ;;
                  --silent|--show-error|--location) shift ;;
                  *) url=$arg; shift ;;
                esac
              done
              printf '%s\n' "$url" >> "$STUB_REQUEST_LOG"
              printf '%s\n' "$auth" >> "$STUB_AUTH_LOG"
              [[ -n "${STUB_TRANSPORT_FAIL:-}" ]] && return 7
              [[ -n "$output" ]] && cp "$STUB_SERVICE_INDEX_FILE" "$output"
              printf '%s' "${STUB_HTTP_STATUS:-200}"
            }
            jq() {
              case "${STUB_JQ_MODE:-ok}" in
                ok) printf '%s\n' "${STUB_BASE_ADDRESS:-}"; return 0 ;;
                error) echo "jq: error: malformed service index" >&2; return 5 ;;
                empty) return 4 ;;
                *) echo "unexpected jq mode" >&2; return 5 ;;
              esac
            }
            {{block}}
            printf 'base=%s\n' "$package_base_address"
            printf 'index=%s\n' "$package_index_url"
            printf 'download=%s\n' "$package_download_url"
            """;
    }
}
