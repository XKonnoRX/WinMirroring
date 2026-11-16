using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Input;
using WinMirroring.Shell;
using WinMirroring.Shell.Protocol;

namespace WinMirroring.App.Views;

/// <summary>
/// A console on another device: a line goes down, what it prints comes back as it prints it. In the place of
/// logging into the machine to look at something or set something right.
///
/// Deliberately not a terminal. It runs one command at a time and shows what that command writes; it has no screen
/// of its own to draw on, so a program that paints one — an editor, anything with a progress bar that rewrites a
/// line, anything waiting to be typed at — will not work here, and saying so plainly is better than half of it
/// working. What it is for is the other ninety per cent: looking at something, copying something, restarting
/// something, running a script.
/// </summary>
public partial class RemoteConsoleWindow : Window
{
    private readonly RemoteConsole _console;
    private readonly List<string> _history = [];
    private readonly StringBuilder _text = new();
    private int _historyAt = -1;
    private bool _closedByUs;

    public RemoteConsoleWindow(RemoteConsole console)
    {
        InitializeComponent();
        _console = console;
        Title = $"Консоль: {console.PeerName}";
        StateText.Text = "Запрашиваю разрешение…";
        Say($"Открываю консоль на {console.PeerName}…");

        // Everything the console raises arrives on the session's receive loop; the window belongs to its own thread.
        _console.Opened += directory => Dispatcher.BeginInvoke(() => OnOpened(directory));
        _console.Refused += reason => Dispatcher.BeginInvoke(() => OnRefused(reason));
        _console.Asking += question => Dispatcher.BeginInvoke(() => OnAsking(question));
        _console.Output += (stream, text) => Dispatcher.BeginInvoke(() => OnOutput(stream, text));
        _console.Done += (code, directory) => Dispatcher.BeginInvoke(() => OnDone(code, directory));
        _console.Finished += () => Dispatcher.BeginInvoke(OnFinished);
    }

    private void OnOpened(string directory)
    {
        StateText.Text = string.Empty;
        WhereText.Text = directory;
        CommandText.IsEnabled = true;
        RunButton.IsEnabled = true;
        Say($"Готово. Команды выполняются от имени пользователя на {_console.PeerName}.");
        Say("Это не терминал: интерактивные программы и те, что рисуют свой экран, здесь работать не будут.");
        CommandText.Focus();
    }

    /// <summary>
    /// Somebody at the device is being asked. Shown rather than left as an unexplained wait: "Запрашиваю
    /// разрешение…" says who is being waited for, a window that sits there saying it for ten minutes looks
    /// broken, and the person watching may be the one who has to go and tap the dialog.
    /// </summary>
    private void OnAsking(string question)
    {
        StateText.Text = "Ждём человека за устройством";
        Say($"{question}.");
    }

    private void OnRefused(string reason)
    {
        StateText.Text = "Отказано";
        Say($"{_console.PeerName} не дал консоль: {reason}.");
        if (reason.Contains("выключено", StringComparison.OrdinalIgnoreCase))
        {
            Say($"Включается на {_console.PeerName}: Настройки → «Разрешить выполнение команд с других устройств».");
        }
    }

    private void OnOutput(ShellStream stream, string text) =>
        Say(stream == ShellStream.Error ? "! " + text : text);

    private void OnDone(int code, string directory)
    {
        WhereText.Text = directory;
        if (code != 0)
        {
            Say($"[код выхода {code}]");
        }

        StateText.Text = string.Empty;
        StopButton.IsEnabled = false;
        RunButton.IsEnabled = true;
        CommandText.IsEnabled = true;
        CommandText.Focus();
    }

    private void OnFinished()
    {
        StateText.Text = "Консоль закрыта";
        StopButton.IsEnabled = false;
        RunButton.IsEnabled = false;
        CommandText.IsEnabled = false;
        if (!_closedByUs)
        {
            Say("Консоль закрыта.");
        }
    }

    private void OnRun(object sender, RoutedEventArgs e) => Run();

    private void Run()
    {
        var command = CommandText.Text;
        if (string.IsNullOrWhiteSpace(command) || !_console.Run(command))
        {
            return;
        }

        Say($"> {command}");
        _history.Add(command);
        _historyAt = _history.Count;
        CommandText.Clear();
        CommandText.IsEnabled = false;
        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StateText.Text = "Выполняется…";
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        Say("Прерываю: оболочка на устройстве завершается, консоль закроется.");
        _console.Cancel();
    }

    /// <summary>Up and down walk what has been typed here, which is most of what makes a console usable.</summary>
    private void OnCommandKey(object sender, KeyEventArgs e)
    {
        if (_history.Count == 0 || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        _historyAt = e.Key == Key.Up ? Math.Max(0, _historyAt - 1) : Math.Min(_history.Count, _historyAt + 1);
        CommandText.Text = _historyAt >= 0 && _historyAt < _history.Count ? _history[_historyAt] : string.Empty;
        CommandText.CaretIndex = CommandText.Text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// Adds a line and keeps the view at the bottom. Trimmed from the front when it gets long: a command that
    /// prints for an hour must not fill this machine's memory with what has already scrolled past.
    /// </summary>
    private void Say(string line)
    {
        const int MaxCharacters = 400_000;
        _text.AppendLine(line);
        if (_text.Length > MaxCharacters)
        {
            _text.Remove(0, _text.Length - (MaxCharacters / 2));
        }

        OutputText.Text = _text.ToString();
        Scroll.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closedByUs = true;
        _console.Close();
        base.OnClosed(e);
    }
}
