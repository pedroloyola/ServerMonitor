using System.Text.Json.Serialization;

namespace ServerMonitor.WidgetContract;

/// <summary>
/// System.Text.Json source-generation context for the widget snapshot. Source-gen keeps serialization
/// reflection-free and AOT-friendly, which the future out-of-process COM provider needs. Enums are
/// written as strings for forward-readability; numbers use the invariant JSON format by construction.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(WidgetStateSnapshot))]
public sealed partial class WidgetJsonContext : JsonSerializerContext
{
}
