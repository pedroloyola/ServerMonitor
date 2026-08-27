using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Core.Tests.Workloads;

public sealed class WorkloadTextSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_null_or_empty_returns_empty(string? value)
    {
        Assert.Equal(string.Empty, WorkloadTextSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_collapses_control_characters_to_single_space()
    {
        Assert.Equal("a b c", WorkloadTextSanitizer.Sanitize("a\nb\tc"));
        Assert.Equal("a b", WorkloadTextSanitizer.Sanitize("a\r\n\n\tb"));
    }

    [Fact]
    public void Sanitize_trims_surrounding_whitespace_and_control()
    {
        Assert.Equal("name", WorkloadTextSanitizer.Sanitize("\n\t  name  \n"));
    }

    [Fact]
    public void Sanitize_removes_ansi_csi_color_sequences_including_letters()
    {
        // A plain control-strip would leave "[31m…[0m" behind; the whole sequence must go.
        Assert.Equal("red", WorkloadTextSanitizer.Sanitize("[31mred[0m"));
        Assert.Equal("bold", WorkloadTextSanitizer.Sanitize("[1;33mbold[m"));
    }

    [Fact]
    public void Sanitize_removes_osc_string_sequences_terminated_by_bel_or_st()
    {
        Assert.Equal("text", WorkloadTextSanitizer.Sanitize("]0;window-titletext"));
        Assert.Equal("text", WorkloadTextSanitizer.Sanitize("]0;title\\text"));
    }

    [Fact]
    public void Sanitize_removes_bidirectional_override_and_isolate_characters()
    {
        // Trojan-Source style spoofing: RLO/PDF and isolates must not survive.
        Assert.Equal("safename", WorkloadTextSanitizer.Sanitize("safe‮name‬"));
        Assert.Equal("ab", WorkloadTextSanitizer.Sanitize("a⁦⁩b"));
        Assert.Equal("ab", WorkloadTextSanitizer.Sanitize("a‎‏؜b"));
    }

    [Fact]
    public void Sanitize_preserves_legitimate_unicode_including_emoji()
    {
        Assert.Equal("café", WorkloadTextSanitizer.Sanitize("café"));
        Assert.Equal("日本語", WorkloadTextSanitizer.Sanitize("日本語"));
        Assert.Equal("web-🚀", WorkloadTextSanitizer.Sanitize("web-🚀"));
        Assert.Equal("a😀b", WorkloadTextSanitizer.Sanitize("a😀b"));
    }

    [Fact]
    public void Sanitize_drops_unpaired_surrogates()
    {
        Assert.Equal("ab", WorkloadTextSanitizer.Sanitize("a\uD800b")); // lone high surrogate
        Assert.Equal("ab", WorkloadTextSanitizer.Sanitize("a\uDC00b")); // lone low surrogate
    }

    [Fact]
    public void Sanitize_clamps_to_the_field_cap()
    {
        var sanitized = WorkloadTextSanitizer.Sanitize(new string('a', WorkloadLimits.MaxTextLength + 50));

        Assert.Equal(WorkloadLimits.MaxTextLength, sanitized.Length);
    }

    [Fact]
    public void Sanitize_never_splits_a_surrogate_pair_at_the_cap()
    {
        // Fill to one below the cap, then an emoji whose pair would straddle the boundary.
        var value = new string('a', WorkloadLimits.MaxTextLength - 1) + "😀tail";

        var sanitized = WorkloadTextSanitizer.Sanitize(value);

        Assert.True(sanitized.Length <= WorkloadLimits.MaxTextLength);
        Assert.DoesNotContain('�', sanitized);
        // The emoji did not fit as a whole, so it was not partially included.
        Assert.Equal(new string('a', WorkloadLimits.MaxTextLength - 1), sanitized);
    }

    [Fact]
    public void Sanitize_does_not_throw_on_a_truncated_escape_sequence()
    {
        Assert.Equal("text", WorkloadTextSanitizer.Sanitize("text["));
        Assert.Equal("text", WorkloadTextSanitizer.Sanitize("text]0;unterminated"));
        Assert.Equal("text", WorkloadTextSanitizer.Sanitize("text"));
    }

    [Fact]
    public void SanitizeOptional_preserves_null_and_maps_blank_to_null()
    {
        Assert.Null(WorkloadTextSanitizer.SanitizeOptional(null));
        Assert.Null(WorkloadTextSanitizer.SanitizeOptional(""));
        Assert.Equal("value", WorkloadTextSanitizer.SanitizeOptional("value"));
    }
}
