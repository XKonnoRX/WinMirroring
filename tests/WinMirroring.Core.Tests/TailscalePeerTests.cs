using System.Net;
using WinMirroring.Core.Network;

namespace WinMirroring.Core.Tests;

/// <summary>
/// Paired devices are looked for on a Tailscale network by name. What has to hold is that the list is read as
/// Tailscale writes it, and that nothing is ever picked from it but an online machine with a paired device's name.
/// </summary>
public class TailscalePeerTests
{
    private const string Status = """
        {
          "Version": "1.80.0",
          "Self": { "HostName": "Frankenshtein", "TailscaleIPs": ["100.64.140.15", "fd7a:115c:a1e0::5a2e:8c10"], "Online": true },
          "Peer": {
            "nodekey:aa": { "HostName": "Banger", "OS": "windows", "TailscaleIPs": ["fd7a:115c:a1e0::1", "100.119.58.45"], "Online": true },
            "nodekey:bb": { "HostName": "SRV-1C-H", "OS": "windows", "TailscaleIPs": ["100.74.252.74"], "Online": true },
            "nodekey:cc": { "HostName": "Konny", "OS": "windows", "TailscaleIPs": ["100.122.77.41"], "Online": false },
            "nodekey:dd": { "HostName": "phone", "OS": "android", "TailscaleIPs": [], "Online": true }
          }
        }
        """;

    [Fact]
    public void TheListIsReadAsTailscaleWritesIt()
    {
        var machines = TailscalePeers.Parse(Status);

        Assert.Equal(3, machines.Count); // the one with no address is not a machine anyone can reach
        Assert.Contains(new TailscalePeers.Machine("Banger", IPAddress.Parse("100.119.58.45"), true), machines);
        Assert.Contains(new TailscalePeers.Machine("Konny", IPAddress.Parse("100.122.77.41"), false), machines);
        Assert.DoesNotContain(machines, m => m.HostName == "Frankenshtein"); // this machine itself
    }

    [Fact]
    public void OnlyAMachineWithAPairedDevicesNameIsEverPicked()
    {
        var machines = TailscalePeers.Parse(Status);

        Assert.Equal([IPAddress.Parse("100.119.58.45")], TailscalePeers.Pick(machines, ["banger"]));
        Assert.Empty(TailscalePeers.Pick(machines, ["Tablet"]));
        Assert.Empty(TailscalePeers.Pick(machines, []));
        Assert.Empty(TailscalePeers.Pick(machines, ["", "  "]));
    }

    [Fact]
    public void AMachineThatIsOfflineIsNotAsked() =>
        Assert.Empty(TailscalePeers.Pick(TailscalePeers.Parse(Status), ["Konny"]));

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"Peer\": null}")]
    [InlineData("{\"Peer\": {\"k\": 5}}")]
    public void WhatIsNotAListIsNoMachines(string output) =>
        Assert.Empty(TailscalePeers.Parse(output));
}
