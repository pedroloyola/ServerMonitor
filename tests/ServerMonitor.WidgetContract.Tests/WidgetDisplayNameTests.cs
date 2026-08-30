using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

public sealed class WidgetDisplayNameTests
{
    // Constructed from explicit code points so the source file carries no invisible characters.
    private const char Esc = (char)0x1B;    // C0 control
    private const char Del = (char)0x7F;    // DEL
    private const char Bel = (char)0x07;    // C0 control
    private const char Rlo = (char)0x202E;  // right-to-left override (format)
    private const char Pdf = (char)0x202C;  // pop directional formatting (format)
    private const char Zwj = (char)0x200D;  // zero-width joiner (format)
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600); // 😀, 2 UTF-16 units

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_becomes_empty(string? raw)
    {
        Assert.Equal(string.Empty, WidgetDisplayName.Sanitize(raw));
    }

    [Fact]
    public void Plain_name_is_preserved()
    {
        Assert.Equal("Home Server", WidgetDisplayName.Sanitize("Home Server"));
    }

    [Fact]
    public void Unicode_letters_are_preserved()
    {
        Assert.Equal("Café Servidor 日本語", WidgetDisplayName.Sanitize("Café Servidor 日本語"));
    }

    [Fact]
    public void Control_characters_are_stripped()
    {
        var raw = $"web{Esc}[31mserver{Del}";
        var sanitized = WidgetDisplayName.Sanitize(raw);

        Assert.Equal("web[31mserver", sanitized);
        Assert.DoesNotContain(Esc, sanitized);
        Assert.DoesNotContain(Del, sanitized);
    }

    [Fact]
    public void Bidi_and_format_characters_are_stripped()
    {
        var raw = $"srv{Rlo}abc{Pdf}{Zwj}def";
        var sanitized = WidgetDisplayName.Sanitize(raw);

        Assert.Equal("srvabcdef", sanitized);
        Assert.True(WidgetDisplayName.IsSanitized(sanitized));
    }

    [Fact]
    public void Newlines_and_tabs_collapse_to_single_spaces()
    {
        Assert.Equal("a b c", WidgetDisplayName.Sanitize("  a\t\t b \n\n c  "));
    }

    [Fact]
    public void Length_is_capped()
    {
        var raw = new string('x', WidgetSchema.MaxDisplayNameLength + 40);
        var sanitized = WidgetDisplayName.Sanitize(raw);

        Assert.Equal(WidgetSchema.MaxDisplayNameLength, sanitized.Length);
        Assert.True(WidgetDisplayName.IsSanitized(sanitized));
    }

    [Fact]
    public void Astral_rune_never_exceeds_cap()
    {
        // Each emoji is two UTF-16 code units; the cap must not be overshot by a surrogate pair.
        var raw = string.Concat(Enumerable.Repeat(Emoji, WidgetSchema.MaxDisplayNameLength));
        var sanitized = WidgetDisplayName.Sanitize(raw);

        Assert.True(sanitized.Length <= WidgetSchema.MaxDisplayNameLength);
        Assert.True(WidgetDisplayName.IsSanitized(sanitized));
    }

    [Fact]
    public void IsSanitized_rejects_control_and_oversize()
    {
        Assert.False(WidgetDisplayName.IsSanitized($"bad{Bel}name"));
        Assert.False(WidgetDisplayName.IsSanitized($"bidi{Rlo}flip"));
        Assert.False(WidgetDisplayName.IsSanitized(new string('x', WidgetSchema.MaxDisplayNameLength + 1)));
        Assert.False(WidgetDisplayName.IsSanitized(null));
    }

    [Fact]
    public void Sanitize_output_is_always_self_consistent()
    {
        var samples = new[]
        {
            "normal", "  spaced  ", $"ctl{Bel}", $"{Rlo}RTL", $"emoji{Emoji}mix", "日本語", string.Empty
        };

        foreach (var sample in samples)
        {
            Assert.True(WidgetDisplayName.IsSanitized(WidgetDisplayName.Sanitize(sample)));
        }
    }
}
