using System.Net;
using System.Runtime.InteropServices;

namespace WindowsGSH.Core.Windows;

/// <summary>
/// Thin wrapper around iphlpapi.dll's GetExtendedTcpTable/GetExtendedUdpTable - finds which process
/// owns a locally bound TCP/UDP endpoint. Used by Server Doctor's local-listening check to confirm a
/// listener actually belongs to the server's own process, rather than inferring ownership from a
/// configured bind address, which this app's own port declarations don't reliably carry.
/// IPv4 only: TCP_TABLE_OWNER_PID_LISTENER/UDP_TABLE_OWNER_PID only expose owning-PID information for
/// AF_INET; a server bound only to an IPv6 address will not be matched by this lookup.
/// </summary>
public static class NativeConnectionTable
{
    public readonly record struct OwnedEndpoint(IPEndPoint Endpoint, int OwningProcessId);

    // Null return means "this lookup could not be performed" (API failure, retry exhaustion, a
    // malformed response) - callers must treat that as "ownership unknown," never as "successfully
    // checked and found nothing owns it." A non-null (possibly empty) list means the table was read
    // successfully; an empty list there genuinely means no such listeners exist.
    public static IReadOnlyList<OwnedEndpoint>? GetTcpListenersWithOwners() =>
        ReadTable<NativeMethods.TcpRowOwnerPid>(
            (buffer, ref size) => NativeMethods.GetExtendedTcpTable(
                buffer, ref size, sort: false, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidListener, reserved: 0),
            row => new OwnedEndpoint(
                new IPEndPoint(new IPAddress(row.LocalAddr), SwapPort(row.LocalPort)),
                unchecked((int)row.OwningPid)));

    public static IReadOnlyList<OwnedEndpoint>? GetUdpListenersWithOwners() =>
        ReadTable<NativeMethods.UdpRowOwnerPid>(
            (buffer, ref size) => NativeMethods.GetExtendedUdpTable(
                buffer, ref size, sort: false, NativeMethods.AfInet, NativeMethods.UdpTableOwnerPid, reserved: 0),
            row => new OwnedEndpoint(
                new IPEndPoint(new IPAddress(row.LocalAddr), SwapPort(row.LocalPort)),
                unchecked((int)row.OwningPid)));

    private delegate uint TableQuery(IntPtr buffer, ref int size);

    // Every GetExtended*Table call follows the same two-call shape: call once with a null buffer to
    // learn the required size, allocate exactly that much, then call again to fill it. The table can
    // grow between those two calls (a process opening a new listener in that window) - ERROR_INSUFFICIENT_BUFFER
    // on the second call means "try again with the size it just told you," not a real failure, so
    // this retries a bounded number of times rather than giving up on the first race it hits.
    // Returns null (not an empty list) whenever the table genuinely could not be read - retry
    // exhaustion, a non-success result code, or a response whose own header doesn't fit what was
    // actually allocated - so a caller can't mistake "we failed to check" for "we checked and found
    // nothing," which would otherwise let an inspection failure masquerade as a confident negative.
    private static IReadOnlyList<OwnedEndpoint>? ReadTable<TRow>(TableQuery query, Func<TRow, OwnedEndpoint> project)
        where TRow : struct
    {
        var size = 0;
        query(IntPtr.Zero, ref size);

        for (var attempt = 0; attempt < NativeMethods.MaxAttempts; attempt++)
        {
            if (size <= 0)
            {
                return null;
            }

            var allocatedSize = size;
            var buffer = Marshal.AllocHGlobal(allocatedSize);
            try
            {
                var result = query(buffer, ref size);
                if (result == NativeMethods.ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != NativeMethods.ErrorSuccess)
                {
                    return null;
                }

                var entryCount = Marshal.ReadInt32(buffer, 0);
                var rowSize = Marshal.SizeOf<TRow>();
                if (entryCount < 0 || rowSize <= 0)
                {
                    return null;
                }

                // dwNumEntries is untrusted data from the OS response, not a value this code
                // controls - a corrupt or unexpected count must be rejected before it's used to
                // compute a row pointer, or a large/negative-wrapping value could read past the
                // buffer actually allocated for this call. long arithmetic (entryCount and rowSize
                // are both int-sized) can't itself overflow here; the check against allocatedSize is
                // what catches a corrupt count, not the arithmetic width.
                var requiredBytes = checked(sizeof(int) + (long)entryCount * rowSize);
                if (requiredBytes > allocatedSize)
                {
                    return null;
                }

                var rows = new List<OwnedEndpoint>(entryCount);
                for (var i = 0; i < entryCount; i++)
                {
                    var rowPtr = IntPtr.Add(buffer, sizeof(int) + i * rowSize);
                    rows.Add(project(Marshal.PtrToStructure<TRow>(rowPtr)));
                }

                return rows;
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                // A malformed response that still slips past the bounds check above (or a checked-
                // arithmetic overflow) must not crash the caller - this lookup is an optional
                // diagnostic enhancement, never a hard requirement - but it's still a failed
                // inspection, so null (not empty), same as every other failure path here.
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    // dwLocalPort stores the port in network byte order within the low 16 bits of the DWORD,
    // regardless of the bSort argument - a well-documented quirk of these APIs. Swapping the two
    // bytes converts it to the ordinary host-order port number every other part of this app uses.
    private static int SwapPort(uint rawPort)
    {
        var low = (ushort)rawPort;
        return ((low & 0xFF) << 8) | (low >> 8);
    }

    private static class NativeMethods
    {
        internal const int AfInet = 2;
        internal const int TcpTableOwnerPidListener = 3;
        internal const int UdpTableOwnerPid = 1;
        internal const uint ErrorSuccess = 0;
        internal const uint ErrorInsufficientBuffer = 122;
        internal const int MaxAttempts = 6;

        [StructLayout(LayoutKind.Sequential)]
        internal struct TcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UdpRowOwnerPid
        {
            public uint LocalAddr;
            public uint LocalPort;
            public uint OwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool sort,
            int ipVersion,
            int tableClass,
            int reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedUdpTable(
            IntPtr udpTable,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool sort,
            int ipVersion,
            int tableClass,
            int reserved);
    }
}
