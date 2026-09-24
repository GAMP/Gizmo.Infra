using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Executable coverage for the package-preflight MSBuild single-property
/// captures and the exact `&lt;Version&gt;3.X&lt;/Version&gt;` and
/// package-qualified stable-tag grammars. Each test runs the exact command or
/// pattern read from the committed action or workflow, so changing the source,
/// not just the wording of an assertion, fails the suite.
/// </summary>
public sealed class ProjectVersionCaptureTests
{
    private const string PreflightAction = "package-preflight";
    private const string ValidationFile = "package-validation.yml";
    private const string DevelopmentFile = "package-development.yml";
    private const string ReleaseFile = "package-release.yml";

    private static readonly string[] ContractFiles = [ValidationFile, DevelopmentFile, ReleaseFile];

    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>Gizmo.Widget.Test</PackageId>
            <Version>3.4</Version>
          </PropertyGroup>
        </Project>
        """;

    // Discovery decides candidacy from these two evaluated properties; the
    // re-evaluated single-property captures below must stay bare values.
    private const string SdkStyleProperty = "-getProperty:UsingMicrosoftNETSdk";
    private const string PackableProperty = "-getProperty:IsPackable";
    private const string PackageIdProperty = "-getProperty:PackageId";
    private const string VersionProperty = "-getProperty:Version";

    [Fact]
    public void SinglePropertyCapture_ReturnsABareValueWithoutANamePrefix()
    {
        using var repository = new TempRepository();
        var project = repository.WriteFile("Widget.csproj", ProjectXml);
        var action = WorkflowShell.ReadAction(PreflightAction);

        var packageId = WorkflowShell.RunSinglePropertyCapture(
            WorkflowShell.AssignedCommand(action, "package_id"), project);
        var compatibilityLine = WorkflowShell.RunSinglePropertyCapture(
            WorkflowShell.AssignedCommand(action, "compatibility_line"), project);

        Assert.Equal("Gizmo.Widget.Test", packageId);
        Assert.Equal("3.4", compatibilityLine);

        // A Name=Value parser would return "PackageId=Gizmo.Widget.Test" here and
        // produce blank package metadata downstream.
        Assert.DoesNotContain("=", packageId, StringComparison.Ordinal);
        Assert.DoesNotContain("=", compatibilityLine, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryEvaluatesSdkStyleAndPackableThroughMsbuild()
    {
        using var repository = new TempRepository();
        var project = repository.WriteFile("Widget.csproj", ProjectXml);
        var action = WorkflowShell.ReadAction(PreflightAction);

        // Both discovery guards run the committed MSBuild command against a real
        // SDK-style project, so a property that cannot be evaluated fails here.
        Assert.Equal(
            "true",
            WorkflowShell.RunSinglePropertyCapture(
                WorkflowShell.AssignedCommand(action, "is_sdk_style"), project));
        Assert.Equal(
            "true",
            WorkflowShell.RunSinglePropertyCapture(
                WorkflowShell.AssignedCommand(action, "is_packable"), project));

        Assert.Contains(SdkStyleProperty, action, StringComparison.Ordinal);
        Assert.Contains(PackableProperty, action, StringComparison.Ordinal);
        Assert.Contains(PackageIdProperty, action, StringComparison.Ordinal);
        Assert.Contains(VersionProperty, action, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityLineGrammar_RequiresExactlyThreeDotX()
    {
        var accepted = new[] { "3.0", "3.1", "3.42", "3.100" };
        var rejected = new[] { "3", "3.01", "4.0", "3.1.0", "3.x", "3.1-dev", " 3.1", "3.1 ", "03.1", "3.-1" };

        AssertPolicy(
            WorkflowShell.AcceptancePattern(
                WorkflowShell.ReadAction(PreflightAction), "compatibility_line"),
            accepted,
            rejected);
    }

    [Fact]
    public void TagGrammar_RequiresAPackageQualifiedStableThreeXTag()
    {
        var accepted = new[] { "v3.0.0", "v3.1.2", "v3.42.7" };
        var rejected = new[]
        {
            "v3.01.0", "v3.1.02", "v3.1", "v3.1.2-dev", "V3.1.2", "v3.1.2.4", "v3.1.2-", "3.1.2", "v3.x.2",
        };

        foreach (var file in ContractFiles)
        {
            AssertPolicy(
                WorkflowShell.AcceptancePattern(WorkflowShell.ReadWorkflow(file), "tag_leaf"),
                accepted,
                rejected);
        }
    }

    private static void AssertPolicy(Regex pattern, IReadOnlyList<string> accepted, IReadOnlyList<string> rejected)
    {
        foreach (var version in accepted)
        {
            Assert.True(pattern.IsMatch(version), $"Expected '{version}' to match /{pattern}/.");
        }

        foreach (var version in rejected)
        {
            Assert.False(pattern.IsMatch(version), $"Expected '{version}' not to match /{pattern}/.");
        }
    }
}
