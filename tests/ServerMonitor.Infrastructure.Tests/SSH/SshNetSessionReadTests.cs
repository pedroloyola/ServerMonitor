using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Probes;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// The PRODUCTION <see cref="SshNetSession"/> path of one registered read, driven without a server: only the connect
/// (through the connector seam) and the one command (through the command seam) are substituted, so the exec, the
/// standard input write, the output caps, the standard error drain and the timeout are the session's own code.
/// </summary>
public sealed class SshNetSessionReadTests
{
    private static readonly byte[] Script = Encoding.UTF8.GetBytes("set -- 'nginx.service'\necho \"$1\"\n");

    [Fact]
    public async Task A_read_runs_the_constant_command_once_writes_its_input_and_returns_the_output()
    {
        var command = new FakeCommand { Output = Encoding.UTF8.GetBytes("line 1\nline 2\n"), ExitStatus = 0 };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec());

        Assert.Equal(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.Equal(["sh -s"], harness.CommandTexts);
        Assert.Equal(Script, command.WrittenInput);
        Assert.True(command.Disposed);
        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.True(read.WasExecuted);
        Assert.Equal(0, read.ExitStatus);
        Assert.Equal("line 1\nline 2\n", read.StandardOutput);
        Assert.False(read.OutputExceededLimit);
        Assert.False(read.TimedOut);
    }

