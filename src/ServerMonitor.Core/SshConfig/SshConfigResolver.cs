using System.Globalization;

namespace ServerMonitor.Core.SshConfig;

/// <summary>
/// Pure resolver for the import subset of ssh_config(5): <c>HostName</c>, <c>User</c>,
/// <c>Port</c> and <c>IdentityFile</c> for concrete <c>Host</c> aliases. Blocks are walked
/// top-to-bottom and, per keyword, the first value obtained wins (so a <c>Host *</c> placed
/// before a concrete block wins). Everything outside the subset is classified, never interpreted.
/// </summary>
/// <remarks>
/// Proxy safety is fail-closed: until the proxy decision (the first applicable
/// <c>ProxyJump</c>/<c>ProxyCommand</c>) is made, anything that is or may be a proxy — a proxy
/// line, valid or not, an unevaluated <c>Match</c> block that sets one, or an unfollowed
/// <c>Include</c> — makes the host non-importable. Only an exact <c>none</c> decides "no proxy".
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

    public static SshConfigImportResult Import(string text, string userProfile)
    {
        var document = SshConfigParser.Parse(text);
        if (document.HasFatalSyntaxError)
        {
            return SshConfigImportResult.Failed(SshConfigImportErrorCode.InvalidSyntax);
        }

        var plan = new ResolutionPlan(document);
        var warnings = new List<SshConfigFileWarning>();
        if (plan.HasInclude)
        {
            warnings.Add(SshConfigFileWarning.IncludeNotFollowed);
        }

        if (plan.HasMatch)
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
            Hosts = aliases.Select(alias => Resolve(plan, alias, userProfile)).ToList(),
            FileWarnings = warnings
        };
    }

    /// <summary>Aliases from <c>Host</c> lines with no wildcard or negation, in file order.</summary>
    public static IReadOnlyList<string> ConcreteAliases(SshConfigDocument document, int limit = int.MaxValue)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (var block in document.Blocks.Where(block => block.Kind == SshConfigBlockKind.Host))
        {
            foreach (var pattern in block.Patterns)
            {
                if (IsConcrete(pattern) && seen.Add(pattern))
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

    public static SshConfigHostEntry Resolve(SshConfigDocument document, string alias, string userProfile) =>
        Resolve(new ResolutionPlan(document), alias, userProfile);

    private static SshConfigHostEntry Resolve(ResolutionPlan plan, string alias, string userProfile)
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

        foreach (var block in plan.BlocksFor(alias))
        {
            if (block.Kind == SshConfigBlockKind.Match)
            {
                // Match criteria are never evaluated. If the block could set a proxy (or pull one in
                // through Include) before the proxy is decided, the host cannot be verified.
                if (!proxyDecided && MatchMayConfigureProxy(block))
                {
                    Block(SshConfigHostBlocker.ProxyMaySetByMatch);
                }

                continue;
            }

            if (block.Kind == SshConfigBlockKind.Host && !BlockApplies(block.Patterns, alias))
            {
                continue;
            }

            foreach (var directive in block.Directives)
            {
                var keyword = CanonicalKeyword(directive.Keyword);
                switch (keyword)
                {
                    case "ProxyJump" or "ProxyCommand":
                        // One shared first-wins slot. Any applicable proxy line decides it; only an
                        // exact "none" means no proxy. A malformed line blocks too (fail-closed).
                        if (proxyDecided)
                        {
                            break;
                        }

                        proxyDecided = true;
                        if (!IsProxyNone(directive, keyword))
                        {
                            Block(keyword == "ProxyJump" ? SshConfigHostBlocker.ProxyJump : SshConfigHostBlocker.ProxyCommand);
                            Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);
                        }

                        break;
                    case "Include":
                        Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);
                        if (!proxyDecided)
                        {
                            Block(SshConfigHostBlocker.ProxyMaySetByInclude);
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
                        if (obtained.Add(keyword)
                            && !(directive.IsValid && string.Equals(directive.Arguments[0], "no", StringComparison.OrdinalIgnoreCase)))
                        {
                            Add(keyword, SshConfigFindingKind.Unsupported, SshConfigFindingReason.UnsupportedKeyword);
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

        if (plan.HasMatch)
        {
            Add("Match", SshConfigFindingKind.Unsupported, SshConfigFindingReason.MatchNotEvaluated);
        }

        var identityFile = identityFileInvalid ? null : ResolveIdentityFile(identityFiles, userProfile, Add);
        return new SshConfigHostEntry
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
        };
    }

    // "none" only when it is the whole argument: ProxyJump has exactly one token "none"; OpenSSH
    // reads ProxyCommand raw (no tokenizing), so its entire raw text must be "none".
    private static bool IsProxyNone(SshConfigDirective directive, string keyword) => keyword == "ProxyCommand"
        ? string.Equals(directive.RawArguments, "none", StringComparison.Ordinal)
        : directive.IsValid
            && directive.Arguments.Count == 1
            && string.Equals(directive.Arguments[0], "none", StringComparison.Ordinal);

    private static bool MatchMayConfigureProxy(SshConfigBlock block) =>
        block.Directives.Any(directive => CanonicalKeyword(directive.Keyword) switch
        {
            "Include" => true,
            "ProxyJump" => !IsProxyNone(directive, "ProxyJump"),
            "ProxyCommand" => !IsProxyNone(directive, "ProxyCommand"),
            _ => false
        });

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
    /// Per-file index so resolving many aliases does not rescan every block for each one: Global
    /// blocks, proxy-relevant Match blocks and wildcard/negated Host blocks are visited for every
    /// alias; purely concrete Host blocks only for the aliases they name. File order is preserved.
    /// </summary>
    private sealed class ResolutionPlan
    {
        private readonly IReadOnlyList<SshConfigBlock> _blocks;
        private readonly List<int> _alwaysVisit = [];
        private readonly Dictionary<string, List<int>> _concreteBlocks = new(StringComparer.OrdinalIgnoreCase);

        public ResolutionPlan(SshConfigDocument document)
        {
            _blocks = document.Blocks;
            for (var index = 0; index < _blocks.Count; index++)
            {
                var block = _blocks[index];
                HasInclude |= block.Directives.Any(directive => CanonicalKeyword(directive.Keyword) == "Include");
                switch (block.Kind)
                {
                    case SshConfigBlockKind.Global:
                        _alwaysVisit.Add(index);
                        break;
                    case SshConfigBlockKind.Match:
                        HasMatch = true;
                        if (MatchMayConfigureProxy(block))
                        {
                            _alwaysVisit.Add(index);
                        }

                        break;
                    default:
                        if (block.Patterns.All(IsConcrete))
                        {
                            foreach (var pattern in block.Patterns.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (!_concreteBlocks.TryGetValue(pattern, out var list))
                                {
                                    _concreteBlocks[pattern] = list = [];
                                }

                                list.Add(index);
                            }
                        }
                        else
                        {
                            _alwaysVisit.Add(index);
                        }

                        break;
                }
            }
        }

        public bool HasInclude { get; }

        public bool HasMatch { get; }

        public IEnumerable<SshConfigBlock> BlocksFor(string alias)
        {
            var own = _concreteBlocks.TryGetValue(alias, out var list) ? list : [];
            int a = 0, o = 0;
            while (a < _alwaysVisit.Count || o < own.Count)
            {
                if (o >= own.Count || (a < _alwaysVisit.Count && _alwaysVisit[a] < own[o]))
                {
                    yield return _blocks[_alwaysVisit[a++]];
                }
                else
                {
                    yield return _blocks[own[o++]];
                }
            }
        }
    }
}
