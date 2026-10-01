using System.Text;

namespace ServerMonitor.TestSupport;

/// <summary>
/// The minimum C# lexing the lexical test fences need, shared by link between test projects. It knows comments,
/// regular, verbatim, interpolated (with holes, nested) and raw string literals, and char literals, so a <c>//</c>
/// inside a string (a URL) is NOT a comment and a brace inside a string does not count. Output keeps the input's
/// length and newlines, so match indexes map back to the source.
/// </summary>
internal static class CSharpSourceText
{
    /// <summary>Comments become spaces; string and char literals are kept as written.</summary>
    public static string StripComments(string code) => Scan(code, blankLiterals: false);

    /// <summary>Comments AND the contents of string/char literals become spaces (delimiters kept).</summary>
    public static string CodeOnly(string code) => Scan(code, blankLiterals: true);

    private static string Scan(string code, bool blankLiterals)
    {
        var output = new StringBuilder(code);
        var i = 0;
        ScanCode(code, output, ref i, blankLiterals, stopAtHoleEnd: false);
        return output.ToString();
    }

    // Scans code until the end (or, inside an interpolation hole, until the '}' that closes it).
    private static void ScanCode(string s, StringBuilder o, ref int i, bool blank, bool stopAtHoleEnd)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    Blank(o, i++);
                }
            }
            else if (c == '/' && next == '*')
            {
                Blank(o, i++);
                Blank(o, i++);
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    Blank(o, i++);
                }

                if (i < s.Length)
                {
                    Blank(o, i++);
                    Blank(o, i++);
                }
            }
            else if (c is '"' or '@' or '$' && StringStart(s, i) is { } literal)
            {
                ScanString(s, o, ref i, literal, blank);
            }
            else if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n')
                {
                    if (s[i] == '\\')
                    {
                        BlankIf(blank, o, i++);
                    }

                    BlankIf(blank, o, i++);
                }

                i++;
            }
            else
            {
                if (stopAtHoleEnd)
                {
                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}' && depth-- == 0)
                    {
                        return; // the '}' closing the hole is left for the string scanner
                    }
                }

                i++;
            }
        }
    }

    private readonly record struct Literal(int PrefixLength, int Quotes, bool Verbatim, int Dollars);

    private static Literal? StringStart(string s, int i)
    {
        var j = i;
        var dollars = 0;
        var verbatim = false;
        while (j < s.Length && (s[j] == '$' || s[j] == '@'))
        {
            if (s[j] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            j++;
        }

        if (j >= s.Length || s[j] != '"' || (j > i && !char.IsWhiteSpace(Prev(s, i)) && char.IsLetterOrDigit(Prev(s, i))))
        {
            return null;
        }

        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"')
        {
            quotes++;
        }

        // "" is an empty regular string; three or more quotes open a raw string.
        return new Literal(j - i, quotes >= 3 ? quotes : 1, verbatim, dollars);
    }

    private static char Prev(string s, int i) => i > 0 ? s[i - 1] : ' ';

    private static void ScanString(string s, StringBuilder o, ref int i, Literal literal, bool blank)
    {
        i += literal.PrefixLength + literal.Quotes;
        var raw = literal.Quotes >= 3;
        var holeBraces = Math.Max(literal.Dollars, 1);

        while (i < s.Length)
        {
            var c = s[i];
            if (raw && Run(s, i, '"') >= literal.Quotes)
            {
                i += literal.Quotes;
                return;
            }

            if (!raw && c == '"')
            {
                if (literal.Verbatim && i + 1 < s.Length && s[i + 1] == '"')
                {
                    BlankIf(blank, o, i++);
                    BlankIf(blank, o, i++);
                    continue;
                }

                i++;
                return;
            }

            if (!raw && !literal.Verbatim && c == '\\')
            {
                BlankIf(blank, o, i++);
                if (i < s.Length)
                {
                    BlankIf(blank, o, i++);
                }

                continue;
            }

            if (!raw && !literal.Verbatim && c == '\n')
            {
                return; // unterminated regular string: stop at the line end
            }

            if (literal.Dollars > 0 && c == '{')
            {
                var run = Run(s, i, '{');
                if (run >= holeBraces && (raw || run % 2 == 1))
                {
                    i += run;
                    ScanCode(s, o, ref i, blank, stopAtHoleEnd: true);
                    i += Math.Min(holeBraces, Math.Max(Run(s, i, '}'), 1));
                    continue;
                }

                for (var k = 0; k < run; k++)
                {
                    BlankIf(blank, o, i++);
                }

                continue;
            }

            BlankIf(blank, o, i++);
        }
    }

    private static int Run(string s, int i, char c)
    {
        var n = 0;
        while (i + n < s.Length && s[i + n] == c)
        {
            n++;
        }

        return n;
    }

    private static void Blank(StringBuilder o, int i)
    {
        if (o[i] is not ('\n' or '\r'))
        {
            o[i] = ' ';
        }
    }

    private static void BlankIf(bool blank, StringBuilder o, int i)
    {
        if (blank)
        {
            Blank(o, i);
        }
    }
}
