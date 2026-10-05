using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executable coverage for the public NuGet.org same-SHA recovery path; each test runs the committed precheck or publish shell block with local stubs for curl, jq, unzip, and sleep, so the structured duplicate discrimination, the finite polling policy, and the fail-closed provenance decisions are proven without a feed or real delay.
/// </summary>
public sealed class PublicPublisherRecoveryTests
{
    private const string Action = "public";
    private const string PublishStepName = "Publish exact package and reconcile duplicate provenance";
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

    // A nested decoy with the caller SHA must not authorize a repository that commits a different SHA.
    private static readonly string NestedDecoyNuspec = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{PackageId}</id>
            <version>{PackageVersion}</version>
            <description>decoy <repository commit="{CurrentSha}" /></description>
            <repository type="git" url="https://github.com/owner/repository" commit="{OtherSha}" />
          </metadata>
        </package>
        """;

    private static string ActionContent => WorkflowShell.ReadAction(Action);

    private static string PublicActionPath() =>
        Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "actions", "public")
            .Replace('\\', '/');

    private static string ProvenanceScriptPath() =>
        Path.Combine(
            InfraRepositoryLocator.ResolveRoot(),
            ".github",
            "actions",
            "public",
            "scripts",
            "nuspec_provenance.py");

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
    public void Precheck_DecoyNestedCommitWithDivergentRepository_FailsClosed()
    {
        // A structural parse must ignore the nested matching-SHA decoy and reject the real repository's divergent commit.
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: NestedDecoyNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "no authenticated provenance for this caller SHA",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_RepositorySignedDifferentBytesWithMatchingMetadata_IsAccepted()
    {
        // NuGet.org repository-signs the stored archive, so differing bytes still prove recovery by matching metadata.
        using var repository = new TempRepository();

        var outcome = RunPrecheck(
            repository,
            indexStatus: "200",
            versionPresent: true,
            nupkgStatus: "200",
            nuspec: MatchingNuspec,
            localBody: "prepared-artifact-bytes",
            downloadedBody: "repository-signed-stored-bytes");

        Assert.True(outcome.Result.ExitCode == 0, outcome.Result.StandardError);
        Assert.Contains("package-state=published", outcome.GithubOutput, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("201")]
    [InlineData("202")]
    public void Push_AcceptedStatus_SucceedsWithoutReadback(string pushStatus)
    {
        // A new package is accepted on the push's own structured status, so no propagation readback is incurred.
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, pushStatus, nupkgStatuses: "404", nuspec: MatchingNuspec);

        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Contains(
            "NuGet.org accepted public package 1.0.14.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(0, ReadbackAttempts(outcome.CurlLog));
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_DuplicateThenDelayedMatchingReadback_Succeeds()
    {
        using var repository = new TempRepository();

        // The push answers 409, and flat-container reads return 404 twice before the package is readable.
        var outcome = RunPublish(repository, "409", nupkgStatuses: "404 404 200", nuspec: MatchingNuspec);

        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Contains(
            "NuGet.org already has public package 1.0.14; reconciling the existing package provenance.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(3, ReadbackAttempts(outcome.CurlLog));
        Assert.Equal("10\n10", outcome.SleepLog.Trim());
        Assert.Contains(
            "Verified published public package 1.0.14 provenance on readback attempt 3.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Push_DuplicateRepositorySignedDifferentBytesWithMatchingMetadata_Succeeds()
    {
        // The stored duplicate is repository-signed and differs from the prepared artifact, but matching metadata still proves this caller SHA.
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            "409",
            nupkgStatuses: "200",
            nuspec: MatchingNuspec,
            localBody: "prepared-artifact-bytes",
            downloadedBody: "repository-signed-stored-bytes");

        Assert.Equal(0, outcome.Result.ExitCode);
        Assert.Contains(
            "Verified published public package 1.0.14 provenance on readback attempt 1.",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_DuplicateThenDivergentProvenance_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "409", nupkgStatuses: "200", nuspec: DivergentNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "divergent provenance for this caller SHA",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        // A readable but foreign package is terminal; it is never retried.
        Assert.Empty(outcome.SleepLog.Trim());
        Assert.Equal(1, ReadbackAttempts(outcome.CurlLog));
    }

    [Fact]
    public void Push_DuplicateThenNeverReadable_FailsClosedAfterBoundedAttempts()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "409", nupkgStatuses: "404", nuspec: MatchingNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "Could not read back the published public package for 1.0.14 after 13 attempts; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);

        // Thirteen attempts with ten seconds between yield exactly twelve bounded sleeps and a 120-second ceiling.
        var delays = outcome.SleepLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(12, delays.Length);
        Assert.All(delays, delay => Assert.Equal("10", delay));
        Assert.Equal(13, ReadbackAttempts(outcome.CurlLog));

        // Publisher failure leaves the tag job unsatisfied, so no tag is created.
        AssertTagJobRequiresPublisherSuccess();
    }

    [Theory]
    [InlineData("400")]
    [InlineData("403")]
    [InlineData("500")]
    public void Push_OtherStatus_FailsBeforeAnyReadback(string pushStatus)
    {
        // Only a duplicate 409 may continue to provenance reconciliation.
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, pushStatus, nupkgStatuses: "200", nuspec: MatchingNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            $"NuGet.org returned HTTP {pushStatus} for the push; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(0, ReadbackAttempts(outcome.CurlLog));
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_TransportFailure_FailsBeforeAnyReadback()
    {
        // A transport failure never yields a structured status, so it fails closed.
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "000", nupkgStatuses: "200", nuspec: MatchingNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "Could not reach the NuGet.org package-publish endpoint.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(0, ReadbackAttempts(outcome.CurlLog));
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_AuthenticatedPut_CarriesApiKeyAndProtocolVersionWithoutRedirect()
    {
        // The OIDC-derived key must travel with the NuGet protocol version, and the key-bearing PUT must never follow a redirect.
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "201", nupkgStatuses: "404", nuspec: MatchingNuspec);

        Assert.Equal(0, outcome.Result.ExitCode);
        var putRequest = outcome.RequestLog
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("method=PUT ", StringComparison.Ordinal));

        Assert.Contains("location=no", putRequest, StringComparison.Ordinal);
        Assert.Contains("url=https://www.nuget.org/api/v2/package", putRequest, StringComparison.Ordinal);
        Assert.Contains("X-NuGet-ApiKey: nuget-api-key", putRequest, StringComparison.Ordinal);
        Assert.Contains("X-NuGet-Protocol-Version: 4.1.0", putRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("X-NuGet-Client-Version", putRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_DuplicateMissingOrMalformedProvenance_FailsClosed()
    {
        foreach (var nuspec in new[] { MissingProvenanceNuspec, MalformedNuspec })
        {
            using var repository = new TempRepository();

            var outcome = RunPublish(repository, "409", nupkgStatuses: "200", nuspec: nuspec);

            Assert.NotEqual(0, outcome.Result.ExitCode);
            Assert.Contains(
                "missing or malformed provenance; failing closed",
                outcome.Result.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(outcome.SleepLog.Trim());
        }
    }

    [Fact]
    public void Push_DuplicateMultipleNuspecEntries_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            "409",
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
    public void Push_DuplicatePackageIdMismatch_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "409", nupkgStatuses: "200", nuspec: WrongIdNuspec);

        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec ID does not match the expected package ID; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
    }

    [Fact]
    public void Push_DuplicateUnexpectedNuspecName_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(
            repository,
            "409",
            nupkgStatuses: "200",
            nuspec: MatchingNuspec,
            nuspecEntries: "decoy.nuspec");

        // A readable duplicate whose only nuspec is not the expected name is terminal; it is never retried.
        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec is not the expected package nuspec; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Verified published public package",
            outcome.Result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(outcome.SleepLog.Trim());
        Assert.Equal(1, ReadbackAttempts(outcome.CurlLog));
        AssertTagJobRequiresPublisherSuccess();
    }

    [Fact]
    public void Push_DuplicatePackageVersionMismatch_FailsClosedImmediately()
    {
        using var repository = new TempRepository();

        var outcome = RunPublish(repository, "409", nupkgStatuses: "200", nuspec: WrongVersionNuspec);

        // A readable duplicate with a mismatched declared version is terminal; it is never retried.
        Assert.NotEqual(0, outcome.Result.ExitCode);
        Assert.Contains(
            "The published public package nuspec version does not match the expected package version; failing closed.",
            outcome.Result.StandardError,
            StringComparison.Ordinal);
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
        string CurlLog,
        string RequestLog);

    private sealed record PrecheckOutcome(ShellResult Result, string GithubOutput);

    private static PublishOutcome RunPublish(
        TempRepository repository,
        string pushStatus,
        string nupkgStatuses,
        string nuspec,
        string? localBody = null,
        string? downloadedBody = null,
        string? nuspecEntries = null)
    {
        var githubOutput = repository.WriteFile("github-output.txt", string.Empty);
        var sleepLog = repository.WriteFile("sleep.log", string.Empty);
        var curlLog = repository.WriteFile("curl.log", string.Empty);
        var requestLog = repository.WriteFile("request.log", string.Empty);
        repository.WriteFile(ArtifactRelativePath, localBody ?? LocalArtifactBody);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EXPECTED_PACKAGE_ID"] = PackageId,
            ["PACKAGE_VERSION"] = PackageVersion,
            ["NUGET_USER"] = "test-nuget-user",
            ["PACKAGE_ARTIFACT"] = ArtifactRelativePath,
            ["GITHUB_SHA"] = CurrentSha,
            ["GITHUB_OUTPUT"] = githubOutput,
            ["GITHUB_ACTION_PATH"] = PublicActionPath(),
            ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "id-token-request-token",
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://pipelines.example/oidc?api-version=2.0",
            ["STUB_PUSH_STATUS"] = pushStatus,
            ["STUB_NUPKG_STATUSES"] = nupkgStatuses,
            ["STUB_NUSPEC"] = nuspec,
            ["STUB_NUPKG_BODY"] = downloadedBody ?? (localBody ?? LocalArtifactBody),
            ["STUB_NUSPEC_ENTRIES"] = nuspecEntries ?? ExpectedNuspecName,
            ["STUB_NUPKG_COUNTER"] = repository.AbsolutePath("nupkg-counter.txt"),
            ["STUB_SLEEP_LOG"] = sleepLog,
            ["STUB_CURL_LOG"] = curlLog,
            ["STUB_REQUEST_LOG"] = requestLog,
        };

        var result = WorkflowShell.RunBash(PublishScript(), repository.Root, environment);
        return new PublishOutcome(
            result,
            File.ReadAllText(githubOutput),
            File.ReadAllText(sleepLog),
            File.ReadAllText(curlLog),
            File.ReadAllText(requestLog));
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
            ["GITHUB_ACTION_PATH"] = PublicActionPath(),
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
              local output='' url='' method='' location='no' headers=''
              while (( $# )); do
                case "$1" in
                  --output) output=$2; shift 2 ;;
                  --header) headers+="$2"$'\n'; shift 2 ;;
                  --request) method=$2; shift 2 ;;
                  --write-out|--data|--form|--connect-timeout|--max-time) shift 2 ;;
                  --location) location='yes'; shift ;;
                  --fail|--silent|--show-error) shift ;;
                  *) url=$1; shift ;;
                esac
              done
              printf '%s\n' "$url" >> "$STUB_CURL_LOG"
              printf 'method=%s location=%s url=%s headers=%s\n' "$method" "$location" "$url" "$(printf '%s' "$headers" | tr '\n' ';')" >> "$STUB_REQUEST_LOG"
              case "$url" in
                *audience=*) printf '%s' '{"value":"oidc-token"}'; return 0 ;;
                https://www.nuget.org/api/v2/token) printf '%s' '{"apiKey":"nuget-api-key"}'; return 0 ;;
                https://www.nuget.org/api/v2/package)
                  [[ -n "$output" ]] && printf '%s' "${STUB_PUSH_BODY:-}" > "$output"
                  if [[ "${STUB_PUSH_STATUS:-201}" == "000" ]]; then return 7; fi
                  printf '%s' "${STUB_PUSH_STATUS:-201}"
                  return 0
                  ;;
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
            unzip() {
              if [[ "$1" == "-Z1" ]]; then printf '%s\n' "${STUB_NUSPEC_ENTRIES:-Gizmo.Widget.nuspec}"; return 0; fi
              printf '%s' "${STUB_NUSPEC:-}"
            }
            {{WorkflowShell.PythonBashFunction()}}
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
                  --write-out|--connect-timeout|--max-time|--header) shift 2 ;;
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
            {{WorkflowShell.PythonBashFunction()}}
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
