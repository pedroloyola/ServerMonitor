using System.Text;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// How one remote probe script is run: ONE exec of the constant <see cref="CommandText"/>, with the script fed on
/// standard input. The login shell only ever sees <c>sh -s</c>, which bash, zsh, dash, fish, csh and tcsh all run the
/// same way, so the script is always read by a POSIX <c>sh</c> and never by the user's own shell dialect. The script is
/// never part of the command line, so it needs no quoting and no character of it (a quote, a <c>$</c>) can escape.
/// </summary>
internal static class ProbeShellInvocation
{
    /// <summary>The exec request sent for every probe. Constant: nothing from the probe or the server goes in it.</summary>
    internal const string CommandText = "sh -s";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The bytes written to the exec's standard input: the script, ending in one LF so its last line runs.</summary>
    internal static byte[] StandardInput(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Utf8.GetBytes(script.EndsWith('\n') ? script : script + "\n");
    }
}
