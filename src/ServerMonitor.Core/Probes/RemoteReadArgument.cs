namespace ServerMonitor.Core.Probes;

/// <summary>
/// The shape rule for the one argument of an <see cref="IRemoteRead"/>. Both rules allow only ASCII letters, digits and
/// a few safe punctuation characters, so a quote, a space, <c>$</c>, <c>;</c>, a backslash, a line break or any
/// non-ASCII character can never be in an argument, and it never starts with <c>-</c> or <c>.</c>.
/// </summary>
public static class RemoteReadArgument
{
    /// <summary>The longest systemd unit id accepted, in characters.</summary>
    public const int MaxSystemdUnitLength = 200;

    /// <summary>The exact length of a full container id.</summary>
    public const int ContainerIdLength = 64;

    private const string ServiceSuffix = ".service";

    /// <summary>
    /// True when <paramref name="argument"/> meets the rule of <paramref name="kind"/>:
    /// <list type="bullet">
    /// <item><see cref="RemoteReadArgumentKind.ContainerId"/>: exactly 64 lower-case hex characters.</item>
    /// <item><see cref="RemoteReadArgumentKind.SystemdUnit"/>: <c>[A-Za-z0-9@_:]</c> then <c>[A-Za-z0-9@._:-]</c>, at
    /// most 200 characters, ending in <c>.service</c>.</item>
    /// </list>
    /// </summary>
    public static bool IsValid(RemoteReadArgumentKind kind, string? argument) => kind switch
    {
        RemoteReadArgumentKind.ContainerId => IsContainerId(argument),
        RemoteReadArgumentKind.SystemdUnit => IsSystemdUnit(argument),
        _ => false,
    };

    private static bool IsContainerId(string? argument)
    {
        if (argument is null || argument.Length != ContainerIdLength)
        {
            return false;
        }

        foreach (var c in argument)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSystemdUnit(string? argument)
    {
        if (argument is null
            || argument.Length <= ServiceSuffix.Length
            || argument.Length > MaxSystemdUnitLength
            || !argument.EndsWith(ServiceSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < argument.Length; i++)
        {
            var c = argument[i];
            var allowed = c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9'
                          || c is '@' or '_' or ':'
                          || (i > 0 && c is '.' or '-');
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
