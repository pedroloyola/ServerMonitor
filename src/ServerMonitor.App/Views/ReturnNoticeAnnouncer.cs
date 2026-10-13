namespace ServerMonitor.App.Views;

/// <summary>
/// UI.12 B12B-02-R: the one announcement of the Servidores return notice ("Servidor ocultado / removido") per page visit.
/// <see cref="AfterFocus"/> runs after the page's focus decision and queues the raise for a later turn, so the focus change
/// is already out when the notification leaves. The queued raise is dropped if the page closed meanwhile
/// (<see cref="Close"/>, Unloaded / navigation replaced it), if the notice is no longer open or no longer on the live tree,
/// or if it was already said (Atlas L1: the old live-region path had the same "still live" gate). No WinUI types, so the
/// queue is controllable in tests.
/// </summary>
internal sealed class ReturnNoticeAnnouncer(
    Func<Action, bool> enqueue,
    Func<bool> isNoticeOpen,
    Func<bool> isNoticeLive,
    Func<string?> content,
    Action<string> raise)
{
    private bool _announced;
    private bool _closed;

    public void AfterFocus()
    {
        if (_closed || !isNoticeOpen())
        {
            return;
        }

        enqueue(() =>
        {
            var text = content();
            if (_closed || !ShouldAnnounce(_announced, isNoticeOpen(), text) || !isNoticeLive())
            {
                return;
            }

            _announced = true;
            raise(text!);
        });
    }

    /// <summary>The page is gone: nothing queued for it may be said any more.</summary>
    public void Close() => _closed = true;

    /// <summary>Pure rule (unit-tested): once, only while the notice is open and has something to say.</summary>
    internal static bool ShouldAnnounce(bool announced, bool noticeOpen, string? content) =>
        !announced && noticeOpen && !string.IsNullOrWhiteSpace(content);
}
