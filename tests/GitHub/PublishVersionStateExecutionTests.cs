using System.Globalization;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executes the committed <c>version-state</c> bash block from
/// <c>package-publish.yml</c> against a stubbed GitHub tag API, so the version
/// and release-tag rules are verified by behavior instead of by matching source
/// text. Development and a new release share one derivation: bootstrap patch 0
/// when the compatibility line has no stable tag, otherwise numeric
/// <c>max(Y)+1</c>. Development appends <c>-dev.N</c> without creating a tag,
/// while release reuses the exact version of a current-SHA tag or claims the
/// next stable version.
/// </summary>
public sealed class PublishVersionStateExecutionTests
{
    private const string PublishFile = "package-publish.yml";
    private const string PackageId = "Gizmo.Widget";

    private static readonly string CurrentSha = new('1', 40);
    private static readonly string OtherSha = new('2', 40);
    private static readonly string ThirdSha = new('3', 40);

    // The version block shells out to curl and jq; neither is part of the local
    // contract suite, so both are replaced by deterministic shims that feed the
    // tag rows under test. An unexpected jq filter text fails the run loudly
    // rather than silently serving a stale response shape.
    private const string ApiStubs = """
        jq() {
          local filter='' arg
          for arg in "$@"; do
            case "$arg" in
              -*) ;;
              *) if [[ -z "$filter" ]]; then filter="$arg"; fi ;;
            esac
          done
          case "$filter" in
            'length') printf '%s\n' "$STUB_TAG_COUNT" ;;
            '.[] | [.ref, .object.type, .object.sha] | @tsv') printf '%s' "$STUB_TAG_ROWS" ;;
            'type == "array"'*) printf 'true\n' ;;
            '.object.type | strings') printf 'commit\n' ;;
            '.object.sha | strings') printf '%s\n' "${STUB_OBJECT_SHA:-}" ;;
            *) echo "unexpected jq filter: $filter" >&2; return 5 ;;
          esac
        }
        curl() {
          local output=''
          while (( $# )); do
            case "$1" in
              --output) output=$2; shift 2 ;;
              --write-out|--header|--user|--data|--request|--connect-timeout|--max-time) shift 2 ;;
              *) shift ;;
            esac
          done
          [[ -n "$output" ]] && : > "$output"
          printf '200'
        }
        """;

