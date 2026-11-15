using System.Globalization;

namespace WinMirroring.Cli;

internal sealed class CliOptions
{
    public string? Name { get; private set; }

    public string? DataDirectory { get; private set; }

    public int? DiscoveryPort { get; private set; }

    public int? TransportPort { get; private set; }

    public List<string> ManualAddresses { get; } = [];

    public bool Loopback { get; private set; }

    public string? PairWith { get; private set; }

    public bool AutoAccept { get; private set; }

    public bool ExitWhenReady { get; private set; }

    public int? TimeoutSeconds { get; private set; }

    public bool Verbose { get; private set; }

    public bool Input { get; private set; }

    /// <summary>The device to run a command on: its name or its id.</summary>
    public string? ExecOn { get; private set; }

    /// <summary>The command itself, or the file it is read from.</summary>
    public string? Command { get; private set; }

    public string? CommandFile { get; private set; }

    /// <summary>
    /// Running a command is the one mode meant to be used from a script, by a person or by anything else. So in it
    /// nothing but what the command prints goes to standard output, everything of ours goes to standard error, and
    /// this process ends with the command's own exit code. Whoever called it can then treat it as the command.
    /// </summary>
    public bool IsExec => ExecOn is { Length: > 0 };

    public static CliOptions? Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} needs a value.");
                }

                return args[++i];
            }

            try
            {
                switch (args[i])
                {
                    case "--name": options.Name = Next(); break;
                    case "--data-dir": options.DataDirectory = Next(); break;
                    case "--discovery-port": options.DiscoveryPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--port": options.TransportPort = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--manual": options.ManualAddresses.Add(Next()); break;
                    case "--loopback": options.Loopback = true; break;
                    case "--pair": options.PairWith = Next(); break;
                    case "--accept": options.AutoAccept = true; break;
                    case "--exit-when-ready": options.ExitWhenReady = true; break;
                    case "--timeout": options.TimeoutSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--verbose": options.Verbose = true; break;
                    case "--input": options.Input = true; break;
                    case "--exec": options.ExecOn = Next(); break;
                    case "--command" or "-c": options.Command = Next(); break;
                    case "--command-file": options.CommandFile = Next(); break;
                    case "-h" or "--help": return null;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return null;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                Console.Error.WriteLine(ex.Message);
                return null;
            }
        }

        if (options.IsExec && options.Command is null && options.CommandFile is null)
        {
            Console.Error.WriteLine("--exec needs --command \"...\" or --command-file <path>.");
            return null;
        }

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            wm-cli: headless WinMirroring node

              --name <name>             device name shown to peers
              --data-dir <dir>          settings/identity directory, this tool's own device
                                        (default: %LocalAppData%\WinMirroring\cli; the app's own directory,
                                        or any other node's, is refused — exit code 66)
              --discovery-port <port>   UDP discovery port (default 47620)
              --port <port>             TCP transport port (default 47621)
              --manual <host[:port]>    peer address to probe (repeatable; port = peer's discovery port)
              --loopback                include the loopback interface (several nodes on one machine)
              --pair <name|id>          start PIN pairing with this peer once it is discovered
              --accept                  auto-accept incoming pairing requests
              --exit-when-ready         exit with code 0 after the first paired session is ready
              --timeout <seconds>       exit with code 2 when nothing is ready in time
              --verbose                 debug logging
              --input                   take part in mouse/keyboard sharing (hooks + injection)

            Running a command on a device, in the place of logging into it:

              --exec <name|id>          the device to run it on (it must be paired with this one, and have
                                        "allow commands from other devices" turned on)
              -c, --command "<text>"    the command; PowerShell, as the signed-in user over there
              --command-file <path>     read the command from a file instead (a whole script, any length)

            In this mode only what the command prints goes to standard output; everything of ours goes to
            standard error, and wm-cli exits with the command's own exit code. So it composes:

              wm-cli --exec tablet -c "Get-Service W32Time | Select-Object -Expand Status"
              wm-cli --exec tablet --command-file .\collect-logs.ps1 > logs.txt
              if (-not (wm-cli --exec tablet -c "Test-Path C:\\build")) { ... }

            The first time, pair this tool with the device once — it is a device of its own, with its own
            identity, exactly as a second machine would be, and it keeps that identity in its own directory:

              wm-cli --pair tablet
            """);
    }
}
