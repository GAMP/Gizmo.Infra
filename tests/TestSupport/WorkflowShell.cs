using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// Reads and executes the shell fragments embedded in the committed reusable
/// workflows. The workflows only run on GitHub-hosted runners, so these helpers
/// exercise the exact command and pattern text locally without a shell.
/// </summary>
public static class WorkflowShell
{
    public static string ReadWorkflow(string fileName) =>
        File.ReadAllText(Path.Combine(
            InfraRepositoryLocator.ResolveRoot(), ".github", "workflows", fileName));

    /// <summary>
    /// Extracts the command inside a <c>variable=$(...)</c> capture, so an
    /// executable test runs the workflow's command text rather than a copy.
    /// </summary>
    public static string AssignedCommand(string content, string variable)
    {
        var match = Regex.Match(content, $@"^\s*{Regex.Escape(variable)}=\$\((?<command>[^\r\n]*)\)\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"workflow has no '{variable}=$(...)' capture.");
        return match.Groups["command"].Value;
    }

    /// <summary>
    /// Extracts the acceptance pattern from a <c>"$variable" =~ pattern ]]</c>
    /// guard, so tests exercise the workflow's own grammar instead of a copy.
    /// </summary>
    public static Regex AcceptancePattern(string content, string variable)
    {
        var match = Regex.Match(
            content,
            @"""\$" + Regex.Escape(variable) + @""" =~ (?<pattern>[^\s]+) \]\]",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"workflow has no '${variable}' acceptance pattern.");
        return new Regex(match.Groups["pattern"].Value, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Runs a captured <c>dotnet msbuild</c> command with the caller project path
    /// substituted for the workflow's <c>$PROJECT_PATH</c> variable.
    /// </summary>
    public static string RunSinglePropertyCapture(string command, string projectPath)
    {
        var tokens = Regex.Matches(command, @"""[^""]*""|\S+")
            .Select(match => match.Value.Trim('"'))
            .ToArray();
        Assert.NotEmpty(tokens);

        var startInfo = new ProcessStartInfo(tokens[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var token in tokens.Skip(1))
        {
            startInfo.ArgumentList.Add(token == "$PROJECT_PATH" ? projectPath : token);
        }

        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException($"Could not start '{tokens[0]}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Capture command failed ({process.ExitCode}): {stderr}");
        return stdout.Trim();
    }
}
