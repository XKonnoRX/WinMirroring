using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinMirroring.Shell.Local;

/// <summary>
/// The pipe a tool on this machine uses to ask the app running here to run something on a device.
///
/// This exists so that a command-line tool is not a device. It was one: it built a node of its own, with its own
/// identity and its own certificate, and had to be paired with every device it wanted to manage — which put a
/// second entry called "wm-cli" in the device list of every one of them, and asked the person at each to confirm a
/// stranger. All of that for something meant to be a tool rather than a machine. Now, when the app is running
/// here, the tool asks it: the app already has a session with every paired device, under the identity those
/// devices have already agreed to, so a device sees a command from the machine it knows and nothing new appears
/// anywhere. The tool only builds a node of its own where there is no app — a headless machine, which is the one
/// case where it really is the device.
///
/// The messages on it are the console channel's own (<see cref="Protocol.ShellCodec"/>), with the console number
/// left at zero and the device to act on carried as the text of the opening message. Deliberately the same
/// protocol: what the app does with them is pass them on, so a second translation between here and the wire would
/// be two chances to disagree about the same thing.
/// </summary>
public static class LocalShellPipe
{
    /// <summary>
    /// One pipe per signed-in user, named after their SID. Two people signed in at once each run their own app
    /// with their own devices, and a pipe shared between them would let either drive the other's.
    /// </summary>
    public static string Name =>
        "WinMirroring-local-" + (CurrentUser()?.Value ?? Environment.UserName);

    /// <summary>How long a tool waits for the app to answer before deciding there is none running here.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A pipe only its owner can open. Everything that reaches it drives the devices this user has paired, which
    /// is the user's own privilege and nobody else's — and the default rights on a named pipe are more generous
    /// than that. Where the rights cannot be set at all, the pipe is still made: an app that will not start is
    /// worse than one whose pipe is as open as the system's default.
    /// </summary>
    public static NamedPipeServerStream CreateServer(int maxInstances, string? name = null)
    {
        name ??= Name;
        try
        {
            if (CurrentUser() is { } me)
            {
                var security = new PipeSecurity();
                security.SetOwner(me);
                security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    name,
                    PipeDirection.InOut,
                    maxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    security);
            }
        }
        catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or IdentityNotMappedException)
        {
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private static SecurityIdentifier? CurrentUser()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