    [Fact]
    public void Development_WithNoStableTags_BootstrapsAtPatchZeroOfTheCompatibilityLine()
    {
        var run = RunVersionState("development", runNumber: "7", tags: []);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0-dev.7", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.0", run.Outputs["release-tag"]);
        Assert.Equal("not-applicable", run.Outputs["release-tag-state"]);
    }

    [Fact]
    public void Development_WithOneStableTag_AdvancesTheCandidateToTheNextPatch()
    {
        var run = RunVersionState("development", "7", [Tag("3.0.0", OtherSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.1", run.Outputs["base-version"]);
        Assert.Equal("3.0.1-dev.7", run.Outputs["package-version"]);
        Assert.Equal("not-applicable", run.Outputs["release-tag-state"]);
    }

    [Fact]
    public void Development_WithMultipleStableTags_UsesTheLineMaximumPlusOne()
    {
        var run = RunVersionState("development", "7", [Tag("3.0.1", OtherSha), Tag("3.0.0", ThirdSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.2", run.Outputs["base-version"]);
        Assert.Equal("3.0.2-dev.7", run.Outputs["package-version"]);
    }

    [Fact]
    public void Development_IgnoresStableTagsFromOtherCompatibilityLines()
    {
        // v3.1.5 is a valid stable tag but not on the 3.0 compatibility line, so
        // it must not raise the 3.0 candidate patch.
        var run = RunVersionState("development", "7", [Tag("3.1.5", OtherSha), Tag("3.0.0", ThirdSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.1", run.Outputs["base-version"]);
        Assert.Equal("3.0.1-dev.7", run.Outputs["package-version"]);
    }

    [Fact]
    public void RepeatedDevelopmentRuns_DoNotAdvanceTheStableCandidateOrTagState()
    {
        (string Ref, string Sha)[] tags = [Tag("3.0.0", OtherSha)];

        var first = RunVersionState("development", "7", tags);
        var second = RunVersionState("development", "8", tags);

        AssertSucceeded(first);
        AssertSucceeded(second);

        // Development creates no stable tag, so the next stable candidate is
        // identical and only the run-number suffix differs.
        Assert.Equal("3.0.1", first.Outputs["base-version"]);
        Assert.Equal("3.0.1-dev.7", first.Outputs["package-version"]);
        Assert.Equal("3.0.1-dev.8", second.Outputs["package-version"]);

        // The observed tag state is unchanged, so a later release cannot be
        // pushed past the patch the repeated development runs advertised.
        Assert.Equal(first.Outputs["base-version"], second.Outputs["base-version"]);
        Assert.Equal(CalculatedTagState(first.Outputs), CalculatedTagState(second.Outputs));
        Assert.Equal(first.Outputs["tag-state-fingerprint"], second.Outputs["tag-state-fingerprint"]);
    }

    [Fact]
    public void Release_WithNoStableTags_BootstrapsAtPatchZeroOfTheCompatibilityLine()
    {
        var run = RunVersionState("release", "7", []);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.0", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
        Assert.DoesNotContain("-dev.", run.Outputs["package-version"], StringComparison.Ordinal);
    }

    [Fact]
    public void Release_WithAStableTagButNoCurrentShaTag_ClaimsTheNextStableVersion()
    {
        var run = RunVersionState("release", "7", [Tag("3.0.0", OtherSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.1", run.Outputs["base-version"]);
        Assert.Equal("3.0.1", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.1", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
        Assert.DoesNotContain("-dev.", run.Outputs["package-version"], StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseRerun_WithTheCurrentShaStableTag_ReusesTheExactStableVersion()
    {
        var run = RunVersionState(
            "release",
            "7",
            [Tag("3.0.0", CurrentSha)],
            githubSha: CurrentSha);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.0", run.Outputs["release-tag"]);
        Assert.Equal("present", run.Outputs["release-tag-state"]);
        Assert.Contains(";current-sha-tags=v3.0.0;", run.Outputs["calculated-state"], StringComparison.Ordinal);
    }

    [Fact]
    public void Release_WithMultipleCurrentShaTags_FailsClosedInsteadOfGuessing()
    {
        var run = RunVersionState(
            "release",
            "7",
            [Tag("3.0.0", CurrentSha), Tag("3.0.1", CurrentSha)],
            githubSha: CurrentSha);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "Multiple package/compatibility-line tags point to the caller commit; refusing ambiguous release rerun.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    private sealed record VersionStateRun(ShellResult Result, IReadOnlyDictionary<string, string> Outputs);

    private static void AssertSucceeded(VersionStateRun run) =>
        Assert.True(
            run.Result.ExitCode == 0,
            $"version-state failed with exit {run.Result.ExitCode}: {run.Result.StandardError}");

    private static (string Ref, string Sha) Tag(string version, string sha) =>
        ($"refs/tags/{PackageId}/v{version}", sha);

    private static VersionStateRun RunVersionState(
        string branchRole,
        string runNumber,
        IReadOnlyList<(string Ref, string Sha)> tags,
        string compatibilityLine = "3.0",
        string? githubSha = null)
    {
        using var repository = new TempRepository();
        var outputPath = repository.AbsolutePath("github_output");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PACKAGE_ID"] = PackageId,
            ["COMPATIBILITY_LINE"] = compatibilityLine,
            ["BRANCH_ROLE"] = branchRole,
            ["GH_TOKEN"] = "contract-test-token",
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_SHA"] = githubSha ?? CurrentSha,
            ["GITHUB_RUN_NUMBER"] = runNumber,
            ["GITHUB_OUTPUT"] = outputPath,
            ["STUB_TAG_COUNT"] = tags.Count.ToString(CultureInfo.InvariantCulture),
            ["STUB_TAG_ROWS"] = string.Concat(tags.Select(tag => $"{tag.Ref}\tcommit\t{tag.Sha}\n")),
        };

        var result = WorkflowShell.RunBash(
            ApiStubs + "\n" + VersionStateScript() + "\n",
            repository.Root,
            environment);
        var outputs = result.ExitCode == 0 && File.Exists(outputPath)
            ? ParseOutputs(File.ReadAllLines(outputPath))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new VersionStateRun(result, outputs);
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
        var root = YamlWorkflowReader.Parse(WorkflowShell.ReadWorkflow(PublishFile));
        var build = YamlWorkflowReader.MappingChild(YamlWorkflowReader.MappingChild(root, "jobs"), "build");
        var step = YamlWorkflowReader.MappingSequence(build, "steps").Single(step =>
            YamlWorkflowReader.HasChild(step, "id")
            && YamlWorkflowReader.ScalarChild(step, "id") == "version-state");

        return YamlWorkflowReader.ScalarChild(step, "run");
    }
}
