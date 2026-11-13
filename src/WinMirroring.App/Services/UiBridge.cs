using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace WinMirroring.App.Services;

/// <summary>
/// What the React pages can ask the app for, and what the app pushes back. The page sends
/// <c>{id, method, args}</c>, gets <c>{id, ok, result|error}</c>, and listens for <c>{event, data}</c>.
/// Everything runs on the UI thread, like the rest of the windows.
/// </summary>
public sealed class UiBridge
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _methods = new(StringComparer.Ordinal);
    private readonly List<CoreWebView2> _pages = [];

    /// <summary>Registers what <paramref name="method"/> does; the argument is the page's <c>args</c>.</summary>
    public void On(string method, Func<JsonElement, object?> handler) =>
        _methods[method] = args => Task.FromResult(handler(args));

    public void OnAsync(string method, Func<JsonElement, Task<object?>> handler) => _methods[method] = handler;

    /// <summary>Starts serving a page (and stops when it goes away).</summary>
    public void Attach(CoreWebView2 page)
    {
        _pages.Add(page);
        page.WebMessageReceived += OnMessage;
    }

    public void Detach(CoreWebView2 page)
    {
        _pages.Remove(page);
        page.WebMessageReceived -= OnMessage;
    }

    /// <summary>Tells every open page that something changed.</summary>
    public void Push(string name, object? data = null)
    {
        var message = JsonSerializer.Serialize(new { @event = name, data }, Json);
        foreach (var page in _pages.ToArray())
        {
            try
            {
                page.PostWebMessageAsJson(message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                _pages.Remove(page);
            }
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (sender is not CoreWebView2 page)
        {
            return;
        }

        var id = 0;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idElement) ? idElement.GetInt32() : 0;
            var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
            var args = root.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;

            if (method is null || !_methods.TryGetValue(method, out var handler))
            {
                Reply(page, id, false, null, $"нет такого метода: {method}");
                return;
            }

            var result = await handler(args);
            Reply(page, id, true, result, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UI request failed");
            Reply(page, id, false, null, ex.Message);
        }
    }

    private static void Reply(CoreWebView2 page, int id, bool ok, object? result, string? error)
    {
        try
        {
            page.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok, result, error }, Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
