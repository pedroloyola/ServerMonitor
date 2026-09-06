using ServerMonitor.Features;

namespace ServerMonitor.Features.Tests;

/// <summary>
/// C2. A feature id is a compile-time capability name, not remote input: these assert that it stays a
/// well-shaped, comparable identifier and reject the shapes that would make ids ambiguous or unreadable.
/// The validation is a shape check, never a trust boundary.
/// </summary>
public sealed class FeatureIdTests
{
    [Theory]
    [InlineData("history.local")]
    [InlineData("workloads.readonly")]
    [InlineData("discovery.mdns")]
    [InlineData("widget.snapshot")]
    [InlineData("a")]
    [InlineData("a.b.c.d")]
    [InlineData("read-only.v2")]
    [InlineData("x9")]
    public void WellFormedIds_AreAccepted(string value)
    {
        var id = new FeatureId(value);

        Assert.True(id.IsSpecified);
        Assert.Equal(value, id.Value);
        Assert.Equal(value, id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("History.Local")]      // uppercase
    [InlineData("history local")]      // whitespace
    [InlineData(".history")]           // leading separator
    [InlineData("history.")]           // trailing separator
    [InlineData("history..local")]     // doubled separator
    [InlineData("history/local")]      // path-ish
    [InlineData("history:local")]
    [InlineData("history_local")]      // underscore is not in the alphabet
    [InlineData("história.local")]     // non-ASCII
    public void MalformedIds_AreRejected(string value)
    {
        Assert.False(FeatureId.IsWellFormed(value));

        var error = Assert.Throws<ArgumentException>(() => new FeatureId(value));
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void NullIsRejected() => Assert.False(FeatureId.IsWellFormed(null));

    [Fact]
    public void LongestAcceptedIdIsBounded()
    {
        var atLimit = new string('a', FeatureId.MaxLength);
        var overLimit = new string('a', FeatureId.MaxLength + 1);

        Assert.True(FeatureId.IsWellFormed(atLimit));
        Assert.False(FeatureId.IsWellFormed(overLimit));
    }

    [Fact]
    public void EqualityIsOrdinalAndValueBased()
    {
        var left = new FeatureId("history.local");
        var right = new FeatureId("history.local");
        var other = new FeatureId("workloads.readonly");

        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.True(left != other);
        Assert.False(left.Equals(other));
    }

    [Fact]
    public void DefaultStructIsUnusableRatherThanSilentlyEmpty()
    {
        FeatureId unset = default;

        Assert.False(unset.IsSpecified);
        Assert.Throws<InvalidOperationException>(() => unset.Value);
    }
}
