using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using WinMirroring.Screen.Encoding;

namespace WinMirroring.Screen.Tests;

/// <summary>
/// The two facts the kept output sample is built on. Both are easy to forget and neither shows up as a crash, so
/// they are written down here rather than only in a comment.
/// </summary>
public class MfOutputSampleTests
{
    /// <summary>
    /// A Media Foundation object outlives the wrapper that was dropped instead of disposed: the finalizer does not
    /// release it, and the garbage collector has no idea how much memory sits behind it. This is why a buffer may
    /// not be allocated per frame — one per decoded 1080p frame is four megabytes kept until the process ends.
    /// </summary>
    [Fact]
    public void ADroppedWrapperNeverGivesItsMemoryBack()
    {
        VideoInterop.EnsureStarted();
        var pointer = CreateAndForget();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
            }

            // Ours is the one reference we added; the dropped wrapper's is the other, and it is still there.
            Assert.Equal(2, RefCount(pointer));
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>
    /// Handing a buffer to a sample makes the sample take a reference of its own, so the wrapper that created the
    /// buffer has one to give back. Dropping it instead is the leak above; disposing it is what frees the memory
    /// when the sample is finally released.
    /// </summary>
    [Fact]
    public void ASampleTakesItsOwnReferenceOnABuffer()
    {
        VideoInterop.EnsureStarted();
        using var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(4096);
        var pointer = buffer.NativePointer;
        Assert.Equal(1, RefCount(pointer));

        sample.AddBuffer(buffer);
        Assert.Equal(2, RefCount(pointer));

        buffer.Dispose();
        Assert.Equal(1, RefCount(pointer));
    }

    /// <summary>One sample is handed out again and again, and comes back empty each time.</summary>
    [Fact]
    public void TheSameSampleIsLentOutAgainAndComesBackEmpty()
    {
        VideoInterop.EnsureStarted();
        using var kept = new MfOutputSample();

        var first = kept.ForSize(4096);
        using (var storage = first.GetBufferByIndex(0))
        {
            storage.CurrentLength = 100;
        }

        var again = kept.ForSize(4096);

        Assert.Same(first, again);
        using (var storage = again.GetBufferByIndex(0))
        {
            Assert.Equal(0, storage.CurrentLength);
            Assert.True(storage.MaxLength >= 4096);
        }

        // A bigger frame needs a bigger buffer, so that one is a sample of its own.
        var larger = kept.ForSize(1 << 20);
        Assert.NotSame(first, larger);
        using (var storage = larger.GetBufferByIndex(0))
        {
            Assert.True(storage.MaxLength >= 1 << 20);
        }
    }

    private static int RefCount(nint pointer)
    {
        Marshal.AddRef(pointer);
        return Marshal.Release(pointer);
    }

    /// <summary>Creates a buffer, adds a reference of our own so the pointer stays valid, and forgets the wrapper.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint CreateAndForget()
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(4096);
        var pointer = buffer.NativePointer;
        Marshal.AddRef(pointer);
        return pointer;
    }
}
