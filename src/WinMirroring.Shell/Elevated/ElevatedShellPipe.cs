using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using WinMirroring.Core.Local;

namespace WinMirroring.Shell.Elevated;

/// <summary>
/// The channel between the app and one elevated console, and the rules that make having it defensible.
///
/// The app listens and the helper connects, because a normal process cannot reach a pipe an elevated one made but
/// an elevated one can reach down to a normal one's. Unlike the input helper's channel, the name here is made up
/// for each console and so is a token that has to come back — and, more to the point, this helper is started the
/// direct way, with the prompt. There is a scheduled task in this app that starts the input helper without one;
/// nothing like it exists here and nothing like it should. A promptless way to start a program that runs whatever
/// it is told, with administrator rights, is a promptless way for anything at all running as this user to become
/// administrator — which is the one thing the prompt is for. So an administrator console costs one prompt, in
/// front of whoever is at the device, every time one is opened.
///
/// Both ends still check each other's exe (<see cref="PipePeer"/>), because the prompt says yes to a program and
/// not to a pipe.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ElevatedShellPipe
{
    /// <summary>How long the helper has to appear before the console is given up on, prompt included.</summary>
    public static readonly TimeSpan HelperWait = TimeSpan.FromSeconds(120);

    public static string NewName() => $"WinMirroring.Shell.{Guid.NewGuid():N}";

    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The app's exe, which the helper expects to find on the other end.</summary>
    public static string AppPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.exe");

    /// <summary>The helper's exe, which the app expects to find on the other end — and starts.</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.InputHost.exe");

    /// <summary>A pipe for one console, which only this user can open.</summary>
    public static NamedPipeServerStream Listen(string name)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is { } me)
            {
                var security = new PipeSecurity();
                security.SetOwner(me);
                security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create(
                    name,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    security);
            }
        }
        catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or IdentityNotMappedException or System.Security.SecurityException)
        {
        }

        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }
}
