using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public string DstPort => Range > 1 ? $"{Port}-{Port + Range - 1}" : Port.ToString();
    }

    // A forward rule after grouping one instance's ports by protocol.
    internal class DesiredRule
    {
        public string Instance;
        public string Proto;      // tcp | udp | tcp_udp
        public string PortSpec;   // combined, e.g. "8110" or "9876,9877"
        public string RuleName;
    }

    internal class SyncPlan
    {
        public List<string> ToCreate = new List<string>();
        public List<string> ToUpdate = new List<string>();
        public List<string> ToDelete = new List<string>();
        public List<string> Conflicts = new List<string>();       // port used by a forward to a DIFFERENT host
        public List<string> AlreadyForwarded = new List<string>(); // already covered by an existing rule to our host
        public List<string> Unchanged = new List<string>();
        public string Error;
    }

    // Reads AMP instances + their ports from disk and reconciles UniFi port-forwards.
    internal class UnifiReconciler
    {
        private readonly UnifiConfig _cfg;

        public UnifiReconciler(UnifiConfig cfg) { _cfg = cfg; }

        // Locate the AMP data directory (contains instances.json) by walking up from known roots.
        private string FindAmpDataPath()
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

        // Every folder that holds instance subfolders: the default instances dir plus any
        // datastore locations named by the Path entries in instances.json. Instances can be
        // created on a separate datastore, so watching only the default dir would miss them.
        public List<string> ResolveInstanceDirs()
        {
            var dirs = new List<string>();
            var root = FindAmpDataPath();
            if (root == null) return dirs;

            void Add(string d)
            {
                if (d != null && Directory.Exists(d) && !dirs.Contains(d)) dirs.Add(d);
            }
            Add(Path.Combine(root, "instances"));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "instances.json")));
                foreach (var inst in CollectInstances(doc.RootElement))
                {
                    var dataDir = InstanceDataDir(inst);
                    if (dataDir != null) Add(Path.GetDirectoryName(dataDir.TrimEnd('/', '\\')));
                }
            }
            catch { /* unreadable json, stick with the default dir */ }
            return dirs;
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

        private static string ProtoName(int p) => p switch { 0 => "tcp", 1 => "udp", 2 => "tcp_udp", _ => "tcp_udp" };

        // Ports that should never be forwarded to the internet (admin/RCON/echo/log).
        private static bool IsAdminPort(string portRef)
        {
            if (string.IsNullOrEmpty(portRef)) return false;
            var r = portRef.ToLowerInvariant();
            return r.Contains("rcon") || r.Contains("admin") || r.Contains("echo");
        }

        // Parse the App.Ports=[...] JSON array from an instance's <Module>.kvp.
        // Returns null when the line is present but does not parse (e.g. we caught the file
        // mid-write), so the caller can leave that instance alone instead of treating it
        // as having no ports and dropping its rules.
        private List<(string Ref, int Proto, int Port, int Range)> ParsePorts(string kvpPath)
        {
            var result = new List<(string, int, int, int)>();
            if (!File.Exists(kvpPath)) return result;
            string line = File.ReadLines(kvpPath).FirstOrDefault(l => l.StartsWith("App.Ports="));
            if (line == null) return result;
            var json = line.Substring("App.Ports=".Length).Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    int proto = e.TryGetProperty("Protocol", out var pr) ? pr.GetInt32() : 2;
                    int port = e.TryGetProperty("Port", out var po) ? po.GetInt32() : 0;
                    int range = e.TryGetProperty("Range", out var ra) ? ra.GetInt32() : 1;
                    string rf = e.TryGetProperty("Ref", out var re) ? (re.GetString() ?? "Port") : "Port";
                    if (port > 0) result.Add((rf, proto, port, range < 1 ? 1 : range));
                }
            }
            catch { return null; }
            return result;
        }

        // Build the set of ports every (non-ADS, non-suspended) instance wants exposed.
        // When onlyInstance is set, restrict to that one instance (used for the live per-instance syncs).
        // Instances whose port config could not be read end up in skipped; their rules must
        // not be treated as orphans.
        public List<DesiredPort> GetDesiredPorts(out string error, out List<string> skipped, string onlyInstance = null)
        {
            error = null;
            skipped = new List<string>();
            var list = new List<DesiredPort>();
            var root = FindAmpDataPath();
            if (root == null) { error = "Could not locate AMP data path (instances.json). Set 'AMP data path' in settings."; return list; }

            var instancesJson = Path.Combine(root, "instances.json");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(instancesJson)); }
            catch (Exception ex) { error = "Failed to read instances.json: " + ex.Message; return list; }

            var instances = CollectInstances(doc.RootElement);

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
                string name = inst.GetProperty("InstanceName").GetString();
                string module = inst.GetProperty("Module").GetString();
                if (onlyInstance != null && !string.Equals(name, onlyInstance, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(module, "ADS", StringComparison.OrdinalIgnoreCase)) continue; // skip controller
                if (inst.TryGetProperty("Suspended", out var s) && s.ValueKind == JsonValueKind.True) continue;

                // Prefer the instance's own Path from instances.json; it differs from the
                // default layout when the instance sits on a datastore.
                var instDir = InstanceDataDir(inst) ?? Path.Combine(root, "instances", name);
                var kvp = Path.Combine(instDir, module + ".kvp");
                var ports = ParsePorts(kvp);
                if (ports == null) { skipped.Add(name); continue; }
                foreach (var (rf, proto, port, range) in ports)
                {
                    if (IsAdminPort(rf)) continue; // never expose RCON/admin ports
                    list.Add(new DesiredPort
                    {
                        Instance = name, Proto = ProtoName(proto), Port = port, Range = range
                    });
                }
            }
            return list;
        }

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

            var desired = GetDesiredPorts(out var err, out var skipped, onlyInstance);
            if (err != null) { plan.Error = err; return plan; }
            if (onlyInstance != null && skipped.Contains(onlyInstance, StringComparer.OrdinalIgnoreCase))
            {
                plan.Error = $"Port config for '{onlyInstance}' could not be read; leaving its rules alone until the next sync.";
                return plan;
            }

            List<PortForward> existing;
            using var client = new UnifiClient(_cfg.Host, _cfg.Port, _cfg.ApiKey, _cfg.SiteName);
            try { existing = await client.GetPortForwardsAsync(); }
            catch (Exception ex) { plan.Error = "UniFi query failed: " + ex.Message; return plan; }

            var ours = existing.Where(f => (f.Name ?? "").StartsWith(prefix)).ToList();
            var foreign = existing.Where(f => !(f.Name ?? "").StartsWith(prefix)).ToList();

            // For a targeted sync, only our rules belonging to that instance are in scope for
            // create/update/orphan-removal. The rule name always starts with "<prefix><instance> ".
            var instScope = onlyInstance == null ? null : $"{prefix}{onlyInstance} ";

            // Group each instance's ports by protocol into a single combined forward rule
            // (UDP with UDP, TCP with TCP, tcp_udp separately), matching how forwards are kept by hand.
            var rules = desired
                .GroupBy(p => (p.Instance, p.Proto))
                .Select(g => new DesiredRule
                {
                    Instance = g.Key.Instance,
                    Proto = g.Key.Proto,
                    PortSpec = string.Join(",", g.Select(p => p.DstPort).Distinct()),
                    RuleName = $"{_cfg.RuleNamePrefix}{g.Key.Instance} {ProtoLabel(g.Key.Proto)}"
                })
                .ToList();

            foreach (var d in rules)
            {
                var wanted = ExpandPorts(d.PortSpec);
                var coveredSameHost = new HashSet<int>();
                var conflictPorts = new HashSet<int>();

                // Walk every rule we don't manage and see which of our wanted ports it touches.
                foreach (var f in foreign.Where(f => ProtoOverlap(f.Proto, d.Proto)))
                {
                    var fp = ExpandPorts(f.DstPort);
                    // A rule only counts as covering us if it forwards every protocol we need.
                    // A tcp-only rule does not cover a tcp_udp port; treat that as a conflict
                    // so the gap gets reported instead of silently leaving udp closed.
                    bool sameHost = string.Equals(f.Fwd, _cfg.TargetLanIp, StringComparison.OrdinalIgnoreCase);
                    bool covers = f.Proto == d.Proto || f.Proto == "tcp_udp";
                    foreach (var p in wanted)
                    {
                        if (!fp.Contains(p)) continue;
                        if (sameHost && covers)
                            coveredSameHost.Add(p);
                        else if (sameHost)
                        {
                            if (conflictPorts.Add(p))
                                plan.Conflicts.Add($"Port {p} for instance '{d.Instance}' needs {d.Proto} but rule '{f.Name}' only forwards {f.Proto}. Extend that rule to {ProtoLabel(d.Proto)} or remove it so the plugin can manage the port.");
                        }
                        else if (conflictPorts.Add(p))
                            plan.Conflicts.Add($"Port {p}/{d.Proto} for instance '{d.Instance}' is already forwarded to another host ({f.Fwd}) by rule '{f.Name}', so it was skipped to avoid overwriting it.");
                    }
                }

                var uncovered = wanted.Where(p => !coveredSameHost.Contains(p) && !conflictPorts.Contains(p))
                                      .OrderBy(p => p).ToList();
                var mine = ours.FirstOrDefault(f => f.Name == d.RuleName);

                if (uncovered.Count == 0)
                {
                    if (coveredSameHost.Count > 0)
                        plan.AlreadyForwarded.Add($"{d.RuleName} ({d.Proto} {d.PortSpec}), covered by existing rule(s)");
                    // Our own rule is redundant now (e.g. a manual rule took over) -> drop it.
                    if (mine != null)
                    {
                        plan.ToDelete.Add(mine.Name);
                        if (apply && !string.IsNullOrEmpty(mine.Id)) await client.DeletePortForwardAsync(mine.Id);
                    }
                    continue;
                }

                var spec = string.Join(",", uncovered);
                if (mine == null)
                {
                    plan.ToCreate.Add($"{d.RuleName} -> {_cfg.TargetLanIp}:{spec}/{d.Proto}");
                    if (apply) await client.CreatePortForwardAsync(ToRule(d, spec));
                }
                else if (mine.Proto != d.Proto || mine.DstPort != spec || mine.FwdPort != spec || mine.Fwd != _cfg.TargetLanIp || !mine.Enabled)
                {
                    plan.ToUpdate.Add($"{d.RuleName} -> {_cfg.TargetLanIp}:{spec}/{d.Proto}");
                    if (apply) { var r = ToRule(d, spec); r.Id = mine.Id; await client.UpdatePortForwardAsync(r); }
                }
                else plan.Unchanged.Add(d.RuleName);
            }

            // Orphans: our forwards with no matching desired rule -> remove. On a targeted
            // sync, only consider this instance's rules so other instances aren't affected.
            // Rules of instances whose config we could not read are off limits too.
            var wantedNames = rules.Select(d => d.RuleName).ToHashSet();
            var skipPrefixes = skipped.Select(n => $"{prefix}{n} ").ToList();
            var orphanScope = instScope == null
                ? ours
                : ours.Where(f => (f.Name ?? "").StartsWith(instScope));
            foreach (var f in orphanScope.Where(f => !wantedNames.Contains(f.Name)))
            {
                if (skipPrefixes.Any(sp => (f.Name ?? "").StartsWith(sp))) continue;
                plan.ToDelete.Add(f.Name);
                if (apply && !string.IsNullOrEmpty(f.Id)) await client.DeletePortForwardAsync(f.Id);
            }
            return plan;
        }

        private static string ProtoLabel(string p) => p switch { "udp" => "UDP", "tcp" => "TCP", "tcp_udp" => "TCP+UDP", _ => p.ToUpperInvariant() };

        private PortForward ToRule(DesiredRule d, string portSpec) => new PortForward
        {
            Name = d.RuleName, Enabled = true, Interface = "wan", Src = "any",
            Proto = d.Proto, DstPort = portSpec, Fwd = _cfg.TargetLanIp, FwdPort = portSpec, DestinationIp = "any"
        };

        private static bool ProtoOverlap(string a, string b)
        {
            if (a == b) return true;
            if (a == "tcp_udp" || b == "tcp_udp") return true;
            return false;
        }

        // Expand a UniFi port spec ("8110", "9876,9877", "7777-7810") into a set of ints.
        private static HashSet<int> ExpandPorts(string spec)
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
    }
}
