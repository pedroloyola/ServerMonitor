using System.Globalization;

namespace ServerMonitor.Core.SshConfig;

/// <summary>
/// Pure resolver for the import subset of ssh_config(5): <c>HostName</c>, <c>User</c>,
/// <c>Port</c> and <c>IdentityFile</c> for concrete <c>Host</c> aliases, plus a single-hop
/// <c>ProxyJump</c>. Blocks are walked top-to-bottom and, per keyword, the first value obtained
/// wins (so a <c>Host *</c> placed before a concrete block wins). Everything outside the subset is
/// classified, never interpreted.
/// </summary>
/// <remarks>
/// It consumes the spliced sequence from <see cref="SshConfigIncludeExpander"/>, so followed
/// <c>Include</c> files take part in first-wins exactly where they appear. Proxy safety is
/// fail-closed: until the proxy decision (the first applicable <c>ProxyJump</c>/<c>ProxyCommand</c>)
/// is made, anything that is or may be a proxy — a proxy line, valid or not, one under an
/// unevaluated <c>Match</c>, or an <c>Include</c> that could not be verified — makes the host
/// non-importable. Only an exact <c>none</c> decides "no proxy". A decided <c>ProxyJump</c> is
/// imported only when it is one verified hop: its host is resolved as a destination against the
/// same config (as the jump's own ssh would), and that resolution must itself be safe, direct and
/// exact; every other shape blocks the host with a specific reason, never a direct import.
/// </remarks>
public static class SshConfigResolver
{
    /// <summary>Concrete aliases offered for import; the rest are dropped with a file warning.</summary>
    public const int MaxHosts = 500;

    /// <summary>Distinct findings kept per host; further ones only set <see cref="SshConfigHostEntry.FindingsTruncated"/>.</summary>
    public const int MaxFindingsPerHost = 32;

