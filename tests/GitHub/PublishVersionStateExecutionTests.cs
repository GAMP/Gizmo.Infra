using System.Globalization;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executes the committed <c>version-state</c> bash block from
/// <c>package-publish.yml</c> against a stubbed GitHub tag API, so the version
/// and release-tag rules are verified by behavior instead of by matching source
/// text. Development and a new release share one derivation: patch 0 when the
/// compatibility line has no stable tag, otherwise numeric <c>max(Y)+1</c>.
/// Development appends <c>-dev.N</c> without creating a tag, while production
/// reuses the exact version of a current-SHA tag or claims the next stable
/// version. The calculation is tag-derived and never consults a registry, so an
/// empty line tag state starts at its first <c>M.m.0</c> without a synthetic tag;
/// stable state is scoped to the exact package prefix and compatibility line, and
/// malformed, foreign-prefix, or ambiguous state fails closed. The highest
/// governed line is computed from the tag set, and the declared compatibility
/// line must equal, advance one minor, or advance one major (with a minor reset)
/// from that governed line.
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
        // v2.5.7 and v1.0.13 are valid stable tags on lines other than 3.0,
        // so they must not raise the 3.0 candidate patch. The declared 3.0
        // line is the highest governed line in the tag set, so the
        // governed-transition check does not mask the per-line exclusion.
        var run = RunVersionState(
            "development",
            "7",
            [Tag("3.0.0", OtherSha), Tag("2.5.7", OtherSha), Tag("1.0.13", ThirdSha)]);

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
    public void Production_WithNoStableTags_BootstrapsAtPatchZeroOfTheCompatibilityLine()
    {
        var run = RunVersionState("production", "7", []);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.0", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
        Assert.DoesNotContain("-dev.", run.Outputs["package-version"], StringComparison.Ordinal);
    }

    [Fact]
    public void Production_WithAStableTagButNoCurrentShaTag_ClaimsTheNextStableVersion()
    {
        var run = RunVersionState("production", "7", [Tag("3.0.0", OtherSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.1", run.Outputs["base-version"]);
        Assert.Equal("3.0.1", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.1", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
        Assert.DoesNotContain("-dev.", run.Outputs["package-version"], StringComparison.Ordinal);
    }

    [Fact]
    public void Development_WithSparseStableTags_AdvancesPastTheObservedLineMaximum()
    {
        // The runtime derives from the tag set alone, so a gap in the tag sequence
        // does not reset the maximum and published packages never raise the patch.
        var run = RunVersionState(
            "development",
            "7",
            [Tag("3.0.2", OtherSha), Tag("3.0.4", OtherSha), Tag("3.0.5", ThirdSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.6", run.Outputs["base-version"]);
        Assert.Equal("3.0.6-dev.7", run.Outputs["package-version"]);
    }

    [Fact]
    public void Production_AfterAnAdoptionTagAtTheHighestLineVersion_ClaimsTheNextPatch()
    {
        // An adoption tag at the proven highest version 3.0.5 resumes
        // steady state one patch later at 3.0.6.
        var run = RunVersionState("production", "7", [Tag("3.0.5", OtherSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.6", run.Outputs["base-version"]);
        Assert.Equal("3.0.6", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.6", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
    }

    [Fact]
    public void ProductionRerun_WithTheCurrentShaStableTag_ReusesTheExactStableVersion()
    {
        var run = RunVersionState(
            "production",
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
    public void Production_WithMultipleCurrentShaTags_FailsClosedInsteadOfGuessing()
    {
        var run = RunVersionState(
            "production",
            "7",
            [Tag("3.0.0", CurrentSha), Tag("3.0.1", CurrentSha)],
            githubSha: CurrentSha);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "Multiple package/compatibility-line tags point to the caller commit; refusing ambiguous release rerun.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Development_WithOnlyLowerCompatibilityLineTags_BootstrapsTheEmptyLineTagState()
    {
        // Tags on lines below the declared line (3.0) establish a lower
        // governed line, so the 3.0 declared line starts at its first 3.0.0.
        // The tags themselves never contribute because no tag is on the 3.0
        // line. The 2.5 governed line accepts 3.0 as the next-major reset.
        var run = RunVersionState(
            "development",
            "7",
            [Tag("2.5.7", OtherSha), Tag("1.0.13", ThirdSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0-dev.7", run.Outputs["package-version"]);
    }

    [Fact]
    public void Production_WithOnlyLowerCompatibilityLineTags_BootstrapsTheEmptyLineTagState()
    {
        // Tags below the declared line establish a lower governed line, so the
        // declared line has an empty line tag state and starts at its first
        // patch. The lower-line tags never contribute because no tag is on the
        // declared line.
        var run = RunVersionState(
            "production",
            "7",
            [Tag("2.5.7", OtherSha), Tag("1.0.13", ThirdSha)]);

        AssertSucceeded(run);
        Assert.Equal("3.0.0", run.Outputs["base-version"]);
        Assert.Equal("3.0.0", run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v3.0.0", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
    }

    [Fact]
    public void Production_OnEmptyLineTagState_ReportsTheTagMissingAndClaimsNoSyntheticCurrentTag()
    {
        var run = RunVersionState("production", "7", []);

        AssertSucceeded(run);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);

        // The preparation advertises the tag it would create but reports no
        // current-SHA tag and invents none; only the release-only tag action may
        // create the tag, and only after a successful release publication.
        Assert.Contains(";current-sha-tags=;", run.Outputs["calculated-state"], StringComparison.Ordinal);
    }

    [Fact]
    public void Production_ForeignPackagePrefixTag_FailsClosedInsteadOfCountingIt()
    {
        var run = RunVersionState("production", "7", [("refs/tags/Other.Package/v3.0.0", OtherSha)]);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "GitHub returned a tag outside the requested package prefix.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_MalformedTagUnderThePackagePrefix_FailsClosed()
    {
        var run = RunVersionState("production", "7", [($"refs/tags/{PackageId}/v3.0", OtherSha)]);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "Malformed tag under the exact package prefix:",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Production_CarriesTheObservedStableTagStateIntoTheConsistencyRecheck()
    {
        var run = RunVersionState("production", "7", [Tag("3.0.0", OtherSha)]);

        AssertSucceeded(run);

        // The exact observed snapshot travels with the calculated state so the
        // publisher and tag rechecks cannot observe a drifted package/tag state.
        var decoded = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(CalculatedTagState(run.Outputs)));

        Assert.Equal($"refs/tags/{PackageId}/v3.0.0={OtherSha}", decoded);
        Assert.Equal($"{PackageId}/v3.0.1", run.Outputs["release-tag"]);
    }

    [Theory]
    [InlineData("1.0", "1.0.0", "1.0.0-dev.7", "Gizmo.Widget/v1.0.0")]
    [InlineData("4.7", "4.7.0", "4.7.0-dev.7", "Gizmo.Widget/v4.7.0")]
    [InlineData("3.2", "3.2.0", "3.2.0-dev.7", "Gizmo.Widget/v3.2.0")]
    [InlineData("10.0", "10.0.0", "10.0.0-dev.7", "Gizmo.Widget/v10.0.0")]
    public void Development_BootstrapsTheEmptyLineTagStateAcrossCompatibilityLines(
        string compatibilityLine,
        string expectedBase,
        string expectedPackage,
        string expectedReleaseTag)
    {
        // An empty stable tag state is the natural-bootstrap case for any
        // canonical numeric <major>.<minor> line; the active line is not fixed
        // and the first development build is <major>.<minor>.0-dev.N.
        var run = RunVersionState("development", "7", [], compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal(expectedPackage, run.Outputs["package-version"]);
        Assert.Equal(expectedReleaseTag, run.Outputs["release-tag"]);
        Assert.Equal("not-applicable", run.Outputs["release-tag-state"]);
    }

    [Theory]
    [InlineData("1.0", "1.0.13", "1.0.14", "1.0.14-dev.7")]
    [InlineData("3.2", "3.2.4", "3.2.5", "3.2.5-dev.7")]
    [InlineData("4.7", "4.7.0", "4.7.1", "4.7.1-dev.7")]
    [InlineData("10.0", "10.0.99", "10.0.100", "10.0.100-dev.7")]
    public void Development_AdvancesTheCandidatePatchOnTheActiveLineForAnyCompatibilityLine(
        string compatibilityLine,
        string existingTag,
        string expectedBase,
        string expectedPackage)
    {
        // The single stable tag on the matching line sets the line maximum and
        // the next development build is exactly max(patch)+1; other lines are
        // excluded from the candidate calculation.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(existingTag, OtherSha)],
            compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal(expectedPackage, run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("1.0", new[] { "1.0.0", "1.0.13", "0.9.9" }, "1.0.14")]
    [InlineData("4.7", new[] { "4.7.2", "4.6.99", "4.6.0" }, "4.7.3")]
    [InlineData("3.2", new[] { "3.2.0", "3.2.4", "3.0.0", "3.1.5" }, "3.2.5")]
    [InlineData("10.0", new[] { "10.0.0", "9.99.99" }, "10.0.1")]
    public void Development_OnlyTagsMatchingTheActiveMajorMinorLineContribute(
        string compatibilityLine,
        string[] tagVersions,
        string expectedBase)
    {
        // Tags for other major or minor lines never advance the candidate; only
        // tags whose <major>.<minor> equals the evaluated compatibility line
        // contribute to the patch calculation. The fixtures only include tags
        // on lines at or below the declared line so the declared line is the
        // highest governed line and only its tags contribute.
        var tags = tagVersions
            .Select(version => Tag(version, OtherSha))
            .ToArray();

        var run = RunVersionState("development", "7", tags, compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal($"{expectedBase}-dev.7", run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("4.8", new[] { "4.8.0", "4.8.5", "4.7.13" }, "4.8.6")]
    [InlineData("1.1", new[] { "1.1.0", "1.0.13", "1.0.7" }, "1.1.1")]
    public void Development_TagsFromThePreviousMinorLineDoNotContributeWhenTheActiveLineAdvances(
        string compatibilityLine,
        string[] tagVersions,
        string expectedBase)
    {
        // A next-minor governed transition (highest governed minor + 1) accepts
        // tags on the previous minor line without letting them advance the
        // patch candidate on the new line.
        var tags = tagVersions
            .Select(version => Tag(version, OtherSha))
            .ToArray();

        var run = RunVersionState("development", "7", tags, compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal($"{expectedBase}-dev.7", run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("2.0", new[] { "2.0.0", "2.0.3", "1.0.13", "1.1.0" }, "2.0.4")]
    [InlineData("5.0", new[] { "5.0.0", "5.0.1", "4.7.2", "4.8.0", "4.6.99" }, "5.0.2")]
    public void Development_TagsFromThePreviousMajorLineDoNotContributeWhenTheActiveLineResets(
        string compatibilityLine,
        string[] tagVersions,
        string expectedBase)
    {
        // A next-major governed transition (highest governed major + 1 with
        // minor == 0) accepts tags on the previous major line without letting
        // them advance the patch candidate on the new line.
        var tags = tagVersions
            .Select(version => Tag(version, OtherSha))
            .ToArray();

        var run = RunVersionState("development", "7", tags, compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal($"{expectedBase}-dev.7", run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("1.0", "1.0.0", "1.0.1", "1.0.1")]
    [InlineData("3.2", "3.2.4", "3.2.5", "3.2.5")]
    [InlineData("4.7", "4.7.0", "4.7.1", "4.7.1")]
    [InlineData("10.0", "10.0.99", "10.0.100", "10.0.100")]
    public void Production_AdvancesTheCandidatePatchOnTheActiveLineForAnyCompatibilityLine(
        string compatibilityLine,
        string existingTag,
        string expectedBase,
        string expectedPackage)
    {
        var run = RunVersionState(
            "production",
            "7",
            [Tag(existingTag, OtherSha)],
            compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
        Assert.Equal(expectedBase, run.Outputs["base-version"]);
        Assert.Equal(expectedPackage, run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v{expectedBase}", run.Outputs["release-tag"]);
        Assert.Equal("missing", run.Outputs["release-tag-state"]);
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("3.2", "3.2.4")]
    [InlineData("4.7", "4.7.0")]
    [InlineData("10.0", "10.0.99")]
    public void ProductionRerun_WithTheCurrentShaStableTag_ReusesTheExactStableVersionOnAnyLine(
        string compatibilityLine,
        string tagVersion)
    {
        // The same-SHA rerun recovery is a generic property: any active
        // compatibility line reuses its exact tagged version when the caller
        // commit already owns exactly one matching stable tag.
        var run = RunVersionState(
            "production",
            "7",
            [Tag(tagVersion, CurrentSha)],
            compatibilityLine: compatibilityLine,
            githubSha: CurrentSha);

        AssertSucceeded(run);
        Assert.Equal(tagVersion, run.Outputs["base-version"]);
        Assert.Equal(tagVersion, run.Outputs["package-version"]);
        Assert.Equal($"{PackageId}/v{tagVersion}", run.Outputs["release-tag"]);
        Assert.Equal("present", run.Outputs["release-tag-state"]);
        Assert.Contains($";current-sha-tags=v{tagVersion};", run.Outputs["calculated-state"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", "v1.0")]
    [InlineData("3.2", "v3.2.4-dev")]
    [InlineData("4.7", "V4.7.0")]
    [InlineData("10.0", "v10.0.0.1")]
    public void Production_FailsClosedOnMalformedTagForAnyCompatibilityLine(
        string compatibilityLine,
        string malformedLeaf)
    {
        // Malformed tags under the exact package prefix are a hard failure on
        // every active line; the ref prefix is unchanged but the leaf does not
        // match the canonical numeric grammar.
        var run = RunVersionState(
            "production",
            "7",
            [($"refs/tags/{PackageId}/{malformedLeaf}", OtherSha)],
            compatibilityLine: compatibilityLine);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "Malformed tag under the exact package prefix:",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    // Governed compatibility-line transitions: a declared line below the
    // highest governed line must be exactly same, next minor, or next major
    // (with a minor reset). Any skipped, backward, or invalid-major-reset
    // transition fails closed.

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.7", "1.7.4")]
    public void GovernedTransition_AcceptedForTheSameDeclaredLine(string compatibilityLine, string tagVersion)
    {
        // The highest governed line equals the declared line; same-line claims
        // remain a normal patch continuation.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(tagVersion, OtherSha)],
            compatibilityLine: compatibilityLine);

        AssertSucceeded(run);
    }

    [Theory]
    [InlineData("1.0.13", "1.1")]
    [InlineData("1.7.4", "1.8")]
    public void GovernedTransition_AcceptedForNextMinorDeclaredLine(
        string highestTag,
        string declaredLine)
    {
        // The next minor line is exactly one minor above the highest governed
        // line, so the first patch on the new line starts at 0.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(highestTag, OtherSha)],
            compatibilityLine: declaredLine);

        AssertSucceeded(run);
        Assert.Equal($"{declaredLine}.0", run.Outputs["base-version"]);
        Assert.Equal($"{declaredLine}.0-dev.7", run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("1.0.13", "2.0")]
    [InlineData("1.7.4", "2.0")]
    [InlineData("9.9.99", "10.0")]
    public void GovernedTransition_AcceptedForNextMajorDeclaredLine(
        string highestTag,
        string declaredLine)
    {
        // The next major line resets the minor to 0 and starts at patch 0; the
        // declared line must be exactly one major above the highest governed
        // line with minor == 0.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(highestTag, OtherSha)],
            compatibilityLine: declaredLine);

        AssertSucceeded(run);
        Assert.Equal($"{declaredLine}.0", run.Outputs["base-version"]);
        Assert.Equal($"{declaredLine}.0-dev.7", run.Outputs["package-version"]);
    }

    [Theory]
    [InlineData("1.0", "1.0.13", "1.2")]   // skipped minor
    [InlineData("1.0", "1.0.13", "1.9")]   // skipped minor
    [InlineData("1.7", "1.7.4", "1.9")]    // skipped minor
    public void GovernedTransition_FailsClosedOnSkippedMinor(string governedTop, string highestTag, string declaredLine)
    {
        // Skipping a minor is not a governed transition and must fail closed.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(highestTag, OtherSha)],
            compatibilityLine: declaredLine);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            $"Declared compatibility line {declaredLine} is not a governed transition from {governedTop}.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", "1.0.13", "2.1")]   // next major but minor != 0
    [InlineData("1.0", "1.0.13", "3.0")]   // skipped major
    [InlineData("9.9", "9.9.99", "11.0")]  // skipped major
    public void GovernedTransition_FailsClosedOnInvalidMajorReset(string governedTop, string highestTag, string declaredLine)
    {
        // A next-major transition must reset the minor to 0; a non-zero minor
        // or any major above the next one is not a governed transition.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(highestTag, OtherSha)],
            compatibilityLine: declaredLine);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            $"Declared compatibility line {declaredLine} is not a governed transition from {governedTop}.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.3", "2.3.5", "2.2")]
    [InlineData("2.3", "2.3.5", "1.9")]
    [InlineData("2.3", "2.3.5", "1.0")]
    public void GovernedTransition_FailsClosedOnBackwardDeclaredLine(string governedTop, string highestTag, string declaredLine)
    {
        // Moving the compatibility line backward is not a governed transition
        // and must fail closed.
        var run = RunVersionState(
            "development",
            "7",
            [Tag(highestTag, OtherSha)],
            compatibilityLine: declaredLine);

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            $"Declared compatibility line {declaredLine} is not a governed transition from {governedTop}.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GovernedTransition_DerivedFromTheHighestPackageQualifiedStableTagNotTheProjectFile()
    {
        // The comparison authority is the package-qualified stable tag history,
        // not a previous project-file value. A declared compat line below the
        // highest governed line fails closed even when it would match an older
        // project descriptor.
        var run = RunVersionState(
            "development",
            "7",
            [Tag("1.0.13", OtherSha), Tag("1.1.0", ThirdSha)],
            compatibilityLine: "1.0");

        Assert.NotEqual(0, run.Result.ExitCode);
        Assert.Contains(
            "Declared compatibility line 1.0 is not a governed transition from 1.1.",
            run.Result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GovernedTransition_DoesNotApplyWhenNoStableTagsExist()
    {
        // With no tag history there is no governed line, so any canonical
        // numeric compatibility line (subject to the major >= 1 grammar) is
        // accepted for natural bootstrap.
        var run = RunVersionState("development", "7", tags: [], compatibilityLine: "4.7");

        AssertSucceeded(run);
        Assert.Equal("4.7.0", run.Outputs["base-version"]);
        Assert.Equal("4.7.0-dev.7", run.Outputs["package-version"]);
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