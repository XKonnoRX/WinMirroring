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