    // Keywords that change which host, user, port, key or host-key trust ssh would actually use.
    // ServerAlyzer does not reproduce them, so they are reported rather than silently ignored.
    private static readonly HashSet<string> UnsupportedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddressFamily",
        "BindAddress",
        "BindInterface",
        "CanonicalDomains",
        "CanonicalizeFallbackLocal",
        "CanonicalizeHostname",
        "CanonicalizeMaxDots",
        "CanonicalizePermittedCNAMEs",
        "CertificateFile",
        "GlobalKnownHostsFile",
        "HostKeyAlias",
        "IdentitiesOnly",
        "IdentityAgent",
        "KnownHostsCommand",
        "PKCS11Provider",
        "PasswordAuthentication",
        "PreferredAuthentications",
        "PubkeyAuthentication",
        "SecurityKeyProvider",
        "StrictHostKeyChecking",
        "UserKnownHostsFile"
    };

    /// <summary>Text-only import: <c>Include</c> is not followed and stays "cannot verify".</summary>
    public static SshConfigImportResult Import(string text, string userProfile) =>
        Resolve(SshConfigIncludeExpander.FromText(text), userProfile);

    /// <summary>Reads <c>~/.ssh/config</c> and follows its includes, read-only, inside <c>~/.ssh</c>.</summary>
    public static SshConfigImportResult Import(
        ISshConfigFileSystem fileSystem,
        string userProfile,
        CancellationToken cancellationToken = default) =>
        Resolve(SshConfigIncludeExpander.Load(fileSystem, userProfile, cancellationToken), userProfile);

    private static SshConfigImportResult Resolve(SshConfigExpansion expansion, string userProfile)
    {
        if (expansion.Failure is not null)
        {
            return expansion.Failure;
        }

        var document = expansion.Document!;
        var plan = new ResolutionPlan(document);
        var warnings = new List<SshConfigFileWarning>();
        if (plan.HasUnverifiedInclude)
        {
            warnings.Add(SshConfigFileWarning.IncludeNotFollowed);
        }

        if (document.HasMatch)
        {
            warnings.Add(SshConfigFileWarning.MatchNotEvaluated);
        }

        var aliases = ConcreteAliases(document, MaxHosts + 1);
        if (aliases.Count > MaxHosts)
        {
            aliases = aliases.Take(MaxHosts).ToList();
            warnings.Add(SshConfigFileWarning.HostsTruncated);
        }

        return new SshConfigImportResult
        {
            Status = SshConfigImportStatus.Loaded,
            Hosts = aliases.Select(alias => Resolve(plan, document.HasMatch, alias, userProfile)).ToList(),
            FileWarnings = warnings,
            Diagnostics = document.Diagnostics
        };
    }

    /// <summary>
    /// Concrete aliases (no wildcard or negation) from <c>Host</c> lines, in spliced order, that can
    /// apply to themselves: a <c>Host</c> inside a file included from a non-matching block never can.
    /// </summary>
    private static List<string> ConcreteAliases(SshConfigSplicedDocument document, int limit)
    {
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (var scope in document.HostScopes)
        {
            foreach (var pattern in scope.Patterns)
            {
                if (IsConcrete(pattern)
                    && !offered.Contains(pattern)
                    && Evaluate(scope, pattern) != Applicability.No
                    && offered.Add(pattern))
                {
                    aliases.Add(pattern);
                    if (aliases.Count >= limit)
                    {
                        return aliases;
                    }
                }
            }
        }

        return aliases;
    }

    private static SshConfigHostEntry Resolve(ResolutionPlan plan, bool hasMatch, string alias, string userProfile)
    {
        var target = ResolveDestination(plan, hasMatch, alias, userProfile, isJump: false);
        if (target.ProxyJump is not { } proxyJump)
        {
            return target.Entry;
        }

        if (target.Entry.Blocker != SshConfigHostBlocker.None)
        {
            return WithProxyJumpFinding(target.Entry);
        }

        var (jump, blocker) = ResolveJump(plan, hasMatch, target.Entry, proxyJump, userProfile);
        return blocker == SshConfigHostBlocker.None
            ? target.Entry with { Jump = jump }
            : WithProxyJumpFinding(target.Entry with { Blocker = blocker });
    }

    /// <summary>
    /// The jump of a target whose effective <c>ProxyJump</c> is <paramref name="directive"/>. ssh runs a
    /// second ssh for the jump host with the same config and <c>-l</c>/<c>-p</c> from the value, so the
    /// jump is resolved here the same way and must itself be direct: a jump that has its own proxy is a
    /// chain (or a cycle), never flattened into one hop.
    /// </summary>
    private static (SshConfigJumpHost? Jump, SshConfigHostBlocker Blocker) ResolveJump(
        ResolutionPlan plan,
        bool hasMatch,
        SshConfigHostEntry target,
        SshConfigDirective directive,
        string userProfile)
    {
        switch (SshConfigProxyJump.Parse(directive, out var spec))
        {
            case SshConfigProxyJumpParse.MultiHop:
                return (null, SshConfigHostBlocker.JumpMultiHop);
            case SshConfigProxyJumpParse.Unparsable:
                return (null, SshConfigHostBlocker.JumpUnparsable);
        }

        if (string.Equals(spec!.Host, target.Alias, StringComparison.OrdinalIgnoreCase))
        {
            return (null, SshConfigHostBlocker.JumpCycle);
        }

        var jump = ResolveDestination(plan, hasMatch, spec.Host, userProfile, isJump: true);
        if (jump.Entry.Blocker != SshConfigHostBlocker.None)
        {
            return (null, jump.Entry.Blocker);
        }

        if (jump.ProxyJump is { } chained)
        {
            // Back to the target or to the jump itself is a cycle; any other proxy is a second hop.
            var loops = SshConfigProxyJump.Parse(chained, out var next) == SshConfigProxyJumpParse.SingleHop
                && (string.Equals(next!.Host, target.Alias, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(next.Host, spec.Host, StringComparison.OrdinalIgnoreCase));
            return (null, loops ? SshConfigHostBlocker.JumpCycle : SshConfigHostBlocker.JumpMultiHop);
        }

        if (jump.Entry.HostName is not { } jumpHostName)
        {
            return (null, SshConfigHostBlocker.JumpHostNameUnresolved);
        }

        var port = spec.Port ?? jump.Entry.Port;
        if (target.HostName is not null
            && string.Equals(jumpHostName, target.HostName, StringComparison.OrdinalIgnoreCase)
            && (port ?? DefaultPort) == (target.Port ?? DefaultPort))
        {
            return (null, SshConfigHostBlocker.JumpCycle);
        }

        return (new SshConfigJumpHost
        {
            Name = spec.Host,
            HostName = jumpHostName,
            User = spec.User ?? jump.Entry.User,
            Port = port,
            IdentityFile = jump.Entry.IdentityFile,
            Findings = jump.Entry.Findings,
            FindingsTruncated = jump.Entry.FindingsTruncated
        }, SshConfigHostBlocker.None);
    }

    // A blocked ProxyJump is also listed among the keywords ServerAlyzer does not reproduce.
    private static SshConfigHostEntry WithProxyJumpFinding(SshConfigHostEntry entry)
    {
        const string keyword = "ProxyJump";
        if (entry.Findings.Any(finding => finding.Keyword == keyword && finding.Kind == SshConfigFindingKind.Unsupported))
        {
            return entry;
        }

        return entry.Findings.Count >= MaxFindingsPerHost
            ? entry with { FindingsTruncated = true }
            : entry with
            {
                Findings = [.. entry.Findings, new SshConfigFinding(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword)]
            };
    }

    /// <summary>The resolved entry and, when the decided proxy is a <c>ProxyJump</c> other than <c>none</c>, its line.</summary>
    private readonly record struct DestinationResolution(SshConfigHostEntry Entry, SshConfigDirective? ProxyJump);

    /// <summary>
    /// Resolves one destination: a listed alias, or the host named by a <c>ProxyJump</c>. For a jump
    /// (<paramref name="isJump"/>) its <c>HostName</c> is a routing decision too, so an unevaluated
    /// <c>Match</c> or an unverifiable <c>Include</c> that may set it before it is obtained blocks.
    /// </summary>
    private static DestinationResolution ResolveDestination(
        ResolutionPlan plan,
        bool hasMatch,
        string alias,
        string userProfile,
        bool isJump)
    {
        var findings = new List<SshConfigFinding>();
        var findingKeys = new HashSet<(string, SshConfigFindingKind)>();
        var findingsTruncated = false;
        var obtained = new HashSet<string>(StringComparer.Ordinal);
        var identityFiles = new List<string>();
        string? hostName = null;
        string? user = null;
        int? port = null;
        var hostNameUnresolved = false;
        var identityFileInvalid = false;
        var proxyDecided = false;
        SshConfigDirective? proxyJump = null;
        var blocker = SshConfigHostBlocker.None;

        void Add(string keyword, SshConfigFindingKind kind, SshConfigFindingReason reason)
        {
            if (!findingKeys.Add((keyword.ToLowerInvariant(), kind)))
            {
                return;
            }

            if (findings.Count >= MaxFindingsPerHost)
            {
                findingsTruncated = true;
                return;
            }

            findings.Add(new SshConfigFinding(keyword, kind, reason));
        }

        void Block(SshConfigHostBlocker reason)
        {
            if (blocker == SshConfigHostBlocker.None)
            {
                blocker = reason;
            }
        }

        foreach (var segment in plan.SegmentsFor(alias))
        {
            var applicability = Evaluate(segment.Scope, alias);
            if (applicability == Applicability.No)
            {
                continue;
            }

            if (applicability == Applicability.Unknown)
            {
                // Under an unevaluated Match: nothing here is applied. If it could set a proxy before
                // the proxy is decided, turn canonicalisation on before that is decided, or set a
                // jump's HostName before it is obtained, the host cannot be verified.
                if ((!proxyDecided && MayConfigureProxy(segment))
                    || MayTurnOnCanonicalization(segment, obtained)
                    || (isJump && MaySetJumpHostName(segment, obtained)))
                {
                    Block(SshConfigHostBlocker.ProxyMaySetByMatch);
                }

                continue;
            }

            foreach (var spliced in segment.Directives)
            {
                if (spliced.IncludeIssue != SshConfigIncludeIssue.None)
                {
                    // An Include that could not be verified may hide anything, including a proxy or
                    // CanonicalizeHostname (or a jump's HostName) not decided yet.
                    Add("Include", SshConfigFindingKind.Unsupported, IncludeReason(spliced.IncludeIssue));
                    if (!proxyDecided
                        || !obtained.Contains("CanonicalizeHostname")
                        || (isJump && !obtained.Contains("HostName")))
                    {
                        Block(SshConfigHostBlocker.ProxyMaySetByInclude);
                    }

                    continue;
                }

                var directive = spliced.Directive;
                var keyword = CanonicalKeyword(directive.Keyword);
                switch (keyword)
                {
                    case "ProxyJump" or "ProxyCommand":
                        // One shared first-wins slot. Any applicable proxy line decides it; only an
                        // exact "none" means no proxy. A ProxyJump line, valid or not, is handed to the
                        // caller to verify as a single hop; a ProxyCommand blocks (fail-closed).
                        if (proxyDecided)
                        {
                            break;
                        }

                        proxyDecided = true;
                        if (IsProxyNone(directive))
                        {
                            break;
                        }

                        if (keyword == "ProxyJump")
                        {
                            proxyJump = directive;
                        }
                        else
                        {
                            Block(SshConfigHostBlocker.ProxyCommand);
                            Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);
                        }

                        break;
                    case "IdentityFile" when !directive.IsValid:
                        identityFileInvalid = true;
                        Add(keyword, SshConfigFindingKind.Invalid, SshConfigFindingReason.InvalidValue);
                        break;
                    case "IdentityFile":
                        // IdentityFile accumulates: ssh tries every value, in order.
                        identityFiles.Add(directive.Arguments[0]);
                        if (directive.Arguments.Count != 1)
                        {
                            identityFileInvalid = true;
                            Add(keyword, SshConfigFindingKind.Invalid, SshConfigFindingReason.InvalidValue);
                        }

                        break;
                    case "HostName" or "User" or "Port":
                        if (!obtained.Add(keyword))
                        {
                            break;
                        }

                        if (!directive.IsValid || directive.Arguments.Count != 1)
                        {
                            Add(keyword, SshConfigFindingKind.Invalid, SshConfigFindingReason.InvalidValue);
                            hostNameUnresolved |= keyword == "HostName";
                            break;
                        }

                        var value = directive.Arguments[0];
                        switch (keyword)
                        {
                            case "HostName" when value.Contains('%'):
                                hostNameUnresolved = true;
                                Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.TokenExpansion);
                                break;
                            case "HostName":
                                hostName = value;
                                break;
                            case "User" when value.Contains('%'):
                                Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.TokenExpansion);
                                break;
                            case "User":
                                user = value;
                                break;
                            default:
                                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                                    && parsed is >= 1 and <= 65535)
                                {
                                    port = parsed;
                                }
                                else
                                {
                                    Add(keyword, SshConfigFindingKind.Invalid, SshConfigFindingReason.InvalidValue);
                                }

                                break;
                        }

                        break;
                    case "CanonicalizeHostname":
                        if (obtained.Add(keyword) && !IsCanonicalizeNo(directive))
                        {
                            Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);

                            // Vigil M14.4c M-3 (+ human decision M14-SSHCFG-CANON-1): ssh canonicalises the
                            // name and then re-reads the config, where the host (or the jump) may gain
                            // another HostName or a proxy. Neither is imported when that can happen.
                            Block(isJump
                                ? SshConfigHostBlocker.JumpHostNameUnresolved
                                : SshConfigHostBlocker.CanonicalizationMayChangeRoute);
                        }

                        break;
                    default:
                        if (!directive.IsValid)
                        {
                            Add(keyword.Length == 0 ? "?" : keyword, SshConfigFindingKind.Invalid, SshConfigFindingReason.InvalidValue);
                        }
                        else if (UnsupportedKeywords.Contains(keyword))
                        {
                            Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);
                        }
                        else
                        {
                            Add(keyword, SshConfigFindingKind.Ignored, SshConfigFindingReason.NotRelevant);
                        }

                        break;
                }
            }
        }

        if (hasMatch)
        {
            Add("Match", SshConfigFindingKind.Unsupported, SshConfigFindingReason.MatchNotEvaluated);
        }

        var identityFile = identityFileInvalid ? null : ResolveIdentityFile(identityFiles, userProfile, Add);
        return new DestinationResolution(new SshConfigHostEntry
        {
            Alias = alias,
            HostName = hostNameUnresolved ? null : hostName ?? alias,
            User = user,
            Port = port,
            IdentityFile = identityFile,
            IdentityFileValues = identityFiles,
            Findings = findings,
            FindingsTruncated = findingsTruncated,
            Blocker = blocker
        }, proxyJump);
    }

    private const int DefaultPort = 22;

    // "none" only when it is the whole raw argument. OpenSSH reads both ProxyCommand and ProxyJump raw
    // (readconf.c parse_jump(str + len): no quote handling), so a quoted "none", 'none' or "none # c" is
    // not "no proxy" to ssh (it tries to jump and fails) and must never be imported as direct.
    private static bool IsProxyNone(SshConfigDirective directive) =>
        string.Equals(directive.RawArguments, "none", StringComparison.Ordinal);

    // Any HostName or CanonicalizeHostname line, valid or not, or an unverifiable Include: it could
    // decide where a jump host is.
    private static bool MaySetHostName(SshConfigSegment segment) =>
        segment.Directives.Any(spliced => spliced.IncludeIssue != SshConfigIncludeIssue.None
            || CanonicalKeyword(spliced.Directive.Keyword) is "HostName" or "CanonicalizeHostname");

    // Whether an unevaluated segment could still turn canonicalisation on (for any destination): a
    // CanonicalizeHostname other than "no", or an unverifiable Include, before it is obtained.
    private static bool MayTurnOnCanonicalization(SshConfigSegment segment, HashSet<string> obtained) =>
        !obtained.Contains("CanonicalizeHostname")
        && segment.Directives.Any(spliced => spliced.IncludeIssue != SshConfigIncludeIssue.None
            || (CanonicalKeyword(spliced.Directive.Keyword) == "CanonicalizeHostname" && !IsCanonicalizeNo(spliced.Directive)));

    // Whether an unevaluated segment could still set a jump's HostName: a HostName line, or an
    // unverifiable Include, before it is obtained.
    private static bool MaySetJumpHostName(SshConfigSegment segment, HashSet<string> obtained) =>
        !obtained.Contains("HostName")
        && segment.Directives.Any(spliced => spliced.IncludeIssue != SshConfigIncludeIssue.None
            || CanonicalKeyword(spliced.Directive.Keyword) == "HostName");

    private static bool IsCanonicalizeNo(SshConfigDirective directive) =>
        directive.IsValid && string.Equals(directive.Arguments[0], "no", StringComparison.OrdinalIgnoreCase);

    private static bool MayConfigureProxy(SshConfigSegment segment) =>
        segment.Directives.Any(spliced => spliced.IncludeIssue != SshConfigIncludeIssue.None
            || CanonicalKeyword(spliced.Directive.Keyword) switch
            {
                "ProxyJump" or "ProxyCommand" => !IsProxyNone(spliced.Directive),
                _ => false
            });

    private static SshConfigFindingReason IncludeReason(SshConfigIncludeIssue issue) => issue switch
    {
        SshConfigIncludeIssue.OutsideSshDirectory => SshConfigFindingReason.IncludeOutsideSshDirectory,
        SshConfigIncludeIssue.ReparsePoint => SshConfigFindingReason.IncludeReparsePoint,
        SshConfigIncludeIssue.NotRegularFile => SshConfigFindingReason.IncludeNotRegularFile,
        SshConfigIncludeIssue.UnsupportedPattern => SshConfigFindingReason.IncludeUnsupportedPattern,
        SshConfigIncludeIssue.UnsupportedExpansion => SshConfigFindingReason.IncludeUnsupportedExpansion,
        SshConfigIncludeIssue.MissingArgument => SshConfigFindingReason.IncludeMissingArgument,
        SshConfigIncludeIssue.FinalPathMismatch => SshConfigFindingReason.IncludeFinalPathMismatch,
        SshConfigIncludeIssue.HardLinked => SshConfigFindingReason.IncludeHardLinked,
        _ => SshConfigFindingReason.IncludeNotFollowed
    };

    private enum Applicability
    {
        Yes,
        No,
        Unknown
    }

    /// <summary>
    /// Whether a line in <paramref name="scope"/> applies to <paramref name="alias"/>: its own
    /// header must apply, and so must the scope of every <c>Include</c> above it. A header in a file
    /// included from a block that does not apply never applies (OpenSSH SSHCONF_NEVERMATCH); a
    /// <c>Match</c> anywhere in the chain makes it unknown.
    /// </summary>
    private static Applicability Evaluate(SshConfigScope scope, string alias)
    {
        var own = scope.Kind switch
        {
            SshConfigBlockKind.Host => BlockApplies(scope.Patterns, alias) ? Applicability.Yes : Applicability.No,
            SshConfigBlockKind.Match => Applicability.Unknown,
            _ => Applicability.Yes
        };
        var inherited = scope.IncludeParent is null ? Applicability.Yes : Evaluate(scope.IncludeParent, alias);
        return inherited == Applicability.No || own == Applicability.No ? Applicability.No
            : inherited == Applicability.Unknown || own == Applicability.Unknown ? Applicability.Unknown
            : Applicability.Yes;
    }

    private static string? ResolveIdentityFile(
        List<string> values,
        string userProfile,
        Action<string, SshConfigFindingKind, SshConfigFindingReason> add)
    {
        const string keyword = "IdentityFile";
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            add(keyword, SshConfigFindingKind.Ambiguous, SshConfigFindingReason.MultipleValues);
            return null;
        }

        var value = values[0];
        if (value.Contains('%'))
        {
            add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.TokenExpansion);
            return null;
        }

        if (value.Contains("${", StringComparison.Ordinal))
        {
            add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.EnvironmentExpansion);
            return null;
        }

        if (value.StartsWith('~'))
        {
            if (value.Length > 1 && value[1] is not ('/' or '\\'))
            {
                add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.OtherUserHome);
                return null;
            }

            var rest = value.Length > 1 ? value[2..] : string.Empty;
            var profile = userProfile.TrimEnd('/', '\\');
            return rest.Length == 0
                ? profile
                : profile + Path.DirectorySeparatorChar + rest.Replace('/', Path.DirectorySeparatorChar);
        }

        if (!Path.IsPathFullyQualified(value))
        {
            add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.RelativePath);
            return null;
        }

        return value;
    }

    /// <summary>A Host block applies when a positive pattern matches and no negated pattern does.</summary>
    public static bool BlockApplies(IReadOnlyList<string> patterns, string alias)
    {
        var positive = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (WildcardMatch(pattern[1..], alias))
                {
                    return false;
                }
            }
            else if (WildcardMatch(pattern, alias))
            {
                positive = true;
            }
        }

        return positive;
    }

    /// <summary>OpenSSH <c>match_pattern</c>: <c>*</c> matches any run, <c>?</c> one character; case-insensitive.</summary>
    public static bool WildcardMatch(string pattern, string value)
    {
        int p = 0, v = 0, starP = -1, starV = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                v = ++starV;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool IsConcrete(string pattern) => pattern.Length > 0 && pattern.IndexOfAny(['*', '?', '!']) < 0;

    private static string CanonicalKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "hostname" => "HostName",
        "user" => "User",
        "port" => "Port",
        "identityfile" => "IdentityFile",
        "proxyjump" => "ProxyJump",
        "proxycommand" => "ProxyCommand",
        "include" => "Include",
        "canonicalizehostname" => "CanonicalizeHostname",
        _ => UnsupportedKeywords.TryGetValue(keyword, out var canonical) ? canonical : keyword
    };

    /// <summary>
    /// Index so resolving many aliases does not rescan every segment for each one. A segment whose
    /// own header is a purely concrete <c>Host</c> can only apply to the aliases it names; segments
    /// without a header, under a wildcard/negated <c>Host</c>, or under a <c>Match</c> that may set
    /// a proxy or a <c>HostName</c> are visited for every destination; other <c>Match</c> segments
    /// never apply and are skipped.
    /// Spliced order is preserved.
    /// </summary>
    private sealed class ResolutionPlan
    {
        private readonly IReadOnlyList<SshConfigSegment> _segments;
        private readonly List<int> _alwaysVisit = [];
        private readonly Dictionary<string, List<int>> _concreteSegments = new(StringComparer.OrdinalIgnoreCase);

        public ResolutionPlan(SshConfigSplicedDocument document)
        {
            _segments = document.Segments;
            for (var index = 0; index < _segments.Count; index++)
            {
                var segment = _segments[index];
                HasUnverifiedInclude |= segment.Directives.Any(spliced => spliced.IncludeIssue != SshConfigIncludeIssue.None);
                var scope = segment.Scope;
                switch (scope.Kind)
                {
                    case SshConfigBlockKind.Match:
                        if (MayConfigureProxy(segment) || MaySetHostName(segment))
                        {
                            _alwaysVisit.Add(index);
                        }

                        break;
                    case SshConfigBlockKind.Host when scope.Patterns.All(IsConcrete):
                        foreach (var pattern in scope.Patterns.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (!_concreteSegments.TryGetValue(pattern, out var list))
                            {
                                _concreteSegments[pattern] = list = [];
                            }

                            list.Add(index);
                        }

                        break;
                    default:
                        _alwaysVisit.Add(index);
                        break;
                }
            }
        }

        public bool HasUnverifiedInclude { get; }

        public IEnumerable<SshConfigSegment> SegmentsFor(string alias)
        {
            var own = _concreteSegments.TryGetValue(alias, out var list) ? list : [];
            int a = 0, o = 0;
            while (a < _alwaysVisit.Count || o < own.Count)
            {
                if (o >= own.Count || (a < _alwaysVisit.Count && _alwaysVisit[a] < own[o]))
                {
                    yield return _segments[_alwaysVisit[a++]];
                }
                else
                {
                    yield return _segments[own[o++]];
                }
            }
        }
    }
}
