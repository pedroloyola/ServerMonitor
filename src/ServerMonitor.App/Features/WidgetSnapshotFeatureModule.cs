using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.WidgetContract;

namespace ServerMonitor.App.Features;

/// <summary>
/// M13 Slice 1 widget snapshot (ADR-018): the recorder rides the SAME cycle signal (no new timer or
/// worker), builds a sanitized fleet snapshot from the live stores, and writes
/// <c>%LOCALAPPDATA%\ServerMonitor\widget-state.json</c> atomically. It is best-effort and
/// failure-isolated: a write fault never touches monitoring. The out-of-process widget provider reads that
/// file; nothing here starts COM, SSH or a second engine.
/// <para>
/// This module owns the ONLY <see cref="IWidgetStateWriter"/> registration in the product. A second writer
/// would put two processes' worth of writes behind one atomic-rename contract, so exactly-one is asserted
/// by test rather than left to review.
/// </para>
/// </summary>
public sealed class WidgetSnapshotFeatureModule : IFeatureModule
{
    public FeatureDescriptor Descriptor => CommunityFeatures.WidgetSnapshot;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(WidgetStateOptions.ForCurrentUser());
        services.AddSingleton<IWidgetStateWriter>(sp => new AtomicWidgetStateWriter(
            sp.GetRequiredService<WidgetStateOptions>(),
            sp.GetRequiredService<ILogger<AtomicWidgetStateWriter>>()));
        services.AddSingleton(sp => new WidgetSnapshotRecorder(
            sp.GetRequiredService<IServerService>(),
            sp.GetRequiredService<IServerMonitoringStateStore>(),
            sp.GetRequiredService<IServerMetricsStore>(),
            sp.GetRequiredService<IWidgetStateWriter>(),
            sp.GetRequiredService<ILogger<WidgetSnapshotRecorder>>()));
    }
}
