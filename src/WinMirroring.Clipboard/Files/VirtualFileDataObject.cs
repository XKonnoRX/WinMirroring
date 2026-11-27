using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard.Native;
using static WinMirroring.Clipboard.Native.ClipboardNative;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Clipboard object for files that live on another device. Explorer reads <c>FileGroupDescriptorW</c> and pulls
/// each file through <c>FileContents</c> as a stream, with its own progress dialog, so nothing is transferred
/// until the user pastes. Small sets additionally offer <c>CF_HDROP</c>, downloaded into a cache when an
/// application asks for it, for programs that only understand real paths.
///
/// It owns what runs ahead of Explorer for it (an <see cref="OfferPrefetcher"/>), and is in turn owned by whoever put
/// it where Explorer finds it — the clipboard thread, or a drag — who says when that is over: <see cref="Retire"/>
/// once it is no longer on offer, <see cref="Dispose"/> when nothing can reach it any more.
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileDataObject : ComTypes.IDataObject, IDisposable
{
    private const int DescriptorSize = 592;
    private const uint FdAttributes = 0x00000004;
    private const uint FdWritesTime = 0x00000020;
    private const uint FdFileSize = 0x00000040;
    private const uint FdProgressUi = 0x00004000;
    private const uint FdUnicode = 0x80000000;
    private const int FileAttributeDirectory = 0x10;
    private const int FileAttributeNormal = 0x80;
    private const int DropEffectCopy = 1;

    private readonly FileOfferManifest _manifest;
    private readonly IRemoteFileSource _source;
    private readonly bool _offerDropFiles;
    private readonly ILogger _logger;
    private readonly ComTypes.FORMATETC[] _formats;
    private readonly object _downloadGate = new();
    private IReadOnlyList<string>? _downloaded;
    private volatile OfferPrefetcher? _prefetcher;
    private volatile bool _retired;
    private volatile bool _disposed;

    public VirtualFileDataObject(FileOfferManifest manifest, IRemoteFileSource source, bool offerDropFiles, string sourceId, ILogger logger)
    {
        SourceId = sourceId;
        _manifest = manifest;
        _source = source;
        _offerDropFiles = offerDropFiles;
        _logger = logger;

        var formats = new List<ComTypes.FORMATETC>
        {
            Format(ClipboardFormats.FileGroupDescriptorW, ComTypes.TYMED.TYMED_HGLOBAL),
            Format(ClipboardFormats.FileContents, ComTypes.TYMED.TYMED_ISTREAM),
            Format(ClipboardFormats.PreferredDropEffect, ComTypes.TYMED.TYMED_HGLOBAL),
        };

        if (offerDropFiles)
        {
            formats.Add(Format(CF_HDROP, ComTypes.TYMED.TYMED_HGLOBAL));
        }

        _formats = [.. formats];
    }

    public FileOfferManifest Manifest => _manifest;

    /// <summary>Device that serves the file contents.</summary>
    public string SourceId { get; }

    /// <summary>Raised when a paste starts pulling the files into the cache (CF_HDROP path).</summary>
    public event Action<FileOfferManifest>? DownloadStarted;

    /// <summary>Bytes copied so far and the whole size, while a download runs (for a progress indicator).</summary>
    public event Action<FileOfferManifest, long, long>? DownloadProgress;

    /// <summary>A download ended; the flag says whether it succeeded.</summary>
    public event Action<FileOfferManifest, bool>? DownloadFinished;

    /// <summary>
    /// This is no longer what a paste or a drop would take: something else is on the clipboard now, or the drag that
    /// carried it is over. Whoever got hold of it before that — Explorer half way through a paste, a drop target
    /// still copying — reads on as before; only the waiting for a reader who has gone is cut short.
    /// </summary>
    public void Retire()
    {
        // Without the download lock: that one is held for as long as a direct paste downloads. The flag is set
        // before the prefetcher is looked at, and looked at after a prefetcher is made, so one of the two sees it.
        _retired = true;
        _prefetcher?.Retire();
    }

    /// <summary>Nothing can reach this object any more (the thread it lived on is ending): what ran ahead for it stops now.</summary>
    public void Dispose()
    {
        _disposed = true;
        _prefetcher?.Dispose();
    }

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        var id = (ushort)format.cfFormat;
        var wantsHGlobal = (format.tymed & ComTypes.TYMED.TYMED_HGLOBAL) != 0;

        if (id == ClipboardFormats.FileGroupDescriptorW && wantsHGlobal)
        {
            medium = HGlobalMedium(BuildDescriptor());
            return;
        }

        if (id == ClipboardFormats.FileContents && (format.tymed & ComTypes.TYMED.TYMED_ISTREAM) != 0)
        {
            medium = StreamMedium(format.lindex);
            return;
        }

        if (id == ClipboardFormats.PreferredDropEffect && wantsHGlobal)
        {
            medium = HGlobalMedium(BitConverter.GetBytes(DropEffectCopy));
            return;
        }

        if (id == CF_HDROP && _offerDropFiles && wantsHGlobal)
        {
            medium = HGlobalMedium(BuildDropFiles(EnsureDownloaded()));
            return;
        }

        throw new COMException("Unsupported clipboard format", DV_E_FORMATETC);
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) =>
        throw new COMException("Not supported", E_NOTIMPL);

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        foreach (var supported in _formats)
        {
            if (supported.cfFormat == format.cfFormat)
            {
                return (supported.tymed & format.tymed) != 0 ? S_OK : DV_E_TYMED;
            }
        }

        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release)
    {
        // Explorer reports "Paste Succeeded" / "Performed DropEffect" here; nothing to keep.
        if (release)
        {
            ReleaseStgMedium(ref medium);
        }
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction) =>
        direction == ComTypes.DATADIR.DATADIR_GET
            ? new FormatEnumerator(_formats)
            : throw new COMException("Not supported", E_NOTIMPL);

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Not supported", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    internal byte[] BuildDescriptor()
    {
        var entries = _manifest.Entries;
        var bytes = new byte[4 + entries.Count * DescriptorSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var d = bytes.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            var flags = FdAttributes | FdWritesTime | FdUnicode | (entry.IsDirectory ? 0 : FdFileSize | FdProgressUi);
            BinaryPrimitives.WriteUInt32LittleEndian(d, flags);
            BinaryPrimitives.WriteInt32LittleEndian(d[36..], entry.IsDirectory ? FileAttributeDirectory : FileAttributeNormal);
            BinaryPrimitives.WriteInt64LittleEndian(d[56..], ToFileTime(entry.LastWriteUtcTicks));
            BinaryPrimitives.WriteUInt32LittleEndian(d[64..], (uint)(entry.Size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], unchecked((uint)entry.Size));

            var name = entry.Path.Length > 259 ? entry.Path[..259] : entry.Path;
            Encoding.Unicode.GetBytes(name, d[72..]);
        }

        return bytes;
    }

    internal static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        var list = string.Concat(paths.Select(p => p + '\0')) + '\0';
        var bytes = new byte[20 + Encoding.Unicode.GetByteCount(list)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 20); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1); // fWide
        Encoding.Unicode.GetBytes(list, bytes.AsSpan(20));
        return bytes;
    }

    private ComTypes.STGMEDIUM StreamMedium(int index)
    {
        if (index < 0 || index >= _manifest.Entries.Count || _manifest.Entries[index].IsDirectory)
        {
            throw new COMException("No file at this index", DV_E_LINDEX);
        }

        var entry = _manifest.Entries[index];
        var stream = new ComStream(
            Prefetcher().Open(index) ?? new RemoteFileStream(_source, _manifest.OfferId, index, entry.Size),
            Path.GetFileName(entry.Path),
            entry.LastWriteUtcTicks);

        return new ComTypes.STGMEDIUM
        {
            tymed = ComTypes.TYMED.TYMED_ISTREAM,
            unionmember = Marshal.GetComInterfaceForObject<ComStream, ComTypes.IStream>(stream),
        };
    }

    /// <summary>Runs ahead of Explorer through the files it pulls one at a time (see <see cref="OfferPrefetcher"/>).</summary>
    private OfferPrefetcher Prefetcher()
    {
        lock (_downloadGate)
        {
            if (_prefetcher is null)
            {
                var prefetcher = new OfferPrefetcher(_manifest, _source, _logger);
                var reported = false;
                prefetcher.Progress += (done, total) =>
                {
                    if (!reported)
                    {
                        reported = true;
                        DownloadStarted?.Invoke(_manifest);
                    }

                    DownloadProgress?.Invoke(_manifest, done, total);
                    TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), done, total, false, false));
                };
                prefetcher.Finished += ok =>
                {
                    reported = false;
                    DownloadFinished?.Invoke(_manifest, ok);
                    TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), ok ? 1 : 0, 1, true, ok));
                };

                _prefetcher = prefetcher;
                if (_retired)
                {
                    prefetcher.Retire();
                }

                if (_disposed)
                {
                    prefetcher.Dispose();
                }
            }

            return _prefetcher;
        }
    }

    private string Describe() => TransferReports.Describe(_manifest);

    private IReadOnlyList<string> EnsureDownloaded()
    {
        lock (_downloadGate)
        {
            if (_downloaded is not null)
            {
                return _downloaded;
            }

            DownloadStarted?.Invoke(_manifest);
            TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), 0, _manifest.TotalSize, false, false));
            IReadOnlyList<string> downloaded;
            try
            {
                // Off the thread Explorer called in on: that thread is the one that draws its windows, and a
                // download run on it froze them — the indicator included, which is why nothing seemed to happen.
                //
                // Explorer gives no way to cancel this, so it goes by the source: when the connection closes or the
                // service stops, the download stops with it instead of being left to run into its next read.
                downloaded = Task.Run(() => OfferDownloader.DownloadAsync(
                    _manifest, _source,
                    (done, total) =>
                    {
                        DownloadProgress?.Invoke(_manifest, done, total);
                        TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), done, total, false, false));
                    },
                    _source.Closed)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Every failure, not only the expected ones: the indicator hides only when it is told the transfer
                // is over, and Explorer gets a plain failure either way.
                var why = ex is OperationCanceledException ? "The connection to the other device closed." : ex.Message;
                if (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    _logger.LogWarning("Pasting files from {Source} failed: {Message}", _manifest.SourceName, why);
                }
                else
                {
                    _logger.LogError(ex, "Pasting files from {Source} failed", _manifest.SourceName);
                }

                DownloadFinished?.Invoke(_manifest, false);
                TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), 0, _manifest.TotalSize, true, false));
                throw new COMException(why, E_FAIL);
            }

            _downloaded = downloaded;
            _logger.LogInformation("Downloaded {Count} clipboard item(s) from {Source}", downloaded.Count, _manifest.SourceName);
            DownloadFinished?.Invoke(_manifest, true);
            TransferReports.Report(new FileTransferProgress(_manifest.OfferId, Describe(), _manifest.TotalSize, _manifest.TotalSize, true, true));
            return downloaded;
        }
    }

    private static ComTypes.STGMEDIUM HGlobalMedium(ReadOnlySpan<byte> data) => new()
    {
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        unionmember = AllocGlobal(data),
    };

    private static ComTypes.FORMATETC Format(uint id, ComTypes.TYMED tymed) => new()
    {
        cfFormat = unchecked((short)id),
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        ptd = 0,
        tymed = tymed,
    };

    private static long ToFileTime(long utcTicks)
    {
        var minimum = new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return new DateTime(Math.Max(utcTicks, minimum), DateTimeKind.Utc).ToFileTimeUtc();
    }
}
