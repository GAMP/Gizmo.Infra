using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Contract and executable coverage for the one-time existing-package
/// bootstrap/adoption boundary. The runtime calculates only from the governed
/// package tags and the publisher rechecks only the calculated package version,
/// so it cannot discover a higher stable registry version. Natural bootstrap
/// (no stable registry package <em>and</em> no matching line tag) is the only
/// case where steady state is safe from the start; any stable line package
/// requires a migration operator to inspect the registry, prove the highest
/// stable version, and create its immutable tag before steady state is enabled.
/// The executable provenance guards prove the committed publishers still fail
/// closed on an existing calculated version without the caller commit, and the
/// steady-state workflows and actions carry no migration or adoption code.
/// </summary>
public sealed class PackageBootstrapAdoptionContractTests
{
    private static readonly string[] WorkflowFiles = ["package-publish.yml", "package-validation.yml"];

    private static readonly string[] ActionDirectories =
    [
        "preflight",
        "private",
        "public",
        "tag",
    ];

    // Legacy bootstrap/adoption or registry-migration branches must never enter
    // the steady-state workflow or actions; the generic empty-tag next-patch
    // derivation is the only bootstrap, and adoption stays an operator contract.
    private static readonly string[] LegacyTokens =
    [
        "adopt", "bootstrap", "migrat", "legacy", "synthetic", "backfill", "pre-existing", "seed",
    ];

