using System.Text;
using System.Text.Json;

namespace ServerMonitor.WidgetContract;

/// <summary>
/// Serializes and deserializes the widget snapshot. Writing is deterministic and invariant (§28);
/// reading is <b>untrusted</b> (§17): malformed input yields <c>null</c> instead of throwing, so a
/// tampered or partially-written file makes the reader fail neutral rather than crash. Structural
/// validity is only the first gate — callers must then run <see cref="WidgetStateValidator"/> for bounds
/// and enum/timestamp checks.
/// </summary>
public static class WidgetStateSerializer
{
    /// <summary>Serializes to UTF-8 JSON bytes (the exact bytes the atomic writer persists).</summary>
    public static byte[] SerializeToUtf8Bytes(WidgetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, WidgetJsonContext.Default.WidgetStateSnapshot);
    }

    /// <summary>Serializes to a UTF-8 JSON string (test/inspection convenience).</summary>
    public static string Serialize(WidgetStateSnapshot snapshot) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(snapshot));

    /// <summary>
    /// Deserializes without trusting the payload. Returns <c>null</c> for ANY malformed input — invalid
    /// JSON, wrong shape, NaN/Infinity numbers, or an unknown enum string — so callers fail neutral (§17).
    /// Never throws for bad data.
    /// </summary>
    public static WidgetStateSnapshot? TryDeserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8Json, WidgetJsonContext.Default.WidgetStateSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>String overload of <see cref="TryDeserialize(ReadOnlySpan{byte})"/>.</summary>
    public static WidgetStateSnapshot? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, WidgetJsonContext.Default.WidgetStateSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
