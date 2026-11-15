using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Tests;

/// <summary>
/// A settings directory is a device: one identity, one certificate, one list of paired peers. Two nodes sharing one
/// hold that file in memory and write their whole view of it whenever anything changes, so the second one to save
/// quietly takes away the first one's identity and everything it was paired with — and the devices on the other
/// side are left trusting a certificate nobody will present again. It happened for real, with the command-line tool
/// pointed at the app's own directory while the app was running, which is why a node now says whether anyone else
/// has the directory it was given.
/// </summary>
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "WinMirroring.DataDirectoryTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Asked from a thread of its own, which is the whole trick to testing this. The claim is held by the thread
    /// that took it and that thread is allowed to take it again — rightly, since a node's own directory is not in
    /// use by <em>another</em> node — and a question asked through the thread pool can come back on the very
    /// thread holding it, which answers "free" and means nothing.
    /// </summary>
    private static bool AskedFromAnotherThread(string directory)
    {
        var answer = false;
        var asking = new Thread(() => answer = SettingsStore.IsInUseByAnotherNode(directory));
        asking.Start();
        asking.Join();
        return answer;
    }

    [Fact]
    public void ADirectoryAnotherNodeHasIsSaidToBeInUseUntilThatNodeLetsGo()
    {
        Assert.False(AskedFromAnotherThread(_directory));

        var store = new SettingsStore(_directory);
        Assert.False(store.SharedWithAnotherNode);
        Assert.True(AskedFromAnotherThread(_directory));

        store.Dispose();
        Assert.False(AskedFromAnotherThread(_directory));
    }

    /// <summary>Held per directory and not for the program as a whole: a second device may be run beside the first.</summary>
    [Fact]
    public void HoldingOneDirectorySaysNothingAboutAnother()
    {
        using var store = new SettingsStore(_directory);
        var other = Path.Combine(_directory, "second-device");

        Assert.False(AskedFromAnotherThread(other));

        using var second = new SettingsStore(other);
        Assert.False(second.SharedWithAnotherNode);
        Assert.True(AskedFromAnotherThread(other));
    }

    /// <summary>
    /// Nothing refuses to work because the directory is shared. The claim is there to stop a mistake being made
    /// silently, not to be another way for a device to fail to start.
    /// </summary>
    [Fact]
    public void ASharedDirectoryStillLoadsAndSaves()
    {
        using var first = new SettingsStore(_directory);
        var settings = first.Load();
        settings.DeviceName = "KONNY";
        first.Save(settings);

        using var second = new SettingsStore(_directory);
        Assert.Equal("KONNY", second.Load().DeviceName);

        second.Save(settings);
        Assert.Equal("KONNY", first.Load().DeviceName);
    }
}
