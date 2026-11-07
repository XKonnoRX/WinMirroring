namespace WinMirroring.Core.Transport;

/// <summary>Logical channel carried in every frame. Modules register a handler per channel on a ready session.</summary>
public enum ChannelId : byte
{
    Control = 0,
    Input = 1,
    Clipboard = 2,
    File = 3,
    Audio = 4,
    Screen = 5,
}
