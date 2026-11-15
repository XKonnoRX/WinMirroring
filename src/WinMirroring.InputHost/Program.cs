using WinMirroring.Input.Injection;
using WinMirroring.Shell.Elevated;

namespace WinMirroring.InputHost;

/// <summary>
/// The elevated helper. It does one of two things, told apart by how it was started, and nothing else: it replays
/// the app's injection commands so that input can reach administrator windows
/// (<see cref="InjectionHelperHost"/>), or it hosts one administrator console for the app
/// (<see cref="ElevatedShellHost"/>). Both connect back over a private pipe to a program they have checked is the
/// app they shipped with; neither has any other way in.
///
/// The console is only ever started the way that asks: the scheduled task that lets the input side start without a
/// prompt names this exe with no arguments at all, so it can only ever reach the injection half. A promptless way
/// into the console half would be a promptless way for anything running as this user to become administrator.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) =>
        args.Contains("--shell-pipe", StringComparer.OrdinalIgnoreCase)
            ? ElevatedShellHost.RunAsync(args)
            : InjectionHelperHost.RunAsync(args);
}
