using WinMirroring.Core.Protocol;

namespace WinMirroring.Core.Transport;

/// <summary>
/// A message longer than the other side will put back together (<see cref="ProtocolConstants.MaxFrameSize"/>),
/// refused here before any of it is written.
///
/// It has to be refused by the sender. A message that goes out in parts is only measured by the receiver as the
/// parts add up, and what the receiver does on finding one too long is what it must do with a peer that is not
/// talking the protocol: it stops reading, and the session is gone — screen, input, sound and all — over one
/// message that could simply not have been sent.
///
/// An <see cref="InvalidOperationException"/> because that is what everything that sends on a session already
/// expects from it when a message cannot go out (a closed session, a channel that is not open yet), so this is
/// taken the same way: the message is not sent, and the session stays.
/// </summary>
public sealed class MessageTooLargeException : InvalidOperationException
{
    public MessageTooLargeException(ChannelId channel, int length)
        : base($"A {channel} message of {length} bytes is over the protocol limit of {ProtocolConstants.MaxFrameSize} bytes and was not sent.")
    {
        Channel = channel;
        Length = length;
    }

    public ChannelId Channel { get; }

    public int Length { get; }
}
