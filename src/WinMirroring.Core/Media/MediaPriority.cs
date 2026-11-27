using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinMirroring.Core.Media;

/// <summary>
/// Asks Windows to send what the media socket sends to a peer ahead of this device's ordinary traffic, through the
/// system's own quality-of-service service (qWAVE).
///
/// This matters most on Wi-Fi, and for a reason that has nothing to do with bandwidth. Wireless traffic is sorted
/// into four classes, and a station's bulk class — where everything on the session's TCP connection lives, a
/// screen stream that travels there included, and whatever else the machine is up or downloading — can hold the
/// air for long stretches at a time. A sound packet behind it waits, and fifty of them a second each waiting a
/// few tens of milliseconds is exactly the uneven arrival that empties a playout buffer. Marking the media socket
/// as voice traffic puts it in the class that goes first, both in Windows' own send queues and in the wireless
/// adapter.
///
/// What this does not do is put sound ahead of the screen's video, though it was written as if it did. To qWAVE a
/// flow is a socket and a destination, and a node has one media socket: the sound and the video datagrams of a
/// screen stream (VideoLink) leave it for the same address and port, so they are one flow with one class. Both ask
/// for it, and are counted, so the marking lasts for as long as either is sending; but whichever asked, both
/// travel as voice, and between themselves they go out in the order they were written. Sound is ahead of the
/// screen only while the screen is on the TCP connection. Putting it ahead of video sent from here would take a
/// socket of its own for one of the two, which is a change to how media is sent and received, not to this class.
///
/// Entirely best-effort: on a machine where the service is off, or where marking is not permitted, every call here
/// quietly does nothing and the media simply travels as it did before.
/// </summary>
internal sealed class MediaPriority : IDisposable
{
    private const int QosTrafficTypeVoice = 4;
    private const uint QosNonAdaptiveFlow = 0x00000002;
    private const int QosSetOutgoingDscpValue = 3;

    /// <summary>Expedited forwarding: what voice traffic is marked with everywhere.</summary>
    private const uint DscpExpeditedForwarding = 46;

    private readonly ILogger _logger;
    private readonly Socket _socket;
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, Flow> _flows = [];
    private nint _handle;
    private bool _unavailable;

    public MediaPriority(Socket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
    }

    /// <summary>
    /// Marks what this socket sends to <paramref name="peer"/> as voice traffic — all of it, sound and video
    /// alike. Counted, so each call is to be matched by one <see cref="Remove"/>.
    /// </summary>
    public void Add(IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            AddCore(peer);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException)
        {
            // No quality-of-service library on this machine at all. Nothing here is worth failing a stream over.
            _unavailable = true;
            _logger.LogDebug("Media cannot be prioritised on this machine: {Message}", ex.Message);
        }
    }

    private void AddCore(IPEndPoint peer)
    {
        lock (_gate)
        {
            if (_flows.TryGetValue(peer, out var existing))
            {
                existing.Users++;
                return;
            }

            if (!TryOpenHandle())
            {
                return;
            }

            var flowId = 0u;
            if (!QOSAddSocketToFlow(_handle, _socket.Handle, Serialize(peer), QosTrafficTypeVoice, QosNonAdaptiveFlow, ref flowId))
            {
                _logger.LogDebug("Media to {Peer} could not be given priority (error {Error})", peer, Marshal.GetLastWin32Error());
                return;
            }

            // Also ask for the usual voice marking in the IP header. Windows only allows this to a process with
            // administrator rights; without it the traffic class above still applies, which is the part that
            // matters on Wi-Fi.
            var dscp = DscpExpeditedForwarding;
            QOSSetFlow(_handle, flowId, QosSetOutgoingDscpValue, sizeof(uint), ref dscp, 0, nint.Zero);

            _flows[peer] = new Flow { Id = flowId, Users = 1 };
            _logger.LogInformation("Media to {Peer}, sound and screen video alike, goes out as voice traffic", peer);
        }
    }

    /// <summary>Gives up the marking for <paramref name="peer"/> once nothing is sending there any more.</summary>
    public void Remove(IPEndPoint peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (_gate)
        {
            if (!_flows.TryGetValue(peer, out var flow) || --flow.Users > 0)
            {
                return;
            }

            _flows.Remove(peer);
            RemoveFlow(flow.Id);
        }
    }

    private bool TryOpenHandle()
    {
        if (_handle != nint.Zero)
        {
            return true;
        }

        if (_unavailable)
        {
            return false;
        }

        var version = new QosVersion { Major = 1, Minor = 0 };
        if (QOSCreateHandle(ref version, out var handle) && handle != nint.Zero)
        {
            _handle = handle;
            return true;
        }

        _unavailable = true;
        _logger.LogDebug(
            "Windows quality of service is not available (error {Error}); media will not be prioritised",
            Marshal.GetLastWin32Error());
        return false;
    }

    private void RemoveFlow(uint flowId)
    {
        try
        {
            QOSRemoveSocketFromFlow(_handle, _socket.Handle, flowId, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static byte[] Serialize(IPEndPoint peer)
    {
        var address = peer.Serialize();
        var bytes = new byte[address.Size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = address[i];
        }

        return bytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == nint.Zero)
            {
                return;
            }

            foreach (var flow in _flows.Values)
            {
                RemoveFlow(flow.Id);
            }

            _flows.Clear();
            try
            {
                QOSCloseHandle(_handle);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            _handle = nint.Zero;
        }
    }

    private sealed class Flow
    {
        public uint Id { get; init; }

        public int Users { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QosVersion
    {
        public ushort Major;
        public ushort Minor;
    }

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSCreateHandle(ref QosVersion version, out nint handle);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSCloseHandle(nint handle);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSAddSocketToFlow(nint qosHandle, nint socket, byte[] destination, int trafficType, uint flags, ref uint flowId);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSRemoveSocketFromFlow(nint qosHandle, nint socket, uint flowId, uint flags);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSSetFlow(nint qosHandle, uint flowId, int operation, uint size, ref uint buffer, uint flags, nint overlapped);
}
