using System.Text.Json;
using UnifiPortSync;
using Xunit;

namespace UnifiPortSync.Tests;

// A UniFi controller in memory. Records every change and can be told to refuse some.
internal class FakeUnifi : IUnifiClient
{
    public List<PortForward> Rules = new();
    public List<string> Log = new();
    public Func<PortForward, string> RefuseCreate = _ => null;
    private int _next = 1;

    public Task<List<PortForward>> GetPortForwardsAsync() => Task.FromResult(Rules.Select(Clone).ToList());

    // Behave like UniFi: refuse a rule that overlaps an existing enabled one.
    public bool EnforceOverlap;
    // Behave like an older controller that only takes plain comma lists.
    public bool RefuseRanges;

    private void Check(PortForward rule)
    {
        if (RefuseRanges && (rule.DstPort ?? "").Contains('-'))
            throw new UnifiApiException(400, "UniFi returned 400: api.err.InvalidPortRange");
        if (EnforceOverlap && Rules.Any(r => r.Id != rule.Id && r.Enabled
                && UnifiReconciler.ProtoOverlap(r.Proto, rule.Proto)
                && UnifiReconciler.InterfaceOverlap(r.Interface, rule.Interface)
                && UnifiReconciler.ExpandPorts(r.DstPort).Overlaps(UnifiReconciler.ExpandPorts(rule.DstPort))))
            throw new UnifiApiException(400, "UniFi returned 400: api.err.PortForwardOverlap");
    }

    public Task CreatePortForwardAsync(PortForward rule)
    {
        var why = RefuseCreate(rule);
        if (why != null) throw new UnifiApiException(400, "UniFi returned 400: " + why);
        Check(rule);
        var r = Clone(rule);
        r.Id = "id" + _next++;
        Rules.Add(r);
        Log.Add($"create {r.Name} {r.Proto} {r.DstPort} {r.Interface}");
        return Task.CompletedTask;
    }

    public Task UpdatePortForwardAsync(PortForward rule)
    {
        Check(rule);
        Rules.RemoveAll(r => r.Id == rule.Id);
        Rules.Add(Clone(rule));
        Log.Add($"update {rule.Name} {rule.Proto} {rule.DstPort} {rule.Interface}");
        return Task.CompletedTask;
    }

    public Task DeletePortForwardAsync(string id)
    {
        var r = Rules.Single(x => x.Id == id);
        Rules.Remove(r);
        Log.Add($"delete {r.Name}");
        return Task.CompletedTask;
    }

    public PortForward Add(string name, string proto, string ports, string fwd = "192.168.1.50", bool enabled = true, string iface = "wan", string src = "any")
    {
        var r = new PortForward { Id = "id" + _next++, Name = name, Proto = proto, DstPort = ports, FwdPort = ports, Fwd = fwd, Enabled = enabled, Interface = iface, Src = src };
        Rules.Add(r);
        return r;
    }

    private static PortForward Clone(PortForward r) => JsonSerializer.Deserialize<PortForward>(JsonSerializer.Serialize(r));
    public void Dispose() { }
}

// A throwaway AMP data folder with instances.json and one GenericModule.kvp per instance.
internal class FakeAmp : IDisposable
{
    public readonly string Root = Directory.CreateTempSubdirectory("ampdata").FullName;
    private readonly List<object> _instances = new() { new { InstanceName = "ADS01", Module = "ADS" } };

    // ports: (Ref, Protocol 0/1/2, Port, Range)
    public FakeAmp Instance(string name, params (string Ref, int Proto, int Port, int Range)[] ports)
    {
        var dir = Path.Combine(Root, "instances", name);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(ports.Select(p => new { p.Ref, Protocol = p.Proto, p.Port, p.Range }));
        File.WriteAllText(Path.Combine(dir, "GenericModule.kvp"), $"Meta.Something=1\nApp.Ports={json}\n");
        _instances.Add(new { InstanceName = name, Module = "GenericModule", Path = dir });
        return this;
    }

    // Listed in instances.json but its config file is missing.
    public FakeAmp InstanceWithoutConfig(string name)
    {
        var dir = Path.Combine(Root, "instances", name);
        Directory.CreateDirectory(dir);
        _instances.Add(new { InstanceName = name, Module = "GenericModule", Path = dir });
        return this;
    }

