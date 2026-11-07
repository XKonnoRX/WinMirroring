using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinMirroring.Core.Media;

/// <summary>
/// AES-256-GCM sealing of media datagrams. The key is created per stream by the sender and handed over inside the
/// TLS session, so UDP packets need no handshake of their own.
/// Packet: magic(1) kind(1) streamId(4) sequence(8) | ciphertext | tag(16). The header is authenticated data and
/// (streamId, sequence) is the nonce, unique for the lifetime of a key.
/// One instance is not thread-safe; give each direction its own.
/// </summary>
public sealed class MediaCipher : IDisposable
{
    public const int KeySize = 32;
    public const int HeaderSize = 14;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;

    private const byte Magic = 0xA7;

    private readonly AesGcm _aes;

    public MediaCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Media keys are {KeySize} bytes.", nameof(key));
        }

        _aes = new AesGcm(key, TagSize);
    }

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>Writes a sealed packet into <paramref name="packet"/> and returns its length.</summary>
    public int Seal(byte kind, uint streamId, ulong sequence, ReadOnlySpan<byte> plaintext, Span<byte> packet)
    {
        var total = Overhead + plaintext.Length;
        if (packet.Length < total)
        {
            throw new ArgumentException("Packet buffer too small.", nameof(packet));
        }

        var header = packet[..HeaderSize];
        header[0] = Magic;
        header[1] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(header[2..], streamId);
        BinaryPrimitives.WriteUInt64BigEndian(header[6..], sequence);

        Span<byte> nonce = stackalloc byte[12];
        header[2..HeaderSize].CopyTo(nonce);
        _aes.Encrypt(nonce, plaintext, packet.Slice(HeaderSize, plaintext.Length), packet.Slice(HeaderSize + plaintext.Length, TagSize), header);
        return total;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out byte kind, out uint streamId, out ulong sequence)
    {
        if (packet.Length < Overhead || packet[0] != Magic)
        {
            kind = 0;
            streamId = 0;
            sequence = 0;
            return false;
        }

        kind = packet[1];
        streamId = BinaryPrimitives.ReadUInt32BigEndian(packet[2..]);
        sequence = BinaryPrimitives.ReadUInt64BigEndian(packet[6..]);
        return true;
    }

    /// <summary>Authenticates and decrypts; false for forged, corrupted or foreign packets.</summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, Span<byte> plaintext, out int length)
    {
        length = packet.Length - Overhead;
        if (length < 0 || plaintext.Length < length || packet[0] != Magic)
        {
            length = 0;
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        packet[2..HeaderSize].CopyTo(nonce);
        try
        {
            _aes.Decrypt(nonce, packet.Slice(HeaderSize, length), packet.Slice(HeaderSize + length, TagSize), plaintext[..length], packet[..HeaderSize]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose() => _aes.Dispose();
}
