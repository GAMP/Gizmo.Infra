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
        var binding = Invoke("validate --role publishable --expect-publisher nuget", Encode(payload),
            new Dictionary<string, string> { ["GITHUB_REPOSITORY"] = "owner/repository" });
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

        var wrongSha = new Dictionary<string, string>(StringComparer.Ordinal) { ["GITHUB_SHA"] = OtherSha };
        var bound = Invoke("validate --role production --expect-publisher publishable", state, wrongSha);
        Assert.NotEqual(0, bound.ExitCode);
        Assert.Contains("binding-mismatch", bound.Error, StringComparison.Ordinal);
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
        var reference = role == "production" ? "refs/heads/release" : "refs/heads/pre-release";
        var request = new
        {
            repo = "owner/repository", sha = currentSha ?? Sha, @ref = reference, run = "700", attempt = "1",
            @event = eventName, dev = "pre-release", prod = "release", role, packageId = PackageId,
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

    private static string Seal(string planned) => Invoke("seal --digest " + new string('d', 64), planned).Output
        .Split('=', 2)[1].Trim();

    private static void AssertValid(string state, string role, string publisher) =>
        Assert.Equal(0, Invoke($"validate --role {role} --expect-publisher {publisher}", state).ExitCode);

    private static void AssertInvalid(string state, string expected) => AssertInvalid(state, "publishable", "nuget", expected);

    private static void AssertInvalid(string state, string role, string publisher, string expected)
    {
        var result = Invoke($"validate --role {role} --expect-publisher {publisher}", state);
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

    private static string StateField(string state, string key)
    {
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(state)));
        var value = json.RootElement.GetProperty(key);
        return value.ValueKind == JsonValueKind.Array ? value[0].GetString()! : value.GetString()!;
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
