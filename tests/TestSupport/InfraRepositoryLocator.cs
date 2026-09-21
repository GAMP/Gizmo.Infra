namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// Finds the local Gizmo.Infra repository root so tests can assert on committed
/// documentation the tool ships with. <see cref="EnvironmentVariable"/> overrides
/// the walk-up default for other layouts.
/// </summary>
public static class InfraRepositoryLocator
{
    public const string EnvironmentVariable = "GIZMO_INFRA_ROOT";

    private const string SolutionFile = "Gizmo.Infra.sln";

    public static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, SolutionFile)))
        {
            return Path.GetFullPath(configured);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not locate the Gizmo.Infra repository (expected {SolutionFile}). "
            + $"Set {EnvironmentVariable} to the repository root.");
    }

    public static string DocsPath(string root, string fileName) => Path.Combine(root, "docs", fileName);
}
