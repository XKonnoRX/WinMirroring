using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinMirroring.Clipboard.Native;
using static WinMirroring.Clipboard.Native.ClipboardNative;

namespace WinMirroring.Clipboard.Tests;

/// <summary>Uses the real Windows clipboard (restores text afterwards), so it runs only on request.</summary>
public class ClipboardThreadLiveTests
{
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "winmirroring-clip-diag.txt");

    [Fact(Explicit = true)]
    public async Task WriteIsSeenByAnotherListenerButNotEchoedByItself()
    {
        File.WriteAllText(TracePath, string.Empty);
        var ct = TestContext.Current.CancellationToken;
        using var writer = new ClipboardThread(NullLogger.Instance, () => 1 << 20);
        using var observer = new ClipboardThread(NullLogger.Instance, () => 1 << 20);
        var writerEvents = new List<ClipboardSnapshot>();
        var observed = new TaskCompletionSource<ClipboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.LocalChanged += s => writerEvents.Add(s);
        observer.LocalChanged += s => observed.TrySetResult(s);
        writer.Start();
        observer.Start();
        Trace("started");

        var original = await writer.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
        Trace($"original: {Describe(original)}");

        var marker = $"WinMirroring clipboard test {Guid.NewGuid():N}";
        try
        {
            await writer.WriteItemsAsync([new ClipboardItem(ClipboardItemKind.UnicodeText, Encoding.Unicode.GetBytes(marker + "\0"))]).WaitAsync(TimeSpan.FromSeconds(3), ct);
            Trace("written");

            var back = await observer.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
            Trace($"read back by observer: {Describe(back)}");

            await Task.Delay(1000, ct);
            Trace($"observer notifications {observer.Notifications}, handled {observer.ChangesHandled}, event {observed.Task.IsCompleted}; writer notifications {writer.Notifications}, handled {writer.ChangesHandled}, events {writerEvents.Count}");

            var seen = await observed.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            var text = Encoding.Unicode.GetString(Assert.Single(seen.Items, i => i.Kind == ClipboardItemKind.UnicodeText).Data).TrimEnd('\0');
            Assert.Equal(marker, text);
            Assert.Empty(writerEvents);
        }
        finally
        {
            if (original is { Items.Count: > 0 })
            {
                await writer.WriteItemsAsync(original.Items).WaitAsync(TimeSpan.FromSeconds(3), ct);
                Trace("restored");
            }
        }
    }

    /// <summary>
    /// What a password manager actually does, done here by hand: text on the clipboard alongside the format that
    /// says it may not leave the machine. The reading must stop at the mark and never touch the text — the whole
    /// point being that the password does not reach this process at all.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task MarkedContentIsNeitherReadNorSent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var thread = new ClipboardThread(NullLogger.Instance, () => 1 << 20);
        thread.Start();

        var original = await thread.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
        var secret = $"password {Guid.NewGuid():N}";
        try
        {
            foreach (var (mark, value, expected) in new (uint Mark, uint? Value, string Expected)[]
                     {
                         (ClipboardFormats.ExcludeFromMonitors, null, ClipboardPrivacy.MonitorsForbidden),
                         (ClipboardFormats.CanUploadToCloud, 0u, ClipboardPrivacy.DevicesForbidden),
                         (ClipboardFormats.CanIncludeInHistory, 0u, ClipboardPrivacy.HistoryForbidden),
                     })
            {
                Put(secret, mark, value);

                var snapshot = await thread.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);

                Assert.NotNull(snapshot);
                Assert.Equal(expected, snapshot.Refused);
                Assert.Empty(snapshot.Items);
                Assert.Empty(snapshot.Files);
            }

            // The same marks saying yes leave the clipboard shared as usual.
            Put("ordinary text", ClipboardFormats.CanUploadToCloud, 1);
            var allowed = await thread.ReadNowAsync().WaitAsync(TimeSpan.FromSeconds(3), ct);
            Assert.NotNull(allowed);
            Assert.Null(allowed.Refused);
            Assert.Equal(
                "ordinary text",
                Encoding.Unicode.GetString(Assert.Single(allowed.Items, i => i.Kind == ClipboardItemKind.UnicodeText).Data).TrimEnd('\0'));
        }
        finally
        {
            if (original is { Items.Count: > 0 })
            {
                await thread.WriteItemsAsync(original.Items).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>Text plus one permission format, exactly as an application that cares would leave it.</summary>
    private static void Put(string text, uint mark, uint? value)
    {
        Assert.True(OpenClipboard(0), "the clipboard could not be opened");
        try
        {
            EmptyClipboard();
            SetClipboardData(CF_UNICODETEXT, Alloc(Encoding.Unicode.GetBytes(text + "\0")));

            // The exclusion format carries nothing: that it is present is the whole message.
            SetClipboardData(mark, Alloc(value is { } dword ? BitConverter.GetBytes(dword) : [0, 0, 0, 0]));
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static nint Alloc(byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        var pointer = GlobalLock(handle);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(handle);
        return handle;
    }

    private static void Trace(string line) => File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");

    private static string Describe(ClipboardSnapshot? snapshot) =>
        snapshot is null
            ? "null (clipboard busy)"
            : string.Join(", ", snapshot.Items.Select(i => i.Kind == ClipboardItemKind.UnicodeText ? $"text '{Encoding.Unicode.GetString(i.Data).TrimEnd('\0')}'" : i.Kind.ToString()))
              + (snapshot.Files.Count > 0 ? $" files {snapshot.Files.Count}" : string.Empty);
}
