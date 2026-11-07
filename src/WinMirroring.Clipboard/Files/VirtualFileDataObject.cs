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
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileDataObject : ComTypes.IDataObject
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

    public VirtualFileDataObject(FileOfferManifest manifest, IRemoteFileSource source, bool offerDropFiles, ILogger logger)
    {
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

    /// <summary>Raised when a paste starts pulling the files into the cache (CF_HDROP path).</summary>
    public event Action<FileOfferManifest>? DownloadStarted;

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
            new RemoteFileStream(_source, _manifest.OfferId, index, entry.Size),
            Path.GetFileName(entry.Path),
            entry.LastWriteUtcTicks);

        return new ComTypes.STGMEDIUM
        {
            tymed = ComTypes.TYMED.TYMED_ISTREAM,
            unionmember = Marshal.GetComInterfaceForObject<ComStream, ComTypes.IStream>(stream),
        };
    }

    private IReadOnlyList<string> EnsureDownloaded()
    {
        lock (_downloadGate)
        {
            if (_downloaded is not null)
            {
                return _downloaded;
            }

            DownloadStarted?.Invoke(_manifest);
            try
            {
                _downloaded = OfferDownloader.DownloadAsync(_manifest, _source, CancellationToken.None).GetAwaiter().GetResult();
                _logger.LogInformation("Downloaded {Count} clipboard item(s) from {Source}", _downloaded.Count, _manifest.SourceName);
                return _downloaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Pasting files from {Source} failed: {Message}", _manifest.SourceName, ex.Message);
                throw new COMException(ex.Message, E_FAIL);
            }
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
