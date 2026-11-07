using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMirroring.Core.Messages;

public static class ControlMessageCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode(ControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, Options);
    }

    /// <summary>Returns null for malformed or unknown messages so a newer peer cannot crash an older one.</summary>
    public static ControlMessage? TryDecode(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(payload, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
