using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Tests;

/// <summary>
/// The settings file is the device: its id, the certificate it is known by, everything it is paired with. When it
/// cannot be read the device starts with fresh settings, and the first save puts those where the old file was —
/// a new device in the old one's place, and every pairing gone on both sides. The unreadable file used to be copied
/// to one fixed name, which the next unreadable file replaced, and nothing said that any of it had happened. Now it
/// is moved out of the way under a name of its own and the store says where, so that it can be mended and put back.
/// </summary>
public sealed class UnreadableSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "WinMirroring.UnreadableSettingsTests", Guid.NewGuid().ToString("N"));

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

    /// <summary>What a file looks like when the write of it was cut short: the pairings are in there, and readable by eye.</summary>
    private const string CutShort = "{\n  \"deviceId\": \"0123456789abcdef\",\n  \"pairedPeers\": [\n    { \"id\": \"fedcba\", \"fingerprint\": \"AA11\" },\n    { \"id\":";

    [Fact]
    public void AFileThatCannotBeReadIsMovedOutOfTheWayAndNothingIsWrittenOverIt()
    {
        using var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, CutShort);

        var settings = store.Load();

        // Fresh settings, as before: a device that will not start is no better.
        Assert.Equal(string.Empty, settings.DeviceId);
        Assert.Empty(settings.PairedPeers);

        // But the store says so, and says where the old file went.
        Assert.NotNull(store.Unreadable);
        Assert.False(string.IsNullOrWhiteSpace(store.Unreadable.Reason));
        var kept = store.Unreadable.KeptAs;
        Assert.NotNull(kept);
        Assert.Equal(_directory, Path.GetDirectoryName(kept));
        Assert.StartsWith("settings.json.corrupt-", Path.GetFileName(kept), StringComparison.Ordinal);
        Assert.Equal(CutShort, File.ReadAllText(kept));

        // Moved, not copied: nothing unreadable is left where the next start would trip over it again.
        Assert.False(File.Exists(store.FilePath));

        // The save that follows — a node saves its new identity straight away — leaves the old file alone.
        settings.DeviceId = "a-new-device";
        store.Save(settings);
        Assert.Equal(CutShort, File.ReadAllText(kept));
        Assert.Equal("a-new-device", store.Load().DeviceId);
    }

    /// <summary>The fixed name the copy used to have meant the second bad file took the place of the first.</summary>
    [Fact]
    public void ASecondUnreadableFileDoesNotTakeThePlaceOfTheFirst()
    {
        using var store = new SettingsStore(_directory);

        File.WriteAllText(store.FilePath, CutShort);
        store.Load();
        var first = store.Unreadable?.KeptAs;

        // Within the same second, which is the hardest case for a name made from the time.
        File.WriteAllText(store.FilePath, "not settings at all");
        store.Load();
        var second = store.Unreadable?.KeptAs;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(CutShort, File.ReadAllText(first));
        Assert.Equal("not settings at all", File.ReadAllText(second));
    }

    /// <summary>
    /// What a power failure tends to leave: a file of the right name with nothing in it, or the right length of
    /// zeroes. And the one word that is good JSON and no settings, which used to be taken for fresh settings without
    /// even the copy being made.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    public void WhateverIsNotSettingsIsKeptAllTheSame(string content)
    {
        using var store = new SettingsStore(_directory);
        File.WriteAllText(store.FilePath, content);

        Assert.Equal(string.Empty, store.Load().DeviceId);

        Assert.NotNull(store.Unreadable?.KeptAs);
        Assert.Equal(content, File.ReadAllText(store.Unreadable.KeptAs));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void SettingsThatCanBeReadAndNoSettingsAtAllAreNeitherOfThemUnreadable()
    {
        using var store = new SettingsStore(_directory);

        // A first start: no file is not a damaged file.
        store.Load();
        Assert.Null(store.Unreadable);

        store.Save(new AppSettings { DeviceId = "kept" });
        Assert.Equal("kept", store.Load().DeviceId);
        Assert.Null(store.Unreadable);
        Assert.Equal("settings.json", Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))));

        // And what one load found is not held against the next.
        File.WriteAllText(store.FilePath, CutShort);
        store.Load();
        Assert.NotNull(store.Unreadable);
        store.Save(new AppSettings { DeviceId = "mended" });
        Assert.Equal("mended", store.Load().DeviceId);
        Assert.Null(store.Unreadable);
    }
}
