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

    public string? Pin { get; private set; }

    public string? PairWith { get; private set; }

    public bool AutoAccept { get; private set; }

    public bool ExitWhenReady { get; private set; }

    public int? TimeoutSeconds { get; private set; }

    public bool Verbose { get; private set; }

    public bool Input { get; private set; }

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
                    case "--pin": options.Pin = Next(); break;
                    case "--pair": options.PairWith = Next(); break;
                    case "--accept": options.AutoAccept = true; break;
                    case "--exit-when-ready": options.ExitWhenReady = true; break;
                    case "--timeout": options.TimeoutSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--verbose": options.Verbose = true; break;
                    case "--input": options.Input = true; break;
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

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            wm-cli: headless WinMirroring node

              --name <name>             device name shown to peers
              --data-dir <dir>          settings/identity directory (default: %LocalAppData%\WinMirroring)
              --discovery-port <port>   UDP discovery port (default 47620)
              --port <port>             TCP transport port (default 47621)
              --manual <host[:port]>    peer address to probe (repeatable; port = peer's discovery port)
              --loopback                include the loopback interface (several nodes on one machine)
              --pin <digits>            fixed PIN instead of a random one (both sides, for tests)
              --pair <name|id>          start PIN pairing with this peer once it is discovered
              --accept                  auto-accept incoming pairing requests
              --exit-when-ready         exit with code 0 after the first paired session is ready
              --timeout <seconds>       exit with code 2 when nothing is ready in time
              --verbose                 debug logging
              --input                   take part in mouse/keyboard sharing (hooks + injection)
            """);
    }
}
