using System.Diagnostics.CodeAnalysis;

namespace ServerMonitor.Features;

/// <summary>
/// Stable, opaque identifier for one composable capability, e.g. <c>history.local</c>.
/// <para>
/// It is a validated string and NOT an enum on purpose (ADR-020 §2): an enum declared in this public
/// assembly could not be extended by an out-of-tree module without editing this assembly, which would
/// invert the dependency direction ADR-019 exists to protect.
/// </para>
/// <para>
/// It is validated, and NOT an arbitrary string, for the opposite reason: a capability id is an internal
/// name chosen at compile time by the module that declares it. It never arrives from a file, a network,
/// a user or a remote party, so this validation is a shape check that keeps ids comparable and
/// diagnosable — it is <b>not</b> a trust boundary and must never be relied on as one.
/// </para>
/// </summary>
public readonly struct FeatureId : IEquatable<FeatureId>
{
    /// <summary>Longest accepted id. Generous for a compile-time constant, bounded so a malformed one is loud.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    public FeatureId(string value)
    {
        if (!IsWellFormed(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a well-formed feature id. Expected 1-{MaxLength} characters of " +
                "lowercase ASCII letters, digits, '-' or '.', with '.' used only as a separator between " +
                "non-empty segments (for example 'history.local').",
                nameof(value));
        }

        _value = value;
    }

    /// <summary>The identifier text. Never null for a constructed id; the default struct is not valid.</summary>
    public string Value => _value ?? throw new InvalidOperationException(
        "This FeatureId is the default value and was never constructed. Feature ids must be created " +
        "explicitly by the module that declares them.");

    /// <summary>True when this id was actually constructed (guards the default-struct hole).</summary>
    public bool IsSpecified => _value is not null;

    /// <summary>Shape check for an id. See the type remarks: this is not a trust boundary.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        // '.' separates non-empty segments: never leading, never trailing, never doubled.
        if (value[0] == '.' || value[^1] == '.')
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var allowed = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '.';
            if (!allowed)
            {
                return false;
            }

            if (c == '.' && value[i - 1] == '.')
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(FeatureId other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is FeatureId other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public override string ToString() => _value ?? "(unspecified)";

    public static bool operator ==(FeatureId left, FeatureId right) => left.Equals(right);

    public static bool operator !=(FeatureId left, FeatureId right) => !left.Equals(right);
}
