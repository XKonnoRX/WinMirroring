using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace WinMirroring.Input.Injection;

/// <summary>
/// A Windows scheduled task that starts the elevated input helper.
///
/// Starting the helper the direct way puts a UAC prompt on the screen every single time, which for a thing meant to
/// be on all the time means a prompt at every launch of the app. A task registered to run with the highest
/// privileges available solves it the way Windows intends: registering it asks for administrator once, and from
/// then on starting it asks nothing. The task has no trigger, so it only ever runs when this app asks for it, and
/// it runs as this user in this session — the helper has to be in the interactive session to inject anything.
///
/// Registering is entirely optional. Without it the app falls back to the prompt, which is what it did before.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedHelperTask
{
    private const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>One task per user: each signed-in user has their own app, helper and task.</summary>
    public static string Name => $"WinMirroring input helper ({Environment.UserName})";

    /// <summary>Whether a task exists that starts the helper this build ships with.</summary>
    public static bool IsRegisteredFor(string helperPath)
    {
        var xml = Query();
        if (xml is null)
        {
            return false;
        }

        try
        {
            var command = XDocument.Parse(xml)
                .Descendants(XName.Get("Command", Namespace))
                .Select(e => e.Value.Trim().Trim('"'))
                .FirstOrDefault();

            return command is { Length: > 0 }
                && string.Equals(Path.GetFullPath(command), Path.GetFullPath(helperPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the task, which asks for administrator once. False with a reason when the prompt was declined or
    /// Windows refused; the app then keeps asking at every start instead, which still works.
    /// </summary>
    public static bool Register(string helperPath, out string error)
    {
        var file = Path.Combine(Path.GetTempPath(), $"WinMirroring.task.{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks reads the definition as Unicode.
            File.WriteAllText(file, Definition(helperPath), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            return RunElevated($"/create /tn \"{Name}\" /xml \"{file}\" /f", out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
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

    /// <summary>Removes the task, which asks for administrator once.</summary>
    public static bool Unregister(out string error) => RunElevated($"/delete /tn \"{Name}\" /f", out error);

    /// <summary>
    /// Starts the helper through the task: elevated, and with nothing on the screen. False when there is no task or
    /// Windows refused to start it.
    /// </summary>
    public static bool TryStart(out string error)
    {
        var exit = Run($"/run /tn \"{Name}\"", out var output);
        error = exit == 0 ? string.Empty : output;
        return exit == 0;
    }

    private static string? Query()
    {
        // /xml ONE prints the task's definition; anything else means there is no such task here.
        var exit = Run($"/query /tn \"{Name}\" /xml ONE", out var output);
        return exit == 0 && output.Contains("<Task", StringComparison.Ordinal) ? output : null;
    }

    /// <summary>
    /// The task: no trigger, so it starts only when this app asks; the current user in the session they are signed
    /// in to, because input can only be injected from inside it; and the highest privileges they have, which is
    /// what lets the helper reach administrator windows.
    /// </summary>
    public static string Definition(string helperPath)
    {
        string user;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User?.Value ?? identity.Name;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            user = Environment.UserName;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(helperPath)) ?? AppContext.BaseDirectory;
        var task = new XElement(
            XName.Get("Task", Namespace),
            new XAttribute("version", "1.2"),
            new XElement(
                XName.Get("RegistrationInfo", Namespace),
                new XElement(XName.Get("Description", Namespace), "Запускает помощника WinMirroring, который передаёт ввод окнам администратора. Задание срабатывает только по запросу самого приложения."),
                new XElement(XName.Get("Date", Namespace), DateTime.Now.ToString("s", CultureInfo.InvariantCulture))),
            new XElement(
                XName.Get("Principals", Namespace),
                new XElement(
                    XName.Get("Principal", Namespace),
                    new XAttribute("id", "Author"),
                    new XElement(XName.Get("UserId", Namespace), user),
                    new XElement(XName.Get("LogonType", Namespace), "InteractiveToken"),
                    new XElement(XName.Get("RunLevel", Namespace), "HighestAvailable"))),
            new XElement(
                XName.Get("Settings", Namespace),
                // A helper left over from a moment ago is still on its way out when a new one is asked for, and
                // refusing the new one would leave the app with nothing connected. Each helper exits as soon as
                // its end of the pipe closes, so a stale one is gone in a moment either way.
                new XElement(XName.Get("MultipleInstancesPolicy", Namespace), "Parallel"),
                new XElement(XName.Get("DisallowStartIfOnBatteries", Namespace), "false"),
                new XElement(XName.Get("StopIfGoingOnBatteries", Namespace), "false"),
                new XElement(XName.Get("AllowHardTerminate", Namespace), "true"),
                new XElement(XName.Get("StartWhenAvailable", Namespace), "false"),
                new XElement(XName.Get("RunOnlyIfNetworkAvailable", Namespace), "false"),
                new XElement(
                    XName.Get("IdleSettings", Namespace),
                    new XElement(XName.Get("StopOnIdleEnd", Namespace), "false"),
                    new XElement(XName.Get("RestartOnIdle", Namespace), "false")),
                new XElement(XName.Get("AllowStartOnDemand", Namespace), "true"),
                new XElement(XName.Get("Enabled", Namespace), "true"),
                new XElement(XName.Get("Hidden", Namespace), "false"),
                new XElement(XName.Get("RunOnlyIfIdle", Namespace), "false"),
                new XElement(XName.Get("WakeToRun", Namespace), "false"),
                new XElement(XName.Get("ExecutionTimeLimit", Namespace), "PT0S"),
                new XElement(XName.Get("Priority", Namespace), "5")),
            new XElement(
                XName.Get("Actions", Namespace),
                new XAttribute("Context", "Author"),
                new XElement(
                    XName.Get("Exec", Namespace),
                    new XElement(XName.Get("Command", Namespace), Path.GetFullPath(helperPath)),
                    new XElement(XName.Get("WorkingDirectory", Namespace), folder))));

        return new XDocument(new XDeclaration("1.0", "UTF-16", null), task).ToString();
    }

    private static int Run(string arguments, out string output)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                output = "schtasks не запустился";
                return -1;
            }

            var text = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);
            output = string.IsNullOrWhiteSpace(errors) ? text : errors.Trim();
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output = ex.Message;
            return -1;
        }
    }

    /// <summary>
    /// The one place a prompt still appears: creating or removing the task itself. Nothing can be read back from an
    /// elevated child, so the exit code is all there is to go on.
    /// </summary>
    private static bool RunElevated(string arguments, out string error)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                error = "schtasks не запустился";
                return false;
            }

            if (!process.WaitForExit(60_000))
            {
                error = "регистрация задания не завершилась";
                return false;
            }

            error = process.ExitCode == 0 ? string.Empty : $"schtasks вернул код {process.ExitCode}";
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = the user said no to the prompt.
            error = ex.NativeErrorCode == 1223 ? "запуск от имени администратора отклонён" : ex.Message;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
