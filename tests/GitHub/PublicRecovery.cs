using System.Text.Json;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class PrivilegedActionStateGuardTests
{
    private static string Root => InfraRepositoryLocator.ResolveRoot();
    private static string StateScript => Path.Combine(Root, ".github", "package", "state.py");
    private static readonly string Sha = new('a', 40);

    [Theory]
    [InlineData("nuget", "nuget", "development")]
    [InlineData("internal", "internal", "development")]
    [InlineData("tag", "publishable", "production")]
    public void Mutators_RejectMalformedStateBeforeAuthenticatedOrMutationCalls(string action, string expectedPublisher, string role)
    {
        using var repository = new TempRepository();
        var content = ReadAction(action);
        var step = FindStep(content, action == "tag" ? "Parse state and revalidate production policy role" : "Parse state and revalidate destination and artifact");
        var outputPath = repository.WriteFile("github-env", string.Empty);
        var tempDirectory = repository.AbsolutePath("runner-temp");
        Directory.CreateDirectory(tempDirectory);
        var curlLog = repository.AbsolutePath("curl.log");
        var result = WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\ncurl() { echo called >> \"$CURL_LOG\"; return 1; }\n" + step, repository.Root,
            ActionEnvironment(repository, "not-base64", outputPath, tempDirectory, curlLog));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("missing, malformed, tampered, or unknown-version", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(curlLog));
        Assert.Empty(File.ReadAllText(outputPath));
        Assert.Contains("--expect-publisher " + expectedPublisher, content, StringComparison.Ordinal);
        Assert.Contains("--role " + (action == "tag" ? "production" : "publishable"), content, StringComparison.Ordinal);
        _ = role;
    }

    [Theory]
    [InlineData("nuget", "public")]
    [InlineData("internal", "private")]
    public void Publisher_RejectsArtifactDigestDriftBeforeCallingGitHub(string publisher, string visibility)
    {
        using var repository = new TempRepository();
        var expectedBytes = "signed-prepared-package";
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "altered-package");
        var state = CreateSealedState(repository, visibility, "development", expectedBytes);
        var content = ReadAction(publisher);
        var step = FindStep(content, "Parse state and revalidate destination and artifact");
        var envFile = repository.WriteFile("github-env", string.Empty);
        var tempDirectory = repository.AbsolutePath("runner-temp");
        Directory.CreateDirectory(tempDirectory);
        var curlLog = repository.AbsolutePath("curl.log");
        var environment = ActionEnvironment(repository, state, envFile, tempDirectory, curlLog);
        environment["GIZMO_ARTIFACT_PATH"] = artifact;
        environment["STUB_VISIBILITY"] = visibility;
        var result = WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n"
            + "curl() { local output=''; while (( $# )); do case \"$1\" in --output) output=$2; shift 2 ;; *) shift ;; esac; done; echo called >> \"$CURL_LOG\"; printf '{\"visibility\":\"%s\"}' \"$STUB_VISIBILITY\" > \"$output\"; printf 200; }\n"
            + "jq() { printf '%s' \"$STUB_VISIBILITY\"; }\n" + step, repository.Root, environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("artifact digest does not match", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("called", File.ReadAllText(curlLog).Trim());
    }

    [Fact]
    public void PublisherSameShaCollisionIsGatedAndTagCannotMoveExistingReleaseRef()
    {
        var nuget = ReadAction("nuget");
        var internalAction = ReadAction("internal");
        Assert.Contains("package-state=published", nuget, StringComparison.Ordinal);
        Assert.Contains("package-state=published", internalAction, StringComparison.Ordinal);
        Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", nuget, StringComparison.Ordinal);
        Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", internalAction, StringComparison.Ordinal);
        Assert.Contains("Release tag already exists for a different commit; refusing to move it.", ReadAction("tag"), StringComparison.Ordinal);
        Assert.Contains("--request POST", ReadAction("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("--request PATCH", ReadAction("tag"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NugetFreshPackage_IsPublishedThroughMockedOidcAndAcceptedPush()
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var shell = StateShell(state, "publishable", "nuget");
        var script = shell + "\n" + CurlPublishingStub() + "\n" + JqPublishingStub() + "\n" + PublishStep("nuget");
        var result = WorkflowShell.RunBash(script, repository.Root, PublishingEnvironment(repository, artifact));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("NuGet.org accepted public package 3.0.0-dev.42.", result.StandardOutput, StringComparison.Ordinal);
        var requests = File.ReadAllText(repository.AbsolutePath("requests.log"));
        Assert.Contains("PUT https://www.nuget.org/api/v2/package", requests, StringComparison.Ordinal);
        Assert.Contains("X-NuGet-ApiKey: nuget-api-key", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("location=yes", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("real", requests, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NugetDuplicatePush_ReconcilesMatchingSameShaProvenanceOnReadback()
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var script = StateShell(state, "publishable", "nuget") + "\n" + CurlPublishingStub() + "\n"
            + JqPublishingStub() + "\nunzip() { if [[ \"$1\" == -Z1 ]]; then printf 'Gizmo.Widget.nuspec'; else printf '%s' \"$STUB_NUSPEC\"; fi; }\n"
            + PublishStep("nuget");
        var environment = PublishingEnvironment(repository, artifact);
        environment["STUB_PUSH_STATUS"] = "409";
        environment["STUB_NUSPEC"] = Nuspec(Sha);
        var result = WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n" + script, repository.Root, environment);

        Assert.True(result.ExitCode == 0, result.StandardError + "\n" + result.StandardOutput);
        Assert.Contains("Verified published public package 3.0.0-dev.42 provenance", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("GET https://api.nuget.org/v3-flatcontainer/gizmo.widget/3.0.0-dev.42/gizmo.widget.3.0.0-dev.42.nupkg", File.ReadAllText(repository.AbsolutePath("requests.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void NugetDuplicateReadback_RetriesDelayedIndexingWithinBoundedBudget()
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var environment = PublishingEnvironment(repository, artifact);
        environment["STUB_PUSH_STATUS"] = "409";
        environment["STUB_READBACK_STATUSES"] = "404 404 200";
        environment["STUB_NUSPEC"] = Nuspec(Sha);
        var result = WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n"
            + StateShell(state, "publishable", "nuget") + "\n" + CurlPublishingStub() + "\n"
            + JqPublishingStub() + "\nunzip() { if [[ \"$1\" == -Z1 ]]; then printf 'Gizmo.Widget.nuspec'; else printf '%s' \"$STUB_NUSPEC\"; fi; }\n"
            + "sleep() { printf '%s\\n' \"$1\" >> \"$STUB_SLEEP_LOG\"; }\n" + PublishStep("nuget"), repository.Root, environment);

        Assert.True(result.ExitCode == 0, result.StandardError + "\n" + result.StandardOutput);
        Assert.Contains("provenance on readback attempt 3", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(new[] { "10", "10" }, File.ReadAllLines(repository.AbsolutePath("sleep.log")));
        Assert.Equal(3, File.ReadAllLines(repository.AbsolutePath("requests.log")).Count(line => line.Contains(".nupkg", StringComparison.Ordinal)));
    }

    [Fact]
    public void NugetDuplicateReadback_ForeignCommitFailsImmediately() =>
        AssertNugetReadbackRejected(Nuspec(new string('b', 40)), "Gizmo.Widget.nuspec", "divergent provenance");

    [Fact]
    public void NugetDuplicateReadback_MalformedNuspecFailsImmediately() =>
        AssertNugetReadbackRejected("<package>", "Gizmo.Widget.nuspec", "missing or malformed provenance");

    [Fact]
    public void NugetDuplicateReadback_WrongPackageIdFailsImmediately() =>
        AssertNugetReadbackRejected(Nuspec(Sha, "Other.Widget"), "Gizmo.Widget.nuspec", "nuspec ID does not match");

    [Fact]
    public void NugetDuplicateReadback_WrongVersionFailsImmediately() =>
        AssertNugetReadbackRejected(Nuspec(Sha, version: "9.9.9"), "Gizmo.Widget.nuspec", "nuspec version does not match");

    [Fact]
    public void NugetDuplicateReadback_MultipleNuspecEntriesFailImmediately() =>
        AssertNugetReadbackRejected(Nuspec(Sha), "Gizmo.Widget.nuspec\ndecoy.nuspec", "exactly one nuspec");

    [Fact]
    public void NugetDuplicateReadback_UnexpectedNuspecNameFailsImmediately() =>
        AssertNugetReadbackRejected(Nuspec(Sha), "decoy.nuspec", "expected package nuspec");

    [Fact]
    public void NugetDuplicateReadback_UnreadablePackageExhaustsThirteenAttemptsAndTwelveMockSleeps()
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var environment = PublishingEnvironment(repository, artifact);
        environment["STUB_PUSH_STATUS"] = "409";
        environment["STUB_READBACK_STATUSES"] = "000";
        var result = RunNugetPublishReadback(repository, state, environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("after 13 attempts", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(12, File.ReadAllLines(repository.AbsolutePath("sleep.log")).Length);
        Assert.All(File.ReadAllLines(repository.AbsolutePath("sleep.log")), delay => Assert.Equal("10", delay));
        Assert.Equal(13, File.ReadAllLines(repository.AbsolutePath("requests.log")).Count(line => line.Contains(".nupkg", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("400")]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("000")]
    public void NugetUnexpectedPushStatus_FailsWithoutReadback(string pushStatus)
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var environment = PublishingEnvironment(repository, artifact);
        environment["STUB_PUSH_STATUS"] = pushStatus;
        var result = WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n"
            + StateShell(state, "publishable", "nuget") + "\n" + CurlPublishingStub() + "\n"
            + JqPublishingStub() + "\n" + PublishStep("nuget"), repository.Root, environment);

        Assert.NotEqual(0, result.ExitCode);
        var expected = pushStatus == "000" ? "Could not reach the NuGet.org package-publish endpoint" : "HTTP " + pushStatus + " for the push";
        Assert.Contains(expected, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(".nupkg", File.ReadAllText(repository.AbsolutePath("requests.log")), StringComparison.Ordinal);
        Assert.Empty(File.ReadAllText(repository.AbsolutePath("sleep.log")));
    }

    [Fact]
    public void NugetSameShaCollision_MatchingProvenanceSkipsDuplicatePush() =>
        AssertNugetCollision(Nuspec(Sha), "Gizmo.Widget.nuspec", shouldPass: true);

    [Fact]
    public void NugetCollision_ForeignCommitProvenanceFailsClosed() =>
        AssertNugetCollision(Nuspec(new string('b', 40)), "Gizmo.Widget.nuspec", shouldPass: false);

    [Fact]
    public void NugetCollision_WrongPackageIdFailsClosed() =>
        AssertNugetCollision(Nuspec(Sha, "Other.Widget"), "Gizmo.Widget.nuspec", shouldPass: false);

    [Fact]
    public void NugetCollision_WrongVersionFailsClosed() =>
        AssertNugetCollision(Nuspec(Sha, version: "9.9.9"), "Gizmo.Widget.nuspec", shouldPass: false);

    [Fact]
    public void NugetCollision_MalformedNuspecFailsClosed() =>
        AssertNugetCollision("<package>", "Gizmo.Widget.nuspec", shouldPass: false);

    [Fact]
    public void NugetCollision_MultipleNuspecEntriesFailClosed() =>
        AssertNugetCollision(Nuspec(Sha), "Gizmo.Widget.nuspec\ndecoy.nuspec", shouldPass: false);

    [Fact]
    public void NugetCollision_PrecheckReturnsUnpublishedForMissingVersionAndRejectsUnreadableExistingPackage()
    {
        using (var repository = new TempRepository())
        {
            var state = CreateSealedState(repository, "public", "development", "prepared");
            var missing = RunCollision(repository, "nuget", state, "public", Nuspec(Sha), "Gizmo.Widget.nuspec", versionPresent: false);
            Assert.Equal(0, missing.ExitCode);
            Assert.Contains("package-state=unpublished", File.ReadAllText(repository.AbsolutePath("github-output")), StringComparison.Ordinal);
        }

        using (var repository = new TempRepository())
        {
            var state = CreateSealedState(repository, "public", "development", "prepared");
            var unreadable = RunCollision(repository, "nuget", state, "public", Nuspec(Sha), "Gizmo.Widget.nuspec", versionPresent: true, packageStatus: "404");
            Assert.NotEqual(0, unreadable.ExitCode);
            Assert.Contains("unverifiable collision", unreadable.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("package-state=published", File.ReadAllText(repository.AbsolutePath("github-output")), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NugetCollision_AcceptsRepositoryResignedArchiveBytesWhenNuspecProvenanceMatches()
    {
        using var repository = new TempRepository();
        var localBytes = "prepared-artifact-bytes";
        var storedBytes = "repository-resigned-stored-bytes";
        var localArtifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", localBytes);
        var state = CreateSealedState(repository, "public", "development", localBytes);
        var collision = RunCollision(repository, "nuget", state, "public", Nuspec(Sha), "Gizmo.Widget.nuspec", versionPresent: true);
        Assert.Equal(0, collision.ExitCode);
        Assert.NotEqual(File.ReadAllText(localArtifact), storedBytes);
        Assert.Equal(storedBytes, File.ReadAllText(repository.AbsolutePath("downloaded-package.nupkg")));
        Assert.Contains("package-state=published", File.ReadAllText(repository.AbsolutePath("github-output")), StringComparison.Ordinal);
    }

    [Fact]
    public void InternalPublisher_RechecksProtectedRouteAndSameShaCollisionWithoutPublishingAgain()
    {
        using var repository = new TempRepository();
        var state = CreateSealedState(repository, "private", "development", "prepared");
        var guard = FindStep(ReadAction("internal"), "Validate internal publisher invocation");
        var protectedPush = WorkflowShell.RunBash(guard, repository.Root,
            new Dictionary<string, string> { ["EVENT_NAME"] = "push", ["REF_PROTECTED"] = "true", ["REF_NAME"] = "refs/heads/pre-release" });
        Assert.Equal(0, protectedPush.ExitCode);

        var collision = RunCollision(repository, "internal", state, "private", Nuspec(Sha), "Gizmo.Widget.nuspec", versionPresent: true);
        Assert.True(collision.ExitCode == 0, collision.StandardError + "\n" + collision.StandardOutput);
        Assert.Contains("package-state=published", File.ReadAllText(repository.AbsolutePath("github-output")), StringComparison.Ordinal);
        Assert.Contains("if: ${{ steps.collision.outputs.package-state != 'published' }}", ReadAction("internal"), StringComparison.Ordinal);
        var requests = File.ReadAllLines(repository.AbsolutePath("curl.log"));
        var serviceIndex = Array.FindIndex(requests, line => line.EndsWith("https://nuget.pkg.github.com/owner/index.json", StringComparison.Ordinal));
        var packageIndex = Array.FindIndex(requests, line => line.EndsWith("/gizmo.widget/index.json", StringComparison.Ordinal));
        var packageDownload = Array.FindIndex(requests, line => line.EndsWith("/gizmo.widget/3.0.0-dev.42/gizmo.widget.3.0.0-dev.42.nupkg", StringComparison.Ordinal));
        Assert.True(serviceIndex >= 0 && packageIndex > serviceIndex && packageDownload > packageIndex,
            string.Join(Environment.NewLine, requests));

        var unprotected = WorkflowShell.RunBash(guard, repository.Root,
            new Dictionary<string, string> { ["EVENT_NAME"] = "push", ["REF_PROTECTED"] = "false", ["REF_NAME"] = "refs/heads/pre-release" });
        Assert.NotEqual(0, unprotected.ExitCode);
        Assert.Contains("protected branch", unprotected.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void TagAction_ReconcilesSameShaAndRefusesForeignShaAndCreatesOnlyMissingTag()
    {
        using var repository = new TempRepository();
        var tagObjectSha = new string('c', 40);
        var tagRows = new object[] { new { @ref = "refs/tags/Gizmo.Widget/v3.0.0", objectSha = tagObjectSha, commit = Sha } };
        var state = CreateSealedState(repository, "public", "production", "prepared", tagRows);
        var vars = StateShell(state, "production", "publishable");
        var recheckStep = FindStep(ReadAction("tag"), "Refetch and recheck calculated state before tagging");
        var refetch = RunTagRecheck(repository, vars, recheckStep, tagObjectSha, Sha);
        Assert.True(refetch.ExitCode == 0, refetch.StandardError + "\n" + refetch.StandardOutput);
        Assert.Contains("/git/tags/" + tagObjectSha, File.ReadAllText(repository.AbsolutePath("requests.log")), StringComparison.Ordinal);

        var drift = RunTagRecheck(repository, vars, recheckStep, new string('d', 40), Sha);
        Assert.NotEqual(0, drift.ExitCode);
        Assert.Contains("Calculated package state drifted before tagging", drift.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("/git/ref/tags/", File.ReadAllText(repository.AbsolutePath("requests.log")), StringComparison.Ordinal);

        var step = FindStep(ReadAction("tag"), "Create or reconcile immutable release tag");
        var same = RunTagMutation(repository, vars, step, "200", "commit", Sha, []);
        Assert.True(same.Result.ExitCode == 0, same.Result.StandardError + "\n" + same.Result.StandardOutput);
        Assert.Contains("GET https://api.github.com/repos/owner/repository/git/ref/tags/Gizmo.Widget/v3.0.0", same.RequestLog, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(repository);

        var foreign = RunTagMutation(repository, vars, step, "200", "commit", new string('b', 40), []);
        Assert.NotEqual(0, foreign.Result.ExitCode);
        Assert.Contains("refusing to move it", foreign.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(repository);

        var missing = RunTagMutation(repository, vars, step, "404", string.Empty, string.Empty, []);
        Assert.Equal(0, missing.Result.ExitCode);
        Assert.Contains("POST https://api.github.com/repos/owner/repository/git/refs", missing.RequestLog, StringComparison.Ordinal);
    }

    [Fact]
    public void TagRelease_AnnotatedOneHopSameCommitReconcilesWithoutMutation()
    {
        var result = RunTagResolution([new TagObjectReply("commit", Sha)]);
        Assert.True(result.Result.ExitCode == 0, result.Result.StandardError + "\n" + result.Result.StandardOutput);
        Assert.Contains("/git/tags/" + new string('c', 40), result.RequestLog, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedMultiHopSameCommitReconcilesWithoutMutation()
    {
        var result = RunTagResolution([
            new TagObjectReply("tag", new string('d', 40)),
            new TagObjectReply("commit", Sha),
        ]);
        Assert.True(result.Result.ExitCode == 0, result.Result.StandardError + "\n" + result.Result.StandardOutput);
        var requests = result.RequestLog;
        Assert.Contains("/git/tags/" + new string('c', 40), requests, StringComparison.Ordinal);
        Assert.Contains("/git/tags/" + new string('d', 40), requests, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedForeignCommitRefusesWithoutMutation()
    {
        var result = RunTagResolution([new TagObjectReply("commit", new string('b', 40))]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("different commit", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_UnsupportedRootObjectTypeFailsClosed()
    {
        var result = RunTagResolution([], referenceType: "tree");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("does not resolve to a commit", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_MissingRootObjectTypeFailsClosed()
    {
        var result = RunTagResolution([], referenceType: "__missing__");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed release-tag response", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_MissingRootObjectShaFailsClosed()
    {
        var result = RunTagResolution([], referenceSha: "__missing__");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed release-tag response", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_InvalidRootObjectShaFailsClosed()
    {
        var result = RunTagResolution([], referenceSha: "not-a-sha");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed release-tag object SHA", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedResponseMissingTypeFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("__missing__", Sha)]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed annotated release-tag response", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedResponseMissingShaFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("commit", "__missing__")]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed annotated release-tag response", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedResponseInvalidShaFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("commit", "not-a-sha")]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("malformed annotated release-tag object SHA", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedApiHttpFailureFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("commit", Sha, "500")]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("HTTP 500 while resolving", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_ReleaseRefApiHttpFailureFailsClosed()
    {
        var result = RunTagResolution([], referenceStatus: "500");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("HTTP 500 while checking the release tag", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_ReleaseRefTransportFailureFailsClosed()
    {
        var result = RunTagResolution([], referenceStatus: "transport");
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("Could not check whether the release tag is already claimed", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedTransportFailureFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("commit", Sha, "transport")]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("Could not resolve the annotated release tag", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedUnsupportedTargetTypeFailsClosed()
    {
        var result = RunTagResolution([new TagObjectReply("tree", Sha)]);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("does not resolve to a commit", result.Result.StandardError, StringComparison.Ordinal);
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagRelease_AnnotatedDepthOverflowFailsClosedWithoutMutation()
    {
        var chain = Enumerable.Range(0, 17)
            .Select(index => new TagObjectReply("tag", index.ToString("x").PadLeft(40, '0')))
            .ToArray();
        var result = RunTagResolution(chain);
        Assert.NotEqual(0, result.Result.ExitCode);
        Assert.Contains("depth limit", result.Result.StandardError, StringComparison.Ordinal);
        Assert.Equal(16, result.RequestLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Contains("/git/tags/", StringComparison.Ordinal)));
        AssertExistingTagWasNeverMutated(result.RequestLog);
    }

    [Fact]
    public void TagStateParser_ExportsTheShaConsumedByTagMutation()
    {
        var stateModule = File.ReadAllText(StateScript);
        Assert.Contains("(\"GIZMO_SHA\", \"sha\")", stateModule, StringComparison.Ordinal);
        Assert.Contains("$GIZMO_SHA", ReadAction("tag"), StringComparison.Ordinal);
        Assert.DoesNotContain("GIZMO_SOURCE_SHA", ReadAction("tag"), StringComparison.Ordinal);
    }

    private static void AssertNugetReadbackRejected(string nuspec, string nuspecEntries, string expectedError)
    {
        using var repository = new TempRepository();
        var artifact = repository.WriteFile("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg", "prepared");
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var environment = PublishingEnvironment(repository, artifact);
        environment["STUB_PUSH_STATUS"] = "409";
        environment["STUB_READBACK_STATUSES"] = "200";
        environment["STUB_NUSPEC"] = nuspec;
        environment["STUB_NUSPEC_ENTRIES"] = nuspecEntries;
        var result = RunNugetPublishReadback(repository, state, environment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(File.ReadAllText(repository.AbsolutePath("sleep.log")));
        Assert.Single(File.ReadAllLines(repository.AbsolutePath("requests.log")), line => line.Contains(".nupkg", StringComparison.Ordinal));
    }

    private static ShellResult RunNugetPublishReadback(TempRepository repository, string state, IReadOnlyDictionary<string, string> environment) =>
        WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n"
            + StateShell(state, "publishable", "nuget") + "\n" + CurlPublishingStub() + "\n"
            + JqPublishingStub() + "\nunzip() { if [[ \"$1\" == -Z1 ]]; then printf '%s\\n' \"$STUB_NUSPEC_ENTRIES\"; else printf '%s' \"$STUB_NUSPEC\"; fi; }\n"
            + "sleep() { printf '%s\\n' \"$1\" >> \"$STUB_SLEEP_LOG\"; }\n" + PublishStep("nuget"), repository.Root, environment);

    private static void AssertNugetCollision(string nuspec, string entries, bool shouldPass)
    {
        using var repository = new TempRepository();
        var state = CreateSealedState(repository, "public", "development", "prepared");
        var result = RunCollision(repository, "nuget", state, "public", nuspec, entries, versionPresent: true);
        var output = File.ReadAllText(repository.AbsolutePath("github-output"));
        if (shouldPass)
        {
            Assert.True(result.ExitCode == 0, result.StandardError + "\n" + result.StandardOutput);
            Assert.Contains("package-state=published", output, StringComparison.Ordinal);
            Assert.Contains("steps.collision.outputs.package-state != 'published'", ReadAction("nuget"), StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("package-state=published", output, StringComparison.Ordinal);
        }
    }

    private static string CreateSealedState(TempRepository repository, string visibility, string role, string digestSource, object[]? tags = null)
    {
        var reference = role == "production" ? "refs/heads/release" : "refs/heads/pre-release";
        var request = JsonSerializer.Serialize(new
        {
            repo = "owner/repository", sha = Sha, @ref = reference, run = "700", attempt = "1", @event = "push",
            dev = "pre-release", prod = "release", role, packageId = "Gizmo.Widget", compatibilityLine = "3.0",
            runNumber = "42", visibility, tags = tags ?? Array.Empty<object>(),
        });
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_EVENT_NAME"] = "push",
            ["GITHUB_REF"] = reference,
            ["GITHUB_BASE_REF"] = string.Empty,
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_SHA"] = Sha,
            ["GITHUB_RUN_ID"] = "700",
            ["GITHUB_RUN_ATTEMPT"] = "1",
        };
        var plan = RunState("plan", request, environment);
        Assert.Equal(0, plan.ExitCode);
        var plannedState = Parse(plan.StandardOutput)["planned-state"];
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(digestSource))).ToLowerInvariant();
        var seal = RunState("seal --digest " + digest, plannedState, environment);
        Assert.Equal(0, seal.ExitCode);
        return Parse(seal.StandardOutput)["state"];
    }

    private static ShellResult RunCollision(
        TempRepository repository,
        string action,
        string state,
        string visibility,
        string nuspec,
        string nuspecEntries,
        bool versionPresent,
        string packageStatus = "200")
    {
        var output = repository.WriteFile("github-output", string.Empty);
        var stateRole = "publishable";
        var publisher = action == "nuget" ? "nuget" : "internal";
        var shellState = StateShell(state, stateRole, publisher);
        var stepName = action == "nuget"
            ? "Recheck tag state and NuGet package provenance"
            : "Recheck tag state and internal package provenance";
        var step = FindStep(ReadAction(action), stepName);
        var packagePath = repository.WriteFile("artifact.nupkg", "stored package");
        var environment = ActionEnvironment(repository, state, output, repository.Root, repository.AbsolutePath("curl.log"));
        environment["GITHUB_ACTION_PATH"] = Path.Combine(Root, ".github", "actions", action).Replace('\\', '/');
        environment["NUSPEC_PARSER"] = Path.Combine(Root, ".github", "package", "nuspec.py").Replace('\\', '/');
        environment["VALIDATE_ORIGIN"] = Path.Combine(Root, ".github", "actions", "internal", "scripts", "validate.mjs").Replace('\\', '/');
        environment["STUB_VISIBILITY"] = visibility;
        environment["STUB_VERSION_PRESENT"] = versionPresent ? "true" : "false";
        environment["STUB_PACKAGE_STATUS"] = packageStatus;
        environment["STUB_NUSPEC"] = nuspec;
        environment["STUB_NUSPEC_ENTRIES"] = nuspecEntries;
        environment["STUB_PACKAGE_FILE"] = packagePath.Replace('\\', '/');
        environment["STUB_PACKAGE_BASE"] = "https://nuget.pkg.github.com/owner/download";
        environment["STUB_NUPKG_BODY"] = "repository-resigned-stored-bytes";
        environment["STUB_DOWNLOADED_PACKAGE"] = repository.AbsolutePath("downloaded-package.nupkg");
        var wrapper = shellState + "\n" + CollisionCurlStub() + "\n" + CollisionJqStub() + "\n" + CollisionUnzipStub();
        return WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n" + wrapper + "\n" + step, repository.Root, environment);
    }

    private static string StateShell(string state, string role, string publisher)
    {
        var identity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_EVENT_NAME"] = "push",
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_SHA"] = Sha,
            ["GITHUB_REF"] = role == "production" ? "refs/heads/release" : "refs/heads/pre-release",
            ["GITHUB_BASE_REF"] = string.Empty,
            ["GITHUB_RUN_ID"] = "700",
            ["GITHUB_RUN_ATTEMPT"] = "1",
        };
        var parsed = WorkflowShell.RunPythonCli(StateScript,
            ["validate", "--role", role, "--expect-publisher", publisher], state, identity);
        Assert.Equal(0, parsed.ExitCode);
        return parsed.StandardOutput;
    }

    private static string CollisionCurlStub() => """
        curl() {
          local output='' method='GET' url=''
          while (( $# )); do
            case "$1" in
              --output) output=$2; shift 2 ;;
              --request) method=$2; shift 2 ;;
              --write-out|--header|--user|--connect-timeout|--max-time|--data) shift 2 ;;
              --silent|--show-error|--location|--fail) shift ;;
              *) url=$1; shift ;;
            esac
          done
          printf '%s %s\n' "$method" "$url" >> "$CURL_LOG"
          case "$url" in
            */git/matching-refs/tags/*) printf '[]' > "$output"; printf 200 ;;
            https://api.nuget.org/*/index.json)
              if [[ "$url" == *flatcontainer* ]]; then
                if [[ "$STUB_VERSION_PRESENT" == true ]]; then printf '{"versions":["%s"]}' "$GIZMO_PACKAGE_VERSION" > "$output"; printf 200; else printf '{"versions":[]}' > "$output"; printf 404; fi
              else printf '{"versions":["%s"]}' "$GIZMO_PACKAGE_VERSION" > "$output"; printf 200; fi ;;
            https://api.nuget.org/*/*.nupkg)
              if [[ "$STUB_PACKAGE_STATUS" == 200 ]]; then printf '%s' "${STUB_NUPKG_BODY:-mock-nupkg}" > "$output"; cp "$output" "$STUB_DOWNLOADED_PACKAGE"; fi
              printf '%s' "$STUB_PACKAGE_STATUS" ;;
            https://nuget.pkg.github.com/owner/index.json)
              printf '{"resources":[{"@type":"PackageBaseAddress/3.0.0","@id":"%s"}]}' "$STUB_PACKAGE_BASE" > "$output"; printf 200 ;;
            https://nuget.pkg.github.com/*/download/*/index.json)
              if [[ "$STUB_VERSION_PRESENT" == true ]]; then printf '{"versions":["%s"]}' "$GIZMO_PACKAGE_VERSION" > "$output"; printf 200; else printf '{"versions":[]}' > "$output"; printf 404; fi ;;
            https://nuget.pkg.github.com/*/download/*.nupkg) printf 'mock-nupkg' > "$output"; printf 200 ;;
            *) echo "unexpected mock URL: $url" >&2; return 9 ;;
          esac
        }
        """;

    private static string CollisionJqStub() => """
        jq() {
          local joined="$*"
          case "$joined" in
            *'PackageBaseAddress/3.0.0'*) printf '%s' "$STUB_PACKAGE_BASE" ;;
            *'index($version)'*) [[ "$STUB_VERSION_PRESENT" == true ]] && return 0 || return 1 ;;
            *'-nc --arg id '*|*'-R -s'*) printf '{"packageId":"%s","tags":[]}' "$GIZMO_PACKAGE_ID" ;;
            *'.[] | [.ref, .object.type, .object.sha] | @tsv'*) printf '%s' "${STUB_TAG_ROWS:-}" ;;
            *'length'*) printf '0\n' ;;
            *'type == "array"'*|*'type == "object"'*) printf 'true\n' ;;
            *) echo "unexpected jq filter: $joined" >&2; return 8 ;;
          esac
        }
        """;

    private static string CollisionUnzipStub() => """
        unzip() {
          if [[ "$1" == -Z1 ]]; then printf '%s\n' "$STUB_NUSPEC_ENTRIES"; else printf '%s' "$STUB_NUSPEC"; fi
        }
        """;

    private static string CurlPublishingStub() => """
        curl() {
          local output='' method='GET' url='' headers=''
          while (( $# )); do
            case "$1" in
              --output) output=$2; shift 2 ;;
              --request) method=$2; shift 2 ;;
              --header) headers+="$2;"; shift 2 ;;
              --write-out|--connect-timeout|--max-time|--data|--form) shift 2 ;;
              --silent|--show-error|--fail) shift ;;
              *) url=$1; shift ;;
            esac
          done
          printf '%s %s headers=%s\n' "$method" "$url" "$headers" >> "$REQUEST_LOG"
          case "$url" in
            *audience=*) printf '{"value":"oidc-token"}' ;;
            https://www.nuget.org/api/v2/token) printf '{"apiKey":"nuget-api-key"}' ;;
            https://www.nuget.org/api/v2/package) [[ "${STUB_PUSH_STATUS:-201}" != 000 ]] || return 7; printf '%s' "${STUB_PUSH_STATUS:-201}" ;;
            https://api.nuget.org/v3-flatcontainer/*.nupkg)
              local count=0
              [[ -f "$STUB_READBACK_COUNTER" ]] && count=$(<"$STUB_READBACK_COUNTER")
              count=$((count + 1)); printf '%s' "$count" > "$STUB_READBACK_COUNTER"
              local statuses=( ${STUB_READBACK_STATUSES:-200} )
              local index=$((count - 1)); (( index < ${#statuses[@]} )) || index=$((${#statuses[@]} - 1))
              local code=${statuses[$index]}
              [[ "$code" != 200 || -z "$output" ]] || printf '%s' "$STUB_NUSPEC" > "$output"
              printf '%s' "$code" ;;
            *) echo "unexpected publish URL: $url" >&2; return 9 ;;
          esac
        }
        """;

    private static string JqPublishingStub() => """
        jq() {
          case "$*" in
            *'.value'*) printf 'oidc-token' ;;
            *'.apiKey'*) printf 'nuget-api-key' ;;
            *'-nc'*) printf '{}' ;;
            *) echo "unexpected jq filter: $*" >&2; return 8 ;;
          esac
        }
        """;

    private static string PublishStep(string action) => FindStep(ReadAction(action), "Publish exact package and reconcile duplicate provenance");

    private static Dictionary<string, string> PublishingEnvironment(TempRepository repository, string artifact) => new(StringComparer.Ordinal)
    {
        ["NUSPEC_PARSER"] = Path.Combine(Root, ".github", "package", "nuspec.py").Replace('\\', '/'),
        ["GITHUB_ACTION_PATH"] = Path.Combine(Root, ".github", "actions", "nuget").Replace('\\', '/'),
        ["GITHUB_SHA"] = Sha,
        ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "mock-request-token",
        ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://oidc.example/token?api-version=2.0",
        ["NUGET_USER"] = "mock-profile",
        ["GIZMO_ARTIFACT_PATH"] = artifact.Replace('\\', '/'),
        ["GIZMO_PACKAGE_ID"] = "Gizmo.Widget",
        ["GIZMO_PACKAGE_VERSION"] = "3.0.0-dev.42",
        ["REQUEST_LOG"] = repository.AbsolutePath("requests.log"),
        ["STUB_READBACK_STATUSES"] = "200",
        ["STUB_NUSPEC"] = Nuspec(Sha),
        ["STUB_NUSPEC_ENTRIES"] = "Gizmo.Widget.nuspec",
        ["STUB_READBACK_COUNTER"] = repository.AbsolutePath("readback-counter.txt"),
        ["STUB_SLEEP_LOG"] = repository.WriteFile("sleep.log", string.Empty),
    };

    private sealed record TagObjectReply(string Type, string Sha, string Status = "200");

    private sealed record TagMutationRun(ShellResult Result, string RequestLog);

    private static TagMutationRun RunTagResolution(
        IReadOnlyList<TagObjectReply> replies,
        string referenceStatus = "200",
        string? referenceType = null,
        string? referenceSha = null)
    {
        using var repository = new TempRepository();
        var state = CreateSealedState(repository, "public", "production", "prepared");
        var shellState = StateShell(state, "production", "publishable");
        var step = FindStep(ReadAction("tag"), "Create or reconcile immutable release tag");
        var rootType = referenceType ?? (replies.Count > 0 ? "tag" : "commit");
        var rootSha = referenceSha ?? (rootType == "tag" ? new string('c', 40) : Sha);
        var run = RunTagMutation(repository, shellState, step, referenceStatus, rootType, rootSha, replies);
        return run with { RequestLog = File.ReadAllText(repository.AbsolutePath("requests.log")) };
    }

    private static TagMutationRun RunTagMutation(
        TempRepository repository,
        string shellState,
        string step,
        string referenceStatus,
        string referenceType,
        string referenceSha,
        IReadOnlyList<TagObjectReply> replies)
    {
        var log = repository.WriteFile("requests.log", string.Empty);
        var curl = """
            curl() {
              local output='' method='GET' url=''
              while (( $# )); do
                case "$1" in
                  --output) output=$2; shift 2 ;;
                  --request) method=$2; shift 2 ;;
                  --header|--data|--write-out) shift 2 ;;
                  --silent|--show-error|--location) shift ;;
                  *) url=$1; shift ;;
                esac
              done
              printf '%s %s\n' "$method" "$url" >> "$REQUEST_LOG"
              if [[ "$method" == POST ]]; then printf 201; return 0; fi
              if [[ "$url" == */git/ref/tags/* ]]; then
                MOCK_CURRENT_TYPE="$MOCK_REF_TYPE"
                MOCK_CURRENT_SHA="$MOCK_REF_SHA"
                [[ "$MOCK_REF_STATUS" != transport ]] || return 7
                if [[ "$MOCK_REF_STATUS" == 200 ]]; then write_tag_response "$output" "$MOCK_CURRENT_TYPE" "$MOCK_CURRENT_SHA"; fi
                printf '%s' "$MOCK_REF_STATUS"
                return 0
              fi
              if [[ "$url" == */git/tags/* ]]; then
                local requested_sha=${url##*/} index=-1 candidate=0
                local request_shas=( ${MOCK_TAG_REQUEST_SHAS:-} )
                local types=( ${MOCK_TAG_TYPES:-} )
                local shas=( ${MOCK_TAG_SHAS:-} )
                local statuses=( ${MOCK_TAG_STATUSES:-} )
                for candidate in "${!request_shas[@]}"; do
                  if [[ "${request_shas[$candidate]}" == "$requested_sha" ]]; then index=$candidate; break; fi
                done
                (( index >= 0 )) || { echo "unexpected mocked tag object SHA: $requested_sha" >&2; return 9; }
                local status=${statuses[$index]:-500}
                MOCK_CURRENT_TYPE=${types[$index]:-__missing__}
                MOCK_CURRENT_SHA=${shas[$index]:-__missing__}
                [[ "$status" != transport ]] || return 7
                if [[ "$status" == 200 ]]; then write_tag_response "$output" "$MOCK_CURRENT_TYPE" "$MOCK_CURRENT_SHA"; fi
                printf '%s' "$status"
                return 0
              fi
              echo "unexpected mocked tag URL: $url" >&2
              return 9
            }
            write_tag_response() {
              local output=$1 object_type=$2 object_sha=$3
              printf '{"object":{' > "$output"
              if [[ "$object_type" != __missing__ ]]; then printf '"type":"%s"' "$object_type" >> "$output"; fi
              if [[ "$object_sha" != __missing__ ]]; then
                [[ "$object_type" == __missing__ ]] || printf ',' >> "$output"
                printf '"sha":"%s"' "$object_sha" >> "$output"
              fi
              printf '}}' >> "$output"
            }
            """;
        var jq = """
            jq() {
              local joined="$*" input_file=${@: -1} value=''
              case "$joined" in
                *'-nc'*) printf '{}' ;;
                *'.object.type | strings'*) value=$(sed -n 's/.*"type":"\([^"]*\)".*/\1/p' "$input_file"); [[ -n "$value" ]] || return 1; printf '%s' "$value" ;;
                *'.object.sha | strings'*) value=$(sed -n 's/.*"sha":"\([^"]*\)".*/\1/p' "$input_file"); [[ -n "$value" ]] || return 1; printf '%s' "$value" ;;
                *) echo "unexpected jq filter: $*" >&2; return 8 ;;
              esac
            }
            """;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GH_TOKEN"] = "mock-token",
            ["REQUEST_LOG"] = log,
            ["MOCK_REF_STATUS"] = referenceStatus,
            ["MOCK_REF_TYPE"] = referenceType,
            ["MOCK_REF_SHA"] = referenceSha,
            ["MOCK_TAG_REQUEST_SHAS"] = string.Join(' ', new[] { referenceSha }.Concat(replies.Where(reply => reply.Type == "tag").Select(reply => reply.Sha))),
            ["MOCK_TAG_TYPES"] = string.Join(' ', replies.Select(reply => reply.Type)),
            ["MOCK_TAG_SHAS"] = string.Join(' ', replies.Select(reply => reply.Sha)),
            ["MOCK_TAG_STATUSES"] = string.Join(' ', replies.Select(reply => reply.Status)),
        };
        var result = WorkflowShell.RunBash(shellState + "\n" + curl + "\n" + jq + "\n" + step, repository.Root, environment);
        return new TagMutationRun(result, File.ReadAllText(log));
    }

    private static void AssertExistingTagWasNeverMutated(TempRepository repository) =>
        AssertExistingTagWasNeverMutated(File.ReadAllText(repository.AbsolutePath("requests.log")));

    private static void AssertExistingTagWasNeverMutated(string requestLog)
    {
        Assert.DoesNotContain("POST https://api.github.com/repos/owner/repository/git/refs", requestLog, StringComparison.Ordinal);
        Assert.DoesNotContain("PATCH", requestLog, StringComparison.Ordinal);
        Assert.DoesNotContain("PUT", requestLog, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", requestLog, StringComparison.Ordinal);
    }

    private static ShellResult RunTagRecheck(TempRepository repository, string shellState, string step, string objectSha, string commitSha)
    {
        var log = repository.WriteFile("requests.log", string.Empty);
        var curl = """
            curl() {
              local output='' url=''
              while (( $# )); do case "$1" in --output) output=$2; shift 2 ;; --write-out|--header) shift 2 ;; --silent|--show-error|--location) shift ;; *) url=$1; shift ;; esac; done
              printf '%s\n' "$url" >> "$REQUEST_LOG"
              case "$url" in
                */git/matching-refs/tags/*) printf '%s' "$STUB_TAG_REFS" > "$output"; printf 200 ;;
                */git/tags/*) printf '{"object":{"type":"commit","sha":"%s"}}' "$STUB_TAG_COMMIT" > "$output"; printf 200 ;;
                *) echo "unexpected mocked tag URL: $url" >&2; return 9 ;;
              esac
            }
            """;
        var jq = """
            jq() {
              local joined="$*"
              case "$joined" in
                *'type == "array"'*) printf true ;;
                *'.[] | [.ref, .object.type, .object.sha] | @tsv'*) printf '%s\n' "$STUB_TAG_ROW" ;;
                *'length'*) printf '1\n' ;;
                *'.object.type | strings'*) printf 'commit' ;;
                *'.object.sha | strings'*) printf '%s' "$STUB_TAG_COMMIT" ;;
                *'-R -s'*) printf '%s' "$STUB_TAGS_JSON" ;;
                *'-nc --arg id '* ) printf '{"packageId":"%s","tags":%s}' "$GIZMO_PACKAGE_ID" "$STUB_TAGS_JSON" ;;
                *) echo "unexpected jq filter: $joined" >&2; return 8 ;;
              esac
            }
            """;
        var tagRef = "refs/tags/Gizmo.Widget/v3.0.0";
        var row = $"{tagRef}\ttag\t{objectSha}";
        var refs = System.Text.Json.JsonSerializer.Serialize(new[] { new { @ref = tagRef, @object = new { type = "tag", sha = objectSha } } });
        var tagsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new { @ref = tagRef, objectSha } });
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITHUB_API_URL"] = "https://api.github.com", ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GH_TOKEN"] = "mock-token", ["REQUEST_LOG"] = log, ["STUB_TAG_REFS"] = refs,
            ["STUB_TAG_ROW"] = row, ["STUB_TAG_COMMIT"] = commitSha, ["STUB_TAGS_JSON"] = tagsJson,
            ["STATE_PARSER"] = StateScript.Replace('\\', '/'),
        };
        return WorkflowShell.RunBash(WorkflowShell.PythonBashFunction() + "\n" + shellState + "\n" + curl + "\n" + jq + "\n" + step, repository.Root, environment);
    }

    private static ShellResult RunState(string arguments, string input, IReadOnlyDictionary<string, string>? environment = null) =>
        WorkflowShell.RunPythonCli(StateScript, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), input, environment);

    private static Dictionary<string, string> ActionEnvironment(TempRepository repository, string state, string envFile, string tempDirectory, string curlLog) =>
        new(StringComparer.Ordinal)
        {
            ["STATE"] = state,
            ["STATE_PARSER"] = Path.Combine(Root, ".github", "package", "state.py").Replace('\\', '/'),
            ["NUSPEC_PARSER"] = Path.Combine(Root, ".github", "package", "nuspec.py").Replace('\\', '/'),
            ["GH_TOKEN"] = "mock-token",
            ["GITHUB_REPOSITORY"] = "owner/repository",
            ["GITHUB_SHA"] = Sha,
            ["GITHUB_REF"] = "refs/heads/pre-release",
            ["GITHUB_RUN_ID"] = "700",
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["GITHUB_ENV"] = envFile,
            ["RUNNER_TEMP"] = tempDirectory,
            ["GITHUB_ACTION_PATH"] = Path.Combine(Root, ".github", "actions", "nuget").Replace('\\', '/'),
            ["GITHUB_API_URL"] = "https://api.github.com",
            ["GITHUB_OUTPUT"] = repository.WriteFile("github-output", string.Empty),
            ["GITHUB_ACTOR"] = "test",
            ["GITHUB_REPOSITORY_OWNER"] = "owner",
            ["CURL_LOG"] = curlLog,
            ["GIZMO_ARTIFACT_PATH"] = repository.AbsolutePath("artifacts/Gizmo.Widget.3.0.0-dev.42.nupkg"),
        };

    private static Dictionary<string, string> Parse(string content) => content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    private static string Nuspec(string commit, string id = "Gizmo.Widget", string version = "3.0.0-dev.42") =>
        $"<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{id}</id><version>{version}</version><repository commit=\"{commit}\" /></metadata></package>";

    private static string ReadAction(string name) => WorkflowShell.ReadAction(name);

    private static string FindStep(string action, string name)
    {
        var root = YamlWorkflowReader.Parse(action);
        var runs = YamlWorkflowReader.MappingChild(root, "runs");
        var step = YamlWorkflowReader.MappingSequence(runs, "steps").Single(item => YamlWorkflowReader.ScalarChild(item, "name") == name);
        return YamlWorkflowReader.ScalarChild(step, "run");
    }
}