    public UnifiConfig Config(string iface = "wan", string target = "192.168.1.50", string exclude = "")
    {
        File.WriteAllText(Path.Combine(Root, "instances.json"), JsonSerializer.Serialize(_instances));
        return new UnifiConfig { AmpDataPath = Root, TargetLanIp = target, Interface = iface, RuleNamePrefix = "AMP:", ExcludedPortNames = exclude };
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class ReconcilerTests
{
    private static Task<SyncPlan> Run(UnifiConfig cfg, FakeUnifi unifi, string only = null)
        => new UnifiReconciler(cfg, () => unifi).ReconcileAsync(apply: true, only);

    [Fact]
    public async Task Creates_grouped_rules_per_protocol()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 3), ("Steam", 0, 27015, 1));
        var unifi = new FakeUnifi();
        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Contains("create AMP:Valheim UDP udp 2456-2458 wan", unifi.Log);
        Assert.Contains("create AMP:Valheim TCP tcp 27015 wan", unifi.Log);
    }

    [Fact]
    public async Task Disabled_manual_rule_does_not_count_as_forwarded()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 1));
        var unifi = new FakeUnifi();
        unifi.Add("Old valheim", "udp", "2456", enabled: false);

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.AlreadyForwarded);
        Assert.Contains("create AMP:Valheim UDP udp 2456 wan", unifi.Log);
    }

    [Fact]
    public async Task Enabled_manual_rule_to_same_host_still_covers_the_port()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 1));
        var unifi = new FakeUnifi();
        unifi.Add("Manual", "tcp_udp", "2456");

        var plan = await Run(amp.Config(), unifi);

        Assert.Single(plan.AlreadyForwarded);
        Assert.Empty(unifi.Log);
    }

    [Fact]
    public async Task Manual_rule_on_other_wan_does_not_cover_and_does_not_clash()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 1));
        var unifi = new FakeUnifi();
        unifi.Add("Manual on WAN2", "udp", "2456", iface: "wan2");

        var plan = await Run(amp.Config(iface: "wan"), unifi);

        Assert.Empty(plan.Conflicts);
        Assert.Contains("create AMP:Valheim UDP udp 2456 wan", unifi.Log);
    }

    [Fact]
    public async Task Manual_rule_on_one_wan_is_a_conflict_when_listening_on_all()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 1));
        var unifi = new FakeUnifi();
        unifi.Add("Manual on WAN1", "udp", "2456", iface: "wan");

        var plan = await Run(amp.Config(iface: "both"), unifi);

        Assert.Contains(plan.Conflicts, c => c.Contains("only listens on WAN 1"));
        Assert.Empty(unifi.Log);
    }

    [Fact]
    public async Task Source_limited_manual_rule_is_reported_not_relied_on()
    {
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 2456, 1));
        var unifi = new FakeUnifi();
        unifi.Add("Friends only", "udp", "2456", src: "203.0.113.7");

        var plan = await Run(amp.Config(), unifi);

        Assert.Contains(plan.Conflicts, c => c.Contains("only forwarded from source"));
        Assert.Empty(plan.AlreadyForwarded);
    }

    [Fact]
    public async Task One_refused_change_does_not_stop_the_rest()
    {
        using var amp = new FakeAmp()
            .Instance("Ark", ("GamePort", 1, 7777, 1))
            .Instance("Rust", ("GamePort", 1, 28015, 1));
        var unifi = new FakeUnifi { RefuseCreate = r => r.Name.StartsWith("AMP:Ark") ? "api.err.PortForwardOverlap" : null };
        unifi.Add("AMP:Gone UDP", "udp", "9999");

        var plan = await Run(amp.Config(), unifi);

        Assert.Single(plan.Failed);
        Assert.Contains("api.err.PortForwardOverlap", plan.Failed[0]);
        Assert.Contains("create AMP:Rust UDP udp 28015 wan", unifi.Log);
        Assert.Contains("delete AMP:Gone UDP", unifi.Log);           // orphan sweep still ran
        Assert.DoesNotContain(plan.ToCreate, c => c.StartsWith("AMP:Ark")); // not reported as done
    }

    [Fact]
    public async Task Two_instances_on_one_port_get_one_rule_and_a_conflict()
    {
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 2, 25565, 1))
            .Instance("Beta", ("GamePort", 0, 25565, 1), ("Query", 1, 25566, 1));
        var unifi = new FakeUnifi();

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Contains("create AMP:Alpha TCP+UDP tcp_udp 25565 wan", unifi.Log);
        Assert.DoesNotContain(unifi.Log, l => l.StartsWith("create AMP:Beta TCP"));
        Assert.Contains("create AMP:Beta UDP udp 25566 wan", unifi.Log);
        Assert.Contains(plan.Conflicts, c => c.Contains("used by both 'Alpha' and 'Beta'"));
    }

    [Fact]
    public async Task Instance_already_holding_a_port_keeps_it()
    {
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 0, 25565, 1))
            .Instance("Beta", ("GamePort", 0, 25565, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Beta TCP", "tcp", "25565");

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(unifi.Log); // Beta keeps its rule, Alpha gets nothing
        Assert.Contains(plan.Conflicts, c => c.Contains("forwarded for 'Beta' only"));
    }

    [Fact]
    public async Task Targeted_sync_does_not_touch_an_instance_with_a_longer_similar_name()
    {
        using var amp = new FakeAmp()
            .Instance("Valheim", ("GamePort", 1, 2456, 1))
            .Instance("Valheim 2", ("GamePort", 1, 2466, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Valheim UDP", "udp", "2456");
        unifi.Add("AMP:Valheim 2 UDP", "udp", "2466");
        unifi.Add("AMP:Valheim 2 TCP", "tcp", "2470"); // stale, but not ours to judge in this sync

        var plan = await Run(amp.Config(), unifi, only: "Valheim");

        Assert.Empty(plan.ToDelete);
        Assert.Empty(unifi.Log);
    }

    [Fact]
    public async Task Missing_config_file_skips_the_instance_and_keeps_its_rules()
    {
        using var amp = new FakeAmp().InstanceWithoutConfig("Ark");
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Ark UDP", "udp", "7777");

        var plan = await Run(amp.Config(), unifi);

        Assert.True(plan.Skipped.ContainsKey("Ark"));
        Assert.Empty(plan.ToDelete);
        Assert.Single(unifi.Rules);
    }

    [Fact]
    public async Task Admin_ports_are_never_forwarded()
    {
        using var amp = new FakeAmp().Instance("7DTD",
            ("GamePort", 2, 26900, 1), ("TelnetPort", 0, 8081, 1), ("ControlPanelPort", 0, 8080, 1),
            ("RCONPort", 0, 27020, 1), ("RestApiPort", 0, 8212, 1));
        var unifi = new FakeUnifi();

        await Run(amp.Config(exclude: "restapi"), unifi);

        Assert.Equal(new[] { "create AMP:7DTD TCP+UDP tcp_udp 26900 wan" }, unifi.Log);
    }

    [Fact]
    public async Task Equivalent_port_spec_in_unifi_is_not_rewritten()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 3));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Ark UDP", "udp", "7777,7778,7779");

        var plan = await Run(amp.Config(), unifi);

        Assert.Single(plan.Unchanged);
        Assert.Empty(unifi.Log);
    }

    [Fact]
    public async Task Duplicate_own_rules_are_cleaned_up()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Ark UDP", "udp", "7777");
        unifi.Add("AMP:Ark UDP", "udp", "7777");

        await Run(amp.Config(), unifi);

        Assert.Single(unifi.Rules);
    }

    [Fact]
    public async Task Invalid_target_ip_stops_the_sync()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 1));
        var unifi = new FakeUnifi();

        var plan = await Run(amp.Config(target: "192.168.1.500"), unifi);

        Assert.NotNull(plan.Error);
        Assert.Empty(unifi.Log);
    }

    [Fact]
    public async Task Removed_instance_rules_are_deleted()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Ark UDP", "udp", "7777");
        unifi.Add("AMP:Deleted UDP", "udp", "8888");
        unifi.Add("Manual thing", "tcp", "443");

        await Run(amp.Config(), unifi);

        Assert.Equal(new[] { "delete AMP:Deleted UDP" }, unifi.Log);
    }

    [Fact]
    public async Task Port_moving_to_a_new_instance_is_freed_before_it_is_claimed()
    {
        // Ark was deleted and Valheim now uses Ark's old port. "AMP:Ark" sorts before
        // "AMP:Valheim", but the create must still come after the delete.
        using var amp = new FakeAmp().Instance("Valheim", ("GamePort", 1, 7777, 1));
        var unifi = new FakeUnifi { EnforceOverlap = true };
        unifi.Add("AMP:Ark UDP", "udp", "7777");

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Equal(new[] { "delete AMP:Ark UDP", "create AMP:Valheim UDP udp 7777 wan" }, unifi.Log);
    }

    [Fact]
    public async Task Ports_swapped_between_instances_go_through()
    {
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 1, 2000, 1))
            .Instance("Beta", ("GamePort", 1, 1000, 1));
        var unifi = new FakeUnifi { EnforceOverlap = true };
        unifi.Add("AMP:Alpha UDP", "udp", "1000");
        unifi.Add("AMP:Beta UDP", "udp", "2000");

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Contains(unifi.Rules, r => r.Name == "AMP:Alpha UDP" && r.DstPort == "2000");
        Assert.Contains(unifi.Rules, r => r.Name == "AMP:Beta UDP" && r.DstPort == "1000");
    }

    [Fact]
    public async Task Partly_moved_ports_shrink_before_they_grow()
    {
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 1, 1001, 1), ("Peer", 1, 2000, 1))
            .Instance("Beta", ("GamePort", 1, 1000, 1));
        var unifi = new FakeUnifi { EnforceOverlap = true };
        unifi.Add("AMP:Alpha UDP", "udp", "1000-1001");
        unifi.Add("AMP:Beta UDP", "udp", "2000");

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Contains(unifi.Rules, r => r.Name == "AMP:Alpha UDP" && r.DstPort == "1001,2000");
        Assert.Contains(unifi.Rules, r => r.Name == "AMP:Beta UDP" && r.DstPort == "1000");
    }

    [Fact]
    public async Task Falls_back_to_a_plain_list_when_ranges_are_refused()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 3));
        var unifi = new FakeUnifi { RefuseRanges = true };

        var plan = await Run(amp.Config(), unifi);

        Assert.Empty(plan.Failed);
        Assert.Equal(new[] { "create AMP:Ark UDP udp 7777,7778,7779 wan" }, unifi.Log);
    }

    [Fact]
    public async Task Targeted_sync_that_frees_a_contested_port_asks_for_a_full_sweep()
    {
        // Alpha and Beta both wanted 27015 and Alpha won. Alpha moves to 27016.
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 1, 27016, 1))
            .Instance("Beta", ("GamePort", 1, 27015, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Alpha UDP", "udp", "27015");

        var plan = await Run(amp.Config(), unifi, only: "Alpha");

        Assert.True(plan.NeedsFullSweep);
        Assert.DoesNotContain(unifi.Log, l => l.Contains("Beta")); // not touched by the targeted sync
    }

    [Fact]
    public async Task Ordinary_targeted_sync_does_not_ask_for_a_full_sweep()
    {
        using var amp = new FakeAmp()
            .Instance("Alpha", ("GamePort", 1, 27016, 1))
            .Instance("Beta", ("GamePort", 1, 27015, 1));
        var unifi = new FakeUnifi();
        unifi.Add("AMP:Alpha UDP", "udp", "27017");

        var plan = await Run(amp.Config(), unifi, only: "Alpha");

        Assert.False(plan.NeedsFullSweep);
    }

    [Fact]
    public async Task Malformed_fingerprint_stops_the_sync()
    {
        using var amp = new FakeAmp().Instance("Ark", ("GamePort", 1, 7777, 1));
        var cfg = amp.Config();
        cfg.CertFingerprint = "SHA1: AB:CD:EF";
        var plan = await new UnifiReconciler(cfg, () => new FakeUnifi()).ReconcileAsync(true);
        Assert.Contains("SHA-256", plan.Error);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("SHA256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01", "INVALID")] // SHA-1
    [InlineData("not a fingerprint", "INVALID")]
    public void Normalizes_fingerprints(string input, string expected)
        => Assert.Equal(expected, UnifiClient.NormalizeFingerprint(input));

    [Theory]
    [InlineData(new[] { 7777, 7778, 7779, 27015 }, "7777-7779,27015")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 3, 1, 2, 9, 10 }, "1-3,9-10")]
    public void Compacts_port_lists(int[] ports, string expected)
        => Assert.Equal(expected, UnifiReconciler.CompactPorts(ports));
}
