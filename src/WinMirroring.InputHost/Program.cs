using WinMirroring.Input.Injection;

namespace WinMirroring.InputHost;

/// <summary>
/// The elevated input helper. It does one thing: connect back to WinMirroring over the private pipe it was launched
/// with and replay the injection commands it receives. All the logic is in <see cref="InjectionHelperHost"/>.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => InjectionHelperHost.RunAsync(args);
}
