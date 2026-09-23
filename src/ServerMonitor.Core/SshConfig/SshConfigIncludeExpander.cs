using System.Text;

namespace ServerMonitor.Core.SshConfig;

/// <summary>Why an <c>Include</c> (or one file it matched) was not followed. Any of these is "cannot verify".</summary>
public enum SshConfigIncludeIssue
{
    None,

    /// <summary>No file system was available (text-only import).</summary>
    NotFollowed,

    /// <summary>The path resolves outside <c>~/.ssh</c>, or is not a plain local path (UNC, device, stream, other drive).</summary>
    OutsideSshDirectory,

    /// <summary>The file, or a directory from <c>~/.ssh</c> down to it, is a symlink/junction/reparse point.</summary>
    ReparsePoint,

    /// <summary>The match is a directory, device or anything else that is not a regular file.</summary>
    NotRegularFile,

    /// <summary>Glob syntax other than <c>*</c>/<c>?</c> in the last segment.</summary>
    UnsupportedPattern,

    /// <summary><c>%</c> tokens, <c>${ENV}</c> or <c>~user</c>.</summary>
    UnsupportedExpansion,

    /// <summary><c>Include</c> without a usable argument.</summary>
    MissingArgument,

    /// <summary>
    /// The opened file is not provably the checked one: its final path (after the open) differs, is on
    /// a network path, or could not be read.
    /// </summary>
    FinalPathMismatch,

    /// <summary>The file has more than one hard link, so its content may also live outside <c>~/.ssh</c>.</summary>
    HardLinked
}

/// <summary>
/// The block context a line belongs to. <see cref="IncludeParent"/> is the scope of the
/// <c>Include</c> line that brought this file in (null in the root file): a line applies only if
/// every scope up that chain applies, which is exactly OpenSSH's inherited <c>activep</c> plus
/// <c>SSHCONF_NEVERMATCH</c>.
/// </summary>
public sealed class SshConfigScope
{
    internal SshConfigScope(SshConfigBlockKind kind, IReadOnlyList<string> patterns, SshConfigScope? includeParent)
    {
        Kind = kind;
        Patterns = patterns;
        IncludeParent = includeParent;
    }

    public SshConfigBlockKind Kind { get; }

    public IReadOnlyList<string> Patterns { get; }

    public SshConfigScope? IncludeParent { get; }
}

/// <param name="IncludeIssue">Not <see cref="SshConfigIncludeIssue.None"/> for an <c>Include</c> that could not be verified.</param>
public sealed record SshConfigSplicedDirective(
    SshConfigDirective Directive,
    string SourcePath,
    SshConfigIncludeIssue IncludeIssue = SshConfigIncludeIssue.None);

public sealed record SshConfigSegment(SshConfigScope Scope, IReadOnlyList<SshConfigSplicedDirective> Directives);

/// <summary>The root file with every followed <c>Include</c> spliced in place, in exact OpenSSH order.</summary>
public sealed record SshConfigSplicedDocument(
    IReadOnlyList<SshConfigSegment> Segments,
    IReadOnlyList<SshConfigScope> HostScopes,
    bool HasMatch,
    IReadOnlyList<SshConfigDiagnostic> Diagnostics);

public sealed record SshConfigExpansion(SshConfigSplicedDocument? Document, SshConfigImportResult? Failure);

/// <summary>
/// Follows <c>Include</c> read-only and only inside <c>~/.ssh</c>, mirroring OpenSSH readconf.c for
/// the user config: arguments left to right, each glob-expanded and processed in ordinal order;
/// included lines are spliced at the <c>Include</c>; an included file starts in the enclosing
/// block's context; a <c>Host</c>/<c>Match</c> inside it can only apply if the <c>Include</c> line
/// itself applied; and the enclosing block resumes after the <c>Include</c>. Anything that cannot be
/// verified becomes an <see cref="SshConfigIncludeIssue"/> marker (the resolver blocks on it);
/// cycles, depth and size limits fail the whole import.
/// </summary>
public static class SshConfigIncludeExpander
{
    /// <summary>OpenSSH READCONF_MAX_DEPTH.</summary>
    public const int MaxDepth = 16;

