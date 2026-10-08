using System.Windows.Input;

namespace ServerMonitor.App.Controls;

/// <summary>
/// UI.8 D-UI8-7 / Beacon c1 B-1: what activating a Compact row does - the row's own server id, carried by the compiled
/// binding (<c>CommandParameter="{x:Bind ServerId}"</c>), goes to the window-mode exit command. MEASURED (c2): under the
/// ItemsRepeater an <c>x:DataType</c> row template receives its item through the generated <c>ProcessBindings</c>, which
/// detaches the DataContext handler and never sets <c>DataContext</c>; the c1 handler matched on <c>DataContext</c> and
/// silently did nothing. The id therefore never comes from <c>DataContext</c>.
/// </summary>
internal static class CompactRowActivation
{
    /// <summary>Runs <paramref name="openServerDetail"/> with the row's id; false (and nothing runs) without a usable id.</summary>
    public static bool Open(object? rowParameter, ICommand? openServerDetail)
    {
        if (rowParameter is not Guid serverId || serverId == Guid.Empty || openServerDetail is null || !openServerDetail.CanExecute(serverId))
        {
            return false;
        }

        openServerDetail.Execute(serverId);
        return true;
    }
}
