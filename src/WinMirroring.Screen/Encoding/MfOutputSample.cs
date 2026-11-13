using Vortice.MediaFoundation;

namespace WinMirroring.Screen.Encoding;

/// <summary>
/// The sample a Media Foundation transform writes one output into, created once and lent out again for every frame.
///
/// Two things make this worth a type of its own. A COM wrapper that is dropped instead of disposed never releases
/// what it points at — the garbage collector knows nothing about Media Foundation, and its finalizer does not
/// release either — so a buffer allocated per frame and handed to a sample stays allocated for as long as the
/// process runs. At 1080p that buffer is four megabytes, which is how a tablet watching a screen ran itself out of
/// memory in minutes. And <c>ProcessOutput</c> hands back a <em>new</em> wrapper around the same sample that holds
/// no reference of its own, so disposing what comes back destroys the sample while the wrapper that created it
/// still points at it.
///
/// Keeping one sample for the transform's whole life settles both: nothing is allocated per frame, and nothing that
/// comes back out of <c>ProcessOutput</c> is ever disposed. Lent to one thread at a time — the one driving the
/// transform — since the sample being handed out again is the whole point.
/// </summary>
internal sealed class MfOutputSample : IDisposable
{
    private IMFSample? _sample;
    private IMFMediaBuffer? _storage;
    private int _size;

    /// <summary>The sample to lend to <c>ProcessOutput</c>: empty, with room for <paramref name="size"/> bytes.</summary>
    public IMFSample ForSize(int size)
    {
        size = Math.Max(size, 1);
        if (_sample is null || _storage is null || _size < size)
        {
            Dispose();
            _storage = MediaFactory.MFCreateMemoryBuffer(size);
            _sample = MediaFactory.MFCreateSample();
            _sample.AddBuffer(_storage);
            _size = size;
            return _sample;
        }

        // The transform writes from the start and sets the length itself; what the last frame left must not count.
        _storage.CurrentLength = 0;
        return _sample;
    }

    public void Dispose()
    {
        // Two references are ours to give back: the buffer's own, and the sample that holds the buffer.
        _storage?.Dispose();
        _sample?.Dispose();
        _storage = null;
        _sample = null;
        _size = 0;
    }
}