    /// <summary>Files read in one import, the root config included.</summary>
    public const int MaxFiles = 64;

    public const int MaxTotalBytes = 1024 * 1024;

    public const int MaxFileBytes = 256 * 1024;

    public const int MaxMatchesPerArgument = 256;

    public const int MaxDirectoryEntries = 4096;

    public const int MaxDiagnostics = 32;

    /// <summary>Include arguments processed in one import (all files together).</summary>
    public const int MaxIncludeArguments = 256;

    /// <summary>Distinct directories enumerated in one import; each is enumerated at most once.</summary>
    public const int MaxEnumerations = 64;

    /// <summary>Directory entries listed in one import, all enumerations together.</summary>
    public const int MaxEnumeratedEntries = 16384;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Text-only expansion: <c>Include</c> is never followed and stays "cannot verify".</summary>
    public static SshConfigExpansion FromText(string text, CancellationToken cancellationToken = default)
    {
        var lines = SshConfigParser.ParseLines(text);
        if (lines.HasFatalSyntaxError)
        {
            return Fail(SshConfigImportErrorCode.InvalidSyntax, detail: null);
        }

        var state = new State(fileSystem: null, userProfile: string.Empty, sshDirectory: null, cancellationToken);
        state.Process(string.Empty, lines, includeParent: null, depth: 0);
        return state.Finish();
    }

    /// <summary>Reads <c>~/.ssh/config</c> through <paramref name="fileSystem"/> and follows its includes.</summary>
    /// <remarks>Cancellation is checked per line and per Include argument, and surfaces as <see cref="OperationCanceledException"/>.</remarks>
    public static SshConfigExpansion Load(
        ISshConfigFileSystem fileSystem,
        string userProfile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return new SshConfigExpansion(null, SshConfigImportResult.NotFound);
        }

        var sshDirectory = SshConfigPathPolicy.NormalizeFullPath(userProfile.TrimEnd('\\', '/') + "\\.ssh");
        if (sshDirectory is null)
        {
            return Fail(SshConfigImportErrorCode.Unreadable, detail: null);
        }

        var rootPath = sshDirectory + "\\config";

        // Fail closed on a linked root: if ~/.ssh or the config itself is a symlink, junction or any
        // other reparse point, the file really read (and every containment check) could be anywhere,
        // so it is never opened.
        if (fileSystem.GetInfo(sshDirectory).IsReparsePoint || fileSystem.GetInfo(rootPath).IsReparsePoint)
        {
            return Fail(SshConfigImportErrorCode.ConfigIsLink, detail: null);
        }

        var read = fileSystem.ReadFile(rootPath, MaxFileBytes);
        switch (read.Status)
        {
            case SshConfigReadStatus.NotFound:
                return new SshConfigExpansion(null, SshConfigImportResult.NotFound);
            case SshConfigReadStatus.TooLarge:
                return Fail(SshConfigImportErrorCode.TooLarge, detail: null);
            case SshConfigReadStatus.Unreadable or SshConfigReadStatus.IdentityUnverifiable:
                return Fail(SshConfigImportErrorCode.Unreadable, detail: null);
            case SshConfigReadStatus.FinalPathMismatch or SshConfigReadStatus.HardLinked:
                // The opened root is not provably the plain ~/.ssh/config that was checked.
                return Fail(SshConfigImportErrorCode.ConfigIsLink, detail: null);
        }

        var text = Decode(read.Bytes);
        if (text is null)
        {
            return Fail(SshConfigImportErrorCode.InvalidEncoding, detail: null);
        }

        var lines = SshConfigParser.ParseLines(text);
        if (lines.HasFatalSyntaxError)
        {
            return Fail(SshConfigImportErrorCode.InvalidSyntax, detail: null);
        }

