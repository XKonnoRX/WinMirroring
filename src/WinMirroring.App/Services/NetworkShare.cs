using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinMirroring.App.Services;

/// <summary>
/// Opening another device's shared folders the way Explorer would if it said what went wrong.
///
/// Handing "\\host" to explorer.exe was all there was, and when the device did not answer on SMB, or answered and
/// wanted a password this account had never given it, Explorer did not ask and did not complain: it opened the
/// Documents folder, which looks like the button doing something unrelated. So the steps are taken one by one here:
/// is file sharing there at all (port 445), will it let this account in with what Windows already knows, and if not,
/// the ordinary Windows sign-in dialog — and only then the folder.
/// </summary>
internal static class NetworkShare
{
    public enum Outcome
    {
        Opened,
        Unreachable,
        Cancelled,
        Refused,
        Failed,
    }

    public sealed record Result(Outcome Outcome, string Unc, int Error = 0, IReadOnlyList<string>? Shares = null);

    /// <summary>Connects (asking for a password if needed) and opens the device's shares in Explorer.</summary>
    public static async Task<Result> OpenAsync(string host, nint owner, CancellationToken ct = default)
    {
        var server = ServerName(host);
        var unc = $@"\\{server}";
        if (!await AnswersSmbAsync(host, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false))
        {
            return new Result(Outcome.Unreachable, unc);
        }

        // The connection is made on a thread of its own: WNetAddConnection3 blocks, and with a dialog up it blocks
        // for as long as the person takes to type a password.
        var error = await Task.Run(() => Connect(server, owner, prompt: false), ct).ConfigureAwait(false);
        if (NeedsCredentials(error))
        {
            error = await Task.Run(() => Connect(server, owner, prompt: true), ct).ConfigureAwait(false);
        }

        switch (error)
        {
            case 0:
            case ErrorSessionCredentialConflict: // already connected under another name: that connection serves
            case ErrorAlreadyAssigned:
                break;

            case ErrorCancelled:
                return new Result(Outcome.Cancelled, unc, error);

            case ErrorBadNetPath:
            case ErrorBadNetName:
            case ErrorNoNetwork:
            case ErrorHostUnreachable:
                return new Result(Outcome.Unreachable, unc, error);

            default:
                return new Result(NeedsCredentials(error) ? Outcome.Refused : Outcome.Failed, unc, error);
        }

        var shares = UserShares(server);
        Process.Start(new ProcessStartInfo(unc) { UseShellExecute = true });
        return new Result(Outcome.Opened, unc, 0, shares);
    }

    /// <summary>The server part of a UNC path: an IPv6 address has to be spelled as a name to be one.</summary>
    private static string ServerName(string host) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? address.ToString().Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net"
            : host;

    private static async Task<bool> AnswersSmbAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, 445, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool NeedsCredentials(int error) => error is
        ErrorAccessDenied or ErrorInvalidPassword or ErrorLogonFailure or ErrorAccountRestriction
        or ErrorInvalidLogonHours or ErrorInvalidWorkstation or ErrorPasswordExpired or ErrorAccountDisabled
        or ErrorNotAuthenticated or ErrorBadUsername or ErrorLogonTypeNotGranted or ErrorNoSuchLogonSession
        or ErrorPasswordMustChange or ErrorAccountLockedOut;

    /// <summary>
    /// A connection to the server's IPC$ share, which is what signing in to a server means; Explorer then lists
    /// its folders over it. With <paramref name="prompt"/> Windows shows its own credentials dialog.
    /// </summary>
    private static int Connect(string server, nint owner, bool prompt)
    {
        var resource = new NETRESOURCEW
        {
            dwType = ResourceTypeAny,
            lpRemoteName = $@"\\{server}\IPC$",
        };

        var flags = ConnectTemporary | (prompt ? ConnectInteractive | ConnectPrompt : 0);
        return WNetAddConnection3W(owner, ref resource, null, null, flags);
    }

    /// <summary>The folders shared on purpose (not the administrative C$, ADMIN$, IPC$); null when they cannot be listed.</summary>
    private static List<string>? UserShares(string server)
    {
        var status = NetShareEnum(server, 1, out var buffer, MaxPreferredLength, out var read, out _, 0);
        try
        {
            if (status != 0 || buffer == 0)
            {
                return null;
            }

            var shares = new List<string>();
            var size = Marshal.SizeOf<SHARE_INFO_1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + (i * size));
                if ((info.shi1_type & 0xFF) == StypeDisktree && (info.shi1_type & StypeSpecial) == 0 && info.shi1_netname is { Length: > 0 } name)
                {
                    shares.Add(name);
                }
            }

            return shares;
        }
        finally
        {
            if (buffer != 0)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorBadNetName = 67;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorNoNetwork = 1222;
    private const int ErrorCancelled = 1223;
    private const int ErrorHostUnreachable = 1232;
    private const int ErrorNotAuthenticated = 1244;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorBadUsername = 2202;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorInvalidLogonHours = 1328;
    private const int ErrorInvalidWorkstation = 1329;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorNoSuchLogonSession = 1312;
    private const int ErrorLogonTypeNotGranted = 1385;
    private const int ErrorPasswordMustChange = 1907;
    private const int ErrorAccountLockedOut = 1909;

    private const int ResourceTypeAny = 0;
    private const int ConnectTemporary = 0x4;
    private const int ConnectInteractive = 0x8;
    private const int ConnectPrompt = 0x10;
    private const int MaxPreferredLength = -1;
    private const uint StypeDisktree = 0;
    private const uint StypeSpecial = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCEW
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string? shi1_netname;
        public uint shi1_type;
        public string? shi1_remark;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection3W(nint owner, ref NETRESOURCEW resource, string? password, string? user, int flags);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out nint buffer, int maxLength, out int read, out int total, int resume);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(nint buffer);
}
