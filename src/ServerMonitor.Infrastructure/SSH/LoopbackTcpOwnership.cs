using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// Owner-PID queries over the IPv4 TCP table (<c>GetExtendedTcpTable</c>, AF_INET). Used by the ProxyJump
/// tunnel to admit only connections that THIS process opened to its loopback listener (M14-PJ-LOOP-1 C1) and
/// to prove the listener really is an IPv4-loopback socket owned by this process (C2).
/// </summary>
internal interface ITcpOwnerLookup
{
    /// <summary>
    /// The owning PID of the IPv4 connection whose LOCAL end is <c>127.0.0.1:localPort</c> and whose REMOTE end
    /// is <c>127.0.0.1:remotePort</c> — i.e. the client side of a loopback connection. <see langword="null"/>
    /// when there is no such row. Throws when the table cannot be read.
    /// </summary>
    int? FindLoopbackConnectionOwner(int localPort, int remotePort);

    /// <summary>Whether an IPv4 listener on exactly <c>127.0.0.1:port</c> is owned by <paramref name="processId"/>.</summary>
    bool IsLoopbackListenerOwnedBy(int port, int processId);
}

internal sealed class IpHelperTcpOwnerLookup : ITcpOwnerLookup
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int RowSize = 24;

    // 127.0.0.1 as the DWORD the table stores (network byte order read as little-endian).
    private static readonly uint LoopbackAddress = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());

    public static IpHelperTcpOwnerLookup Instance { get; } = new();

    public int? FindLoopbackConnectionOwner(int localPort, int remotePort)
    {
        foreach (var row in ReadTable(TcpTableOwnerPidConnections))
        {
            if (row.LocalAddress == LoopbackAddress
                && row.LocalPort == localPort
                && row.RemoteAddress == LoopbackAddress
                && row.RemotePort == remotePort)
            {
                return row.OwningPid;
            }
        }

        return null;
    }

    public bool IsLoopbackListenerOwnedBy(int port, int processId)
    {
        foreach (var row in ReadTable(TcpTableOwnerPidListener))
        {
            if (row.LocalAddress == LoopbackAddress && row.LocalPort == port && row.OwningPid == processId)
            {
                return true;
            }
        }

        return false;
    }

    private static List<Row> ReadTable(int tableClass)
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, tableClass, 0);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (status is not (NoError or ErrorInsufficientBuffer))
            {
                break;
            }

            // Headroom: the table can grow between the size query and the read.
            size += 16 * RowSize;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, false, AfInet, tableClass, 0);
                if (status == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (status != NoError)
                {
                    break;
                }

                var count = Marshal.ReadInt32(buffer);
                var rows = new List<Row>(count);
                for (var index = 0; index < count; index++)
                {
                    var row = buffer + 4 + (index * RowSize);
                    rows.Add(new Row(
                        LocalAddress: unchecked((uint)Marshal.ReadInt32(row, 4)),
                        LocalPort: Port(Marshal.ReadInt32(row, 8)),
                        RemoteAddress: unchecked((uint)Marshal.ReadInt32(row, 12)),
                        RemotePort: Port(Marshal.ReadInt32(row, 16)),
                        OwningPid: Marshal.ReadInt32(row, 20)));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException($"The TCP table could not be read (status {status}).");
    }

    // The port is the low 16 bits of the DWORD, in network byte order.
    private static int Port(int raw) => BinaryPrimitives.ReverseEndianness(unchecked((ushort)(raw & 0xFFFF)));

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    private readonly record struct Row(uint LocalAddress, int LocalPort, uint RemoteAddress, int RemotePort, int OwningPid);
}

internal enum OriginatorRejection
{
    None,
    NotArmed,
    NotLoopback,
    OwnerNotFound,
    LookupFailed,
    ForeignOwner
}

internal readonly record struct OriginatorDecision(bool Admitted, OriginatorRejection Rejection, int? OwnerPid)
{
    public static OriginatorDecision Admit(int ownerPid) => new(true, OriginatorRejection.None, ownerPid);

    public static OriginatorDecision Reject(OriginatorRejection rejection, int? ownerPid = null) =>
        new(false, rejection, ownerPid);
}

/// <summary>
/// M14-PJ-LOOP-1 C1: the originator gate of the tunnel's loopback listener. It is armed for exactly ONE
/// connection immediately before each target connect and seals itself on admitting it. A connection is
/// admitted only while armed AND when the TCP table shows its originator (<c>127.0.0.1:originatorPort</c> →
/// <c>127.0.0.1:boundPort</c>) is owned by this process. Every other outcome — unarmed, not IPv4 loopback,
/// not found, lookup failure, another owner — is a rejection (fail closed). A rejected foreign connection
/// never consumes the arm, so it cannot starve our own connect of its slot.
/// </summary>
internal sealed class LoopbackOriginatorGate : ISshConnectGate
{
    private readonly ITcpOwnerLookup _lookup;
    private readonly int _processId;
    private int _armed;

    public LoopbackOriginatorGate()
        : this(IpHelperTcpOwnerLookup.Instance, Environment.ProcessId)
    {
    }

    internal LoopbackOriginatorGate(ITcpOwnerLookup lookup, int processId)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _processId = processId;
    }

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>Arms the gate for exactly one admission.</summary>
    public void ArmNext() => Volatile.Write(ref _armed, 1);

    /// <summary>Seals the gate: nothing is admitted until the next <see cref="ArmNext"/>.</summary>
    public void Seal() => Volatile.Write(ref _armed, 0);

    void ISshConnectGate.BeforeConnect() => ArmNext();

    void ISshConnectGate.AfterConnect() => Seal();

    public OriginatorDecision Evaluate(string? originatorHost, uint originatorPort, uint boundPort)
    {
        // The owner is resolved even when the gate is sealed, so every rejection can be logged with the
        // originator's PID; every branch below still rejects unless ALL conditions hold.
        if (!IPAddress.TryParse(originatorHost, out var address)
            || !address.Equals(IPAddress.Loopback)
            || originatorPort is 0 or > 65535
            || boundPort is 0 or > 65535)
        {
            return OriginatorDecision.Reject(OriginatorRejection.NotLoopback);
        }

        int? owner;
        try
        {
            owner = _lookup.FindLoopbackConnectionOwner((int)originatorPort, (int)boundPort);
        }
        catch
        {
            return OriginatorDecision.Reject(OriginatorRejection.LookupFailed);
        }

        if (owner is null)
        {
            return OriginatorDecision.Reject(OriginatorRejection.OwnerNotFound);
        }

        if (owner.Value != _processId)
        {
            return OriginatorDecision.Reject(OriginatorRejection.ForeignOwner, owner);
        }

        if (!IsArmed)
        {
            return OriginatorDecision.Reject(OriginatorRejection.NotArmed, owner);
        }

        // Consume the single arm atomically: a second connection racing the first is rejected.
        return Interlocked.CompareExchange(ref _armed, 0, 1) == 1
            ? OriginatorDecision.Admit(owner.Value)
            : OriginatorDecision.Reject(OriginatorRejection.NotArmed, owner);
    }
}
