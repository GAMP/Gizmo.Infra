using Gizmo.Infra.Tests.TestSupport;

namespace Gizmo.Infra.Tests.GitHub;

/// <summary>
/// Fails when an inline interpreter program reappears in a production composite action, its checked-in modules, or the executable test helpers; inline programs are opaque to review and endpoint scanning, so a named module is required instead.
/// </summary>
public sealed class InlineInterpreterContractTests
{
    // Production action YAML plus its modules, and the test helpers that stand in for a runner.
    private static readonly string[] ScanRoots =
    [
        Path.Combine(".github", "actions"),
        Path.Combine("tests", "TestSupport"),
        Path.Combine("tests", "GitHub", "fixtures"),
    ];

    // Inline evaluation and heredoc forms execute an opaque anonymous program, so a named module is required.
    private static readonly string[] ForbiddenTokens =
    [
        "node -e",
        "node --eval",
        "python -c",
        "python3 -c",
        "python - <<",
        "python3 - <<",
    ];

    [Fact]
    public void NoProductionActionModuleOrTestHelper_EmbedsAnInlineInterpreterProgram()
    {
        var root = InfraRepositoryLocator.ResolveRoot();
        var offenders = new List<string>();

        foreach (var scanRoot in ScanRoots)
        {
            var directory = Path.Combine(root, scanRoot);
            Assert.True(Directory.Exists(directory), $"expected scan root '{scanRoot}' to exist.");

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                if (ForbiddenTokens.Any(token => content.Contains(token, StringComparison.Ordinal)))
                {
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "inline interpreter programs are forbidden; use a checked-in named module in: "
            + string.Join(", ", offenders));
    }
}
