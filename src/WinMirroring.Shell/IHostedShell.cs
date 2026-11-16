namespace WinMirroring.Shell;

/// <summary>
/// A console being hosted on this device, as the service that owns it sees one. Two things are: a PowerShell this
/// app started itself (<see cref="RemoteShell"/>), and one started for it with administrator rights inside the
/// helper (<see cref="Elevated.ElevatedHelper"/>).
///
/// They look the same from here on purpose. The elevated one is the same <see cref="RemoteShell"/>, running inside
/// the helper, with its words carried back over a pipe — so everything that was learned the hard way about feeding
/// a shell (the marker that says a command is over, the base64 a command travels as, the four places encoding has
/// to be set) is in one place and is not written twice.
///
/// Everything here is raised on whatever thread read the shell's output, so nothing handling these may wait.
/// </summary>
internal interface IHostedShell : IDisposable
{
    event Action<ShellStreamKind, string>? Output;

    event Action<int, string>? Done;

    event Action? Ended;

    /// <summary>Completes once the shell has said where it is and is ready for a command.</summary>
    Task Ready { get; }

    string Directory { get; }

    bool Run(string command);

    /// <summary>Stops what is running, which ends the shell: a console has to be opened again after it.</summary>
    void Cancel();
}
