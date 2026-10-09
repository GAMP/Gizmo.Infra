using System.Text;
using System.Text.Json;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class PackageStatePlannerTests
{
    private const string PackageId = "Gizmo.Widget";
    private static readonly string Sha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);
    private static string StateScript => Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "package", "state.py");

    [Fact]
    public void Planner_ProducesPrDevelopmentAndProductionRoutesFromOneVersionSource()
    {
        var pr = Plan(eventName: "pull_request", role: "development", visibility: "public", runNumber: "42");
        Assert.Equal("none", pr["publisher"]);
        Assert.Equal("false", pr["tag"]);
        Assert.Equal("3.0.0-pr.42", StateField(pr["planned-state"], "packageVersion"));

        var development = Plan(eventName: "push", role: "development", visibility: "public", runNumber: "42");
        Assert.Equal("nuget", development["publisher"]);
        Assert.Equal("false", development["tag"]);
        Assert.Equal("3.0.0-dev.42", StateField(development["planned-state"], "packageVersion"));

        var production = Plan(eventName: "push", role: "production", visibility: "private", runNumber: "42");
        Assert.Equal("internal", production["publisher"]);
        Assert.Equal("true", production["tag"]);
        Assert.Equal("3.0.0", StateField(production["planned-state"], "packageVersion"));
        Assert.Equal("Gizmo.Widget/v3.0.0", production["release-tag"]);
    }

    [Fact]
    public void Planner_UsesCurrentShaAnnotatedTagForSameShaRecoveryAndRejectsAmbiguity()
    {
        var currentTag = Tag("3.0.7", Sha, "tag");
        var plan = Plan(eventName: "push", role: "production", tags: [currentTag]);
        Assert.Equal("present", plan["release-tag-state"]);
        Assert.Equal("3.0.7", plan["package-version"]);
        Assert.Equal("v3.0.7", StateField(plan["planned-state"], "currentShaTags"));

        var ambiguous = Plan(eventName: "push", role: "production", tags: [currentTag, Tag("3.0.8", Sha)]);
        Assert.NotEqual(0, ExitCode(ambiguous));
        Assert.Contains("ambiguous-current-tag", Error(ambiguous), StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_PreservesVersionGovernanceAndPaginableSortedTagFingerprint()
    {
        var tags = new[] { Tag("3.0.8", OtherSha), Tag("3.0.99", OtherSha), Tag("2.99.999", OtherSha) };
        var maximum = Plan(eventName: "push", role: "development", tags: tags);
        Assert.Equal("3.0.100-dev.42", maximum["package-version"]);

        var nextMinor = Plan(eventName: "push", role: "development", compatibilityLine: "3.1", tags: [Tag("3.0.7", OtherSha)]);
        Assert.Equal("3.1.0-dev.42", nextMinor["package-version"]);
        foreach (var line in new[] { "3.2", "4.1" })
        {
            var invalid = Plan(eventName: "push", role: "development", compatibilityLine: line, tags: [Tag("3.0.7", OtherSha)]);
            Assert.NotEqual(0, ExitCode(invalid));
            Assert.Contains("line-transition", Error(invalid), StringComparison.Ordinal);
        }

        var first = Plan(eventName: "push", role: "development", tags: tags);
        var reversed = Plan(eventName: "push", role: "development", tags: tags.Reverse().ToArray());
        Assert.Equal(first["tag-state-fingerprint"], reversed["tag-state-fingerprint"]);
    }

    [Fact]
    public void Planner_ReturnsCheapNoOpForUnrelatedBranchAndFailsUnsupportedVisibilityClosed()
    {
        var gate = RunBash("role=none; [[ \"$REF\" == refs/heads/\"$DEV_BRANCH\" ]] && role=development; printf '%s' \"$role\";",
            new Dictionary<string, string> { ["REF"] = "refs/heads/topic", ["DEV_BRANCH"] = "dev" });
        Assert.Equal(0, gate.ExitCode);
        Assert.Equal("none", gate.StandardOutput);

        var unsupported = Plan(eventName: "push", role: "development", visibility: "internal");
        Assert.NotEqual(0, ExitCode(unsupported));
        Assert.Contains("unsupported-routing", Error(unsupported), StringComparison.Ordinal);
    }

    [Fact]
    public void StateCli_SealsArtifactDigestAndRejectsMalformedUnknownDuplicateOversizeAndTamperedState()
    {
        var planned = Plan(eventName: "push", role: "development");
        var sealedState = Seal(planned["planned-state"]);
        AssertValid(sealedState, "publishable", "nuget");

        var payload = Decode(sealedState);
        payload["v"] = 2;
        AssertInvalid(Encode(payload), "unknown-version");
        payload = Decode(sealedState);
        payload["surprise"] = "extra";
        AssertInvalid(Encode(payload), "malformed");
        AssertInvalid(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":1,\"v\":1}")), "duplicate-key");
        AssertInvalid(new string('A', 70000), "malformed");

        payload = Decode(sealedState);
        payload["publisher"] = "internal";
        AssertInvalid(Encode(payload), "malformed");
        payload = Decode(sealedState);
        payload["artifactDigest"] = new string('f', 64);
        AssertValid(Encode(payload), "publishable", "nuget");
        payload = Decode(sealedState);
        payload["repo"] = "foreign/repo";
        var bindingEnvironment = ContextForState(sealedState);
        bindingEnvironment["GITHUB_REPOSITORY"] = "owner/repository";
        var binding = Invoke("validate --role publishable --expect-publisher nuget", Encode(payload), bindingEnvironment);
        Assert.NotEqual(0, binding.ExitCode);
        Assert.Contains("binding-mismatch", binding.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void StateCli_BindsCurrentGitHubRunAndEnforcesMutatorPolicyAndRoute()
    {
        var planned = Plan(eventName: "push", role: "production", visibility: "public");
        var state = Seal(planned["planned-state"]);
        AssertValid(state, "production", "publishable");
        AssertInvalid(state, "production", "internal", "publisher-mismatch");
        var payload = Decode(state);
        payload["role"] = "development";
        AssertInvalid(Encode(payload), "production", "publishable", "role-mismatch");

        var wrongSha = ContextForState(state);
        wrongSha["GITHUB_SHA"] = OtherSha;
        var bound = Invoke("validate --role production --expect-publisher publishable", state, wrongSha);
        Assert.NotEqual(0, bound.ExitCode);
        Assert.Contains("binding-mismatch", bound.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pre-release", "development")]
    [InlineData("release", "production")]
    public void PullRequestPlanAndSeal_BindEffectiveBaseWhileRunnerRefIsMergeRef(string baseBranch, string role)
    {
        var request = PlanFor("pull_request", role, "pre-release", "release", "refs/heads/" + baseBranch);
        Assert.Equal(0, ExitCode(request));
        Assert.Equal("none", request["publisher"]);
        Assert.Equal("false", request["tag"]);
        Assert.Equal("3.0.0-pr.42", request["package-version"]);
        Assert.Equal("not-applicable", request["release-tag-state"]);
        Assert.Equal("refs/heads/" + baseBranch, StateField(request["planned-state"], "ref"));

        var env = RunnerEnvironment("pull_request", "refs/pull/123/merge", baseBranch);
        var sealedState = Seal(request["planned-state"], env);
        Assert.Equal("pull_request", StateField(sealedState, "event"));
        Assert.Equal("refs/heads/" + baseBranch, StateField(sealedState, "ref"));
        Assert.Equal("none", StateField(sealedState, "publisher"));
        Assert.Equal("false", StateField(sealedState, "tag"));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("base-ref")]
    [InlineData("repository")]
    [InlineData("sha")]
    [InlineData("run")]
    [InlineData("attempt")]
    public void PullRequestSeal_RejectsMisboundRunnerContext(string binding)
    {
        var request = PlanFor("pull_request", "development", "pre-release", "release", "refs/heads/pre-release");
        var env = RunnerEnvironment("pull_request", "refs/pull/123/merge", "pre-release");
        switch (binding)
        {
            case "event": env["GITHUB_EVENT_NAME"] = "push"; env["GITHUB_REF"] = "refs/heads/pre-release"; break;
            case "base-ref": env["GITHUB_BASE_REF"] = "release"; break;
            case "repository": env["GITHUB_REPOSITORY"] = "foreign/repository"; break;
            case "sha": env["GITHUB_SHA"] = OtherSha; break;
            case "run": env["GITHUB_RUN_ID"] = "701"; break;
            case "attempt": env["GITHUB_RUN_ATTEMPT"] = "2"; break;
        }

        var result = Invoke("seal --digest " + new string('d', 64), request["planned-state"], env);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("binding-mismatch", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PushSeal_ContinuesToBindTheActualBranchRef()
    {
        var request = PlanFor("push", "development", "pre-release", "release", "refs/heads/pre-release");
        var env = RunnerEnvironment("push", "refs/heads/release", string.Empty);
        var result = Invoke("seal --digest " + new string('d', 64), request["planned-state"], env);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("binding-mismatch", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release+candidate", "production", "development")]
    [InlineData("feature/with.dots", "release", "development")]
    [InlineData("topic@name", "release", "development")]
    [InlineData("@", "release", "development")]
    [InlineData("café", "release", "development")]
    [InlineData("development", "stable/release.2", "production")]
    public void GitAcceptedNontrivialBranchNames_ResolveRolePlanSealAndValidate(string dev, string prod, string role)
    {
        AssertGitAcceptsBranch(dev);
        AssertGitAcceptsBranch(prod);
        var branch = role == "production" ? prod : dev;
        var expectedRef = "refs/heads/" + branch;
        var gate = RunPreflightRole(dev, prod, expectedRef);
        Assert.Equal(role, gate.StandardOutput);

        var plan = PlanFor("push", role, dev, prod, expectedRef);
        Assert.Equal(0, ExitCode(plan));
        var env = RunnerEnvironment("push", expectedRef, string.Empty);
        var state = Seal(plan["planned-state"], env);
        var validation = Invoke("validate --role publishable --expect-publisher nuget", state, env);
        Assert.Equal(0, validation.ExitCode);
        Assert.Equal(expectedRef, StateField(state, "ref"));
    }

    [Theory]
    [InlineData("bad branch")]
    [InlineData("bad..branch")]
    [InlineData("-leading")]
    [InlineData("trailing/")]
    [InlineData("name.lock")]
    [InlineData("part/@{revision")]
    [InlineData("path//component")]
    [InlineData("path/../escape")]
    [InlineData("revision~1")]
    [InlineData("line\nbreak")]
    [InlineData("branch\\name")]
    public void GitRejectedUnsafeBranchNames_RemainRejectedByPlanner(string branch)
    {
        Assert.NotEqual(0, GitBranchCheck(branch).ExitCode);
        var plan = PlanFor("push", "development", branch, "release", "refs/heads/" + branch);
        Assert.NotEqual(0, ExitCode(plan));
    }

    [Fact]
    public void GitValidBranchLongerThan255Characters_IsAcceptedWithinTheBoundedStateProtocol()
    {
        var branch = string.Join('/', Enumerable.Repeat(new string('a', 120), 3));
        Assert.True(branch.Length > 255);
        Assert.Equal(0, GitBranchCheck(branch).ExitCode);
        var reference = "refs/heads/" + branch;
        var plan = PlanFor("push", "development", branch, "release", reference);
        Assert.Equal(0, ExitCode(plan));
        var env = RunnerEnvironment("push", reference, string.Empty);
        var state = Seal(plan["planned-state"], env);
        Assert.Equal(reference, StateField(state, "ref"));
    }

    private static Dictionary<string, string> Plan(
        string eventName,
        string role,
        string visibility = "public",
        string compatibilityLine = "3.0",
        string runNumber = "42",
        string? currentSha = null,
        IReadOnlyList<object>? tags = null)
    {
        var dev = "pre-release";
        var prod = "release";
        var reference = role == "production" ? "refs/heads/" + prod : "refs/heads/" + dev;
        return PlanFor(eventName, role, dev, prod, reference, visibility, compatibilityLine, runNumber, currentSha, tags);
    }

    private static Dictionary<string, string> PlanFor(
        string eventName,
        string role,
        string dev,
        string prod,
        string reference,
        string visibility = "public",
        string compatibilityLine = "3.0",
        string runNumber = "42",
        string? currentSha = null,
        IReadOnlyList<object>? tags = null)
    {
        var request = new
        {
            repo = "owner/repository", sha = currentSha ?? Sha, @ref = reference, run = "700", attempt = "1",
            @event = eventName, dev, prod, role, packageId = PackageId,
            compatibilityLine, runNumber, visibility, tags = tags ?? [],
        };
        var result = Invoke("plan", JsonSerializer.Serialize(request));
        if (result.ExitCode != 0) return new Dictionary<string, string> { ["ExitCode"] = result.ExitCode.ToString(), ["Error"] = result.Error };
        return Parse(result.Output);
    }

    private static object Tag(string version, string commit, string objectType = "commit") => new
    {
        @ref = $"refs/tags/{PackageId}/v{version}", objectSha = objectType == "tag" ? new string('c', 40) : commit, commit,
    };

    private static string Seal(string planned, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = Invoke("seal --digest " + new string('d', 64), planned, environment ?? ContextForState(planned));
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output.Split('=', 2)[1].Trim();
    }

    private static void AssertValid(string state, string role, string publisher) =>
        Assert.Equal(0, Invoke($"validate --role {role} --expect-publisher {publisher}", state, ContextForState(state)).ExitCode);

    private static void AssertInvalid(string state, string expected) => AssertInvalid(state, "publishable", "nuget", expected);

    private static void AssertInvalid(string state, string role, string publisher, string expected)
    {
        var result = Invoke($"validate --role {role} --expect-publisher {publisher}", state, ContextForState(state));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error) Invoke(string command, string input, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = WorkflowShell.RunPythonCli(StateScript, command.Split(' ', StringSplitOptions.RemoveEmptyEntries), input, environment);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static ShellResult RunBash(string script, IReadOnlyDictionary<string, string> environment) =>
        WorkflowShell.RunBash(script, Path.GetTempPath(), environment);

    private static Dictionary<string, string> RunnerEnvironment(string eventName, string currentRef, string baseRef) => new(StringComparer.Ordinal)
    {
        ["GITHUB_EVENT_NAME"] = eventName,
        ["GITHUB_REF"] = currentRef,
        ["GITHUB_BASE_REF"] = baseRef,
        ["GITHUB_REPOSITORY"] = "owner/repository",
        ["GITHUB_SHA"] = Sha,
        ["GITHUB_RUN_ID"] = "700",
        ["GITHUB_RUN_ATTEMPT"] = "1",
    };

    private static Dictionary<string, string> ContextForState(string state)
    {
        var fallback = RunnerEnvironment("push", "refs/heads/pre-release", string.Empty);
        try
        {
            var normalized = state.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(normalized)));
            if (!json.RootElement.TryGetProperty("event", out var eventNode)
                || !json.RootElement.TryGetProperty("ref", out var refNode)
                || eventNode.ValueKind != JsonValueKind.String
                || refNode.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }

            var eventName = eventNode.GetString()!;
            var stateRef = refNode.GetString()!;
            var isPullRequest = eventName == "pull_request";
            var baseRef = isPullRequest && stateRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? stateRef["refs/heads/".Length..]
                : string.Empty;
            return RunnerEnvironment(eventName, isPullRequest ? "refs/pull/123/merge" : stateRef, baseRef);
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static ShellResult GitBranchCheck(string branch) =>
        WorkflowShell.RunBash("git check-ref-format --branch \"$TEST_BRANCH\"", Path.GetTempPath(),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TEST_BRANCH"] = branch });

    private static void AssertGitAcceptsBranch(string branch) => Assert.Equal(0, GitBranchCheck(branch).ExitCode);

    private static ShellResult RunPreflightRole(string dev, string prod, string currentRef)
    {
        var action = WorkflowShell.ReadAction("preflight");
        var block = WorkflowShell.ExtractBlock(action, "[[ -n \"$DEV_BRANCH\"", "fi");
        return WorkflowShell.RunBash("set -euo pipefail\nfail() { echo \"$1\" >&2; exit 1; }\n" + block + "\nprintf '%s' \"$branch_role\"",
            Path.GetTempPath(), new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DEV_BRANCH"] = dev, ["PROD_BRANCH"] = prod, ["CURRENT_REF"] = currentRef,
            });
    }

    private static string StateField(string state, string key)
    {
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state)));
        var value = json.RootElement.GetProperty(key);
        return value.ValueKind switch
        {
            JsonValueKind.Array => value[0].GetString()!,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetString()!,
        };
    }

    private static JsonObjectWrapper Decode(string state) => new(JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state.Replace('-', '+').Replace('_', '/')))).RootElement);
    private static string Encode(JsonObjectWrapper value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJson())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static int ExitCode(IReadOnlyDictionary<string, string> result) => int.Parse(result.GetValueOrDefault("ExitCode", "0"));
    private static string Error(IReadOnlyDictionary<string, string> result) => result.GetValueOrDefault("Error", string.Empty);

    private static Dictionary<string, string> Parse(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('=', 2)).Where(pair => pair.Length == 2)
        .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private sealed class JsonObjectWrapper
    {
        private readonly Dictionary<string, object?> _values;
        public JsonObjectWrapper(JsonElement element) => _values = element.EnumerateObject().ToDictionary(property => property.Name, property => JsonSerializer.Deserialize<object>(property.Value.GetRawText()), StringComparer.Ordinal);
        public object? this[string key] { set => _values[key] = value; }
        public string ToJson() => JsonSerializer.Serialize(_values);
    }
}
