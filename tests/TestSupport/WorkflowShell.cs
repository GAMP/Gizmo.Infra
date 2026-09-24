using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// Captured exit code and trimmed output from a helper process.
/// </summary>
public sealed record ShellResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Reads and executes the shell fragments embedded in the committed reusable
/// workflows and the bundled composite action. The workflows only run on
/// GitHub-hosted runners, so these helpers exercise the exact command, bash, and
/// Node program text locally without GitHub.
/// </summary>
public static class WorkflowShell
{
    private static readonly Lazy<string> BashExecutable = new(FindBash);
    public static string ReadWorkflow(string fileName) =>
        File.ReadAllText(Path.Combine(
            InfraRepositoryLocator.ResolveRoot(), ".github", "workflows", fileName));

    public static string ReadAction(string directory) =>
        File.ReadAllText(Path.Combine(
            InfraRepositoryLocator.ResolveRoot(), ".github", "actions", directory, "action.yml"));

    /// <summary>
    /// Extracts the command inside a <c>variable=$(...)</c> capture, so an
    /// executable test runs the workflow's or action's command text rather than a
    /// copy. The capture may be guarded or followed by a failure branch.
    /// </summary>
    public static string AssignedCommand(string content, string variable)
    {
        var match = Regex.Match(
            content,
            $@"{Regex.Escape(variable)}=\$\((?<command>[^\r\n]*)\)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"source has no '{variable}=$(...)' capture.");
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
    /// substituted for whichever project-path shell variable the source declares.
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
            startInfo.ArgumentList.Add(token is "$PROJECT_PATH" or "$project" or "$project_path"
                ? projectPath
                : token);
        }

        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException($"Could not start '{tokens[0]}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Capture command failed ({process.ExitCode}): {stderr}");
        return stdout.Trim();
    }

    /// <summary>
    /// Extracts the contiguous source region from <paramref name="startMarker"/>
    /// through the first following <paramref name="endMarker"/> shell token, so an
    /// executable test runs the committed block instead of a hand copy.
    /// </summary>
    public static string ExtractBlock(string content, string startMarker, string endMarker)
    {
        Assert.True(endMarker.Length > 0, "endMarker must not be empty.");
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"source has no '{startMarker}' marker.");
        var end = FindTokenEnd(content, start + startMarker.Length, endMarker);
        Assert.True(end >= 0, $"source has no '{endMarker}' token after '{startMarker}'.");
        return content[start..(end + endMarker.Length)];
    }

    /// <summary>
    /// Finds the first <paramref name="endMarker"/> occurrence that is a standalone
    /// shell token. A plain substring search would close an <c>if</c> block at the
    /// <c>fi</c> inside <c>response_file</c> and yield truncated, unparsable shell.
    /// </summary>
    private static int FindTokenEnd(string content, int start, string endMarker)
    {
        for (var search = start; search <= content.Length - endMarker.Length;)
        {
            var candidate = content.IndexOf(endMarker, search, StringComparison.Ordinal);
            if (candidate < 0)
            {
                return -1;
            }

            if (IsTokenDelimited(content, candidate, endMarker))
            {
                return candidate;
            }

            search = candidate + 1;
        }

        return -1;
    }

    private static bool IsTokenDelimited(string content, int index, string marker)
    {
        if (IsShellWordCharacter(marker[0]) && index > 0 && IsShellWordCharacter(content[index - 1]))
        {
            return false;
        }

        var after = index + marker.Length;
        return !IsShellWordCharacter(marker[^1])
            || after >= content.Length
            || !IsShellWordCharacter(content[after]);
    }

    private static bool IsShellWordCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '_';

    /// <summary>
    /// Runs a bash script against a real working tree. GitHub-hosted runners use
    /// GNU coreutils, so the harness forces that implementation ahead of any
    /// non-GNU <c>find.exe</c> a Windows PATH may expose first.
    /// </summary>
    public static ShellResult RunBash(
        string script,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(BashExecutable.Value)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // GitHub feeds the run script to bash on stdin; doing the same avoids
        // Windows command-line quoting entirely for multi-line extracted blocks.
        startInfo.ArgumentList.Add("-s");
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return Execute(startInfo, "export PATH=\"/usr/bin:/bin:$PATH\"\n" + script);
    }

    /// <summary>
    /// Runs the exact multi-line <c>node -e</c> program the action embeds, with
    /// <paramref name="argument"/> available as <c>process.argv[1]</c>. The
    /// program is loaded from a file so a multi-line program never has to survive
    /// Windows command-line quoting.
    /// </summary>
    public static ShellResult RunNode(string program, string argument)
    {
        var programDirectory = Directory.CreateTempSubdirectory("gizmo-infra-node-");
        var programPath = Path.Combine(programDirectory.FullName, "program.js");
        File.WriteAllText(programPath, program);
        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("require(process.env.GIZMO_INFRA_PREFLIGHT_PROGRAM)");
            startInfo.ArgumentList.Add(argument);
            startInfo.Environment["GIZMO_INFRA_PREFLIGHT_PROGRAM"] = programPath;

            try
            {
                return Execute(startInfo);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new Xunit.Sdk.XunitException("node is required to exercise the embedded preflight config parser.");
            }
        }
        finally
        {
            try
            {
                programDirectory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: a locked temp directory must not fail the test.
            }
        }
    }

    private static ShellResult Execute(ProcessStartInfo startInfo, string? standardInput = null)
    {
        using var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException($"Could not start '{startInfo.FileName}'.");
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
        }

        // Closing stdin gives EOF to any command that unexpectedly reads it, so a
        // misparsed invocation fails instead of blocking the run forever.
        if (startInfo.RedirectStandardInput)
        {
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ShellResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static string FindBash()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                candidates.Add(Path.Combine(root, "Git", "usr", "bin", "bash.exe"));
                candidates.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
                candidates.Add(Path.Combine(root, "Programs", "Git", "usr", "bin", "bash.exe"));
            }

            candidates.Add("bash.exe");
            candidates.Add("bash");
        }
        else
        {
            candidates.Add("/bin/bash");
            candidates.Add("/usr/bin/bash");
            candidates.Add("bash");
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsWorkingBash(candidate))
            {
                return candidate;
            }
        }

        throw new Xunit.Sdk.XunitException(
            "No working bash was found; the preflight execution tests require Git Bash or /bin/bash.");
    }

    private static bool IsWorkingBash(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf gizmo-bash-ok");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && stdout == "gizmo-bash-ok";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}
