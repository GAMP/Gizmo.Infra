using System.Globalization;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class PublishVersionStateExecutionTests
{
    private const string PackageId = "Gizmo.Widget";
    private static readonly string CurrentSha = new('1', 40);
    private static readonly string OtherSha = new('2', 40);

    private const string ApiStubs = """
        jq() {
          local filter='' arg input_file='' active_page value_name
          for arg in "$@"; do
            case "$arg" in
              -*) ;;
              *) if [[ -z "$filter" ]]; then filter="$arg"; fi ;;
            esac
            [[ -f "$arg" ]] && input_file=$arg
          done
          active_page=$(<"$input_file")
          case "$filter" in
            'length')
              value_name="STUB_PAGE_${active_page}_COUNT"
              printf '%s\n' "${!value_name}"
              ;;
            '.[] | [.ref, .object.type, .object.sha] | @tsv')
              value_name="STUB_PAGE_${active_page}_ROWS"
              printf '%s' "${!value_name}"
              ;;
            'type == "array"'*) printf 'true\n' ;;
            '.object.type | strings') printf 'commit\n' ;;
            '.object.sha | strings') printf '%s\n' "${STUB_OBJECT_SHA:-}" ;;
            *) echo "unexpected jq filter: $filter" >&2; return 5 ;;
          esac
        }
        curl() {
          local output='' page=1
          while (( $# )); do
            case "$1" in
              --output) output=$2; shift 2 ;;
              --write-out|--header|--user|--data|--request|--connect-timeout|--max-time) shift 2 ;;
              *)
                case "$1" in *page=*) page=${1##*page=} ;; esac
                shift
                ;;
            esac
          done
          printf '%s\n' "$page" >> "$STUB_PAGE_LOG"
          [[ -n "$output" ]] && printf '%s' "$page" > "$output"
          printf '200'
        }
        """;

    [Fact]
    public void Development_BootstrapsAndAdvancesTheActiveLineMaximum()
    {
        var bootstrap = Run("development", []);
        var maximum = Run("development", [Tag("3.0.8", OtherSha), Tag("3.0.99", OtherSha), Tag("2.99.999", OtherSha)]);

        AssertVersion(bootstrap, "3.0.0", "3.0.0-dev.7", "not-applicable");
        AssertVersion(maximum, "3.0.100", "3.0.100-dev.7", "not-applicable");
    }

    [Fact]
    public void Production_BootstrapsWithoutSyntheticTagAndClaimsNextPatch()
    {
        var bootstrap = Run("production", []);
        var next = Run("production", [Tag("3.0.99", OtherSha)]);

        AssertVersion(bootstrap, "3.0.0", "3.0.0", "missing");
        Assert.Contains(";current-sha-tags=;", bootstrap.Outputs["calculated-state"], StringComparison.Ordinal);
        AssertVersion(next, "3.0.100", "3.0.100", "missing");
        Assert.Equal($"{PackageId}/v3.0.100", next.Outputs["release-tag"]);
    }

    [Fact]
    public void ProductionRerun_ReusesTheExactCurrentShaVersionAndFingerprint()
    {
        var tag = Tag("3.0.7", CurrentSha);
        var first = Run("production", [tag], githubSha: CurrentSha);
        var retry = Run("production", [tag], githubSha: CurrentSha);

        AssertVersion(first, "3.0.7", "3.0.7", "present");
        Assert.Equal($"{PackageId}/v3.0.7", first.Outputs["release-tag"]);
        Assert.Contains(";current-sha-tags=v3.0.7;", first.Outputs["calculated-state"], StringComparison.Ordinal);
        Assert.Equal(first.Outputs["tag-state-fingerprint"], retry.Outputs["tag-state-fingerprint"]);
    }

    [Fact]
    public void ProductionRerun_FailsClosedOnMultipleCurrentShaTags()
    {
        var run = Run("production", [Tag("3.0.7", CurrentSha), Tag("3.0.8", CurrentSha)], githubSha: CurrentSha);

        AssertFailure(run, "Multiple package/compatibility-line tags point to the caller commit; refusing ambiguous release rerun.");
    }

    [Fact]
    public void TagResolution_FailsClosedOnForeignOrMalformedPackageRefs()
    {
        var foreign = Run("production", [("refs/tags/Other.Package/v3.0.0", OtherSha)]);
        var malformed = Run("production", [("refs/tags/Gizmo.Widget/v3.0", OtherSha)]);

        AssertFailure(foreign, "GitHub returned a tag outside the requested package prefix.");
        AssertFailure(malformed, "Malformed tag under the exact package prefix:");
    }

    [Fact]
    public void GovernedTransitions_AcceptSameNextMinorAndNextMajorLines()
    {
        var same = Run("development", [Tag("3.0.7", OtherSha)], compatibilityLine: "3.0");
        var minor = Run("development", [Tag("3.0.7", OtherSha)], compatibilityLine: "3.1");
        var major = Run("development", [Tag("3.9.7", OtherSha)], compatibilityLine: "4.0");

        AssertSucceeded(same);
        Assert.Equal("3.0.8", same.Outputs["base-version"]);
        AssertSucceeded(minor);
        Assert.Equal("3.1.0", minor.Outputs["base-version"]);
        AssertSucceeded(major);
        Assert.Equal("4.0.0", major.Outputs["base-version"]);
    }

    [Fact]
    public void GovernedTransitions_RejectSkippedBackwardAndInvalidMajorReset()
    {
        var skipped = Run("development", [Tag("3.0.7", OtherSha)], compatibilityLine: "3.2");
        var backward = Run("development", [Tag("3.2.7", OtherSha)], compatibilityLine: "3.1");
        var badReset = Run("development", [Tag("3.9.7", OtherSha)], compatibilityLine: "4.1");

        foreach (var run in new[] { skipped, backward, badReset })
        {
            AssertFailure(run, "Declared compatibility line");
        }
    }

    [Fact]
    public void Production_CarriesTheObservedTagSnapshotForRechecks()
    {
        var run = Run("production", [Tag("3.0.4", OtherSha)]);
        AssertSucceeded(run);

        var snapshot = Convert.FromBase64String(CalculatedTagState(run.Outputs));
        Assert.Equal($"refs/tags/{PackageId}/v3.0.4={OtherSha}", System.Text.Encoding.UTF8.GetString(snapshot));
        Assert.Contains("compatibility-line=3.0;", run.Outputs["calculated-state"], StringComparison.Ordinal);
        Assert.Contains("base-version=3.0.5;", run.Outputs["calculated-state"], StringComparison.Ordinal);
        Assert.Contains("package-version=3.0.5;", run.Outputs["calculated-state"], StringComparison.Ordinal);
    }

    [Fact]
    public void TagDiscovery_PaginatesTwoFullPagesAndStopsAfterTheShortFinalPage()
    {
        IReadOnlyList<(string Ref, string Sha)>[] pages =
        [
            Enumerable.Range(0, 100).Select(patch => Tag($"3.0.{patch}", OtherSha)).ToArray(),
            [Tag("3.0.250", OtherSha)],
        ];

        var run = Run("production", pages[0], tagPages: pages);

        AssertVersion(run, "3.0.251", "3.0.251", "missing");
        Assert.Equal(new[] { "1", "2" }, run.RequestedPages);
    }

    private sealed record VersionStateRun(
        ShellResult Result,
        IReadOnlyDictionary<string, string> Outputs,
        IReadOnlyList<string> RequestedPages);

    private static (string Ref, string Sha) Tag(string version, string sha) =>
        ($"refs/tags/{PackageId}/v{version}", sha);

    private static VersionStateRun Run(
        string branchRole,
        IReadOnlyList<(string Ref, string Sha)> tags,
        string compatibilityLine = "3.0",
        string? githubSha = null,
        IReadOnlyList<(string Ref, string Sha)>[]? tagPages = null)
    {
        using var repository = new TempRepository();
        var outputPath = repository.AbsolutePath("github_output");
        var pageLogPath = repository.AbsolutePath("pages.log");
        tagPages ??= [tags];
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PACKAGE_ID"] = PackageId,
            ["COMPATIBILITY_LINE"] = compatibilityLine,
            ["BRANCH_ROLE"] = branchRole,
            ["GH_TOKEN"] = "contract-test-token",
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_SHA"] = githubSha ?? CurrentSha,
            ["GITHUB_RUN_NUMBER"] = "7",
            ["GITHUB_OUTPUT"] = outputPath,
            ["STUB_PAGE_LOG"] = pageLogPath,
        };
        for (var index = 0; index < 3; index++)
        {
            var page = index < tagPages.Length ? tagPages[index] : [];
            var pageNumber = index + 1;
            environment[$"STUB_PAGE_{pageNumber}_COUNT"] = page.Count.ToString(CultureInfo.InvariantCulture);
            environment[$"STUB_PAGE_{pageNumber}_ROWS"] = string.Concat(page.Select(tag => $"{tag.Ref}\tcommit\t{tag.Sha}\n"));
        }

        var script = VersionStateScript();
        var result = WorkflowShell.RunBash(ApiStubs + "\n" + script + "\n", repository.Root, environment);
        var outputs = result.ExitCode == 0 && File.Exists(outputPath)
            ? ParseOutputs(File.ReadAllLines(outputPath))
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var requestedPages = File.Exists(pageLogPath) ? File.ReadAllLines(pageLogPath) : [];
        return new VersionStateRun(result, outputs, requestedPages);
    }

    private static void AssertVersion(VersionStateRun run, string baseVersion, string packageVersion, string tagState)
    {
        AssertSucceeded(run);
        Assert.Equal(baseVersion, run.Outputs["base-version"]);
        Assert.Equal(packageVersion, run.Outputs["package-version"]);
        Assert.Equal(tagState, run.Outputs["release-tag-state"]);
    }

    private static void AssertSucceeded(VersionStateRun run) =>
        Assert.True(run.Result.ExitCode == 0, $"version-state failed with exit {run.Result.ExitCode}: {run.Result.StandardError}");

    private static void AssertFailure(VersionStateRun run, string message)
    {
        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(message, run.Result.StandardError, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> ParseOutputs(IEnumerable<string> lines)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                outputs[line[..separator]] = line[(separator + 1)..];
            }
        }
        return outputs;
    }

    private static string CalculatedTagState(IReadOnlyDictionary<string, string> outputs)
    {
        const string marker = ";tag-state=";
        var state = outputs["calculated-state"];
        var index = state.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "calculated-state must carry a tag-state segment.");
        return state[(index + marker.Length)..];
    }

    private static string VersionStateScript()
    {
        var root = YamlWorkflowReader.Parse(WorkflowShell.ReadWorkflow("package-publish.yml"));
        var build = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), "build");
        var step = YamlWorkflowReader.MappingSequence(build, "steps").Single(step =>
            YamlWorkflowReader.HasChild(step, "id") && YamlWorkflowReader.ScalarChild(step, "id") == "version-state");
        return YamlWorkflowReader.ScalarChild(step, "run");
    }
}
