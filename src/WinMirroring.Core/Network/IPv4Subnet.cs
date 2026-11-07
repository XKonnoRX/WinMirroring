using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace WinMirroring.Core.Network;

/// <summary>An IPv4 network in CIDR form with the usual membership and broadcast math.</summary>
public readonly record struct IPv4Subnet
{
    public IPv4Subnet(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(prefixLength, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);

        PrefixLength = prefixLength;
        Network = ToAddress(ToUInt32(address) & MaskBits(prefixLength));
    }

    public IPAddress Network { get; }

    public int PrefixLength { get; }

    public IPAddress Broadcast => ToAddress(ToUInt32(Network) | ~MaskBits(PrefixLength));

    /// <summary>/31 and /32 networks have no usable broadcast address (VPN tunnels are typically /32).</summary>
    public bool IsPointToPoint => PrefixLength >= 31;

    public static IPv4Subnet FromAddressAndMask(IPAddress address, IPAddress mask) =>
        new(address, PrefixFromMask(mask));

    public static int PrefixFromMask(IPAddress mask) => BitOperations.PopCount(ToUInt32(mask));

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && (ToUInt32(address) & MaskBits(PrefixLength)) == ToUInt32(Network);

    public override string ToString() => $"{Network}/{PrefixLength}";

    internal static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var written) || written != 4)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    internal static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    internal static uint MaskBits(int prefixLength) =>
        prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
}
