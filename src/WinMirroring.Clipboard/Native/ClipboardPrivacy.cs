namespace WinMirroring.Clipboard.Native;

/// <summary>
/// What the application that copied something allows to be done with it.
///
/// Windows has three clipboard formats for this, and a password manager sets them on everything it copies. They
/// are aimed at programs exactly like this one: something that watches the clipboard and carries what it finds to
/// another machine is both a "monitor" and an "upload", which is what two of the three forbid outright. Honouring
/// them is the difference between a shared clipboard and a password leaving the machine it was meant for.
///
/// The decision is kept apart from the reading of the clipboard so it can be stated plainly and checked, which
/// matters more here than in most places: getting it the wrong way round fails silently and invisibly.
/// </summary>
internal static class ClipboardPrivacy
{
    public const string MonitorsForbidden = "источник запретил обрабатывать это содержимое другим программам";
    public const string DevicesForbidden = "источник запретил передавать это содержимое на другие устройства";
    public const string HistoryForbidden = "источник запретил запоминать это содержимое";

    /// <summary>
    /// Why what is on the clipboard must not leave this machine, or null when nothing forbids it.
    /// </summary>
    /// <param name="excludedFromMonitors">
    /// ExcludeClipboardContentFromMonitorProcessing is on the clipboard. Its presence is the whole message — it
    /// carries no value worth reading — and it means no other program should touch the content at all.
    /// </param>
    /// <param name="canUploadToCloud">
    /// The value of CanUploadToCloudClipboard, or null when the format is absent. Zero is no: carrying the content
    /// to another machine is precisely what it refuses, whoever's machine it is.
    /// </param>
    /// <param name="canIncludeInHistory">
    /// The value of CanIncludeInClipboardHistory, or null when the format is absent. Zero is no. It is about
    /// keeping a local history rather than about other devices, but an application unwilling to have something
    /// remembered on this machine is certainly unwilling to have it sent to another.
    /// </param>
    public static string? WhySyncIsRefused(bool excludedFromMonitors, uint? canUploadToCloud, uint? canIncludeInHistory)
    {
        if (excludedFromMonitors)
        {
            return MonitorsForbidden;
        }

        if (canUploadToCloud == 0)
        {
            return DevicesForbidden;
        }

        if (canIncludeInHistory == 0)
        {
            return HistoryForbidden;
        }

        return null;
    }
}