        var state = new State(fileSystem, userProfile.TrimEnd('\\', '/'), sshDirectory, cancellationToken);
        state.Account(rootPath, read.Bytes.Length);
        state.IncludeStack.Add(rootPath);
        state.Process(rootPath, lines, includeParent: null, depth: 0);
        return state.Finish();
    }

    /// <summary>Strict UTF-8 (a leading BOM is skipped); <see langword="null"/> for invalid bytes.</summary>
    public static string? Decode(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(Encoding.UTF8.Preamble))
        {
            span = span[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static SshConfigExpansion Fail(SshConfigImportErrorCode code, string? detail) =>
        new(null, SshConfigImportResult.Failed(code) with { ErrorDetail = detail });

    private sealed class State(
        ISshConfigFileSystem? fileSystem,
        string userProfile,
        string? sshDirectory,
        CancellationToken cancellationToken)
    {
        private readonly List<SshConfigSegment> _segments = [];
        private readonly List<SshConfigScope> _hostScopes = [];
        private readonly List<SshConfigDiagnostic> _diagnostics = [];
        private bool _hasMatch;
        private readonly Dictionary<string, IReadOnlyList<string>> _enumerated = new(StringComparer.OrdinalIgnoreCase);
        private int _fileCount;
        private long _totalBytes;
        private int _includeArguments;
        private int _enumeratedEntries;
        private SshConfigExpansion? _failure;

        /// <summary>Normalized paths of the files currently being processed (cycle detection).</summary>
        public List<string> IncludeStack { get; } = [];

        public SshConfigExpansion Finish() => _failure ?? new SshConfigExpansion(
            new SshConfigSplicedDocument(_segments, _hostScopes, _hasMatch, _diagnostics),
            null);

        public bool Account(string path, int bytes)
        {
            _fileCount++;
            _totalBytes += bytes;
            if (_totalBytes > MaxTotalBytes)
            {
                SetFailure(SshConfigImportErrorCode.TooLarge, path);
                return false;
            }

            return true;
        }

        /// <summary>Processes one file's lines; returns the scope in effect at its end.</summary>
        public SshConfigScope Process(string path, SshConfigLines lines, SshConfigScope? includeParent, int depth)
        {
            var scope = new SshConfigScope(SshConfigBlockKind.Global, [], includeParent);
            var pending = new List<SshConfigSplicedDirective>();

            void Flush()
            {
                if (pending.Count > 0)
                {
                    _segments.Add(new SshConfigSegment(scope, pending));
                    pending = [];
                }
            }

            foreach (var directive in lines.Directives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_failure is not null)
                {
                    break;
                }

                if (SshConfigParser.HeaderKind(directive) is { } kind)
                {
                    Flush();
                    scope = new SshConfigScope(kind, directive.Arguments, includeParent);
                    if (kind == SshConfigBlockKind.Host)
                    {
                        _hostScopes.Add(scope);
                    }
                    else
                    {
                        _hasMatch = true;
                    }

                    continue;
                }

                if (!string.Equals(directive.Keyword, "Include", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Add(new SshConfigSplicedDirective(directive, path));
                    continue;
                }

                Flush();
                var enclosing = scope;
                var lastIncluded = ExpandInclude(directive, path, enclosing, depth);

                // OpenSSH restores *activep after an included file: the lines after the Include
                // belong to the enclosing block again, not to lastIncluded (whatever Host/Match the
                // included file opened last).
                scope = enclosing;
            }

            Flush();
            return scope;
        }

        private SshConfigScope? ExpandInclude(SshConfigDirective directive, string fromPath, SshConfigScope includeScope, int depth)
        {
            if (fileSystem is null || sshDirectory is null)
            {
                Marker(directive, fromPath, includeScope, SshConfigIncludeIssue.NotFollowed, argument: null);
                return null;
            }

            if (!directive.IsValid)
            {
                Marker(directive, fromPath, includeScope, SshConfigIncludeIssue.MissingArgument, directive.RawArguments);
                return null;
            }

            SshConfigScope? last = null;
            foreach (var argument in directive.Arguments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Global budget: a file of repeated Include lines cannot make the import unbounded.
                if (++_includeArguments > MaxIncludeArguments)
                {
                    SetFailure(SshConfigImportErrorCode.TooManyFiles, fromPath);
                    return last;
                }

                var candidates = Candidates(argument, out var issue);
                if (_failure is not null)
                {
                    return last;
                }

                if (issue != SshConfigIncludeIssue.None)
                {
                    Marker(directive, fromPath, includeScope, issue, argument);
                    continue;
                }

                if (candidates.Count == 0)
                {
                    Diagnose(SshConfigDiagnosticKind.IncludeMatchedNoFiles, argument);
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    var candidateIssue = CheckCandidate(candidate);
                    if (candidateIssue is null)
                    {
                        // Vanished or never a file (ENOENT): OpenSSH contributes nothing.
                        Diagnose(SshConfigDiagnosticKind.IncludeMatchedNoFiles, argument);
                        continue;
                    }

                    if (candidateIssue != SshConfigIncludeIssue.None)
                    {
                        Marker(directive, fromPath, includeScope, candidateIssue.Value, argument);
                        continue;
                    }

                    last = IncludeFile(candidate, argument, directive, fromPath, includeScope, depth) ?? last;
                    if (_failure is not null)
                    {
                        return last;
                    }
                }
            }

            return last;
        }

        private SshConfigScope? IncludeFile(
            string path,
            string argument,
            SshConfigDirective directive,
            string fromPath,
            SshConfigScope includeScope,
            int depth)
        {
            if (IncludeStack.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                SetFailure(SshConfigImportErrorCode.IncludeCycle, path);
                return null;
            }

            if (depth + 1 > MaxDepth)
            {
                SetFailure(SshConfigImportErrorCode.IncludeTooDeep, path);
                return null;
            }

            if (_fileCount + 1 > MaxFiles)
            {
                SetFailure(SshConfigImportErrorCode.TooManyFiles, path);
                return null;
            }

            var read = fileSystem!.ReadFile(path, MaxFileBytes);
            switch (read.Status)
            {
                case SshConfigReadStatus.NotFound:
                    Diagnose(SshConfigDiagnosticKind.IncludeMatchedNoFiles, argument);
                    return null;
                case SshConfigReadStatus.TooLarge:
                    SetFailure(SshConfigImportErrorCode.TooLarge, path);
                    return null;
                case SshConfigReadStatus.Unreadable:
                    SetFailure(SshConfigImportErrorCode.Unreadable, path);
                    return null;
                case SshConfigReadStatus.FinalPathMismatch or SshConfigReadStatus.IdentityUnverifiable:
                    Marker(directive, fromPath, includeScope, SshConfigIncludeIssue.FinalPathMismatch, argument);
                    return null;
                case SshConfigReadStatus.HardLinked:
                    Marker(directive, fromPath, includeScope, SshConfigIncludeIssue.HardLinked, argument);
                    return null;
            }

            if (!Account(path, read.Bytes.Length))
            {
                return null;
            }

            var text = Decode(read.Bytes);
            if (text is null)
            {
                SetFailure(SshConfigImportErrorCode.InvalidEncoding, path);
                return null;
            }

            var lines = SshConfigParser.ParseLines(text);
            if (lines.HasFatalSyntaxError)
            {
                SetFailure(SshConfigImportErrorCode.InvalidSyntax, path);
                return null;
            }

            IncludeStack.Add(path);
            var last = Process(path, lines, includeScope, depth + 1);
            IncludeStack.RemoveAt(IncludeStack.Count - 1);
            return last;
        }

        /// <summary>Full, normalized, contained candidate paths for one argument, in glob(3) order.</summary>
        private List<string> Candidates(string argument, out SshConfigIncludeIssue issue)
        {
            issue = SshConfigIncludeIssue.None;
            if (argument.Length == 0)
            {
                issue = SshConfigIncludeIssue.MissingArgument;
                return [];
            }

            if (argument.Contains('%') || argument.Contains("${", StringComparison.Ordinal))
            {
                issue = SshConfigIncludeIssue.UnsupportedExpansion;
                return [];
            }

            string raw;
            if (argument.StartsWith('~'))
            {
                if (argument.Length > 1 && argument[1] is not ('/' or '\\'))
                {
                    issue = SshConfigIncludeIssue.UnsupportedExpansion;
                    return [];
                }

                raw = userProfile + argument[1..];
            }
            else if (argument.StartsWith('/') || argument.StartsWith('\\') || (argument.Length > 1 && argument[1] == ':'))
            {
                raw = argument;
            }
            else
            {
                // OpenSSH: a relative user-config Include is relative to ~/.ssh.
                raw = sshDirectory + "\\" + argument;
            }

            // UNC, \\?\, \\.\ and root-relative paths are never a plain path inside ~/.ssh.
            if (raw.StartsWith('\\') || raw.StartsWith('/'))
            {
                issue = SshConfigIncludeIssue.OutsideSshDirectory;
                return [];
            }

            if (SshConfigPathPolicy.HasUnsupportedGlob(argument))
            {
                issue = SshConfigIncludeIssue.UnsupportedPattern;
                return [];
            }

            var separator = raw.LastIndexOfAny(['\\', '/']);
            var directoryPart = separator >= 0 ? raw[..separator] : raw;
            var namePart = separator >= 0 ? raw[(separator + 1)..] : string.Empty;
            if (SshConfigPathPolicy.HasGlob(directoryPart))
            {
                issue = SshConfigIncludeIssue.UnsupportedPattern;
                return [];
            }

            if (!SshConfigPathPolicy.HasGlob(namePart))
            {
                var full = SshConfigPathPolicy.NormalizeFullPath(raw);
                if (full is null || !SshConfigPathPolicy.IsStrictlyInside(full, sshDirectory!))
                {
                    issue = SshConfigIncludeIssue.OutsideSshDirectory;
                    return [];
                }

                if (namePart.Length == 0)
                {
                    issue = SshConfigIncludeIssue.NotRegularFile;
                    return [];
                }

                return [Canonical(full)];
            }

            var directory = SshConfigPathPolicy.NormalizeFullPath(directoryPart);
            if (directory is null
                || !SshConfigPathPolicy.IsSameOrInside(directory, sshDirectory!)
                || namePart.IndexOfAny([':', '<', '>', '|', '"']) >= 0)
            {
                issue = SshConfigIncludeIssue.OutsideSshDirectory;
                return [];
            }

            directory = Canonical(directory);

            // Only enumerate a directory whose whole chain from ~/.ssh is plain directories.
            var directoryIssue = CheckDirectoryChain(directory);
            if (directoryIssue is null)
            {
                return [];
            }

            if (directoryIssue != SshConfigIncludeIssue.None)
            {
                issue = directoryIssue.Value;
                return [];
            }

            var names = Enumerate(directory);
            if (names is null)
            {
                return [];
            }

            var matches = names
                .Where(name => SshConfigPathPolicy.GlobMatch(namePart, name))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (matches.Count > MaxMatchesPerArgument)
            {
                SetFailure(SshConfigImportErrorCode.TooManyFiles, directory);
                return [];
            }

            var candidates = new List<string>(matches.Count);
            foreach (var name in matches)
            {
                var full = SshConfigPathPolicy.NormalizeFullPath(directory + "\\" + name);
                if (full is null || !SshConfigPathPolicy.IsStrictlyInside(full, sshDirectory!))
                {
                    issue = SshConfigIncludeIssue.OutsideSshDirectory;
                    return [];
                }

                candidates.Add(Canonical(full));
            }

            return candidates;
        }

        /// <summary>
        /// Re-spells a path already proven to be inside ~/.ssh with the canonical ~/.ssh prefix, so every
        /// later check, open, cycle comparison and message uses one spelling.
        /// </summary>
        private string Canonical(string contained) => sshDirectory + contained[sshDirectory!.Length..];

        /// <summary>
        /// Lists a directory at most once per import (cached), within the global enumeration and entry
        /// budgets; <see langword="null"/> after recording a failure.
        /// </summary>
        private IReadOnlyList<string>? Enumerate(string directory)
        {
            if (_enumerated.TryGetValue(directory, out var cached))
            {
                return cached;
            }

            if (_enumerated.Count + 1 > MaxEnumerations)
            {
                SetFailure(SshConfigImportErrorCode.TooManyFiles, directory);
                return null;
            }

            var names = fileSystem!.EnumerateNames(directory);
            if (names is null)
            {
                SetFailure(SshConfigImportErrorCode.Unreadable, directory);
                return null;
            }

            _enumeratedEntries += names.Count;
            if (names.Count > MaxDirectoryEntries || _enumeratedEntries > MaxEnumeratedEntries)
            {
                SetFailure(SshConfigImportErrorCode.TooManyFiles, directory);
                return null;
            }

            _enumerated[directory] = names;
            return names;
        }

        /// <summary>
        /// <see cref="SshConfigIncludeIssue.None"/> for a plain regular file whose every directory from
        /// ~/.ssh down is a plain directory; <see langword="null"/> when it does not exist; otherwise the issue.
        /// Nothing flagged is ever opened.
        /// </summary>
        private SshConfigIncludeIssue? CheckCandidate(string path)
        {
            var parent = path[..path.LastIndexOf('\\')];
            var chain = CheckDirectoryChain(parent);
            if (chain != SshConfigIncludeIssue.None)
            {
                return chain;
            }

            var info = fileSystem!.GetInfo(path);
            if (info.IsReparsePoint)
            {
                return SshConfigIncludeIssue.ReparsePoint;
            }

            return info.Kind switch
            {
                SshConfigPathKind.Missing => null,
                SshConfigPathKind.RegularFile => SshConfigIncludeIssue.None,
                _ => SshConfigIncludeIssue.NotRegularFile
            };
        }

        private SshConfigIncludeIssue? CheckDirectoryChain(string directory)
        {
            var current = sshDirectory!;
            while (true)
            {
                var info = fileSystem!.GetInfo(current);
                if (info.IsReparsePoint)
                {
                    return SshConfigIncludeIssue.ReparsePoint;
                }

                if (info.Kind == SshConfigPathKind.Missing)
                {
                    return null;
                }

                if (info.Kind != SshConfigPathKind.Directory)
                {
                    return SshConfigIncludeIssue.NotRegularFile;
                }

                if (current.Length >= directory.Length)
                {
                    return SshConfigIncludeIssue.None;
                }

                var next = directory.IndexOf('\\', current.Length + 1);
                current = next < 0 ? directory : directory[..next];
            }
        }

        private void Marker(
            SshConfigDirective directive,
            string fromPath,
            SshConfigScope scope,
            SshConfigIncludeIssue issue,
            string? argument)
        {
            _segments.Add(new SshConfigSegment(scope, [new SshConfigSplicedDirective(directive, fromPath, issue)]));
            if (issue != SshConfigIncludeIssue.NotFollowed)
            {
                Diagnose(SshConfigDiagnosticKind.IncludeNotVerified, argument ?? string.Empty, issue);
            }
        }

        private void Diagnose(SshConfigDiagnosticKind kind, string argument, SshConfigIncludeIssue issue = SshConfigIncludeIssue.None)
        {
            var diagnostic = new SshConfigDiagnostic(kind, argument, issue);
            if (_diagnostics.Count < MaxDiagnostics && !_diagnostics.Contains(diagnostic))
            {
                _diagnostics.Add(diagnostic);
            }
        }

        private void SetFailure(SshConfigImportErrorCode code, string path) => _failure ??= Fail(code, path);
    }
}
