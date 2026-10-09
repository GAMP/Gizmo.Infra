using System.Text.RegularExpressions;
using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

public sealed class ProjectVersionCaptureTests
{
    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Gizmo.Widget.Test</PackageId><Version>3.4</Version></PropertyGroup></Project>
        """;

    [Fact]
    public void Preflight_CapturesBareMsbuildPackageMetadata()
    {
        using var repository = new TempRepository();
        var project = repository.WriteFile("Widget.csproj", ProjectXml);
        var action = WorkflowShell.ReadAction("preflight");
        Assert.Equal("Gizmo.Widget.Test", WorkflowShell.RunSinglePropertyCapture(WorkflowShell.AssignedCommand(action, "package_id"), project));
        Assert.Equal("3.4", WorkflowShell.RunSinglePropertyCapture(WorkflowShell.AssignedCommand(action, "compatibility_line"), project));
        Assert.Contains("-getProperty:UsingMicrosoftNETSdk", action, StringComparison.Ordinal);
        Assert.Contains("-getProperty:IsPackable", action, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityLineGrammar_RejectsNonCanonicalProjectVersion()
    {
        var pattern = WorkflowShell.AcceptancePattern(WorkflowShell.ReadAction("preflight"), "compatibility_line");
        Assert.All(new[] { "1.0", "3.4", "10.2" }, value => Assert.Matches(pattern, value));
        Assert.All(new[] { "0.1", "03.1", "3.01", "3.1.0", "3.x", "3.1-dev", "" }, value => Assert.DoesNotMatch(pattern, value));

        var state = File.ReadAllText(Path.Combine(InfraRepositoryLocator.ResolveRoot(), ".github", "package", "state.py"));
        Assert.Contains("TAG_LEAF = re.compile", state, StringComparison.Ordinal);
        Assert.Contains("^v(", state, StringComparison.Ordinal);
    }
}
