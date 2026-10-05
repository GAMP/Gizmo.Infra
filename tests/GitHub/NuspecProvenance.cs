using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executable fixture coverage for the checked-in public nuspec provenance parser; each case feeds raw metadata to the committed script and asserts the fail-closed verdict for decoys, non-nuspec namespaces, DTDs, entities, and malformed XML.
/// </summary>
public sealed class NuspecProvenanceParserTests
{
    private const string PackageId = "Gizmo.Widget";
    private const string PackageVersion = "1.0.14";
    private const string Namespace = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";

    private static readonly string Sha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);

    private static string ScriptPath => Path.Combine(
        InfraRepositoryLocator.ResolveRoot(),
        ".github",
        "actions",
        "public",
        "scripts",
        "nuspec_provenance.py");

    [Fact]
    public void ValidNamespacedMetadata_Accepts() =>
        AssertOk(Metadata(
            $"<id>{PackageId}</id><version>{PackageVersion}</version>"
            + $"<repository type=\"git\" url=\"https://github.com/owner/repository\" commit=\"{Sha}\" />"));

    [Fact]
    public void Utf8BomAndUppercaseCommit_Accepts()
    {
        var nuspec = "\uFEFF" + Metadata(
            $"<id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha.ToUpperInvariant()}\" />");
        AssertOk(nuspec);
    }

    [Fact]
    public void DivergentCommit_Rejects() =>
        AssertToken("commit", Metadata(
            $"<id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{OtherSha}\" />"));

    [Fact]
    public void WrongId_Rejects() =>
        AssertToken("id", Metadata(
            $"<id>Other.Widget</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" />"));

    [Fact]
    public void WrongVersion_Rejects() =>
        AssertToken("version", Metadata(
            $"<id>{PackageId}</id><version>9.9.9</version><repository commit=\"{Sha}\" />"));

    [Fact]
    public void NestedMatchingCommitDecoyWithDivergentRepository_FailsClosed() =>
        AssertToken("commit", Metadata(
            $"<id>{PackageId}</id><version>{PackageVersion}</version>"
            + $"<description>decoy <repository commit=\"{Sha}\" /></description>"
            + $"<repository commit=\"{OtherSha}\" />"));

    [Fact]
    public void NestedMatchingIdDecoyWithMismatchedMetadata_FailsClosed() =>
        AssertToken("id", Metadata(
            $"<id>Other.Widget</id><version>{PackageVersion}</version>"
            + $"<description>decoy <id>{PackageId}</id></description>"
            + $"<repository commit=\"{Sha}\" />"));

    [Theory]
    [InlineData("<id>Gizmo.Widget</id><version>1.0.14</version><repository type=\"git\" />")]
    [InlineData("<id>Gizmo.Widget</id><version>1.0.14</version><repository commit=\"not-a-sha\" />")]
    [InlineData("<id>Gizmo.Widget</id><id>Gizmo.Widget</id><version>1.0.14</version><repository commit=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" />")]
    [InlineData("<id>Gizmo.Widget</id><version>1.0.14</version><repository commit=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" /><repository commit=\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" />")]
    public void AmbiguousOrMissingMetadata_FailsClosed(string metadata) =>
        AssertToken("malformed", Metadata(metadata));

    [Fact]
    public void DuplicateMetadataElement_FailsClosed() =>
        AssertToken("malformed", Package(
            $"<metadata><id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" /></metadata>"
            + $"<metadata><id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" /></metadata>"));

    [Fact]
    public void MissingNuspecNamespace_FailsClosed() =>
        AssertToken("malformed",
            $"<package><metadata><id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" /></metadata></package>");

    [Fact]
    public void ForeignRootNamespace_FailsClosed() =>
        AssertToken("malformed",
            $"<package xmlns=\"http://evil.example/ns\"><metadata><id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" /></metadata></package>");

    [Fact]
    public void MalformedXml_FailsClosed() =>
        AssertToken("malformed", Metadata($"<id>{PackageId}</id><version>{PackageVersion}</version>"));

    [Fact]
    public void DtdDeclaration_FailsClosed() =>
        AssertToken("malformed",
            $"<?xml version=\"1.0\"?><!DOCTYPE package><package xmlns=\"{Namespace}\"><metadata>"
            + $"<id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" /></metadata></package>");

    [Fact]
    public void ExternalEntity_FailsClosed() =>
        AssertToken("malformed",
            "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + $"<package xmlns=\"{Namespace}\"><metadata><id>&xxe;</id><version>{PackageVersion}</version>"
            + $"<repository commit=\"{Sha}\" /></metadata></package>");

    [Fact]
    public void MissingExpectedIdentity_FailsClosed() =>
        AssertToken("malformed", Metadata(
            $"<id>{PackageId}</id><version>{PackageVersion}</version><repository commit=\"{Sha}\" />"), sha: "");

    private static void AssertOk(string nuspec)
    {
        var result = Run(nuspec);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StandardOutput);
    }

    private static void AssertToken(string expected, string nuspec, string? sha = null)
    {
        var result = Run(nuspec, sha);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput);
    }

    private static ShellResult Run(string nuspec, string? sha = null) =>
        WorkflowShell.RunPythonScript(
            ScriptPath,
            nuspec,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EXPECTED_PACKAGE_ID"] = PackageId,
                ["PACKAGE_VERSION"] = PackageVersion,
                ["GITHUB_SHA"] = sha ?? Sha,
            });

    private static string Package(string metadata) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"{Namespace}\">{metadata}</package>";

    private static string Metadata(string inner) => Package($"<metadata>{inner}</metadata>");
}
