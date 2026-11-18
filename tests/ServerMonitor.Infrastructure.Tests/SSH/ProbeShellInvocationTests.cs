using System.Diagnostics;
using System.Text;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M16.1: a probe script runs as ONE exec of the constant "sh -s", with the script on standard input, so the login
/// shell (fish, csh...) never parses it and nothing in it reaches the command line.
/// </summary>
public sealed class ProbeShellInvocationTests
{
    private const string Script =
        "if [ -n \"$HOME\" ]; then\n  echo 'home set'\nfi\nfor n in 1 2 3; do\n  echo \"n=$n\"\ndone\necho \"@@rc=$?\"";

    [Fact]
    public void The_exec_request_is_the_constant_sh_dash_s()
    {
        Assert.Equal("sh -s", ProbeShellInvocation.CommandText);
    }

    [Fact]
    public void The_script_goes_verbatim_to_standard_input_with_one_final_lf()
    {
        var input = ProbeShellInvocation.StandardInput(Script);

        Assert.Equal(Script + "\n", Encoding.UTF8.GetString(input));
        Assert.Equal(Script + "\n", Encoding.UTF8.GetString(ProbeShellInvocation.StandardInput(Script + "\n")));
        Assert.DoesNotContain("'", ProbeShellInvocation.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_runs_probes_through_the_invocation_and_never_puts_the_script_on_the_command_line()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.Infrastructure", "SSH", "SshNetSession.cs"));

        Assert.Contains("ProbeShellInvocation.CommandText,", source, StringComparison.Ordinal);
        Assert.Contains("ProbeShellInvocation.StandardInput(request.Command)", source, StringComparison.Ordinal);
        Assert.Contains("command.CreateInputStream()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCommand(request.Command", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_multi_line_posix_script_on_stdin_runs_under_a_real_sh()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return; // Needs a POSIX sh; CI on Windows covers the pure parts above.
        }

        var start = new ProcessStartInfo("/bin/sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        await process.StandardInput.BaseStream.WriteAsync(ProbeShellInvocation.StandardInput(Script));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("home set\nn=1\nn=2\nn=3\n@@rc=0\n", output);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }
}
