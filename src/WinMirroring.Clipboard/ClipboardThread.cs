using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Native;
using static WinMirroring.Clipboard.Native.ClipboardNative;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard;

/// <summary>
/// An STA thread that owns the clipboard listener window and does every clipboard read and write, so OLE clipboard
/// objects live on a thread that pumps messages. Changes made by this thread are recognised by the clipboard
/// sequence number and never reported back, which prevents echo between devices.
/// </summary>
internal sealed class ClipboardThread : IDisposable
{
    // Unique per instance: a window class carries its window procedure, so two instances sharing one class name
    // would have the second instance's messages handled by the first instance's object.
    private readonly string ClassName = $"WinMirroring.ClipboardSink.{Guid.NewGuid():N}";
    private const uint WmInvoke = WM_APP + 1;
    private const nuint DebounceTimerId = 1;
    private const uint DebounceMs = 150;
    private const int ClipboardCantOpen = unchecked((int)0x800401D0);

    private readonly ILogger _logger;
    private readonly Func<long> _maxInlineBytes;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _wndProc;
    private readonly ConcurrentQueue<Action> _queue = new();

    private nint _instance;
    private nint _hwnd;
    private bool _classRegistered;
    private uint _threadId;
    private Exception? _startupError;
    private uint _ignoreUpToSequence;
    private ComTypes.IDataObject? _ownDataObject;
    private int _disposed;

    public ClipboardThread(ILogger logger, Func<long> maxInlineBytes)
    {
        _logger = logger;
        _maxInlineBytes = maxInlineBytes;
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinMirroring.Clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The user copied something on this device. Raised on the clipboard thread.</summary>
    public event Action<ClipboardSnapshot>? LocalChanged;

    /// <summary>Diagnostics: clipboard update notifications received / changes handled after debounce.</summary>
    internal int Notifications;

    internal int ChangesHandled;

    public void Start()
    {
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            throw new InvalidOperationException("Clipboard listener could not start.", _startupError);
        }
    }

