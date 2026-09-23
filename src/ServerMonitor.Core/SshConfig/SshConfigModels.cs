namespace ServerMonitor.Core.SshConfig;

/// <summary>How a keyword found in the user's <c>~/.ssh/config</c> was treated by the import.</summary>
public enum SshConfigFindingKind
{
    /// <summary>The keyword changes how ssh connects and ServerAlyzer does not reproduce it.</summary>
    Unsupported,

    /// <summary>ssh would use several values; the import will not pick one on the user's behalf.</summary>
    Ambiguous,

    /// <summary>The value is malformed; ssh itself would reject it.</summary>
    Invalid,

    /// <summary>The keyword does not affect the host, user, port or key, so it is not relevant here.</summary>
    Ignored
}

public enum SshConfigFindingReason
{
    UnsupportedKeyword,
    TokenExpansion,
    EnvironmentExpansion,
    RelativePath,
    OtherUserHome,
    MatchNotEvaluated,
    MultipleValues,
    InvalidValue,
    NotRelevant
}

public sealed record SshConfigFinding(
    string Keyword,
    SshConfigFindingKind Kind,
    SshConfigFindingReason Reason);

/// <summary>
/// The effective subset of ssh_config(5) for one concrete alias. Values that could not be
/// resolved exactly are <see langword="null"/> and explained by a <see cref="Findings"/> entry;
/// nothing here is approximated.
/// </summary>
public sealed record SshConfigHostEntry
{
    public required string Alias { get; init; }

    /// <summary>The address ssh would connect to: <c>HostName</c>, or the alias when absent.</summary>
    public string? HostName { get; init; }

    public string? User { get; init; }

    public int? Port { get; init; }

    /// <summary>The single usable key path (a path only; the file is never opened).</summary>
    public string? IdentityFile { get; init; }

    /// <summary>Every <c>IdentityFile</c> value ssh would try, as written in the file.</summary>
    public IReadOnlyList<string> IdentityFileValues { get; init; } = [];

    public IReadOnlyList<SshConfigFinding> Findings { get; init; } = [];

    /// <summary>More findings existed than are listed (see <see cref="SshConfigResolver.MaxFindingsPerHost"/>).</summary>
    public bool FindingsTruncated { get; init; }

    /// <summary>Why the host cannot be imported as a direct connection, if it cannot.</summary>
    public SshConfigHostBlocker Blocker { get; init; }

    public bool IsImportable => Blocker == SshConfigHostBlocker.None;
}

/// <summary>
/// Reasons a host is listed but never imported: it needs, or may need, a proxy that
/// ServerAlyzer does not support, and importing it as a direct connection would be wrong.
/// </summary>
public enum SshConfigHostBlocker
{
    None,

    /// <summary>The effective <c>ProxyJump</c> is set (or malformed).</summary>
    ProxyJump,

    /// <summary>The effective <c>ProxyCommand</c> is set (or malformed).</summary>
    ProxyCommand,

    /// <summary>An unevaluated <c>Match</c> block may set a proxy before one is decided.</summary>
    ProxyMaySetByMatch,

    /// <summary>An unfollowed <c>Include</c> may set a proxy before one is decided.</summary>
    ProxyMaySetByInclude
}

public enum SshConfigFileWarning
{
    /// <summary><c>Include</c> is not followed, so values may be incomplete.</summary>
    IncludeNotFollowed,

    /// <summary><c>Match</c> blocks are not evaluated, so values after one may be inexact.</summary>
    MatchNotEvaluated,

    /// <summary>Only the first <see cref="SshConfigResolver.MaxHosts"/> concrete aliases are listed.</summary>
    HostsTruncated
}

public enum SshConfigImportStatus
{
    /// <summary>There is no <c>~/.ssh/config</c>. Not an error.</summary>
    NotFound,

    Loaded,

    Error
}

public enum SshConfigImportErrorCode
{
    None,
    TooLarge,
    Unreadable,
    InvalidEncoding,

    /// <summary>A malformed <c>Host</c>/<c>Match</c> line or a quoted keyword: the file is not trusted.</summary>
    InvalidSyntax
}

public sealed record SshConfigImportResult
{
    public required SshConfigImportStatus Status { get; init; }

    public SshConfigImportErrorCode ErrorCode { get; init; } = SshConfigImportErrorCode.None;

    public IReadOnlyList<SshConfigHostEntry> Hosts { get; init; } = [];

    public IReadOnlyList<SshConfigFileWarning> FileWarnings { get; init; } = [];

    public static SshConfigImportResult NotFound { get; } = new() { Status = SshConfigImportStatus.NotFound };

    public static SshConfigImportResult Failed(SshConfigImportErrorCode errorCode) =>
        new() { Status = SshConfigImportStatus.Error, ErrorCode = errorCode };
}
