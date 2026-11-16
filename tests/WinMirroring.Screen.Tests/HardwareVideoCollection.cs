namespace WinMirroring.Screen.Tests;

/// <summary>
/// The tests that use this machine's video hardware, run one at a time.
///
/// There is one encoder, one decoder and one card, and they are shared with everything else on the machine. Two
/// tests asking for a 1080p frame at once is not a thing a hardware encoder owes anybody: it may simply hold what
/// it has been given and hand it back later, which to a test that fed it one frame looks like a frame that never
/// came. That failure says nothing about the code under test, which is the worst kind of failing test to have.
/// </summary>
[CollectionDefinition(DisableParallelization = true)]
public class HardwareVideoCollection;
