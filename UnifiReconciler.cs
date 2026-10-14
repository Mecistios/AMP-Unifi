using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace UnifiPortSync
{
    // One port an instance needs open.
    internal class DesiredPort
    {
        public string Instance;
        public string Proto;    // tcp | udp | tcp_udp
        public int Port;
        public int Range = 1;
    }

    // A forward rule after grouping one instance's ports by protocol.
    internal class DesiredRule
    {
        public string Instance;
        public string Proto;              // tcp | udp | tcp_udp
        public SortedSet<int> Ports;      // every single port, ranges expanded
        public string RuleName;
    }

    // An AMP instance as recorded in instances.json.
    internal class AmpInstance
    {
        public string Name;
        public string Module;
        public string Dir;
        public bool Suspended;
    }

    internal class SyncPlan
    {
        public List<string> ToCreate = new List<string>();
        public List<string> ToUpdate = new List<string>();
        public List<string> ToDelete = new List<string>();
        public List<string> Conflicts = new List<string>();        // port used by a forward we must not touch, or by two instances
        public List<string> AlreadyForwarded = new List<string>(); // already covered by an existing rule to our host
        public List<string> Unchanged = new List<string>();
        public List<string> Failed = new List<string>();           // UniFi refused a change; the rest of the sync still ran
        public Dictionary<string, string> Skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // instance -> why its ports could not be read
        public string Error;
        // Set by a targeted sync when another instance's rules need a look as well.
        public bool NeedsFullSweep;
    }

    // Reads AMP instances + their ports from disk and reconciles UniFi port-forwards.
    internal class UnifiReconciler
    {
        private readonly UnifiConfig _cfg;
        private readonly Func<IUnifiClient> _clientFactory;

        // Port Refs containing any of these are never forwarded to the internet: remote
        // consoles and admin/control panels (RCON, 7 Days to Die telnet and web control
        // panel, web admin ports, ...).
        internal static readonly string[] DefaultExcludedPortNames = { "rcon", "admin", "echo", "telnet", "console", "control" };

        private static readonly string[] ProtoLabels = { "TCP", "UDP", "TCP+UDP" };

        public UnifiReconciler(UnifiConfig cfg, Func<IUnifiClient> clientFactory = null)
        {
            _cfg = cfg;
            _clientFactory = clientFactory ?? (() => new UnifiClient(cfg.Host, cfg.Port, cfg.ApiKey, cfg.SiteName, cfg.CertFingerprint));
        }

        // Locate the AMP data directory (contains instances.json) by walking up from known roots.
        public string FindAmpDataPath()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.AmpDataPath) && File.Exists(Path.Combine(_cfg.AmpDataPath, "instances.json")))
                return _cfg.AmpDataPath;
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "instances.json")))
                        return dir.FullName;
            }
            return null;
        }

        // Every instance listed in instances.json with the folder its config lives in. Instances
        // can sit on a separate datastore, so the folder comes from the instance's own Path.
        // Returns null when instances.json is missing or unreadable.
        public List<AmpInstance> ReadInstances(out string error)
        {
            error = null;
            var root = FindAmpDataPath();
            if (root == null) { error = "Could not locate AMP data path (instances.json). Set 'AMP data path' in settings."; return null; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances.json")));
                return CollectInstances(doc.RootElement).Select(inst =>
                {
                    var name = inst.GetProperty("InstanceName").GetString();
                    return new AmpInstance
                    {
                        Name = name,
                        Module = inst.GetProperty("Module").GetString(),
                        Dir = InstanceDataDir(inst) ?? Path.Combine(root, "instances", name),
                        Suspended = inst.TryGetProperty("Suspended", out var s) && s.ValueKind == JsonValueKind.True
                    };
                }).ToList();
            }
            catch (Exception ex) { error = "Failed to read instances.json: " + ex.Message; return null; }
        }

        // instances.json nests the instance objects under per-target entries; pull them all out.
        private static List<JsonElement> CollectInstances(JsonElement rootEl)
        {
            var found = new List<JsonElement>();
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("InstanceName", out _) && e.TryGetProperty("Module", out _)) found.Add(e);
                    foreach (var p in e.EnumerateObject()) Walk(p.Value);
                }
                else if (e.ValueKind == JsonValueKind.Array)
                    foreach (var i in e.EnumerateArray()) Walk(i);
            }
            Walk(rootEl);
            return found;
        }

        // The instance's own data directory as recorded in instances.json. Not always
        // <root>/instances/<name>: an instance made on a datastore lives wherever the
        // datastore points.
        private static string InstanceDataDir(JsonElement inst)
        {
            if (inst.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var val = p.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            return null;
        }

        private static string ProtoName(int p) => p switch { 0 => "tcp", 1 => "udp", _ => "tcp_udp" };

        // AMP writes Protocol as a number (0 TCP, 1 UDP, 2 Both); accept the names too in case
        // a version serializes the enum as text.
        private static int ParseProto(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)) return n;
            if (e.ValueKind == JsonValueKind.String)
            {
                var s = e.GetString()?.Trim().ToLowerInvariant();
                if (s == "tcp" || s == "0") return 0;
                if (s == "udp" || s == "1") return 1;
                return 2;
            }
            return 2;
        }

        private static int GetInt(JsonElement e, string prop, int fallback)
        {
            if (!e.TryGetProperty(prop, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return fallback;
        }

        private string[] ExcludedWords => DefaultExcludedPortNames
            .Concat((_cfg.ExcludedPortNames ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Distinct()
            .ToArray();

        // Ports that should never be forwarded to the internet (admin/RCON/console/...).
        internal bool IsExcludedPort(string portRef)
        {
            if (string.IsNullOrEmpty(portRef)) return false;
            var r = portRef.ToLowerInvariant();
            return ExcludedWords.Any(r.Contains);
        }

        // Module names and kvp filenames do not always match: module "Minecraft" writes
        // MinecraftModule.kvp and "ADS" writes ADSModule.kvp, while "GenericModule" matches
        // as-is. Try the plain spelling first, then with the Module suffix.
        internal static string ResolveKvpPath(string instDir, string module)
        {
            var kvp = Path.Combine(instDir, module + ".kvp");
            if (File.Exists(kvp)) return kvp;
            var alt = Path.Combine(instDir, module + "Module.kvp");
            return File.Exists(alt) ? alt : kvp;
        }

        // Read one line "key=value" from a kvp. Throws on IO trouble (e.g. a file locked mid-save);
        // the caller turns that into a skip.
        private static string ReadKvpValue(string kvpPath, string key)
        {
            var line = File.ReadLines(kvpPath).FirstOrDefault(l => l.StartsWith(key + "="));
            return line?.Substring(key.Length + 1).Trim();
        }

        // The dedicated Minecraft module has no App.Ports array; its game port is a single
        // Minecraft.PortNumber value. Forwarded as tcp+udp so an enabled query port is covered too.
        // Returns null (with a reason) when the port can't be read, so the instance is skipped.
        private static List<(string Ref, int Proto, int Port, int Range)> ParseMinecraftPorts(string kvpPath, out string why)
        {
            why = null;
            var val = ReadKvpValue(kvpPath, "Minecraft.PortNumber");
            if (val == null) { why = "no Minecraft.PortNumber in " + Path.GetFileName(kvpPath); return null; }
            if (!int.TryParse(val, out var port)) { why = "Minecraft.PortNumber is not a number"; return null; }
            var result = new List<(string, int, int, int)>();
            if (port > 0) result.Add(("GamePort", 2, port, 1));
            return result;
        }

        // Parse the App.Ports=[...] JSON array from an instance's <Module>.kvp.
        // Returns null (with a reason) when the line is missing or does not parse (e.g. we caught
        // the file mid-write), so the caller leaves that instance alone instead of treating it as
        // having no ports and dropping its rules.
        private static List<(string Ref, int Proto, int Port, int Range)> ParsePorts(string kvpPath, out string why)
        {
            why = null;
            var json = ReadKvpValue(kvpPath, "App.Ports");
            if (json == null) { why = "no App.Ports in " + Path.GetFileName(kvpPath); return null; }
            var result = new List<(string, int, int, int)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                    AddPort(e, 0, result);
            }
            catch (Exception ex) { why = "App.Ports did not parse (" + ex.Message + ")"; return null; }
            return result;
        }

        // Some templates nest ports under a parent, e.g. the ARK peer port (game port + 1).
        // AMP places a child at parent port + Offset, so do the same and walk them too.
        private static void AddPort(JsonElement e, int parentPort, List<(string, int, int, int)> result)
        {
            int proto = e.TryGetProperty("Protocol", out var pr) ? ParseProto(pr) : 2;
            int port = GetInt(e, "Port", 0);
            int range = GetInt(e, "Range", 1);
            int offset = GetInt(e, "Offset", 0);
            string rf = e.TryGetProperty("Ref", out var re) && re.ValueKind == JsonValueKind.String ? (re.GetString() ?? "Port") : "Port";
            if (parentPort > 0 && offset != 0) port = parentPort + offset;
            if (port > 0 && port <= 65535) result.Add((rf, proto, port, range < 1 ? 1 : range));

            if (e.TryGetProperty("ChildPorts", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var c in kids.EnumerateArray())
                    AddPort(c, port, result);
        }

        // Build the set of ports every (non-ADS, non-suspended) instance wants exposed.
        // Instances whose port config could not be read end up in skipped (with the reason);
        // their rules must not be treated as orphans.
        public List<DesiredPort> GetDesiredPorts(out string error, out Dictionary<string, string> skipped)
        {
            skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<DesiredPort>();
            var instances = ReadInstances(out error);
            if (instances == null) return list;

            // An empty result here almost certainly means the file format changed on us
            // (the ADS entry alone should always be present). Bail out rather than let the
            // orphan sweep conclude that every rule we manage should go.
            if (instances.Count == 0)
            {
                error = "No instances found in instances.json; skipping the sync as a precaution.";
                return list;
            }

            foreach (var inst in instances)
            {
                if (string.Equals(inst.Module, "ADS", StringComparison.OrdinalIgnoreCase)) continue; // skip controller
                if (inst.Suspended) continue;

                var kvp = ResolveKvpPath(inst.Dir, inst.Module);
                List<(string Ref, int Proto, int Port, int Range)> ports;
                string why;
                try
                {
                    if (!File.Exists(kvp)) { skipped[inst.Name] = "config file not found (" + kvp + ")"; continue; }
                    ports = string.Equals(inst.Module, "Minecraft", StringComparison.OrdinalIgnoreCase)
                        ? ParseMinecraftPorts(kvp, out why)
                        : ParsePorts(kvp, out why);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    skipped[inst.Name] = "config file could not be read (" + ex.Message + ")";
                    continue;
                }
                if (ports == null) { skipped[inst.Name] = why; continue; }

                foreach (var (rf, proto, port, range) in ports)
                {
                    if (IsExcludedPort(rf)) continue; // never expose RCON/admin ports
                    list.Add(new DesiredPort { Instance = inst.Name, Proto = ProtoName(proto), Port = port, Range = range });
                }
            }
            return list;
        }

        // The rule names this plugin may use for an instance. Matching on these exact names
        // (rather than a "<prefix><instance> " prefix) keeps "Valheim" from claiming the rules of
        // an instance called "Valheim 2".
        private string RuleName(string instance, string proto) => $"{_cfg.RuleNamePrefix}{instance} {ProtoLabel(proto)}";
        private bool IsRuleOf(string ruleName, string instance)
            => ProtoLabels.Any(l => string.Equals(ruleName, $"{_cfg.RuleNamePrefix}{instance} {l}", StringComparison.Ordinal));

        // Compute what would change in UniFi to match the desired ports. Optionally apply.
        // When onlyInstance is set, only that instance's rules are touched. Everything else
        // is left alone, so one instance change can't disturb the rest.
        public async Task<SyncPlan> ReconcileAsync(bool apply, string onlyInstance = null)
        {
            var plan = new SyncPlan();

            // Without a prefix we cannot tell our rules from anyone else's, and the orphan
            // sweep would be a hazard. Refuse to run rather than guess.
            string prefix = _cfg.RuleNamePrefix ?? "";
            if (string.IsNullOrWhiteSpace(prefix))
            {
                plan.Error = "Rule name prefix is empty; set one so the plugin can recognize its own rules.";
                return plan;
            }
            if (!IPAddress.TryParse((_cfg.TargetLanIp ?? "").Trim(), out var target) || target.AddressFamily != AddressFamily.InterNetwork)
            {
                plan.Error = $"Forward Target LAN IP '{_cfg.TargetLanIp}' is not a valid IPv4 address.";
                return plan;
            }
            var targetIp = target.ToString();
            if (UnifiClient.NormalizeFingerprint(_cfg.CertFingerprint) == UnifiClient.Invalid)
            {
                plan.Error = "UniFi Certificate Fingerprint isn't a SHA-256 fingerprint (64 hex characters). Use Test Connection to get the right value, or clear the setting.";
                return plan;
            }

            // Always read every instance, even for a targeted sync: whether a port is free
            // depends on what the other instances use.
            var desired = GetDesiredPorts(out var err, out var skipped);
            if (err != null) { plan.Error = err; return plan; }
            plan.Skipped = skipped;
            if (onlyInstance != null && skipped.TryGetValue(onlyInstance, out var why))
            {
                plan.Error = $"Port config for '{onlyInstance}' could not be read ({why}); leaving its rules alone until the next sync.";
                return plan;
            }

            using var client = _clientFactory();
            List<PortForward> existing;
            try { existing = await client.GetPortForwardsAsync(); }
            catch (Exception ex) { plan.Error = "UniFi query failed: " + ex.Message; return plan; }

            var ours = existing.Where(f => (f.Name ?? "").StartsWith(prefix, StringComparison.Ordinal)).ToList();
            // A disabled rule forwards nothing, so it neither covers a port nor blocks one.
            var foreign = existing.Where(f => !(f.Name ?? "").StartsWith(prefix, StringComparison.Ordinal) && f.Enabled).ToList();

            bool InScope(string instance) => onlyInstance == null || string.Equals(instance, onlyInstance, StringComparison.OrdinalIgnoreCase);

            // Group each instance's ports by protocol into a single combined forward rule
            // (UDP with UDP, TCP with TCP, tcp_udp separately), matching how forwards are kept by hand.
            var rules = desired
                .GroupBy(p => (p.Instance, p.Proto))
                .Select(g => new DesiredRule
                {
                    Instance = g.Key.Instance,
                    Proto = g.Key.Proto,
                    Ports = new SortedSet<int>(g.SelectMany(p => Enumerable.Range(p.Port, Math.Min(p.Range, 65536 - p.Port)))),
                    RuleName = RuleName(g.Key.Instance, g.Key.Proto)
                })
                .OrderBy(r => r.Instance, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Proto, StringComparer.Ordinal)
                .ToList();

            var clashOutsideScope = ResolveInstanceClashes(rules, ours, plan, InScope);

            // Work out every change first, then apply them: deletes, then updates that release
            // ports, then the other updates, then creates. That way a port freed by a removed or shrunk rule is gone from UniFi
            // before another rule claims it, instead of UniFi rejecting the new rule as an overlap.
            var deletes = new List<(string Desc, Func<Task> Op)>();
            var shrinks = new List<(string Desc, Func<Task> Op)>();   // updates that only release ports
            var updates = new List<(string Desc, Func<Task> Op)>();
            var creates = new List<(string Desc, Func<Task> Op)>();

            foreach (var d in rules.Where(r => InScope(r.Instance)))
            {
                var wanted = d.Ports;
                var coveredSameHost = new HashSet<int>();
                var conflictPorts = new HashSet<int>();

                // Walk every active rule we don't manage and see which of our wanted ports it touches.
                foreach (var f in foreign.Where(f => ProtoOverlap(f.Proto, d.Proto) && InterfaceOverlap(f.Interface, _cfg.Interface)))
                {
                    var fp = ExpandPorts(f.DstPort);
                    bool sameHost = string.Equals((f.Fwd ?? "").Trim(), targetIp, StringComparison.OrdinalIgnoreCase);
                    // A rule only counts as covering us if it forwards every protocol we need, on every
                    // WAN we listen on, from any source. Anything less leaves part of the port closed,
                    // so report it instead of silently relying on it.
                    bool protoCovers = f.Proto == d.Proto || f.Proto == "tcp_udp";
                    bool ifaceCovers = InterfaceCovers(f.Interface, _cfg.Interface);
                    bool srcAny = IsAny(f.Src);
                    foreach (var p in wanted)
                    {
                        if (!fp.Contains(p)) continue;
                        if (sameHost && protoCovers && ifaceCovers && srcAny) { coveredSameHost.Add(p); continue; }
                        if (!conflictPorts.Add(p)) continue;
                        if (!sameHost)
                            plan.Conflicts.Add($"Port {p}/{d.Proto} for instance '{d.Instance}' is already forwarded to another host ({f.Fwd}) by rule '{f.Name}', so it was skipped to avoid overwriting it.");
                        else if (!protoCovers)
                            plan.Conflicts.Add($"Port {p} for instance '{d.Instance}' needs {d.Proto} but rule '{f.Name}' only forwards {f.Proto}. Extend that rule to {ProtoLabel(d.Proto)} or remove it so the plugin can manage the port.");
                        else if (!ifaceCovers)
                            plan.Conflicts.Add($"Port {p} for instance '{d.Instance}' should listen on {IfaceLabel(_cfg.Interface)} but rule '{f.Name}' only listens on {IfaceLabel(f.Interface)}. Change that rule or remove it so the plugin can manage the port.");
                        else
                            plan.Conflicts.Add($"Port {p} for instance '{d.Instance}' is only forwarded from source '{f.Src}' by rule '{f.Name}'. Open that rule to any source or remove it so the plugin can manage the port.");
                    }
                }

                var uncovered = wanted.Where(p => !coveredSameHost.Contains(p) && !conflictPorts.Contains(p)).ToList();
                var mineAll = ours.Where(f => f.Name == d.RuleName).ToList();
                var mine = mineAll.FirstOrDefault();

                // Duplicates of our own rule (e.g. created twice after a crash) -> keep one.
                foreach (var dup in mineAll.Skip(1).Where(f => !string.IsNullOrEmpty(f.Id)))
                    deletes.Add(($"{dup.Name} (duplicate)", () => client.DeletePortForwardAsync(dup.Id)));

                if (uncovered.Count == 0)
                {
                    if (coveredSameHost.Count > 0)
                        plan.AlreadyForwarded.Add($"{d.RuleName} ({d.Proto} {CompactPorts(wanted)}), covered by existing rule(s)");
                    // Our own rule is redundant now (e.g. a manual rule took over) -> drop it.
                    if (mine != null && !string.IsNullOrEmpty(mine.Id))
                        deletes.Add((mine.Name, () => client.DeletePortForwardAsync(mine.Id)));
                    continue;
                }

                var desc = $"{d.RuleName} -> {targetIp}:{CompactPorts(uncovered)}/{d.Proto}";
                if (mine == null)
                {
                    creates.Add((desc, () => WithRangeFallback(uncovered, spec => client.CreatePortForwardAsync(ToRule(d, spec, targetIp)))));
                }
                else if (mine.Proto != d.Proto
                         || !ExpandPorts(mine.DstPort).SetEquals(uncovered)
                         || !ExpandPorts(mine.FwdPort).SetEquals(uncovered)
                         || !string.Equals((mine.Fwd ?? "").Trim(), targetIp, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(mine.Interface ?? "wan", _cfg.Interface, StringComparison.OrdinalIgnoreCase)
                         || !IsAny(mine.Src)
                         || !mine.Enabled)
                {
                    Task Update(List<int> ports) => WithRangeFallback(ports, spec =>
                    {
                        var r = ToRule(d, spec, targetIp);
                        r.Id = mine.Id;
                        return client.UpdatePortForwardAsync(r);
                    });
                    var held = ExpandPorts(mine.DstPort);
                    var keep = uncovered.Where(held.Contains).ToList();
                    if (held.Count > 0 && keep.Count == 0)
                    {
                        // Every port changes (e.g. two instances swapped ports): replace the rule,
                        // so its old ports are free before anything else claims them.
                        deletes.Add(($"{mine.Name} (replaced)", () => client.DeletePortForwardAsync(mine.Id)));
                        creates.Add((desc, () => WithRangeFallback(uncovered, spec => client.CreatePortForwardAsync(ToRule(d, spec, targetIp)))));
                    }
                    else if (held.Any(p => !uncovered.Contains(p)) && keep.Count < uncovered.Count)
                    {
                        // Gives up some ports and gains others: shrink first, grow later.
                        shrinks.Add((null, () => Update(keep)));
                        updates.Add((desc, () => Update(uncovered)));
                    }
                    else if (held.Any(p => !uncovered.Contains(p)))
                        shrinks.Add((desc, () => Update(uncovered)));   // only gives ports up
                    else
                        updates.Add((desc, () => Update(uncovered)));
                }
                else plan.Unchanged.Add(d.RuleName);
            }

            // Orphans: our forwards with no matching desired rule -> remove. On a targeted
            // sync, only consider this instance's rules so other instances aren't affected.
            // Rules of instances whose config we could not read are off limits too.
            var wantedNames = rules.Select(d => d.RuleName).ToHashSet(StringComparer.Ordinal);
            var orphanScope = onlyInstance == null ? ours : ours.Where(f => IsRuleOf(f.Name, onlyInstance));
            foreach (var f in orphanScope.Where(f => !wantedNames.Contains(f.Name)).ToList())
            {
                if (skipped.Keys.Any(n => IsRuleOf(f.Name, n))) continue;
                if (string.IsNullOrEmpty(f.Id)) continue;
                deletes.Add((f.Name, () => client.DeletePortForwardAsync(f.Id)));
            }

            // Run the changes. A refusal from UniFi is recorded and the sync carries on, so one
            // bad rule can't block every other instance (or the orphan clean-up).
            async Task Run(List<(string Desc, Func<Task> Op)> ops, List<string> bucket)
            {
                foreach (var (desc, op) in ops)
                {
                    // A null description is the first half of a shrink-then-grow update; the
                    // second half reports it.
                    if (!apply) { if (desc != null) bucket.Add(desc); continue; }
                    try { await op(); if (desc != null) bucket.Add(desc); }
                    catch (Exception ex) { if (desc != null) plan.Failed.Add($"{desc}: {ex.Message}"); }
                }
            }
            await Run(deletes, plan.ToDelete);
            await Run(shrinks, plan.ToUpdate);
            await Run(updates, plan.ToUpdate);
            await Run(creates, plan.ToCreate);

            // A targeted sync only touches one instance's rules. If that instance shared a port
            // with another one, or gave up ports another instance wants, the other instance's
            // rule needs a look too, so ask for a full sweep.
            if (onlyInstance != null)
            {
                var keeps = rules.Where(r => InScope(r.Instance)).SelectMany(r => r.Ports).ToHashSet();
                var released = ours.Where(f => IsRuleOf(f.Name, onlyInstance))
                                   .SelectMany(f => ExpandPorts(f.DstPort))
                                   .Where(p => !keeps.Contains(p))
                                   .ToHashSet();
                plan.NeedsFullSweep = clashOutsideScope
                    || rules.Any(r => !InScope(r.Instance) && r.Ports.Overlaps(released));
            }
            return plan;
        }

        // Newer UniFi Network versions take ranges inside a port list ("7777-7779,27015"). If the
        // controller refuses that form, try once more with every port listed separately, which
        // every version accepts.
        private static async Task WithRangeFallback(List<int> ports, Func<string, Task> send)
        {
            var compact = CompactPorts(ports);
            try { await send(compact); }
            catch (UnifiApiException ex) when (ex.StatusCode == 400 && compact.Contains('-'))
            {
                await send(string.Join(",", ports.OrderBy(p => p)));
            }
        }

        // Two instances asking for the same port can't both have it. The instance whose rule
        // already holds the port keeps it; otherwise the first by name wins. The loser's port is
        // dropped from its rule and reported, instead of UniFi rejecting the second rule.
        // Returns true when an in-scope instance clashed with one outside the scope.
        private bool ResolveInstanceClashes(List<DesiredRule> rules, List<PortForward> ours, SyncPlan plan, Func<string, bool> inScope)
        {
            bool outside = false;
            bool Holds(DesiredRule r, int port) => ours.Any(f => f.Enabled && f.Name == r.RuleName && ExpandPorts(f.DstPort).Contains(port));

            for (int i = 0; i < rules.Count; i++)
            for (int j = i + 1; j < rules.Count; j++)
            {
                var a = rules[i];
                var b = rules[j];
                if (string.Equals(a.Instance, b.Instance, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ProtoOverlap(a.Proto, b.Proto)) continue;
                foreach (var p in a.Ports.Intersect(b.Ports).ToList())
                {
                    // rules are ordered by instance name, so a wins ties
                    var (winner, loser) = Holds(b, p) && !Holds(a, p) ? (b, a) : (a, b);
                    loser.Ports.Remove(p);
                    if (inScope(winner.Instance) || inScope(loser.Instance))
                    {
                        plan.Conflicts.Add($"Port {p} is used by both '{winner.Instance}' and '{loser.Instance}'. It is forwarded for '{winner.Instance}' only; change the port on '{loser.Instance}'.");
                        if (!inScope(winner.Instance) || !inScope(loser.Instance)) outside = true;
                    }
                }
            }
            return outside;
        }

        private static string ProtoLabel(string p) => p switch { "udp" => "UDP", "tcp" => "TCP", "tcp_udp" => "TCP+UDP", _ => p.ToUpperInvariant() };
        private static string IfaceLabel(string i) => (i ?? "wan") switch { "wan" => "WAN 1", "wan2" => "WAN 2", "both" => "all WANs", var x => x };

        private PortForward ToRule(DesiredRule d, string portSpec, string targetIp) => new PortForward
        {
            Name = d.RuleName, Enabled = true, Interface = _cfg.Interface, Src = "any",
            Proto = d.Proto, DstPort = portSpec, Fwd = targetIp, FwdPort = portSpec, DestinationIp = "any"
        };

        private static bool IsAny(string src) => string.IsNullOrWhiteSpace(src) || string.Equals(src.Trim(), "any", StringComparison.OrdinalIgnoreCase);

        internal static bool ProtoOverlap(string a, string b)
        {
            if (a == b) return true;
            if (a == "tcp_udp" || b == "tcp_udp") return true;
            return false;
        }

        // Do two rules listen on at least one common WAN?
        internal static bool InterfaceOverlap(string a, string b)
        {
            a = string.IsNullOrEmpty(a) ? "wan" : a.ToLowerInvariant();
            b = string.IsNullOrEmpty(b) ? "wan" : b.ToLowerInvariant();
            return a == b || a == "both" || b == "both";
        }

        // Does a rule on interface `have` listen on every WAN that `need` asks for?
        internal static bool InterfaceCovers(string have, string need)
        {
            have = string.IsNullOrEmpty(have) ? "wan" : have.ToLowerInvariant();
            need = string.IsNullOrEmpty(need) ? "wan" : need.ToLowerInvariant();
            return have == need || have == "both";
        }

        // Expand a UniFi port spec ("8110", "9876,9877", "7777-7810") into a set of ints.
        internal static HashSet<int> ExpandPorts(string spec)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(spec)) return set;
            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    if (int.TryParse(p.Substring(0, dash), out var lo) && int.TryParse(p.Substring(dash + 1), out var hi))
                        for (int i = lo; i <= hi && (i - lo) < 65536; i++) set.Add(i);
                }
                else if (int.TryParse(p, out var v)) set.Add(v);
            }
            return set;
        }

        // The reverse: 7777,7778,7779,27015 -> "7777-7779,27015". Keeps rules short and readable.
        internal static string CompactPorts(IEnumerable<int> ports)
        {
            var sorted = ports.Distinct().OrderBy(p => p).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(j == i ? sorted[i].ToString() : $"{sorted[i]}-{sorted[j]}");
                i = j + 1;
            }
            return string.Join(",", parts);
        }
    }
}
