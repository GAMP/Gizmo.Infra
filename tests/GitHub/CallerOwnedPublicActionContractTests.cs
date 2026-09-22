using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;
using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class CallerOwnedPublicActionContractTests
{
    private static readonly string[] ActionDirectories =
    [
        "package-development-prepare",
        "package-development-public",
        "package-release-prepare",
        "package-release-public",
        "package-release-tag",
    ];

    private static string ActionPath(string directory) =>
        Path.Combine(
            InfraRepositoryLocator.ResolveRoot(),
            ".github",
            "actions",
            directory,
            "action.yml");

    private static string Read(string directory) => File.ReadAllText(ActionPath(directory));

    [Fact]
    public void EveryPilotAction_ExistsAndParsesAsYaml()
    {
        foreach (var directory in ActionDirectories)
        {
            var content = Read(directory);
            var yaml = new YamlStream();
            using var reader = new StringReader(content);
            yaml.Load(reader);
            Assert.Single(yaml.Documents);
        }
    }

    [Fact]
    public void CompositeActions_DoNotReachIntoCallerNeedsContext()
    {
        foreach (var directory in ActionDirectories)
        {
            Assert.DoesNotContain("${{ needs.", Read(directory), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryNestedActionReference_IsPinnedToAFullSha()
    {
        var usesLine = new Regex(
            @"^\s*uses:\s+([^\s@]+)@([0-9a-f]{40})\s+#\s+v[0-9][^\s]*\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        foreach (var directory in ActionDirectories)
        {
            var content = Read(directory);
            var declared = Regex.Matches(content, @"^\s*uses:", RegexOptions.Multiline);
            Assert.Equal(declared.Count, usesLine.Matches(content).Count);
            Assert.DoesNotContain("@main", content, StringComparison.Ordinal);
            Assert.DoesNotContain("@master", content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("package-development-prepare", "-dev.${GITHUB_RUN_NUMBER}")]
    [InlineData("package-release-prepare", "release_tag=\"${PACKAGE_ID}/v${base_version}\"")]
    public void PrepareActions_PreserveVersionAndArtifactLogic(string directory, string expected)
    {
        var content = Read(directory);

        Assert.Contains("Calculate ", content, StringComparison.Ordinal);
        Assert.Contains(expected, content, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", content, StringComparison.Ordinal);
        Assert.Contains("dotnet build", content, StringComparison.Ordinal);
        Assert.Contains("dotnet pack", content, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@", content, StringComparison.Ordinal);
        Assert.Contains("tag-state-fingerprint", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN_REQUEST_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("package-development-public")]
    [InlineData("package-release-public")]
    public void PublicPublishActions_AreCallerOwnedOidcPublishers(string directory)
    {
        var content = Read(directory);

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("refs/heads/", content, StringComparison.Ordinal);
        Assert.Contains("github.event_name", content, StringComparison.Ordinal);
        Assert.Contains("ACTIONS_ID_TOKEN_REQUEST_TOKEN", content, StringComparison.Ordinal);
        Assert.Contains("audience=https%3A%2F%2Fwww.nuget.org", content, StringComparison.Ordinal);
        Assert.Contains("https://www.nuget.org/api/v2/token", content, StringComparison.Ordinal);
        Assert.Contains("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.Contains("Calculated package state drifted before publication", content, StringComparison.Ordinal);
        Assert.DoesNotContain("NUGET_API_KEY", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseTagAction_IsOidcFreeAndNeverMovesATag()
    {
        var content = Read("package-release-tag");

        Assert.Contains("github.ref_protected", content, StringComparison.Ordinal);
        Assert.Contains("Refetch and recheck calculated state before tagging", content, StringComparison.Ordinal);
        Assert.Contains("Create or reconcile immutable release tag", content, StringComparison.Ordinal);
        Assert.Contains("refs/tags/$RELEASE_TAG", content, StringComparison.Ordinal);
        Assert.Contains("--request POST", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIONS_ID_TOKEN_REQUEST_TOKEN", content, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet nuget push", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--request PATCH", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--request DELETE", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("force", content, StringComparison.OrdinalIgnoreCase);
    }
}