    private static readonly string CurrentSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);

    private const string ProvenanceFailure = "no authenticated provenance for this caller SHA";

    private const string NuspecWithoutRepositoryCommit =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Gizmo.Widget</id>
            <version>3.0.0</version>
            <repository type="git" url="https://github.com/owner/repository" />
          </metadata>
        </package>
        """;

    [Fact]
    public void ExistingPackageWithoutCallerRepositoryCommitProvenance_FailsClosed()
    {
        foreach (var actionDirectory in new[] { "public", "private" })
        {
            var action = WorkflowShell.ReadAction(actionDirectory);
            Assert.Contains(ProvenanceFailure, action, StringComparison.Ordinal);
            Assert.Contains("creating a recovery tag", action, StringComparison.Ordinal);

            foreach (var nuspec in new[] { NuspecWithoutRepositoryCommit, Nuspec(OtherSha) })
            {
                var run = RunProvenanceGuard(actionDirectory, nuspec, CurrentSha);
                Assert.NotEqual(0, run.Result.ExitCode);
                Assert.Contains(ProvenanceFailure, run.Result.StandardError, StringComparison.Ordinal);
            }
        }
        ExistingPackageWithMalformedRepositoryCommitProvenance_FailsClosed();
        ExistingPackageWithExactCallerCommitProvenance_IsAcceptedAndNormalized();
    }

    private void ExistingPackageWithMalformedRepositoryCommitProvenance_FailsClosed()
    {
        foreach (var commit in new[] { new string('a', 39), new string('z', 40) })
        {
            var run = RunProvenanceGuard("public", Nuspec(commit), CurrentSha);
            Assert.NotEqual(0, run.Result.ExitCode);
            Assert.Contains(ProvenanceFailure, run.Result.StandardError, StringComparison.Ordinal);
        }
    }

    private void ExistingPackageWithExactCallerCommitProvenance_IsAcceptedAndNormalized()
    {
        foreach (var actionDirectory in new[] { "public", "private" })
        {
            var run = RunProvenanceGuard(actionDirectory, Nuspec(CurrentSha.ToUpperInvariant()), CurrentSha);
            Assert.Equal(0, run.Result.ExitCode);
            Assert.Equal(CurrentSha, run.RepositoryCommit);
        }
    }

    [Fact]
    public void SteadyStateSurface_CarriesNoLegacyBootstrapOrAdoptionLogic()
    {
        foreach (var file in WorkflowFiles)
        {
            AssertNoLegacyTokens(WorkflowShell.ReadWorkflow(file));
        }

        foreach (var directory in ActionDirectories)
        {
            AssertNoLegacyTokens(WorkflowShell.ReadAction(directory));
        }
        PreparationWorkflows_DoNotCreateTags();
        RegistryInspection_IsRequiredBeforeSteadyStateIsEnabled();
        Runtime_ChecksOnlyTheCalculatedVersionAndCannotDiscoverHigherPackages();
        HigherRegistryPackagesWithNoTags_AreNotClaimedToFailClosedAtRuntime();
        NaturalBootstrap_RequiresNoStableRegistryPackageAndNoLineTag();
        Migration_AdoptsTheHighestStableLineVersionThenAdvancesOnePatch();
        MigrationFailure_LeavesPublishingDisabled();
        MigrationAndRuntime_FailClosedSeparately();
        MigrationProvenanceRequirements_AreDocumented();
        BootstrapAndAdoption_ExcludeOtherCompatibilityLines();
        SteadyStatePublishing_IsTagDerivedAndAdoptsNothing();
        GizmoShared10_MigrationAdoptionAdvancesOnePatch();
    }

    private void PreparationWorkflows_DoNotCreateTags()
    {
        // "No synthetic tag": only the release-only tag action may write a ref.
        foreach (var file in WorkflowFiles)
        {
            var content = WorkflowShell.ReadWorkflow(file);
            Assert.DoesNotContain("git/refs", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--request POST", content, StringComparison.Ordinal);
            Assert.DoesNotContain(".github/actions/tag", content, StringComparison.Ordinal);
        }

        Assert.Contains("git/refs", WorkflowShell.ReadAction("tag"), StringComparison.Ordinal);
    }

    private void RegistryInspection_IsRequiredBeforeSteadyStateIsEnabled()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // The runtime never sees higher registry versions, so registry inspection
        // is a migration gate that must complete before steady state is enabled.
        Assert.Contains(
            "gated on a migration operator inspecting every stable registry version for the exact package ID before steady state is enabled",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "gated on a migration operator inspecting all stable registry versions first, before steady state is enabled",
            provider,
            StringComparison.Ordinal);
    }

    private void Runtime_ChecksOnlyTheCalculatedVersionAndCannotDiscoverHigherPackages()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // The publisher's collision recheck reads only the calculated version, so
        // any other published version stays invisible to the runtime.
        Assert.Contains("the publisher rechecks only the calculated package version", caller, StringComparison.Ordinal);
        Assert.Contains(
            "it cannot discover any other published version, including a higher stable one",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "the publisher rechecks only the calculated package version",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "it cannot discover any other published version, including a higher stable one",
            provider,
            StringComparison.Ordinal);
    }

    private void HigherRegistryPackagesWithNoTags_AreNotClaimedToFailClosedAtRuntime()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // Registry <major>.<minor>.4 and <major>.<minor>.5 with no line tags
        // calculate <major>.<minor>.0, which the publisher sees as unpublished,
        // so it would publish a new lower version rather than detect the higher
        // packages.
        Assert.Contains(
            "versions `<major>.<minor>.4` and `<major>.<minor>.5` and no line tags",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("the runtime will not catch it", caller, StringComparison.Ordinal);
        Assert.Contains(
            "would publish a new lower `<major>.<minor>.0` instead of failing closed",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "would publish a new lower `<major>.<minor>.0` rather than detect the higher packages",
            provider,
            StringComparison.Ordinal);

        Assert.DoesNotContain("fails closed on the existing-package collision", caller, StringComparison.Ordinal);
        Assert.DoesNotContain("so the run fails closed", caller, StringComparison.Ordinal);
        Assert.DoesNotContain("so the run fails closed", provider, StringComparison.Ordinal);
    }

    private void NaturalBootstrap_RequiresNoStableRegistryPackageAndNoLineTag()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // Bootstrap is the conjunction: the line starts at its first
        // <major>.<minor>.0 only when the registry has no stable package for the
        // line *and* no matching tag exists. "No tag" alone is not bootstrap.
        Assert.Contains(
            "the active compatibility line has no stable package in the selected registry *and* no tag under `<package-id>/` for that line",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "the workflow derives the first `<major>.<minor>.0`",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "the active compatibility line has no stable package in the selected registry *and* no matching stable tag",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("only case where steady state is safe without a migration step", provider, StringComparison.Ordinal);
    }

    private void Migration_AdoptsTheHighestStableLineVersionThenAdvancesOnePatch()
    {
        var caller = Flatten(CallerDoc());

        // The migration candidate is the highest published line version, and the
        // next release after its immutable tag claims one patch later.
        Assert.Contains(
            "The migration candidate is the highest stable `<major>.<minor>.<patch>`",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "registry versions `<major>.<minor>.4` and `<major>.<minor>.5` yield candidate `<major>.<minor>.5`, never the first `<major>.<minor>.0`",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "create the immutable `<package-id>/v<major>.<minor>.<patch>` tag deliberately",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "a candidate of `<major>.<minor>.5` resumes at `<major>.<minor>.6`",
            caller,
            StringComparison.Ordinal);
    }

    private void MigrationFailure_LeavesPublishingDisabled()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // An unprovable highest version is not a runtime failure the workflow can
        // detect; the operator must keep steady state disabled.
        Assert.Contains("Unprovable highest — migration stays disabled", caller, StringComparison.Ordinal);
        Assert.Contains(
            "migration remains disabled: do not enable or run steady state, do not create a tag, and do not publish",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "The runtime provides no safety net for the unadopted higher version",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("Migration stays disabled", provider, StringComparison.Ordinal);
        Assert.Contains(
            "do not enable steady state, do not publish, and do not create a tag",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("The runtime cannot detect the unadopted higher version", provider, StringComparison.Ordinal);
    }

    private void MigrationAndRuntime_FailClosedSeparately()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // Runtime fail-closed guards the calculated version and governed tags;
        // migration fail-closed is the operator's obligation for unseen versions.
        Assert.Contains("Runtime fail-closed", caller, StringComparison.Ordinal);
        Assert.Contains("Migration fail-closed", caller, StringComparison.Ordinal);
        Assert.Contains(
            "the operator keeps steady state disabled whenever the registry shows a stable line package that cannot be adopted",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("this is an operator obligation and not a workflow guarantee", caller, StringComparison.Ordinal);
        Assert.Contains("Runtime and migration fail closed separately", provider, StringComparison.Ordinal);
        Assert.Contains("because the runtime never observes those higher versions", provider, StringComparison.Ordinal);
    }

    private void MigrationProvenanceRequirements_AreDocumented()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        Assert.Contains("Missing, malformed, or foreign provenance fails migration", caller, StringComparison.Ordinal);
        Assert.Contains("git cat-file -e <sha>^{commit}", caller, StringComparison.Ordinal);
        Assert.Contains(
            "a valid lower candidate that sits below an unproven higher line version",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("fails migration and adopts nothing", caller, StringComparison.Ordinal);
        Assert.Contains(
            "conclusive `RepositoryCommit` provenance tied to a real caller commit",
            provider,
            StringComparison.Ordinal);
    }

    private void BootstrapAndAdoption_ExcludeOtherCompatibilityLines()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        Assert.Contains("Versions for another compatibility line are excluded", caller, StringComparison.Ordinal);
        Assert.Contains("never raise or lower the conclusion", caller, StringComparison.Ordinal);
        Assert.Contains(
            "tags for another package or another compatibility line never advance the candidate",
            provider,
            StringComparison.Ordinal);
    }

    private void SteadyStatePublishing_IsTagDerivedAndAdoptsNothing()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // The runtime is tag-derived and carries no registry query for candidate
        // selection, so bootstrap and adoption stay an operator contract. The
        // provider also states the same-SHA recovery and no-synthetic-tag rules.
        Assert.Contains("Steady-state publishing is tag-derived", caller, StringComparison.Ordinal);
        Assert.Contains("it never queries a registry", caller, StringComparison.Ordinal);
        Assert.Contains(
            "separate, deliberate, one-time caller action, outside the workflow",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("do not create a synthetic tag", caller, StringComparison.Ordinal);
        Assert.Contains(
            "The steady-state workflow does not adopt an existing package",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("same-SHA recovery is the only automatic success", provider, StringComparison.Ordinal);
        Assert.Contains("never creates a synthetic tag", provider, StringComparison.Ordinal);
        Assert.Contains(
            "or action carries registry migration or adoption code",
            provider,
            StringComparison.Ordinal);
    }

    private void GizmoShared10_MigrationAdoptionAdvancesOnePatch()
    {
        var caller = Flatten(CallerDoc());
        var provider = Flatten(ProviderDoc());

        // Gizmo.Shared is on compatibility line 1.0 with stable 1.0.13 already
        // in the selected registry, so the line is not a natural bootstrap and
        // needs migration adoption before steady state is enabled.
        Assert.Contains("Gizmo.Shared 1.0 migration and adoption", caller, StringComparison.Ordinal);
        Assert.Contains("`Gizmo.Shared` is on compatibility line 1.0", caller, StringComparison.Ordinal);
        Assert.Contains("`Gizmo.Shared 1.0.13` package already exists", caller, StringComparison.Ordinal);
        Assert.Contains("the line is not a natural bootstrap", caller, StringComparison.Ordinal);

        Assert.Contains("Gizmo.Shared 1.0 migration and adoption", provider, StringComparison.Ordinal);
        Assert.Contains("`Gizmo.Shared` is on compatibility line 1.0", provider, StringComparison.Ordinal);
        Assert.Contains("`Gizmo.Shared 1.0.13` package already exists", provider, StringComparison.Ordinal);
        Assert.Contains("it is not a natural bootstrap and needs migration", provider, StringComparison.Ordinal);

        // Provenance proof is required before the deliberate adoption tag.
        Assert.Contains("`RepositoryCommit`", caller, StringComparison.Ordinal);
        Assert.Contains("real commit in the caller repository", caller, StringComparison.Ordinal);
        Assert.Contains("`RepositoryCommit`", provider, StringComparison.Ordinal);
        Assert.Contains("real caller commit as its `RepositoryCommit`", provider, StringComparison.Ordinal);

        Assert.Contains(
            "deliberately create the immutable `Gizmo.Shared/v1.0.13` tag",
            caller,
            StringComparison.Ordinal);
        Assert.Contains(
            "deliberately create the immutable `Gizmo.Shared/v1.0.13` tag",
            provider,
            StringComparison.Ordinal);

        // Steady state resumes at patch+1: 1.0.14-dev.N, 1.0.14, Gizmo.Shared/v1.0.14.
        Assert.Contains("the next development build is `1.0.14-dev.N`", caller, StringComparison.Ordinal);
        Assert.Contains("the next stable release publishes `1.0.14`", caller, StringComparison.Ordinal);
        Assert.Contains(
            "creates the immutable `Gizmo.Shared/v1.0.14` tag",
            caller,
            StringComparison.Ordinal);
        Assert.Contains("the next development build is `1.0.14-dev.N`", provider, StringComparison.Ordinal);
        Assert.Contains("the next stable release calculates `1.0.14`", provider, StringComparison.Ordinal);
        Assert.Contains(
            "creates the immutable `Gizmo.Shared/v1.0.14` tag",
            provider,
            StringComparison.Ordinal);

        // The evaluated project <Version> stays the compatibility line only; the
        // pilot ultimately uses <Version>1.0</Version> and the 1.0.14 patch is
        // infrastructure-owned and never encoded in the project Version.
        Assert.Contains("compatibility line only", caller, StringComparison.Ordinal);
        Assert.Contains("`<Version>1.0</Version>`", caller, StringComparison.Ordinal);
        Assert.Contains("`1.0.14` patch remains infrastructure-owned", caller, StringComparison.Ordinal);
        Assert.Contains("never encoded in the project `Version`", caller, StringComparison.Ordinal);

        Assert.Contains("compatibility line only", provider, StringComparison.Ordinal);
        Assert.Contains("`<Version>1.0</Version>`", provider, StringComparison.Ordinal);
        Assert.Contains("infrastructure-owned and is never encoded", provider, StringComparison.Ordinal);
    }

    private sealed record ProvenanceRun(ShellResult Result, string RepositoryCommit);

    /// <summary>
    /// Runs the committed nuspec provenance extraction and guard from a publisher
    /// action against a stubbed <c>unzip</c>, so the actual comparison decides
    /// rather than a hand copy.
    /// </summary>
    private static ProvenanceRun RunProvenanceGuard(string actionDirectory, string nuspec, string githubSha)
    {
        var guard = WorkflowShell.ExtractBlock(
            WorkflowShell.ReadAction(actionDirectory),
            "nuspec=$(unzip",
            "fi");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STUB_NUSPEC"] = nuspec,
            ["GITHUB_SHA"] = githubSha,
        };

        var script =
            "set -euo pipefail\n"
            + "package_file=/dev/null\n"
            + "unzip() { printf '%s' \"$STUB_NUSPEC\"; }\n"
            + guard
            + "\nprintf 'repository_commit=%s\\n' \"$repository_commit\"\n";

        var result = WorkflowShell.RunBash(script, Path.GetTempPath(), environment);
        var outputs = ParsePrinted(result.StandardOutput);
        var commit = outputs.TryGetValue("repository_commit", out var value) ? value : string.Empty;
        return new ProvenanceRun(result, commit);
    }

    private static void AssertNoLegacyTokens(string content)
    {
        foreach (var token in LegacyTokens)
        {
            Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Nuspec(string commit) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Gizmo.Widget</id>
            <version>3.0.0</version>
            <repository type="git" url="https://github.com/owner/repository" commit="{commit}" />
          </metadata>
        </package>
        """;

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

    // The docs wrap Markdown by hand, so phrase assertions read against a single
    // whitespace-normalized line instead of one physical line.
    private static string Flatten(string content) =>
        string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string CallerDoc() => Read("docs", "caller.md");

    private static string ProviderDoc() => Read("docs", "provider.md");

    private static string Read(string directory, string file) =>
        File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), directory, file));
}
