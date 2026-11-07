using System.Runtime.InteropServices;
using static WinMirroring.Clipboard.Native.ClipboardNative;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.Files;

/// <summary>Read-only COM IStream over a managed stream, handed to Explorer for FileContents.</summary>
[ComVisible(true)]
internal sealed class ComStream : ComTypes.IStream
{
    private const int StatFlagNoName = 1;
    private const int StgTyStream = 2;

    private readonly Stream _stream;
    private readonly string _name;
    private readonly long _lastWriteFileTime;
    private readonly object _gate = new();

    public ComStream(Stream stream, string name, long lastWriteUtcTicks)
    {
        _stream = stream;
        _name = name;
        _lastWriteFileTime = new DateTime(Math.Max(lastWriteUtcTicks, new DateTime(1601, 1, 2).Ticks), DateTimeKind.Utc).ToFileTimeUtc();
    }

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        int total;
        try
        {
            lock (_gate)
            {
                total = 0;
                while (total < cb)
                {
                    var read = _stream.Read(pv, total, cb - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
        }
        catch (IOException ex)
        {
            throw new COMException(ex.Message, STG_E_READFAULT);
        }

        if (pcbRead != 0)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new COMException("Read-only stream", STG_E_ACCESSDENIED);

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        long position;
        lock (_gate)
        {
            position = _stream.Seek(dlibMove, (SeekOrigin)dwOrigin);
        }

        if (plibNewPosition != 0)
        {
            Marshal.WriteInt64(plibNewPosition, position);
        }
    }

    public void SetSize(long libNewSize) => throw new COMException("Read-only stream", STG_E_ACCESSDENIED);

    public void CopyTo(ComTypes.IStream pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        var buffer = new byte[RemoteFileStream.ChunkSize];
        long copied = 0;
        while (copied < cb)
        {
            int read;
            lock (_gate)
            {
                read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, cb - copied));
            }

            if (read == 0)
            {
                break;
            }

            pstm.Write(buffer, read, 0);
            copied += read;
        }

        if (pcbRead != 0)
        {
            Marshal.WriteInt64(pcbRead, copied);
        }

        if (pcbWritten != 0)
        {
            Marshal.WriteInt64(pcbWritten, copied);
        }
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => throw new COMException("Not supported", E_NOTIMPL);

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported", STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported", STG_E_INVALIDFUNCTION);

    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag)
    {
        var time = new ComTypes.FILETIME
        {
            dwLowDateTime = unchecked((int)_lastWriteFileTime),
            dwHighDateTime = (int)(_lastWriteFileTime >> 32),
        };

        pstatstg = new ComTypes.STATSTG
        {
            pwcsName = (grfStatFlag & StatFlagNoName) != 0 ? null! : _name,
            type = StgTyStream,
            cbSize = _stream.Length,
            mtime = time,
            ctime = time,
            atime = time,
        };
    }

    public void Clone(out ComTypes.IStream ppstm) => throw new COMException("Not supported", E_NOTIMPL);
}
