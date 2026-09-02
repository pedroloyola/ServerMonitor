using System.Runtime.InteropServices;
using ServerMonitor.WidgetProvider.Diagnostics;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// M13-QA-7 regression suite, second half: the provider must not be able to show a Windows Error
/// Reporting dialog. A GUI-subsystem process has no console to print a stack trace into, so an exception
/// escaping Main ends the process through WER — the last residual path from this provider to pixels on
/// the user's desktop during a board activation. <see cref="Program.RunGuarded"/> is the barrier; these
/// tests exercise it on the same seam Main uses.
/// </summary>
public sealed class WidgetProviderFatalErrorTests
{
    private const int EFail = unchecked((int)0x80004005);
    private const int EAccessDenied = unchecked((int)0x80070005);

    [Fact]
    public void A_successful_body_returns_its_own_exit_code()
    {
        var exitCode = Program.RunGuarded(() => NullWidgetProviderLog.Instance, _ => 0);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void A_registration_failure_hresult_is_returned_unchanged_and_not_swallowed()
    {
        // The registration path reports its own HRESULT by RETURNING it; the guard must stay transparent
        // to that rather than remapping it.
        var log = new RecordingLog();

        var exitCode = Program.RunGuarded(() => log, _ => EAccessDenied);

        Assert.Equal(EAccessDenied, exitCode);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void An_unhandled_body_exception_is_reported_and_converted_to_a_failure_hresult()
    {
        var log = new RecordingLog();

        var exitCode = Program.RunGuarded(() => log, _ => throw new InvalidOperationException("boom"));

        Assert.True(exitCode < 0, $"0x{exitCode:X8} is not a failure HRESULT.");
        Assert.Contains(log.Warnings, warning => warning.Contains(nameof(InvalidOperationException)));
        // Coarse diagnostics only: the message never carries the exception text (ADR-018 §31).
        Assert.DoesNotContain(log.Warnings, warning => warning.Contains("boom"));
    }

    [Fact]
    public void A_com_exception_keeps_its_own_hresult()
    {
        var exitCode = Program.RunGuarded(
            () => NullWidgetProviderLog.Instance,
            _ => throw new COMException("registration", EAccessDenied));

        Assert.Equal(EAccessDenied, exitCode);
    }

    [Fact]
    public void A_failure_without_a_usable_hresult_falls_back_to_e_fail()
    {
        Assert.Equal(EFail, Program.FailureHResult(new SuccessCodedException()));
    }

    [Fact]
    public void A_log_that_cannot_be_created_still_cannot_reach_wer()
    {
        var exitCode = Program.RunGuarded(
            () => throw new InvalidOperationException("no log"),
            _ => 0);

        Assert.True(exitCode < 0, $"0x{exitCode:X8} is not a failure HRESULT.");
    }

    [Fact]
    public void A_log_that_throws_while_reporting_still_cannot_reach_wer()
    {
        var exitCode = Program.RunGuarded(
            () => new ThrowingLog(),
            _ => throw new InvalidOperationException("boom"));

        Assert.True(exitCode < 0, $"0x{exitCode:X8} is not a failure HRESULT.");
    }

    private sealed class SuccessCodedException : Exception
    {
        public SuccessCodedException()
        {
            HResult = 0;
        }
    }

    private sealed class RecordingLog : IWidgetProviderLog
    {
        public List<string> Warnings { get; } = [];

        public void Warn(string message) => Warnings.Add(message);

        public void Info(string message)
        {
        }
    }

    private sealed class ThrowingLog : IWidgetProviderLog
    {
        public void Warn(string message) => throw new InvalidOperationException("log is down");

        public void Info(string message) => throw new InvalidOperationException("log is down");
    }
}
