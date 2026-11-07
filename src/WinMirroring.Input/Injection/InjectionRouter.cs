using WinMirroring.Input.Protocol;

namespace WinMirroring.Input.Injection;

/// <summary>
/// The sink every <see cref="InputInjector"/> and touch injector uses by default. It always has a local sink and,
/// while "control of administrator windows" is on and its helper is connected, an additional remote one. Each call
/// goes to the remote sink when it is preferred and connected, otherwise to the local sink, so losing the helper
/// never stops input — it just stops reaching administrator windows, as before.
/// </summary>
public sealed class InjectionRouter : IInjectionSink
{
    /// <summary>The process-wide default. Injectors use this unless given their own sink (tests, the helper).</summary>
    public static InjectionRouter Instance { get; } = new(new LocalInjectionSink());

    private readonly IInjectionSink _local;
    private volatile IInjectionSink? _remote;
    private volatile bool _preferRemote;

    public InjectionRouter(IInjectionSink local) => _local = local;

    /// <summary>True while input is actually being routed to the elevated helper.</summary>
    public bool UsingRemote => _preferRemote && _remote is { IsConnected: true };

    public bool IsConnected => true;

    /// <summary>Installs (or clears) the helper sink and whether it should be used.</summary>
    public void SetRemote(IInjectionSink? remote, bool prefer)
    {
        _remote = remote;
        _preferRemote = prefer && remote is not null;
    }

    private IInjectionSink Active => UsingRemote ? _remote! : _local;

    public void MoveAbsolute(int x, int y) => Active.MoveAbsolute(x, y);

    public void MoveRelative(int dx, int dy) => Active.MoveRelative(dx, dy);

    public void Button(MouseButton button, bool down) => Active.Button(button, down);

    public void Wheel(int delta, bool horizontal) => Active.Wheel(delta, horizontal);

    public void Key(ushort virtualKey, ushort scanCode, bool up, bool extended) => Active.Key(virtualKey, scanCode, up, extended);

    public void Unicode(char ch, bool up) => Active.Unicode(ch, up);

    public void TouchFrame(IReadOnlyList<TouchContactInfo> contacts) => Active.TouchFrame(contacts);
}
