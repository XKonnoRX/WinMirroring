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
