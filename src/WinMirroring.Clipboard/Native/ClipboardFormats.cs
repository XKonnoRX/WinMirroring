using static WinMirroring.Clipboard.Native.ClipboardNative;

namespace WinMirroring.Clipboard.Native;

/// <summary>Clipboard format ids, registered once per process.</summary>
internal static class ClipboardFormats
{
    public static readonly uint Html = RegisterClipboardFormatW("HTML Format");
    public static readonly uint Rtf = RegisterClipboardFormatW("Rich Text Format");
    public static readonly uint Png = RegisterClipboardFormatW("PNG");
    public static readonly uint FileGroupDescriptorW = RegisterClipboardFormatW("FileGroupDescriptorW");
    public static readonly uint FileContents = RegisterClipboardFormatW("FileContents");
    public static readonly uint PreferredDropEffect = RegisterClipboardFormatW("Preferred DropEffect");

    // What an application says may be done with what it copied. A password manager puts these on the clipboard,
    // and this app is precisely what they are aimed at: a program that watches the clipboard and carries what it
    // finds to another machine. The first forbids any such program from touching the content at all; the other two
    // are a DWORD where zero is no — no clipboard history, no copy to other machines.
    public static readonly uint ExcludeFromMonitors = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    public static readonly uint CanIncludeInHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    public static readonly uint CanUploadToCloud = RegisterClipboardFormatW("CanUploadToCloudClipboard");

    /// <summary>Order matters: richer formats first, so the size budget keeps them when space runs out.</summary>
    public static IEnumerable<(ClipboardItemKind Kind, uint Id)> Shared =>
    [
        (ClipboardItemKind.UnicodeText, CF_UNICODETEXT),
        (ClipboardItemKind.Html, Html),
        (ClipboardItemKind.Rtf, Rtf),
        (ClipboardItemKind.Png, Png),
        (ClipboardItemKind.Dib, CF_DIB),
    ];

    public static uint IdOf(ClipboardItemKind kind) => kind switch
    {
        ClipboardItemKind.UnicodeText => CF_UNICODETEXT,
        ClipboardItemKind.Html => Html,
        ClipboardItemKind.Rtf => Rtf,
        ClipboardItemKind.Png => Png,
        ClipboardItemKind.Dib => CF_DIB,
        _ => 0,
    };
}
