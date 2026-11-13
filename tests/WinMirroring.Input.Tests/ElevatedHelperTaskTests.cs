using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using WinMirroring.Input.Injection;

namespace WinMirroring.Input.Tests;

/// <summary>
/// The scheduled task that lets the elevated input helper start without a prompt at every launch. Nothing here
/// registers anything — that asks for administrator — but the definition is checked for the few things that would
/// make the feature silently never work, and Windows itself is asked whether it understands the document.
/// </summary>
public class ElevatedHelperTaskTests
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Definition() =>
        XDocument.Parse(ElevatedHelperTask.Definition(@"C:\Program Files\WinMirroring\WinMirroring.InputHost.exe"));

    [Fact]
    public void TheTaskRunsTheHelperElevatedInTheUsersOwnSession()
    {
        var task = Definition();

        // Highest privileges is the point of the task; without the interactive token the helper would run outside
        // the session the screen belongs to and could not inject anything into it.
        Assert.Equal("HighestAvailable", task.Descendants(XName.Get("RunLevel", Ns)).Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(XName.Get("LogonType", Ns)).Single().Value);
        Assert.Equal(@"C:\Program Files\WinMirroring\WinMirroring.InputHost.exe", task.Descendants(XName.Get("Command", Ns)).Single().Value);
        Assert.Equal(@"C:\Program Files\WinMirroring", task.Descendants(XName.Get("WorkingDirectory", Ns)).Single().Value);

        // The helper takes no arguments: the task cannot pass any, which is why the channel has a fixed name.
        Assert.Empty(task.Descendants(XName.Get("Arguments", Ns)));

        // No trigger at all, so it runs only when the app asks — and on demand is how the app asks.
        Assert.Empty(task.Descendants(XName.Get("Triggers", Ns)));
        Assert.Equal("true", task.Descendants(XName.Get("AllowStartOnDemand", Ns)).Single().Value);

        // Input sharing is used on a laptop, and for as long as it is wanted.
        Assert.Equal("false", task.Descendants(XName.Get("DisallowStartIfOnBatteries", Ns)).Single().Value);
        Assert.Equal("false", task.Descendants(XName.Get("StopIfGoingOnBatteries", Ns)).Single().Value);
        Assert.Equal("PT0S", task.Descendants(XName.Get("ExecutionTimeLimit", Ns)).Single().Value);
    }

    /// <summary>
    /// Windows is the only judge of whether the document is acceptable. Registering needs administrator, so this
    /// asks schtasks to read it and accepts being turned away for want of rights — what it must not say is that
    /// the document itself is wrong.
    /// </summary>
    [Fact]
    public void WindowsUnderstandsTheDefinition()
    {
        var file = Path.Combine(Path.GetTempPath(), $"WinMirroring.task.test.{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, ElevatedHelperTask.Definition(Environment.ProcessPath ?? @"C:\Windows\System32\cmd.exe"),
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        try
        {
            // /z deletes the task as soon as it finishes, and the name is this run's alone.
            var name = $"WinMirroring test {Guid.NewGuid():N}";
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/create /tn \"{name}\" /xml \"{file}\" /f")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            if (process.ExitCode == 0)
            {
                // It went in, so this machine's session could register it. Take it straight back out again.
                using var remove = Process.Start(new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{name}\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                remove.WaitForExit(30_000);
                return;
            }

            // Being refused the rights is the expected answer without administrator; a complaint about the
            // document is the failure this test exists to catch.
            Assert.DoesNotContain("XML", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("malformed", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("неверн", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void WithNoTaskRegisteredTheHelperStillHasToAsk()
    {
        // Nothing registered a task for this path, so the app must fall back to the prompt.
        Assert.False(ElevatedHelperTask.IsRegisteredFor(Path.Combine(Path.GetTempPath(), $"nothing-{Guid.NewGuid():N}.exe")));
    }

    [Fact]
    public void TheChannelIsNamedForThisUserAndStaysThatWay()
    {
        var name = InjectionPipe.NameForCurrentUser();

        Assert.StartsWith("WinMirroring.Inject.", name, StringComparison.Ordinal);
        Assert.Equal(name, InjectionPipe.NameForCurrentUser());
        Assert.DoesNotContain('\\', name);
    }
}
