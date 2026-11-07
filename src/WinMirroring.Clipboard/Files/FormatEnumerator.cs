using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace WinMirroring.Clipboard.Files;

[ComVisible(true)]
internal sealed class FormatEnumerator : ComTypes.IEnumFORMATETC
{
    private const int SFalse = 1;

    private readonly ComTypes.FORMATETC[] _formats;
    private int _index;

    public FormatEnumerator(ComTypes.FORMATETC[] formats, int index = 0)
    {
        _formats = formats;
        _index = index;
    }

    public int Next(int celt, ComTypes.FORMATETC[] rgelt, int[]? pceltFetched)
    {
        var fetched = 0;
        while (fetched < celt && _index < _formats.Length && fetched < rgelt.Length)
        {
            rgelt[fetched++] = _formats[_index++];
        }

        if (pceltFetched is { Length: > 0 })
        {
            pceltFetched[0] = fetched;
        }

        return fetched == celt ? 0 : SFalse;
    }

    public int Skip(int celt)
    {
        _index = Math.Min(_formats.Length, _index + celt);
        return _index < _formats.Length ? 0 : SFalse;
    }

    public int Reset()
    {
        _index = 0;
        return 0;
    }

    public void Clone(out ComTypes.IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(_formats, _index);
}