    public Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!PostMessageW(_hwnd, WmInvoke, 0, 0))
        {
            completion.TrySetException(new InvalidOperationException("The clipboard thread is not running."));
        }

        return completion.Task;
    }

    /// <summary>Reads the clipboard right now (diagnostics and tests).</summary>
    public Task<ClipboardSnapshot?> ReadNowAsync()
    {
        var completion = new TaskCompletionSource<ClipboardSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = InvokeAsync(() => completion.TrySetResult(ReadSnapshot()))
            .ContinueWith(t => completion.TrySetException(t.Exception!.InnerExceptions), TaskContinuationOptions.OnlyOnFaulted);
        return completion.Task;
    }

    /// <summary>Replaces the clipboard with data formats from another device.</summary>
    public Task WriteItemsAsync(IReadOnlyList<ClipboardItem> items) => InvokeAsync(() => WriteItems(items));

    /// <summary>Replaces the clipboard with an OLE data object (virtual files from another device).</summary>
    public Task SetDataObjectAsync(ComTypes.IDataObject dataObject) => InvokeAsync(() => SetDataObject(dataObject));

    private void Run()
    {
        try
        {
            var hr = OleInitialize(0);
            if (hr < 0)
            {
                throw new COMException("OleInitialize failed", hr);
            }

            _threadId = GetCurrentThreadId();
            _instance = GetModuleHandleW(null);
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = _instance,
                lpszClassName = ClassName,
            };

            if (RegisterClassExW(ref windowClass) != 0)
            {
                _classRegistered = true;
            }

            _hwnd = CreateWindowExW(0, ClassName, "WinMirroring clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, _instance, 0);
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
            }

            if (!AddClipboardFormatListener(_hwnd))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed");
            }

            _ignoreUpToSequence = GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            _startupError = ex;
            Cleanup();
            _ready.Set();
            return;
        }

        _ready.Set();
        _logger.LogInformation("Clipboard listener started");

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }

        Cleanup();
    }

    private void Cleanup()
    {
        if (_ownDataObject is { } own)
        {
            try
            {
                if (OleIsCurrentClipboard(own) == S_OK)
                {
                    OleSetClipboard(null);
                }
            }
            catch (COMException)
            {
            }

            _ownDataObject = null;
        }

        if (_hwnd != 0)
        {
            RemoveClipboardFormatListener(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_classRegistered)
        {
            UnregisterClassW(ClassName, _instance);
            _classRegistered = false;
        }

        OleUninitialize();
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_CLIPBOARDUPDATE:
                // Apps often set several formats in a row; read once things settle.
                Notifications++;
                SetTimer(hWnd, DebounceTimerId, DebounceMs, 0);
                return 0;

            case WM_TIMER when wParam == (nint)DebounceTimerId:
                KillTimer(hWnd, DebounceTimerId);
                ChangesHandled++;
                HandleChange();
                return 0;

            case WmInvoke:
                while (_queue.TryDequeue(out var action))
                {
                    action();
                }

                return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleChange()
    {
        var sequence = GetClipboardSequenceNumber();
        if (unchecked((int)(sequence - _ignoreUpToSequence)) <= 0)
        {
            return;
        }

        _ownDataObject = null;
        ClipboardSnapshot? snapshot;
        try
        {
            snapshot = ReadSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the clipboard failed");
            return;
        }

        // A refused snapshot carries nothing but still travels on, so the user is told why their copy did not
        // appear on the other device instead of being left to wonder.
        if (snapshot is not null && (!snapshot.IsEmpty || snapshot.Refused is not null))
        {
            try
            {
                LocalChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clipboard change handler failed");
            }
        }
    }

    private ClipboardSnapshot? ReadSnapshot()
    {
        if (!OpenWithRetry())
        {
            _logger.LogDebug("Clipboard is busy; skipping this change");
            return null;
        }

        try
        {
            if (WhySyncIsRefused() is { } refused)
            {
                // Nothing is read at all. Reading and then deciding not to send would already have put someone's
                // password into this process's memory, and the point of the mark is that it never gets there.
                _logger.LogDebug("The clipboard was left alone: {Reason}", refused);
                return new ClipboardSnapshot([], [], refused);
            }

            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                return new ClipboardSnapshot([], ReadDropFiles(GetClipboardData(CF_HDROP)));
            }

            var budget = _maxInlineBytes();
            var items = new List<ClipboardItem>();
            foreach (var (kind, id) in ClipboardFormats.Shared)
            {
                if (id == 0 || !IsClipboardFormatAvailable(id))
                {
                    continue;
                }

                var data = ReadGlobal(GetClipboardData(id), budget);
                if (data is null)
                {
                    continue;
                }

                items.Add(new ClipboardItem(kind, data));
                budget -= data.Length;
            }

            return new ClipboardSnapshot(items, []);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Why what is on the clipboard must not leave this machine, or null when nothing forbids it. The clipboard is
    /// already open; what the marks mean is <see cref="ClipboardPrivacy"/>'s business, this only reads them.
    /// </summary>
    private static string? WhySyncIsRefused() => ClipboardPrivacy.WhySyncIsRefused(
        ClipboardFormats.ExcludeFromMonitors != 0 && IsClipboardFormatAvailable(ClipboardFormats.ExcludeFromMonitors),
        ReadPermission(ClipboardFormats.CanUploadToCloud),
        ReadPermission(ClipboardFormats.CanIncludeInHistory));

    /// <summary>
    /// The DWORD a permission format carries, or null when the format is not on the clipboard at all. A format
    /// that is there but cannot be read counts as zero: it was put there for a reason, and the wrong way to be
    /// wrong here is to send someone's password anyway.
    /// </summary>
    private static uint? ReadPermission(uint id)
    {
        if (id == 0 || !IsClipboardFormatAvailable(id))
        {
            return null;
        }

        var value = ReadGlobal(GetClipboardData(id), 64);
        return value is null || value.Length < 4 ? 0u : BitConverter.ToUInt32(value, 0);
    }

    internal static IReadOnlyList<string> ReadDropFiles(nint handle)
    {
        var bytes = ReadGlobal(handle, 16 * 1024 * 1024);
        if (bytes is null || bytes.Length < 20)
        {
            return [];
        }

        // DROPFILES: DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; then a double-null-terminated list.
        var offset = BitConverter.ToInt32(bytes, 0);
        var wide = BitConverter.ToInt32(bytes, 16) != 0;
        if (offset < 20 || offset >= bytes.Length)
        {
            return [];
        }

        var text = wide
            ? Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset)
            : Encoding.Default.GetString(bytes, offset, bytes.Length - offset);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 2)
            .ToList();
    }

    private void WriteItems(IReadOnlyList<ClipboardItem> items)
    {
        if (!OpenWithRetry())
        {
            throw new IOException("The clipboard is held by another application.");
        }

        try
        {
            EmptyClipboard();
            foreach (var item in items)
            {
                var id = ClipboardFormats.IdOf(item.Kind);
                if (id == 0)
                {
                    continue;
                }

                var handle = AllocGlobal(item.Data);
                if (SetClipboardData(id, handle) == 0)
                {
                    GlobalFree(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        _ownDataObject = null;
        _ignoreUpToSequence = GetClipboardSequenceNumber();
    }

    private void SetDataObject(ComTypes.IDataObject dataObject)
    {
        var hr = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            hr = OleSetClipboard(dataObject);
            if (hr != ClipboardCantOpen)
            {
                break;
            }

            Thread.Sleep(20);
        }

        if (hr < 0)
        {
            throw new COMException("OleSetClipboard failed", hr);
        }

        _ownDataObject = dataObject;
        _ignoreUpToSequence = GetClipboardSequenceNumber();
    }

    private bool OpenWithRetry()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(_hwnd))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _ready.Dispose();
    }
}
