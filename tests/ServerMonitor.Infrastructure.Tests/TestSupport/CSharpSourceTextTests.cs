using ServerMonitor.TestSupport;

namespace ServerMonitor.Infrastructure.Tests.TestSupport;

/// <summary>The lexer under the lexical fences: comments go, strings stay (or are blanked), length is preserved.</summary>
public sealed class CSharpSourceTextTests
{
    [Theory]
    [InlineData("var u = \"https://example.test/x\"; // gone", "var u = \"https://example.test/x\";")]
    [InlineData("var u = @\"C:\\x\"\"//y\"; /* gone */", "var u = @\"C:\\x\"\"//y\";")]
    [InlineData("var u = $\"{a[\"k\"]}//z\"; // gone", "var u = $\"{a[\"k\"]}//z\";")]
    [InlineData("var u = \"\"\"\n// kept\n\"\"\"; // gone", "var u = \"\"\"\n// kept\n\"\"\";")]
    [InlineData("var c = '/'; // gone", "var c = '/';")]
    public void Comments_are_removed_but_never_inside_a_literal(string code, string expected)
    {
        var stripped = CSharpSourceText.StripComments(code);

        Assert.Equal(code.Length, stripped.Length);
        Assert.Equal(expected, stripped.TrimEnd());
    }

    [Fact]
    public void Code_only_blanks_literal_contents_so_braces_in_strings_do_not_count()
    {
        const string code = "void M() { var s = \"}{\"; var t = $\"{x}}}\"; var c = '}'; }";

        var blanked = CSharpSourceText.CodeOnly(code);

        // Left: the method's pair and the interpolation hole's pair. Blanked: "}{", the escaped "}}" and '}'.
        Assert.Equal(code.Length, blanked.Length);
        Assert.Equal(2, blanked.Count(c => c == '{'));
        Assert.Equal(2, blanked.Count(c => c == '}'));
    }

    [Fact]
    public void Code_only_keeps_identifiers_outside_literals()
    {
        const string code = "var s = \"new WindowsCredentialStore()\"; var t = new WindowsCredentialStore();";

        var blanked = CSharpSourceText.CodeOnly(code);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(blanked, @"new\s+WindowsCredentialStore\s*\("));
    }
}
