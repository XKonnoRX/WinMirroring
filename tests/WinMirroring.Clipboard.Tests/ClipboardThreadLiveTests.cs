using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static void Trace(string line) => File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");

    private static string Describe(ClipboardSnapshot? snapshot) =>
        snapshot is null
            ? "null (clipboard busy)"
            : string.Join(", ", snapshot.Items.Select(i => i.Kind == ClipboardItemKind.UnicodeText ? $"text '{Encoding.Unicode.GetString(i.Data).TrimEnd('\0')}'" : i.Kind.ToString()))
              + (snapshot.Files.Count > 0 ? $" files {snapshot.Files.Count}" : string.Empty);
}
