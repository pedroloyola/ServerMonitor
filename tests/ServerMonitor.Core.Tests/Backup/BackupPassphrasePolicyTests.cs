using System.Text;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.Core.Tests.Backup;

public sealed class BackupPassphrasePolicyTests
{
    private const string Composed = "café crème brûlée!";
    private const string Decomposed = "café crème brûlée!";

    [Theory]
    [InlineData(11, BackupPassphraseProblem.TooShort)]
    [InlineData(12, BackupPassphraseProblem.None)]
    [InlineData(256, BackupPassphraseProblem.None)]
    [InlineData(257, BackupPassphraseProblem.TooLong)]
    public void Export_LengthBounds(int length, BackupPassphraseProblem expected)
    {
        var passphrase = new string('p', length);

        Assert.Equal(expected, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
    }

    [Fact]
    public void Export_Empty_IsEmpty()
    {
        Assert.Equal(BackupPassphraseProblem.Empty, BackupPassphrasePolicy.ValidateForExport("", ""));
    }

    // Length is counted in Unicode scalar values: a surrogate pair is one character.
    [Theory]
    [InlineData(11, BackupPassphraseProblem.TooShort)]
    [InlineData(12, BackupPassphraseProblem.None)]
    [InlineData(256, BackupPassphraseProblem.None)]
    [InlineData(257, BackupPassphraseProblem.TooLong)]
    public void Export_LengthCountsScalarValues(int emoji, BackupPassphraseProblem expected)
    {
        var passphrase = string.Concat(Enumerable.Repeat("\U0001F511", emoji));

        Assert.Equal(expected, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
    }

    // Length is counted after NFC: "e" + combining acute is one character.
    [Theory]
    [InlineData(11, BackupPassphraseProblem.TooShort)]
    [InlineData(12, BackupPassphraseProblem.None)]
    public void Export_LengthIsMeasuredOnTheNfcForm(int count, BackupPassphraseProblem expected)
    {
        var passphrase = string.Concat(Enumerable.Repeat("é", count));

        Assert.Equal(expected, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
    }

    [Fact]
    public void Export_AbsurdlyLongInput_IsTooLong_WithoutNormalizing()
    {
        var passphrase = new string('p', BackupPassphrasePolicy.MaximumInputLength + 1);

        Assert.Equal(BackupPassphraseProblem.TooLong, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
        Assert.Equal(BackupPassphraseProblem.TooLong, BackupPassphrasePolicy.ValidateForRestore(passphrase));
    }

    // Built at runtime: attribute strings are stored as UTF-8 in metadata and a lone surrogate would
    // silently become U+FFFD, a valid character.
    public static TheoryData<string> LoneSurrogates() =>
    [
        '\uD800' + " then twelve more",
        "twelve more then " + '\uDC00',
        "reversed pair " + '\uDC00' + '\uD800' + " x",
        "high at the very end xx" + '\uDBFF',
    ];

    [Theory]
    [MemberData(nameof(LoneSurrogates), DisableDiscoveryEnumeration = true)]
    public void LoneSurrogate_IsAPolicyProblem_NotACrash(string passphrase)
    {
        Assert.Contains(passphrase, char.IsSurrogate);

        Assert.Equal(BackupPassphraseProblem.InvalidCharacters, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
        Assert.Equal(BackupPassphraseProblem.InvalidCharacters, BackupPassphrasePolicy.ValidateForRestore(passphrase));
    }

    [Fact]
    public void Export_ConfirmationWithLoneSurrogate_IsMismatch_NotACrash()
    {
        Assert.Equal(
            BackupPassphraseProblem.ConfirmationMismatch,
            BackupPassphrasePolicy.ValidateForExport("valid passphrase", "valid passphrase" + '\uD800'));
    }

    [Theory]
    [InlineData("valid passphrase", "valid passphrasE")]
    [InlineData("valid passphrase", "valid passphrase ")]
    [InlineData("valid passphrase", "")]
    public void Export_ConfirmationMustMatchExactly_NoTrimming(string passphrase, string confirmation)
    {
        Assert.Equal(
            BackupPassphraseProblem.ConfirmationMismatch,
            BackupPassphrasePolicy.ValidateForExport(passphrase, confirmation));
    }

    [Fact]
    public void Export_ConfirmationTypedWithDifferentNormalization_Matches()
    {
        Assert.Equal(BackupPassphraseProblem.None, BackupPassphrasePolicy.ValidateForExport(Composed, Decomposed));
    }

    [Fact]
    public void Export_NoCompositionRules_TwelveSpacesAreAccepted()
    {
        var passphrase = new string(' ', 12);

        Assert.Equal(BackupPassphraseProblem.None, BackupPassphrasePolicy.ValidateForExport(passphrase, passphrase));
    }

    [Theory]
    [InlineData("short", BackupPassphraseProblem.None)]
    [InlineData("", BackupPassphraseProblem.Empty)]
    public void Restore_DoesNotEnforceTheMinimum_ButRejectsEmpty(string passphrase, BackupPassphraseProblem expected)
    {
        Assert.Equal(expected, BackupPassphrasePolicy.ValidateForRestore(passphrase));
    }

    [Fact]
    public void Restore_EnforcesTheMaximum()
    {
        Assert.Equal(BackupPassphraseProblem.None, BackupPassphrasePolicy.ValidateForRestore(new string('p', 256)));
        Assert.Equal(BackupPassphraseProblem.TooLong, BackupPassphrasePolicy.ValidateForRestore(new string('p', 257)));
    }

    [Fact]
    public void ToNormalizedUtf8_ComposedAndDecomposed_ProduceIdenticalNfcBytes()
    {
        var composed = BackupPassphrasePolicy.ToNormalizedUtf8(Composed);
        var decomposed = BackupPassphrasePolicy.ToNormalizedUtf8(Decomposed);

        Assert.Equal(Encoding.UTF8.GetBytes(Composed), composed);
        Assert.Equal(composed, decomposed);
    }

    public static TheoryData<string> InvalidForEncoding() => ["secret " + '\uD800' + " marker", string.Empty];

    [Theory]
    [MemberData(nameof(InvalidForEncoding), DisableDiscoveryEnumeration = true)]
    public void ToNormalizedUtf8_Invalid_ThrowsWithoutEchoingTheInput(string passphrase)
    {
        var exception = Assert.Throws<ArgumentException>(() => BackupPassphrasePolicy.ToNormalizedUtf8(passphrase));

        Assert.DoesNotContain("marker", exception.Message, StringComparison.Ordinal);
    }
}
