namespace ServerMonitor.WidgetContract;

/// <summary>Why a snapshot was rejected, for neutral logging (never the payload itself, §31).</summary>
public enum WidgetValidationFailure
{
    None = 0,
    NullSnapshot,
    UnsupportedSchemaVersion,
    GeneratedTimestampOutOfRange,
    TooManyServers,
    NullServerList,
    NullServer,
    EmptyServerId,
    DisplayNameNotSanitized,
    UndefinedHealth,
    UndefinedOverallHealth,
    MetricOutOfRange,
    LastUpdatedOutOfRange
}

/// <summary>Result of validating a snapshot read from the untrusted file (§17).</summary>
public readonly record struct WidgetValidationResult(bool IsValid, WidgetValidationFailure Failure)
{
    public static readonly WidgetValidationResult Valid = new(true, WidgetValidationFailure.None);

    public static WidgetValidationResult Invalid(WidgetValidationFailure failure) => new(false, failure);
}

/// <summary>
/// Validates a deserialized snapshot against the schema's bounds and enums before anything trusts it
/// (§17/§18). Applies L-018 defense-in-depth: even though the app wrote this file, an old, altered, or
/// logically incompatible file must never drive the widget with out-of-range or hostile values. A reader
/// that gets <see cref="WidgetValidationResult.IsValid"/> == <c>false</c> shows the "unavailable" state.
/// Pure and deterministic; the caller supplies "now" so the future check is testable.
/// </summary>
public static class WidgetStateValidator
{
    /// <summary>
    /// Validates <paramref name="snapshot"/> as of <paramref name="nowUtc"/>. Returns the first failure
    /// found, or <see cref="WidgetValidationResult.Valid"/>.
    /// </summary>
    public static WidgetValidationResult Validate(WidgetStateSnapshot? snapshot, DateTimeOffset nowUtc)
    {
        if (snapshot is null)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.NullSnapshot);
        }

        if (snapshot.SchemaVersion != WidgetSchema.CurrentVersion)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.UnsupportedSchemaVersion);
        }

        if (!IsTimestampInRange(snapshot.GeneratedAtUtc, nowUtc))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.GeneratedTimestampOutOfRange);
        }

        if (!IsDefined(snapshot.OverallHealth))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.UndefinedOverallHealth);
        }

        if (snapshot.Servers is null)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.NullServerList);
        }

        if (snapshot.Servers.Count > WidgetSchema.MaxServers)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.TooManyServers);
        }

        foreach (var server in snapshot.Servers)
        {
            var result = ValidateServer(server, nowUtc);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return WidgetValidationResult.Valid;
    }

    private static WidgetValidationResult ValidateServer(WidgetServerState? server, DateTimeOffset nowUtc)
    {
        if (server is null)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.NullServer);
        }

        if (server.Id == Guid.Empty)
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.EmptyServerId);
        }

        if (!WidgetDisplayName.IsSanitized(server.DisplayName))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.DisplayNameNotSanitized);
        }

        if (!IsDefined(server.Health))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.UndefinedHealth);
        }

        if (!IsPercentValid(server.CpuUsagePercent) ||
            !IsPercentValid(server.MemoryUsagePercent) ||
            !IsPercentValid(server.DiskUsagePercent))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.MetricOutOfRange);
        }

        if (server.LastUpdatedUtc is { } lastUpdated && !IsTimestampInRange(lastUpdated, nowUtc))
        {
            return WidgetValidationResult.Invalid(WidgetValidationFailure.LastUpdatedOutOfRange);
        }

        return WidgetValidationResult.Valid;
    }

    // null = unknown, always allowed (§19). A present value must be a finite number within [0, 100];
    // NaN/Infinity/out-of-range are rejected rather than clamped, because on read they signal tampering.
    private static bool IsPercentValid(double? value) =>
        value is not { } percent || (double.IsFinite(percent) && percent >= 0d && percent <= 100d);

    private static bool IsTimestampInRange(DateTimeOffset timestamp, DateTimeOffset nowUtc) =>
        timestamp > WidgetSchema.MinTimestampUtc && timestamp <= nowUtc + WidgetSchema.MaxClockSkew;

    private static bool IsDefined(WidgetHealth health) =>
        health is WidgetHealth.Unknown
            or WidgetHealth.Healthy
            or WidgetHealth.Warning
            or WidgetHealth.Critical
            or WidgetHealth.Offline;
}
