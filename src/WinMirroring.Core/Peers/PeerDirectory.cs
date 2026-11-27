using System.Collections.Concurrent;

namespace WinMirroring.Core.Peers;

/// <summary>Thread-safe registry of known peers. Consumers get snapshots; <see cref="Changed"/> carries a copy.</summary>
public sealed class PeerDirectory
{
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);

    public event Action<PeerInfo>? Changed;

    public IReadOnlyList<PeerInfo> Snapshot()
    {
        var list = new List<PeerInfo>();
        foreach (var peer in _peers.Values)
        {
            lock (peer)
            {
                list.Add(peer.Clone());
            }
        }

        return list;
    }

    public bool TryGet(string id, out PeerInfo snapshot)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            lock (peer)
            {
                snapshot = peer.Clone();
            }

            return true;
        }

        snapshot = null!;
        return false;
    }

    /// <summary>Applies <paramref name="mutate"/> under the peer's lock and notifies listeners.</summary>
    public PeerInfo Update(string id, Action<PeerInfo> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var peer = _peers.GetOrAdd(id, static key => new PeerInfo(key));
        PeerInfo snapshot;
        lock (peer)
        {
            mutate(peer);
            snapshot = peer.Clone();
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Says of every device that is listed as announcing itself, and has not been heard from for
    /// <paramref name="after"/>, that it is no longer on the network. Returns how many that was.
    ///
    /// "Discovered" means beacons arrive, and it was only ever taken back when a session closed or a pairing was
    /// removed — so a device with no session that simply stopped announcing itself (switched off, gone to another
    /// network) went on being shown as on the network for as long as the program ran. Only that one state is
    /// touched: a device with a session, or one being connected to, is not judged by its beacons.
    ///
    /// Nothing is taken off the list. A paired device belongs there whether it is on or not, and a device nobody
    /// has paired with may still have a place in the layout the devices share, which is saved from the list as it
    /// stands — so whether to drop it, and when, is not a thing to be decided by a timer down here.
    /// </summary>
    internal int ExpireUnseen(DateTimeOffset now, TimeSpan after)
    {
        List<PeerInfo>? gone = null;
        foreach (var peer in _peers.Values)
        {
            lock (peer)
            {
                if (peer.State != PeerState.Discovered || (peer.LastSeen is { } seen && now - seen < after))
                {
                    continue;
                }

                peer.State = PeerState.Offline;
                (gone ??= []).Add(peer.Clone());
            }
        }

        if (gone is null)
        {
            return 0;
        }

        foreach (var snapshot in gone)
        {
            Changed?.Invoke(snapshot);
        }

        return gone.Count;
    }

    public void Remove(string id)
    {
        if (_peers.TryRemove(id, out var peer))
        {
            PeerInfo snapshot;
            lock (peer)
            {
                peer.State = PeerState.Offline;
                peer.IsPaired = false;
                snapshot = peer.Clone();
            }

            Changed?.Invoke(snapshot);
        }
    }
}
