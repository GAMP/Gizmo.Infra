namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// Isolated repository root for the MSBuild capture tests. Every instance owns a
/// unique directory and removes it on dispose, so tests never share file state.
/// </summary>
public sealed class TempRepository : IDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "gizmo-infra-tests");

    public TempRepository()
    {
        Root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string AbsolutePath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string WriteFile(string relativePath, string content)
    {
        var absolutePath = AbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
        return absolutePath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a locked temp directory must not fail the test run.
        }
    }
}
