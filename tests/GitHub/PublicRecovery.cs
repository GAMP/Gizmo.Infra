using System.Globalization;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executable coverage for the public NuGet.org same-SHA recovery path. Each test
/// runs the committed precheck or publish/readback shell block with local stubs
/// for curl, jq, dotnet, unzip, and sleep, so the finite polling policy and the
/// fail-closed provenance decisions are proven without a feed or any real delay.
/// </summary>
public sealed class PublicPublisherRecoveryTests
{
    private const string Action = "public";
    private const string PublishStepName = "Publish exact package and verify provenance readback";
    private const string PackageId = "Gizmo.Widget";
    private const string PackageVersion = "1.0.14";

    private const string LocalArtifactBody = "local-prepared-nupkg-bytes";
    private const string ExpectedNuspecName = PackageId + ".nuspec";
    private const string ArtifactRelativePath = "artifacts/Gizmo.Widget.1.0.14.nupkg";

    private static readonly string CurrentSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);
    private static readonly string MatchingNuspec = Nuspec(CurrentSha);
    private static readonly string DivergentNuspec = Nuspec(OtherSha);
    private static readonly string MalformedNuspec = Nuspec(new string('z', 40));
    private static readonly string WrongIdNuspec = Nuspec(CurrentSha, id: "Other.Widget");
    private static readonly string WrongVersionNuspec = Nuspec(CurrentSha, version: "9.9.9");
    private static readonly string MissingProvenanceNuspec = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Gizmo.Widget</id>
            <version>1.0.14</version>
            <repository type="git" url="https://github.com/owner/repository" />
          </metadata>
        </package>
        """;

    private static string ActionContent => WorkflowShell.ReadAction(Action);

    [Fact]
    public void Precheck_ExactVersionWithSameShaProvenance_MarksPublishedAndSkipsPush()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: MatchingNuspec);

        Assert.True(outcome.Result.ExitCode == 0, outcome.Result.StandardError);
        Assert.Contains("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("package-state=unpublished", outcome.GithubOutput, StringComparison.Ordinal);

        // The publish step is gated on the unpublished state, so a matching collision never pushes.
        Assert.Contains(
            "if: ${{ steps.collision.outputs.package-state != 'published' }}",
            ActionContent,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_ExactVersionWithDifferentShaProvenance_FailsClosed()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: DivergentNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "no authenticated provenance for this caller SHA",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_SuccessThenMatchingReadback_Succeeds()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: MatchingNuspec);

        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Contains("--skip-duplicate", outcome.DotnetLog, StringComparison.Ordinal);
        Assert.Contains(
            "Verified published public package 1.0.14 provenance on readback attempt 1.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_DuplicateSkipThenEventualMatchingReadback_Succeeds()
    {
        using var repository = new TempRepository();

        // The precheck saw the version absent, the push skipped an existing package, and flat-container lag returned 404 before the package read.
        var outcome = RunPublish(repository, nupkgStatuses: "404 200", nuspec: MatchingNuspec);

        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Contains("--skip-duplicate", outcome.DotnetLog, StringComparison.Ordinal);
        Assert.Equal("2", outcome.SleepLog.Trim());
        Assert.Contains(
            "https://api.nuget.org/v3-flatcontainer/gizmo.widget/1.0.14/gizmo.widget.1.0.14.nupkg",
            outcome.CurlLog,
            StringComparison.Ordinal);
        Assert.Contains(
            "Verified published public package 1.0.14 provenance on readback attempt 2.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Push_DuplicateSkipThenDivergentProvenance_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: DivergentNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "divergent provenance for this caller SHA",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        // A readable but foreign package is terminal; it is never retried.
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_DuplicateSkipThenNeverReadable_FailsClosedAfterBoundedAttempts()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "404", nuspec: MatchingNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "Could not read back the published public package for 1.0.14 after 6 attempts; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);

        // Six attempts with two seconds between yield exactly five bounded sleeps.
        var delays = outcome.SleepLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, delays.Length);
        Assert.All(delays, delay => Assert.Equal("2", delay));

        // Publisher failure leaves the tag job unsatisfied, so no tag is created.
        AssertTagJobRequiresPublisherSuccess();
    }

    [Fact]
    public void Push_SuccessThenNeverReadable_FailsClosedAndLeavesTagJobUnsatisfied()
    {
        using var repository = new TempRepository();

        // A real (non-duplicate) push also must not report success without readback.
        var outcome = RunPublish(repository, nupkgStatuses: "404", nuspec: MatchingNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Equal(5, outcome.SleepLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        AssertTagJobRequiresPublisherSuccess();
    }

    [Fact]
    public void Push_NonDuplicateError_FailsBeforeAnyReadback()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: MatchingNuspec, dotnetExit: 2);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Empty(outcome.SleepLog.Trim());
        Assert.DoesNotContain(".nupkg", outcome.CurlLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_MissingOrMalformedProvenance_FailsClosed()
    {
        foreach (var nuspec in new[] { MissingProvenanceNuspec, MalformedNuspec })
        {
            using var repository = new TempRepository();

            var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: nuspec);

            Assert.NotEqual(0, outcome.Result.ExitCode);
            Assert.Contains(
                "missing or malformed provenance; failing closed",
                outcome.Result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(outcome.SleepLog.Trim());
        }
    }

    [Fact]
    public void Precheck_ForgedMatchingCommitButDifferentDigest_FailsClosed()
    {
        // A package-controlled nuspec may forge the exact caller commit, but the
        // downloaded bytes differ from the locally prepared artifact, so the digest
        // gate refuses to accept it or authorize a recovery tag.
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: MatchingNuspec,
            localBody: "locally-prepared-bytes",
            downloadedBody: "attacker-forged-nupkg-bytes");

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The existing public package version is not byte-identical to the exact prepared artifact; failing closed instead of publishing or creating a recovery tag.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_ForgedMatchingCommitButDifferentDigest_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            nupkgStatuses: "200",
            nuspec: MatchingNuspec,
            localBody: "locally-prepared-bytes",
            downloadedBody: "attacker-forged-nupkg-bytes");

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package is not byte-identical to the exact prepared artifact; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains("--skip-duplicate", outcome.DotnetLog, StringComparison.Ordinal);
        // A readable but digest-divergent package is terminal; it is never retried.
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Precheck_MultipleNuspecEntries_FailsClosed()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: MatchingNuspec,
            nuspecEntries: ExpectedNuspecName + "\ndecoy.nuspec");

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "does not contain exactly one nuspec",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_UnexpectedNuspecName_FailsClosed()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: MatchingNuspec,
            nuspecEntries: "decoy.nuspec");

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "nuspec is not the expected package nuspec",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_PackageIdMismatch_FailsClosed()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: WrongIdNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "nuspec ID does not match the expected package ID",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_PackageVersionMismatch_FailsClosed()
    {
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: WrongVersionNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "nuspec version does not match the expected package version",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Push_MultipleNuspecEntries_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            nupkgStatuses: "200",
            nuspec: MatchingNuspec,
            nuspecEntries: ExpectedNuspecName + "\ndecoy.nuspec");

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package does not contain exactly one nuspec; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_PackageIdMismatch_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: WrongIdNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec ID does not match the expected package ID; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_UnexpectedNuspecName_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            nupkgStatuses: "200",
            nuspec: MatchingNuspec,
            nuspecEntries: "decoy.nuspec");

        // A readable duplicate whose only nuspec is not the expected name is terminal; it is never retried.
        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec is not the expected package nuspec; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains("--skip-duplicate", outcome.DotnetLog, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Verified published public package",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
        Assert.Equal(1, ReadbackAttempts(outcome.CurlLog));
        AssertTagJobRequiresPublisherSuccess();
    }

    [Fact]
    public void Push_PackageVersionMismatch_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, nupkgStatuses: "200", nuspec: WrongVersionNuspec);

        // A readable duplicate with a mismatched declared version is terminal; it is never retried.
        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec version does not match the expected package version; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains("--skip-duplicate", outcome.DotnetLog, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Verified published public package",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
        Assert.Equal(1, ReadbackAttempts(outcome.CurlLog));
        AssertTagJobRequiresPublisherSuccess();
    }

    [Fact]
    public void CommittedPublishAndPrecheckSteps_AreValidBash()
    {
        var precheck = WorkflowShell.ExtractBlock(ActionContent, "package_id_lower=$(printf", "esac");
        var publish = RunStep(PublishStepName);

        Assert.Equal(0, WorkflowShell.CheckBashSyntax(precheck).ExitCode);
        Assert.Equal(0, WorkflowShell.CheckBashSyntax(publish).ExitCode);
    }

    private static int ReadbackAttempts(string curlLog) =>
        curlLog.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.EndsWith(".nupkg", StringComparison.Ordinal));

    private static void AssertTagJobRequiresPublisherSuccess()
    {
        var template = File.ReadAllText(Path.Combine(
            InfraRepositoryLocator.ResolveRoot(), ".github", "templates", "package.yml"));

        Assert.Contains("needs.publish-public.result == 'success'", template, StringComparison.Ordinal);
        Assert.Contains("needs.publish-private.result == 'success'", template, StringComparison.Ordinal);
    }

    private sealed record PublishOutcome(
        ShellResult Result,
        string GithubOutput,
        string SleepLog,
        string DotnetLog,
        string CurlLog);

    private sealed record PrecheckOutcome(ShellResult Result, string GithubOutput);

    private static PublishOutcome RunPublish(
        TempRepository repository,
        string nupkgStatuses,
        string nuspec,
        int dotnetExit = 0,
        string? localBody = null,
        string? downloadedBody = null,
        string? nuspecEntries = null)
    {
        var githubOutput = repository.WriteFile("github-output.txt", string.Empty);
        var sleepLog = repository.WriteFile("sleep.log", string.Empty);
        var dotnetLog = repository.WriteFile("dotnet.log", string.Empty);
        var curlLog = repository.WriteFile("curl.log", string.Empty);
        repository.WriteFile(ArtifactRelativePath, localBody ?? LocalArtifactBody);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EXPECTED_PACKAGE_ID"] = PackageId,
            ["PACKAGE_VERSION"] = PackageVersion,
            ["NUGET_USER"] = "test-nuget-user",
            ["PACKAGE_ARTIFACT"] = ArtifactRelativePath,
            ["GITHUB_SHA"] = CurrentSha,
            ["GITHUB_OUTPUT"] = githubOutput,
            ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "id-token-request-token",
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://pipelines.example/oidc?api-version=2.0",
            ["STUB_NUPKG_STATUSES"] = nupkgStatuses,
            ["STUB_NUSPEC"] = nuspec,
            ["STUB_NUPKG_BODY"] = downloadedBody ?? (localBody ?? LocalArtifactBody),
            ["STUB_NUSPEC_ENTRIES"] = nuspecEntries ?? ExpectedNuspecName,
            ["STUB_NUPKG_COUNTER"] = repository.AbsolutePath("nupkg-counter.txt"),
            ["STUB_SLEEP_LOG"] = sleepLog,
            ["STUB_DOTNET_LOG"] = dotnetLog,
            ["STUB_DOTNET_EXIT"] = dotnetExit.ToString(CultureInfo.InvariantCulture),
            ["STUB_CURL_LOG"] = curlLog,
        };

        var result = WorkflowShell.RunBash(PublishScript(), repository.Root, environment);
        return new PublishOutcome(
            result,
            File.ReadAllText(githubOutput),
            File.ReadAllText(sleepLog),
            File.ReadAllText(dotnetLog),
            File.ReadAllText(curlLog));
    }

    private static PrecheckOutcome RunPrecheck(
        TempRepository repository,
        string indexStatus,
        bool versionPresent,
        string nupkgStatus,
        string nuspec,
        string? localBody = null,
        string? downloadedBody = null,
        string? nuspecEntries = null)
    {
        var githubOutput = repository.WriteFile("github-output.txt", string.Empty);
        repository.WriteFile(ArtifactRelativePath, localBody ?? LocalArtifactBody);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EXPECTED_PACKAGE_ID"] = PackageId,
            ["PACKAGE_VERSION"] = PackageVersion,
            ["PACKAGE_ARTIFACT"] = ArtifactRelativePath,
            ["GITHUB_SHA"] = CurrentSha,
            ["GITHUB_OUTPUT"] = githubOutput,
            ["STUB_INDEX_STATUS"] = indexStatus,
            ["STUB_NUPKG_STATUS"] = nupkgStatus,
            ["STUB_NUSPEC"] = nuspec,
            ["STUB_NUPKG_BODY"] = downloadedBody ?? (localBody ?? LocalArtifactBody),
            ["STUB_NUSPEC_ENTRIES"] = nuspecEntries ?? ExpectedNuspecName,
            ["STUB_VERSION_PRESENT"] = versionPresent ? "1" : string.Empty,
            ["STUB_RESPONSE_FILE"] = repository.AbsolutePath("response.json"),
            ["STUB_PACKAGE_FILE"] = repository.AbsolutePath("package.nupkg"),
        };

        var result = WorkflowShell.RunBash(PrecheckScript(), repository.Root, environment);
        return new PrecheckOutcome(result, File.ReadAllText(githubOutput));
    }

    private static string RunStep(string name)
    {
        var runs = YamlWorkflowReader.MappingChild(YamlWorkflowReader.Parse(ActionContent), "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(candidate =>
            YamlWorkflowReader.ScalarChild(candidate, "name") == name);
        return YamlWorkflowReader.ScalarChild(step, "run");
    }

    private static string PublishScript()
    {
        var run = RunStep(PublishStepName);
        return $$"""
            set -euo pipefail
            curl() {
              local output='' url=''
              while (( $# )); do
                case "$1" in
                  --output) output=$2; shift 2 ;;
                  --write-out|--request|--data|--header) shift 2 ;;
                  --fail|--silent|--show-error|--location) shift ;;
                  *) url=$1; shift ;;
                esac
              done
              printf '%s\n' "$url" >> "$STUB_CURL_LOG"
              case "$url" in
                *audience=*) printf '%s' '{"value":"oidc-token"}'; return 0 ;;
                https://www.nuget.org/api/v2/token) printf '%s' '{"apiKey":"nuget-api-key"}'; return 0 ;;
                *.nupkg)
                  local count=0
                  if [[ -f "$STUB_NUPKG_COUNTER" ]]; then count=$(cat "$STUB_NUPKG_COUNTER"); fi
                  count=$(( count + 1 ))
                  printf '%s' "$count" > "$STUB_NUPKG_COUNTER"
                  local statuses=( $STUB_NUPKG_STATUSES )
                  local index=$(( count - 1 ))
                  if (( index >= ${#statuses[@]} )); then index=$(( ${#statuses[@]} - 1 )); fi
                  local code=${statuses[$index]}
                  if [[ "$code" == 000 ]]; then return 7; fi
                  [[ -n "$output" ]] && printf '%s' "${STUB_NUPKG_BODY:-nupkg-bytes}" > "$output"
                  printf '%s' "$code"
                  return 0
                  ;;
                *) printf '%s' '200'; return 0 ;;
              esac
            }
            jq() {
              local joined="$*"
              case "$joined" in
                *'.value'*) printf '%s' 'oidc-token' ;;
                *'.apiKey'*) printf '%s' 'nuget-api-key' ;;
                *) printf '%s' '{}' ;;
              esac
            }
            dotnet() {
              printf '%s\n' "$*" >> "$STUB_DOTNET_LOG"
              return "${STUB_DOTNET_EXIT:-0}"
            }
            unzip() {
              if [[ "$1" == "-Z1" ]]; then printf '%s\n' "${STUB_NUSPEC_ENTRIES:-Gizmo.Widget.nuspec}"; return 0; fi
              printf '%s' "${STUB_NUSPEC:-}"
            }
            sleep() { printf '%s\n' "$1" >> "$STUB_SLEEP_LOG"; }
            {{run}}
            """;
    }

    private static string PrecheckScript()
    {
        var block = WorkflowShell.ExtractBlock(ActionContent, "package_id_lower=$(printf", "esac");
        return $$"""
            set -euo pipefail
            response_file="$STUB_RESPONSE_FILE"
            package_file="$STUB_PACKAGE_FILE"
            curl() {
              local output='' url=''
              while (( $# )); do
                case "$1" in
                  --output) output=$2; shift 2 ;;
                  --write-out) shift 2 ;;
                  --silent|--show-error|--location) shift ;;
                  *) url=$1; shift ;;
                esac
              done
              case "$url" in
                *.nupkg)
                  [[ -n "$output" ]] && printf '%s' "${STUB_NUPKG_BODY:-nupkg-bytes}" > "$output"
                  printf '%s' "${STUB_NUPKG_STATUS:-200}"
                  ;;
                *index.json)
                  [[ -n "$output" ]] && printf '%s' '{"versions":[]}' > "$output"
                  printf '%s' "${STUB_INDEX_STATUS:-200}"
                  ;;
                *) printf '%s' '200' ;;
              esac
              return 0
            }
            jq() {
              local joined="$*"
              case "$joined" in
                *'index($version)'*) [[ -n "${STUB_VERSION_PRESENT:-}" ]] && return 0 || return 1 ;;
                *) return 0 ;;
              esac
            }
            unzip() {
              if [[ "$1" == "-Z1" ]]; then printf '%s\n' "${STUB_NUSPEC_ENTRIES:-Gizmo.Widget.nuspec}"; return 0; fi
              printf '%s' "${STUB_NUSPEC:-}"
            }
            {{block}}
            """;
    }

    private static string Nuspec(string commit, string id = PackageId, string version = PackageVersion) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{id}</id>
            <version>{version}</version>
            <repository type="git" url="https://github.com/owner/repository" commit="{commit}" />
          </metadata>
        </package>
        """;
}
