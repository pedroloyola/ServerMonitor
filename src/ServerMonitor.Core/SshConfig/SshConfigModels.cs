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
    NotRelevant,

    // An Include that could not be verified (see SshConfigIncludeIssue).
    IncludeNotFollowed,
    IncludeOutsideSshDirectory,
    IncludeReparsePoint,
    IncludeNotRegularFile,
    IncludeUnsupportedPattern,
    IncludeUnsupportedExpansion,
    IncludeMissingArgument,
    IncludeFinalPathMismatch,
    IncludeHardLinked
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

    /// <summary>Why the host cannot be imported, if it cannot.</summary>
    public SshConfigHostBlocker Blocker { get; init; }

    /// <summary>
    /// The single jump host the effective <c>ProxyJump</c> routes through. Only ever set on an
    /// importable entry; an importable entry without it is a direct connection.
    /// </summary>
    public SshConfigJumpHost? Jump { get; init; }

    public bool IsImportable => Blocker == SshConfigHostBlocker.None;
}

/// <summary>
/// A single-hop <c>ProxyJump</c> resolved as ssh would: the jump's own destination is looked up in the
/// same config, and an explicit <c>user@</c>/<c>:port</c> in the <c>ProxyJump</c> value wins over it.
/// </summary>
public sealed record SshConfigJumpHost
{
    /// <summary>The host as written in the <c>ProxyJump</c> value (the name shown as "via ...").</summary>
    public required string Name { get; init; }

    /// <summary>The address ssh would connect to for the jump: its <c>HostName</c>, or <see cref="Name"/>.</summary>
    public required string HostName { get; init; }

    /// <summary>The explicit <c>user@</c>, else the jump's config <c>User</c>; null leaves it for the user.</summary>
    public string? User { get; init; }

    /// <summary>The explicit <c>:port</c>, else the jump's config <c>Port</c>; null means ssh's default (22).</summary>
    public int? Port { get; init; }

    /// <summary>The jump's single usable key path (a path only; the file is never opened).</summary>
    public string? IdentityFile { get; init; }

    /// <summary>How the jump's own config keywords were classified.</summary>
    public IReadOnlyList<SshConfigFinding> Findings { get; init; } = [];

    public bool FindingsTruncated { get; init; }
}

/// <summary>
/// Reasons a host is listed but never imported: it needs, or may need, a route that ServerAlyzer
/// does not support or cannot verify, and importing it (least of all as a direct connection) would be wrong.
/// </summary>
public enum SshConfigHostBlocker
{
    None,

    /// <summary>The effective <c>ProxyCommand</c> of the host or of its jump host is set (or malformed).</summary>
    ProxyCommand,

    /// <summary>The <c>ProxyJump</c> is a comma list, or the jump host itself has a <c>ProxyJump</c> (chained).</summary>
    JumpMultiHop,

    /// <summary>The <c>ProxyJump</c> value is empty, malformed, a <c>ssh://</c> URI, has tokens, or is otherwise not verified.</summary>
    JumpUnparsable,

    /// <summary>The jump host is the host itself, reaches the same endpoint, or jumps back (A→B→A).</summary>
    JumpCycle,

    /// <summary>The jump host's own <c>HostName</c> cannot be resolved exactly (tokens or an invalid value).</summary>
    JumpHostNameUnresolved,

    /// <summary>An unevaluated <c>Match</c> block may set a proxy (or, for the jump host, its <c>HostName</c>) before it is decided.</summary>
    ProxyMaySetByMatch,

    /// <summary>An <c>Include</c> that could not be verified may set a proxy (or, for the jump host, its <c>HostName</c>) before it is decided.</summary>
    ProxyMaySetByInclude
}

public enum SshConfigFileWarning
{
    /// <summary>At least one <c>Include</c> could not be verified and was not followed, so values may be incomplete.</summary>
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
    InvalidSyntax,

    /// <summary>
    /// Over an Include budget: 64 files, 256 matches per argument, 256 Include arguments, 64 enumerated
    /// directories, 4096 entries in one directory or 16384 listed entries in total.
    /// </summary>
    TooManyFiles,

    /// <summary>A file includes itself, directly or through other files.</summary>
    IncludeCycle,

    /// <summary>Includes nested deeper than OpenSSH allows (16).</summary>
    IncludeTooDeep,

    /// <summary>
    /// <c>~/.ssh</c> or <c>~/.ssh/config</c> is a symlink, junction or other reparse point (never opened), or the
    /// opened config is not provably that plain file (its final path differs, or it is hard-linked).
    /// </summary>
    ConfigIsLink
}

public enum SshConfigDiagnosticKind
{
    /// <summary>A missing file or a glob with no matches: OpenSSH silently contributes nothing.</summary>
    IncludeMatchedNoFiles,

    /// <summary>An Include (or one of its matches) could not be verified and was not followed.</summary>
    IncludeNotVerified
}

/// <summary>An informational note about <c>Include</c>, with the argument as written.</summary>
public sealed record SshConfigDiagnostic(
    SshConfigDiagnosticKind Kind,
    string Argument,
    SshConfigIncludeIssue Issue = SshConfigIncludeIssue.None);

public sealed record SshConfigImportResult
{
    public required SshConfigImportStatus Status { get; init; }

    public SshConfigImportErrorCode ErrorCode { get; init; } = SshConfigImportErrorCode.None;

    public IReadOnlyList<SshConfigHostEntry> Hosts { get; init; } = [];

    public IReadOnlyList<SshConfigFileWarning> FileWarnings { get; init; } = [];

    public IReadOnlyList<SshConfigDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The file an error is about, when it is not <c>~/.ssh/config</c> itself.</summary>
    public string? ErrorDetail { get; init; }

    public static SshConfigImportResult NotFound { get; } = new() { Status = SshConfigImportStatus.NotFound };

    public static SshConfigImportResult Failed(SshConfigImportErrorCode errorCode) =>
        new() { Status = SshConfigImportStatus.Error, ErrorCode = errorCode };
}
