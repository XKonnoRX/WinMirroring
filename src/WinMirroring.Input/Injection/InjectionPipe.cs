using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using WinMirroring.Core.Local;

namespace WinMirroring.Input.Injection;

/// <summary>
/// The channel between the app and its elevated input helper, and the checks that bind the two ends to each other.
///
/// The app is the one that listens: it runs at normal integrity, and Windows lets an elevated process reach down to
/// a pipe a normal one created, but not the other way round. The name is the same every time, because the helper is
/// started through a scheduled task and a task takes no arguments — which is the whole point of the task, since it
/// is what lets the helper start without a prompt every single time. A name that can be guessed is only safe
/// because neither end takes the other on trust: the helper refuses any server that is not this app's exe, and the
/// app refuses any client that is not the helper exe sitting next to it. A process of this user that squats on the
/// name gets nothing — the helper will not talk to it — and one that connects to the app gets nothing either.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InjectionPipe
{
    /// <summary>
    /// One name per user: two people signed in at once each get their own app, helper and channel, and neither can
    /// reach the other's (the pipe's own permissions already limit it to the user that created it).
    /// </summary>
    public static string NameForCurrentUser()
    {
        string who;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            who = identity.User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            who = Environment.UserName;
        }

        return $"WinMirroring.Inject.{who}";
    }

    /// <summary>The app's exe, which the helper expects on the other end of the pipe.</summary>
    public static string AppPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.exe");

    /// <summary>The helper's exe, which the app expects on the other end of the pipe.</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "WinMirroring.InputHost.exe");

    /// <summary>
    /// The process at the other end of a pipe is the exe it should be. The how of it lives in
    /// <see cref="PipePeer"/>, in the core, because the console's elevated helper asks exactly the same question
    /// of exactly the same pair of programs, and a check like this is not one to have two copies of.
    /// </summary>
    public static bool IsPeer(uint pid, string expectedImage) => PipePeer.Is(pid, expectedImage);

    public static bool TryGetServerProcessId(NamedPipeClientStream pipe, out uint pid) =>
        PipePeer.TryGetServerProcessId(pipe, out pid);

    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint pid) =>
        PipePeer.TryGetClientProcessId(pipe, out pid);
}