    [Fact]
    public async Task Standard_error_over_16_KiB_is_dropped_and_the_output_is_still_returned()
    {
        var command = new FakeCommand
        {
            Output = Encoding.UTF8.GetBytes("out\n"),
            Error = new byte[256 * 1024],
            ExitStatus = 0,
        };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec());

        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.True(read.WasExecuted);
        Assert.False(read.OutputExceededLimit);
        Assert.Equal("out\n", read.StandardOutput);
        Assert.True(command.ErrorFullyRead);
    }

    [Fact]
    public async Task Output_over_the_read_limit_returns_no_output()
    {
        var command = new FakeCommand { Output = new byte[1025], ExitStatus = 0 };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec(outputLimit: 1024));

        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.True(read.OutputExceededLimit);
        Assert.Null(read.StandardOutput);
        Assert.False(read.TimedOut);
    }

    [Fact]
    public async Task Output_at_the_read_limit_is_returned()
    {
        var command = new FakeCommand { Output = Encoding.ASCII.GetBytes(new string('x', 1024)), ExitStatus = 0 };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec(outputLimit: 1024));

        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.False(read.OutputExceededLimit);
        Assert.Equal(1024, read.StandardOutput!.Length);
    }

    [Fact]
    public async Task A_command_that_outlives_the_read_timeout_is_reported_as_timed_out()
    {
        var command = new FakeCommand { NeverEnds = true };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec(timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(SshConnectionErrorCode.None, result.ErrorCode);
        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.True(read.TimedOut);
        Assert.False(read.WasExecuted);
        Assert.Null(read.StandardOutput);
        Assert.True(command.ExecutionCancelled);
    }

    [Fact]
    public async Task A_failed_input_write_fails_the_read_and_stops_the_command()
    {
        var command = new FakeCommand { NeverEnds = true, InputWriteFails = true };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec());

        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.True(result.AuthenticationCompleted);
        Assert.Null(result.RemoteRead);
        Assert.True(command.ExecutionCancelled);
        Assert.True(command.Disposed);
    }

    [Fact]
    public async Task A_caller_cancellation_is_not_reported_as_a_timeout()
    {
        var command = new FakeCommand { NeverEnds = true };
        using var harness = new Harness(command);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await harness.RunAsync(Exec(timeout: TimeSpan.FromMinutes(1)), cancel.Token);

        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.Null(result.RemoteRead);
    }

    [Fact]
    public async Task Any_command_text_but_the_constant_is_refused_before_a_command_is_created()
    {
        var command = new FakeCommand { Output = Encoding.UTF8.GetBytes("never"), ExitStatus = 0 };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec() with { CommandText = "sh -s; id" });

        Assert.NotEqual(SshConnectionErrorCode.None, result.ErrorCode);
        Assert.Null(result.RemoteRead);
        Assert.Empty(harness.CommandTexts);
    }

    [Fact]
    public async Task Output_that_is_not_utf8_is_returned_as_no_output()
    {
        var command = new FakeCommand { Output = [0x6f, 0x6b, 0xff, 0x0a], ExitStatus = 0 };
        using var harness = new Harness(command);

        var result = await harness.RunAsync(Exec());

        var read = Assert.IsType<RemoteReadExecution>(result.RemoteRead);
        Assert.True(read.WasExecuted);
        Assert.Null(read.StandardOutput);
    }

    [Theory]
    [InlineData(typeof(SshNetSession))]
    [InlineData(typeof(SshNetRemoteCommand))]
    [InlineData(typeof(BoundedRemoteOutputReader))]
    public void The_read_path_holds_no_logger_so_its_input_and_output_cannot_be_logged(Type type)
    {
        // ExecuteReadAsync and ExecuteDetailedAsync log nothing because the session and what they call hold no logger:
        // no field (instance or static, primary-constructor captures included) and no constructor parameter of a
        // logging type. Only SshConnectionService logs, and only ids and the state.
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        Assert.DoesNotContain(type.GetFields(all), field => IsLogging(field.FieldType));
        Assert.DoesNotContain(type.GetProperties(all), property => IsLogging(property.PropertyType));
        Assert.DoesNotContain(
            type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters()),
            parameter => IsLogging(parameter.ParameterType));
    }

    private static bool IsLogging(Type type) =>
        typeof(ILogger).IsAssignableFrom(type)
        || typeof(ILoggerFactory).IsAssignableFrom(type)
        || typeof(ILoggerProvider).IsAssignableFrom(type);

    private static RemoteReadExec Exec(int outputLimit = 64 * 1024, TimeSpan? timeout = null) =>
        new(ProbeShellInvocation.CommandText, Script, outputLimit, timeout ?? TimeSpan.FromSeconds(10));

    private sealed class Harness : IDisposable
    {
        private readonly SshNetSession _session;

        public Harness(FakeCommand command)
        {
            var password = new PasswordAuthenticationMethod("tester", "not-a-real-password");
            var info = new ConnectionInfo("127.0.0.1", 22, "tester", password) { Timeout = TimeSpan.FromSeconds(5) };
            _session = new SshNetSession(
                info,
                password,
                authenticationResource: null,
                connectGate: null,
                connectClient: static (_, _) => Task.CompletedTask,
                createCommand: (_, text, _) =>
                {
                    CommandTexts.Add(text);
                    return command;
                });
        }

        public List<string> CommandTexts { get; } = [];

        public Task<SshSessionResult> RunAsync(RemoteReadExec exec, CancellationToken cancellationToken = default) =>
            _session.RunReadAsync(_ => true, exec, cancellationToken);

        public void Dispose() => _session.Dispose();
    }

    private sealed class FakeCommand : IRemoteCommand
    {
        private readonly MemoryStream _input = new();
        private TrackingStream? _error;

        public byte[] Output { get; init; } = [];

        public byte[] Error { get; init; } = [];

        public bool NeverEnds { get; init; }

        public bool InputWriteFails { get; init; }

        public int? ExitStatus { get; init; }

        public bool ExecutionCancelled { get; private set; }

        public bool Disposed { get; private set; }

        public bool ErrorFullyRead => _error?.ReachedEnd == true;

        public byte[] WrittenInput => _input.ToArray();

        public Stream OutputStream => new MemoryStream(Output, writable: false);

        public Stream ExtendedOutputStream => _error ??= new TrackingStream(Error);

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            if (!NeverEnds)
            {
                return;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ExecutionCancelled = true;
                throw;
            }
        }

        public Stream CreateInputStream() =>
            InputWriteFails ? new FailingStream() : _input;

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool ReachedEnd { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            ReachedEnd |= read == 0;
            return read;
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The channel is closed."));
    }
}
