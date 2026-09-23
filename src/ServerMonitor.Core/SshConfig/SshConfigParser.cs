using System.Text;

namespace ServerMonitor.Core.SshConfig;

/// <summary>
/// One <c>Keyword arguments</c> line. <see cref="IsValid"/> is false for malformed lines.
/// <see cref="RawArguments"/> is the untokenized rest of the line after the keyword, which is how
/// OpenSSH reads <c>ProxyCommand</c> (no quote or comment handling).
/// </summary>
public sealed record SshConfigDirective(
    int LineNumber,
    string Keyword,
    IReadOnlyList<string> Arguments,
    bool IsValid,
    string RawArguments = "");

public enum SshConfigBlockKind
{
    /// <summary>Lines before the first <c>Host</c>/<c>Match</c>: they apply to every host.</summary>
    Global,
    Host,
    Match
}

public sealed record SshConfigBlock(
    SshConfigBlockKind Kind,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<SshConfigDirective> Directives);

/// <param name="HasFatalSyntaxError">
/// The file cannot be read faithfully, so nothing in it is trusted: a <c>Host</c> or <c>Match</c>
/// line without valid arguments (ssh rejects the whole file), or a keyword containing a quote.
/// OpenSSH strips quotes anywhere in a keyword (<c>"ProxyJump" bastion</c> and
/// <c>Proxy"Jump" bastion</c> are both a real ProxyJump); rather than emulate that, any keyword
/// with a quote in any position fails the file closed.
/// </param>
public sealed record SshConfigDocument(IReadOnlyList<SshConfigBlock> Blocks, bool HasFatalSyntaxError = false);

/// <summary>
/// Pure, I/O-free tokenizer for ssh_config(5) text. It only splits lines into keywords and
/// arguments and groups them into <c>Host</c>/<c>Match</c> blocks; it interprets nothing.
/// </summary>
public static class SshConfigParser
{
    public static SshConfigDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var blocks = new List<SshConfigBlock>();
        var currentKind = SshConfigBlockKind.Global;
        IReadOnlyList<string> currentPatterns = [];
        var currentDirectives = new List<SshConfigDirective>();
        var hasFatalSyntaxError = false;

        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var directive = ParseLine(rawLine.TrimEnd('\r'), lineNumber);
            if (directive is null)
            {
                continue;
            }

            if (directive.Keyword.IndexOfAny(['"', '\'']) >= 0)
            {
                hasFatalSyntaxError = true;
            }

            var isHost = string.Equals(directive.Keyword, "Host", StringComparison.OrdinalIgnoreCase);
            var isMatch = string.Equals(directive.Keyword, "Match", StringComparison.OrdinalIgnoreCase);
            if (isHost || isMatch)
            {
                hasFatalSyntaxError |= !directive.IsValid;
                blocks.Add(new SshConfigBlock(currentKind, currentPatterns, currentDirectives));
                currentKind = isHost ? SshConfigBlockKind.Host : SshConfigBlockKind.Match;
                currentPatterns = directive.IsValid ? directive.Arguments : [];
                currentDirectives = [];
                continue;
            }

            currentDirectives.Add(directive);
        }

        blocks.Add(new SshConfigBlock(currentKind, currentPatterns, currentDirectives));
        return new SshConfigDocument(blocks, hasFatalSyntaxError);
    }

    private static SshConfigDirective? ParseLine(string line, int lineNumber)
    {
        var index = 0;
        SkipWhitespace(line, ref index);
        if (index >= line.Length || line[index] == '#')
        {
            return null;
        }

        // Keyword ends at whitespace or '='; "Keyword value", "Keyword=value" and
        // "Keyword = value" are all accepted (a single '=' separator).
        var keywordStart = index;
        while (index < line.Length && !char.IsWhiteSpace(line[index]) && line[index] != '=')
        {
            index++;
        }

        var keyword = line[keywordStart..index];
        var rawArguments = line[index..].TrimStart(' ', '\t', '=').TrimEnd();
        SkipWhitespace(line, ref index);
        if (index < line.Length && line[index] == '=')
        {
            index++;
            SkipWhitespace(line, ref index);
        }

        if (keyword.Length == 0)
        {
            return new SshConfigDirective(lineNumber, string.Empty, [], IsValid: false, rawArguments);
        }

        var arguments = SplitArguments(line, index, out var isValid);
        return new SshConfigDirective(lineNumber, keyword, arguments, isValid && arguments.Count > 0, rawArguments);
    }

    // Mirrors OpenSSH argv_split: whitespace-separated words, single or double quotes group a
    // word, a backslash escapes a quote, a backslash or whitespace, and an unquoted '#' at the
    // start of a word ends the line.
    private static IReadOnlyList<string> SplitArguments(string line, int index, out bool isValid)
    {
        var arguments = new List<string>();
        isValid = true;
        while (true)
        {
            SkipWhitespace(line, ref index);
            if (index >= line.Length || line[index] == '#')
            {
                return arguments;
            }

            var word = new StringBuilder();
            char? quote = null;
            while (index < line.Length)
            {
                var c = line[index];
                if (c == '\\' && index + 1 < line.Length
                    && line[index + 1] is '\\' or '"' or '\'' or ' ' or '\t')
                {
                    word.Append(line[index + 1]);
                    index += 2;
                    continue;
                }

                if (quote is null && char.IsWhiteSpace(c))
                {
                    break;
                }

                if (quote is null && c is '"' or '\'')
                {
                    quote = c;
                }
                else if (quote == c)
                {
                    quote = null;
                }
                else
                {
                    word.Append(c);
                }

                index++;
            }

            if (quote is not null)
            {
                isValid = false;
                return arguments;
            }

            arguments.Add(word.ToString());
        }
    }

    private static void SkipWhitespace(string line, ref int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
    }
}
