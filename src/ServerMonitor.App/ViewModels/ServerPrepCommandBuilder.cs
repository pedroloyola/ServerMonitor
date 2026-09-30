using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ServerMonitor.App.ViewModels;

/// <summary>The two copyable commands of the "How do I prepare my server?" helper.</summary>
/// <param name="GenerateKey">Local PowerShell command that creates a key pair.</param>
/// <param name="CopyPublicKey">Local PowerShell command that appends the PUBLIC key to the server's authorized_keys.</param>
/// <param name="UsesPlaceholders">True when a form value was refused and a placeholder is shown in its place.</param>
public sealed record ServerPrepCommands(string GenerateKey, string CopyPublicKey, bool UsesPlaceholders);

/// <summary>
/// M14.5 — builds the helper's commands. The user, host, port and jump come from the form (and the host
/// possibly from an untrusted mDNS announcement) and end up in text the user pastes into PowerShell, so a
/// value is substituted ONLY when it matches a strict ASCII grammar; anything else becomes a placeholder.
/// No command ever uses sudo/root, and nothing here runs a command: it only produces text.
/// </summary>
public static class ServerPrepCommandBuilder
{
    public const string GenerateKeyCommand = "ssh-keygen -t ed25519";

    private const string DefaultPublicKeyFile = "id_ed25519.pub";
    // One PowerShell SINGLE-quoted token with no ' or " inside: PowerShell expands nothing in it and 5.1 has no
    // embedded quote to mangle. On the server it starts a new line first when authorized_keys does not end in
    // one (appending blindly would glue the new key to the last key and break both), and strips the CR that a
    // Windows pipe adds. The text carries two backslashes before r; the remote shell hands tr a single \r.
    internal const string RemoteScript =
        "'umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; "
        + "test ! -s ~/.ssh/authorized_keys || test $(tail -c1 ~/.ssh/authorized_keys | wc -l) -eq 1 "
        + "|| echo >> ~/.ssh/authorized_keys; tr -d \\\\r >> ~/.ssh/authorized_keys'";
    private const int MaxUserLength = 64;
    private const int MaxHostLength = 253;
    private const int MaxZoneLength = 16;

    // The only private-key names whose ".pub" sibling is named in the command (OpenSSH defaults).
    private static readonly string[] DefaultKeyFileNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    public static ServerPrepCommands Build(
        string? username,
        string? host,
        string? port,
        string? selectedKeyFileName,
        string userPlaceholder,
        string hostPlaceholder,
        bool useJumpHost = false,
        string? jumpUsername = null,
        string? jumpHost = null,
        string? jumpPort = null,
        string jumpPlaceholder = "")
    {
        var usesPlaceholders = false;

        string user;
        if (TryGetSafeUser(username, out var safeUser))
        {
            user = safeUser;
        }
        else
        {
            user = userPlaceholder;
            usesPlaceholders = true;
        }

        string target;
        if (TryGetSafeHost(host, out var safeHost, out _))
        {
            target = safeHost;
        }
        else
        {
            target = hostPlaceholder;
            usesPlaceholders = true;
        }

        var options = string.Empty;
        if (useJumpHost)
        {
            if (TryBuildJump(jumpUsername, jumpHost, jumpPort, out var jump))
            {
                options += "-J " + jump + " ";
            }
            else
            {
                options += "-J " + jumpPlaceholder + " ";
                usesPlaceholders = true;
            }
        }

        // An unusable port is left out (the form refuses it anyway); 22 is ssh's default.
        if (TryGetSafePort(port, out var safePort) && safePort != 22)
        {
            options += "-p " + safePort.ToString(CultureInfo.InvariantCulture) + " ";
        }

        var publicKeyFile = PublicKeyFileFor(selectedKeyFileName);
        var copy = $"type $env:USERPROFILE\\.ssh\\{publicKeyFile} | ssh {options}{user}@{target} {RemoteScript}";
        return new ServerPrepCommands(GenerateKeyCommand, copy, usesPlaceholders);
    }

    /// <summary>"id_x.pub" only for an exact OpenSSH default name; anything else falls back to the Ed25519 default.</summary>
    internal static string PublicKeyFileFor(string? selectedKeyFileName)
    {
        foreach (var name in DefaultKeyFileNames)
        {
            if (string.Equals(name, selectedKeyFileName, StringComparison.Ordinal))
            {
                return name + ".pub";
            }
        }

        return DefaultPublicKeyFile;
    }

    /// <summary>ASCII letters, digits, '.', '_' and '-'; never a leading '-' (it would read as an ssh option).</summary>
    internal static bool TryGetSafeUser(string? value, out string user)
    {
        user = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaxUserLength || value[0] == '-')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        user = value;
        return true;
    }

    /// <summary>
    /// A DNS name / IPv4 address (ASCII letters, digits, '.', '-', starting with a letter or digit) or an IPv6
    /// address, bracketed or bare, with an optional alphanumeric zone. IPv6 is returned bare: ssh takes
    /// <c>user@addr</c> without brackets.
    /// </summary>
    internal static bool TryGetSafeHost(string? value, out string host, out bool isIpv6)
    {
        host = string.Empty;
        isIpv6 = false;
        if (string.IsNullOrEmpty(value) || value.Length > MaxHostLength)
        {
            return false;
        }

        var candidate = value;
        var bracketed = candidate.Length > 2 && candidate[0] == '[' && candidate[^1] == ']';
        if (bracketed)
        {
            candidate = candidate[1..^1];
        }

        if (bracketed || candidate.Contains(':'))
        {
            if (!IsSafeIpv6(candidate))
            {
                return false;
            }

            host = candidate;
            isIpv6 = true;
            return true;
        }

        if (!IsAsciiLetterOrDigit(candidate[0]))
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not ('.' or '-'))
            {
                return false;
            }
        }

        host = candidate;
        return true;
    }

    internal static bool TryGetSafePort(string? value, out int port)
    {
        port = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 5)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        port = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535;
    }

    // ssh -J takes [user@]host[:port]. An IPv6 jump would need brackets there; it is shown as a placeholder instead.
    private static bool TryBuildJump(string? username, string? host, string? port, out string jump)
    {
        jump = string.Empty;
        if (!TryGetSafeUser(username, out var user)
            || !TryGetSafeHost(host, out var safeHost, out var isIpv6)
            || isIpv6
            || !TryGetSafePort(port, out var safePort))
        {
            return false;
        }

        jump = $"{user}@{safeHost}:{safePort.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    private static bool IsSafeIpv6(string candidate)
    {
        var address = candidate;
        var zoneIndex = candidate.IndexOf('%');
        if (zoneIndex >= 0)
        {
            var zone = candidate[(zoneIndex + 1)..];
            if (zone.Length is 0 or > MaxZoneLength)
            {
                return false;
            }

            foreach (var character in zone)
            {
                if (!IsAsciiLetterOrDigit(character))
                {
                    return false;
                }
            }

            address = candidate[..zoneIndex];
        }

        foreach (var character in address)
        {
            if (!IsAsciiHexDigit(character) && character is not (':' or '.'))
            {
                return false;
            }
        }

        return IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsAsciiHexDigit(char character) =>
        character is (>= 'a' and <= 'f') or (>= 'A' and <= 'F') or (>= '0' and <= '9');
}
