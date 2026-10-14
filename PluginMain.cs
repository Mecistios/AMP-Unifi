using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModuleShared;

namespace UnifiPortSync
{
    // AMP looks for a PluginMain class in each plugin assembly. The ctor args get injected.
    public class PluginMain : AMPPlugin
    {
        private readonly PluginSettings _settings;
        private readonly ILogger _log;
        private readonly IPluginMessagePusher _messages;
        private readonly IRunningTasksManager _tasks;

        private Timer _timer;
        private int _timerMinutes = -1;
        private bool _wasEnabled;

        // One watcher per instance folder (non-recursive: the .kvp sits at the top of the
        // instance folder) plus one on the AMP data folder for instances.json. Watching the
        // instance trees recursively would also watch every world/mod/log folder of every game.
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _dirToInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        private FileSystemWatcher _rootWatcher;
        private int _loggedWatchCount = -1;
        private readonly object _watchLock = new object();

        private Timer _debounce;
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _pendingFull;
        private readonly object _pendingLock = new object();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _buildMismatch;

        // What we last told the user about, so the same conflict, skip or failure isn't
        // re-announced on every timer tick.
        private HashSet<string> _notifiedConflicts = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _notifiedSkips = new HashSet<string>(StringComparer.Ordinal);
        private HashSet<string> _notifiedFailures = new HashSet<string>(StringComparer.Ordinal);
        private bool _warnedUntrustedCert;

        public PluginMain(ILogger log, IConfigSerializer config, IAMPInstanceInfo info, IPluginMessagePusher messages, IRunningTasksManager tasks)
        {
            _log = log;
            _messages = messages;
            _tasks = tasks;
            _settings = config.Load<PluginSettings>("", true, null);
        }

        // The plugin ships a small WebRoot/Plugin.js to render result and conflict pop-ups.
        public override bool HasFrontendContent => true;

        public override IEnumerable<SettingStore> SettingStores => new List<SettingStore> { _settings };

        public override Task<WebMethodsBase> InitAsync()
            => Task.FromResult<WebMethodsBase>(new WebMethods(this, _settings, _log));

        // Kick off the periodic sync once everything is up, and start watching instance port
        // config so a create/port-change/remove reconciles right away instead of at the next tick.
        public override Task PostInitAsync()
        {
            if (!SettingsAttributesResolve())
            {
                _buildMismatch = true;
                _log.Error("UniFi Port Sync: this AMP update changed the plugin interfaces, so this build of the plugin no longer matches and its settings cannot load. Sync stays off. Download the latest release from https://github.com/Mecistios/AMP-Unifi/releases and replace the plugin DLL.");
                return Task.CompletedTask;
            }
            _wasEnabled = _settings.Unifi.Enabled;
            ApplyTimer(initialDelay: TimeSpan.FromSeconds(30));
            _debounce = new Timer(_ => { _ = FlushPending(); }, null, Timeout.Infinite, Timeout.Infinite);
            StartWatchers();
            // Settings changes (interval, master switch) take effect straight away.
            _settings.SettingModified += OnSettingModified;
            _settings.Unifi.SettingModified += OnSettingModified;
            return Task.CompletedTask;
        }

        // An AMP update can change the WebSettingAttribute ctor. Attribute ctor signatures are
        // baked into a compiled plugin, so after such an update every attribute lookup on our
        // settings throws MissingMethodException and the settings can neither load nor save
        // (AMP 2.8.0.4 did exactly this). Probe one of our own fields up front so we can log
        // plainly that a newer plugin build is needed instead of leaving cryptic errors.
        private static bool SettingsAttributesResolve()
        {
            try
            {
                var field = typeof(PluginSettings.UnifiSettings).GetField(nameof(PluginSettings.UnifiSettings.Enabled));
                Attribute.GetCustomAttribute(field, typeof(WebSettingAttribute));
                return true;
            }
            catch (MemberAccessException) { return false; }
            catch (TypeLoadException) { return false; }
        }

        private void OnSettingModified(object sender, SettingModifiedEventArgs e)
        {
            try
            {
                lock (_timerLock)
                {
                    if (_settings.Unifi.SyncIntervalMinutes != _timerMinutes)
                        ApplyTimer(initialDelay: TimeSpan.FromSeconds(5));
                    var enabled = _settings.Unifi.Enabled;
                    if (enabled && !_wasEnabled)
                    {
                        _log.Info("UniFi Port Sync enabled; running a full sync.");
                        QueueFullSweep();
                    }
                    _wasEnabled = enabled;
                }
            }
            catch (Exception ex) { _log.Warning($"UniFi sync: couldn't apply a settings change ({ex.Message})."); }
        }

        private readonly object _timerLock = new object();

        private void ApplyTimer(TimeSpan initialDelay)
        {
            lock (_timerLock)
            {
                var mins = _settings.Unifi.SyncIntervalMinutes;
                _timerMinutes = mins;
                _timer?.Dispose();
                _timer = null;
                if (mins > 0)
                    _timer = new Timer(_ => { _ = Tick(); }, null, initialDelay, TimeSpan.FromMinutes(mins));
            }
        }

        private static string NormDir(string d) => Path.GetFullPath(d).TrimEnd('/', '\\');

        private UnifiReconciler NewReconciler() => new UnifiReconciler(ToConfig(_settings.Unifi));

        // AMP has no plugin-facing event for a port/config change (only version upgrades raise
        // one), but every change rewrites the instance's <Module>.kvp on disk, and creating or
        // removing an instance rewrites instances.json.
        private void StartWatchers()
        {
            try
            {
                var reconciler = NewReconciler();
                var root = reconciler.FindAmpDataPath();
                if (root == null)
                {
                    _log.Info("UniFi sync: AMP data folder not found, relying on the timer.");
                    return;
                }
                _rootWatcher = new FileSystemWatcher(root, "instances.json")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                // The watchers are refreshed by the full sweep this queues, two seconds later, by
                // which time a new instance's folder exists and instances.json is fully written.
                FileSystemEventHandler onRoot = (_, __) => QueueFullSweep();
                _rootWatcher.Changed += onRoot;
                _rootWatcher.Created += onRoot;
                _rootWatcher.Renamed += (_, __) => QueueFullSweep();
                _rootWatcher.Error += OnWatcherError;
                _rootWatcher.EnableRaisingEvents = true;
                RefreshInstanceWatchers();
            }
            catch (Exception ex)
            {
                _log.Warning($"UniFi sync: couldn't watch instance config ({ex.Message}); relying on the timer.");
            }
        }

        // Bring the per-instance watchers in line with instances.json: add new instances
        // (including ones on a datastore added after start-up), drop removed ones.
        private void RefreshInstanceWatchers()
        {
            var instances = NewReconciler().ReadInstances(out var err);
            if (instances == null) { if (err != null) _log.Warning($"UniFi sync: {err}"); return; }
            lock (_watchLock)
            {
                var want = instances
                    .Where(i => !string.Equals(i.Module, "ADS", StringComparison.OrdinalIgnoreCase) && Directory.Exists(i.Dir))
                    .GroupBy(i => NormDir(i.Dir))
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

                foreach (var gone in _watchers.Keys.Where(d => !want.ContainsKey(d)).ToList())
                {
                    _watchers[gone].Dispose();
                    _watchers.Remove(gone);
                }
                _dirToInstance.Clear();
                foreach (var (dir, name) in want)
                {
                    _dirToInstance[dir] = name;
                    if (_watchers.ContainsKey(dir)) continue;
                    try
                    {
                        var w = new FileSystemWatcher(dir, "*.kvp")
                        {
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                        };
                        w.Changed += OnPortConfigChanged;
                        w.Created += OnPortConfigChanged;
                        w.Deleted += OnPortConfigChanged;
                        w.Renamed += OnPortConfigChanged;
                        w.Error += OnWatcherError;
                        w.EnableRaisingEvents = true;
                        _watchers[dir] = w;
                    }
                    catch (Exception ex) { _log.Warning($"UniFi sync: couldn't watch '{dir}' ({ex.Message}); the timer will cover it."); }
                }
                if (_watchers.Count != _loggedWatchCount)
                {
                    _loggedWatchCount = _watchers.Count;
                    _log.Info($"UniFi sync: watching {_watchers.Count} instance folder(s) for port changes.");
                }
            }
        }

        // A .kvp in an instance folder changed, so queue that instance for a targeted reconcile.
        private void OnPortConfigChanged(object sender, FileSystemEventArgs e)
        {
            if (!_settings.Unifi.Enabled) return;
            var folder = Path.GetDirectoryName(e.FullPath);
            if (folder == null) return;
            string name;
            lock (_watchLock)
                if (!_dirToInstance.TryGetValue(NormDir(folder), out name)) name = new DirectoryInfo(folder).Name;
            lock (_pendingLock) _pending.Add(name);
            _debounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        private void QueueFullSweep()
        {
            if (!_settings.Unifi.Enabled) return;
            lock (_pendingLock) _pendingFull = true;
            _debounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        // The watcher's event buffer can overflow when a lot changes at once. Events got
        // lost at that point, so catch up with a full sweep instead of waiting for the timer.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _log.Warning($"UniFi sync: file watcher error ({e.GetException()?.Message}); running a full sweep to catch up.");
            QueueFullSweep();
        }

        // Coalesce the burst of writes a single config save produces into one reconcile.
        private async Task FlushPending()
        {
            List<string> names;
            bool full;
            lock (_pendingLock) { names = _pending.ToList(); _pending.Clear(); full = _pendingFull; _pendingFull = false; }
            if (!_settings.Unifi.Enabled) return;
            if (full) { await Tick(); return; } // a full sweep covers the individual instances too
            var needFull = false;
            foreach (var name in names)
            {
                try { needFull |= (await RunReconcileAsync(apply: true, name)).NeedsFullSweep; }
                catch (Exception ex) { _log.Warning($"UniFi sync for '{name}' errored: {ex.Message}"); }
            }
            // The change freed or contested a port another instance uses; let that one catch up now.
            if (needFull) await Tick();
        }

        // Full sweep. Also brings the folder watchers up to date, so an instance whose folder
        // appeared late (or a missed instances.json change) is watched from now on.
        private async Task Tick()
        {
            if (!_settings.Unifi.Enabled) return;
            if (_rootWatcher != null)
            {
                try { RefreshInstanceWatchers(); }
                catch (Exception ex) { _log.Warning($"UniFi sync: couldn't refresh folder watchers ({ex.Message})."); }
            }
            try { await RunReconcileAsync(apply: true); }
            catch (Exception ex) { _log.Warning($"Scheduled UniFi sync errored: {ex.Message}"); }
        }

        // True when the startup probe found that this build no longer matches the running AMP.
        internal bool BuildMismatch => _buildMismatch;

        // Lets the web methods pop a result dialog in the browser. AMP does not reliably
        // show the text of a successful ActionResult, so the buttons push their outcome
        // through the frontend script instead. RequiredPermission must be null: the
        // websocket broadcast checks HasPermission on anything non-null, and an empty
        // string matches no permission node, which silently drops the message.
        internal void PushPopup(string type, string text) => _messages.Push(type, text, 20, null, "UnifiPortSync");

        internal UnifiClient CreateClient()
        {
            var u = _settings.Unifi;
            return new UnifiClient(u.Host, u.Port, u.ApiKey, u.SiteName, u.CertFingerprint);
        }

        internal static UnifiConfig ToConfig(PluginSettings.UnifiSettings u) => new UnifiConfig
        {
            Host = u.Host, Port = u.Port, ApiKey = u.ApiKey,
            SiteName = u.SiteName, TargetLanIp = u.TargetLanIp,
            Interface = InterfaceValue(u.Interface),
            RuleNamePrefix = u.RuleNamePrefix, AmpDataPath = u.AmpDataPath,
            ExcludedPortNames = u.ExcludedPortNames, CertFingerprint = u.CertFingerprint
        };

        private static string InterfaceValue(WanInterface i) => i switch
        {
            WanInterface.Wan2 => "wan2",
            WanInterface.All => "both",
            _ => "wan"
        };

        // Shared entry point used by the timer, the buttons and the file watcher. Only one
        // runs at a time. When instanceName is set, only that instance's rules are touched.
        internal async Task<SyncPlan> RunReconcileAsync(bool apply, string instanceName = null)
        {
            if (_buildMismatch)
                return new SyncPlan { Error = "This plugin build does not match the installed AMP version. Update the plugin from https://github.com/Mecistios/AMP-Unifi/releases" };
            await _gate.WaitAsync();
            try
            {
                UnifiClient lastClient = null;
                var cfg = ToConfig(_settings.Unifi);
                var reconciler = new UnifiReconciler(cfg, () =>
                    lastClient = new UnifiClient(cfg.Host, cfg.Port, cfg.ApiKey, cfg.SiteName, cfg.CertFingerprint));
                var plan = await reconciler.ReconcileAsync(apply, instanceName);

                if (lastClient != null && lastClient.AcceptedUntrustedCert && !_warnedUntrustedCert)
                {
                    _warnedUntrustedCert = true;
                    _log.Warning($"UniFi sync: the console's certificate isn't trusted and no fingerprint is pinned. To pin it, set 'UniFi Certificate Fingerprint' to {UnifiClient.Pretty(lastClient.LastSeenFingerprint)}");
                }

                if (plan.Error != null)
                {
                    _log.Warning($"UniFi sync: {plan.Error}");
                    return plan;
                }

                // Dry runs only report; announcing is for real syncs.
                if (apply) Announce(plan, instanceName == null);

                var scope = instanceName == null ? "all" : $"'{instanceName}'";
                _log.Info($"UniFi sync {scope} ({(apply ? "applied" : "dry-run")}): +{plan.ToCreate.Count} ~{plan.ToUpdate.Count} -{plan.ToDelete.Count}, conflicts {plan.Conflicts.Count}, failed {plan.Failed.Count}, skipped {plan.Skipped.Count}, ok {plan.AlreadyForwarded.Count}");
                return plan;
            }
            finally { _gate.Release(); }
        }

        // Log and pop up conflicts, failures and skipped instances, but only the ones not
        // already announced, so a standing conflict doesn't fire a dialog every 15 minutes.
        private void Announce(SyncPlan plan, bool fullSweep)
        {
            var newConflicts = plan.Conflicts.Where(c => !_notifiedConflicts.Contains(c)).ToList();
            foreach (var c in newConflicts) _log.Warning($"UniFi port conflict: {c}");
            // A full sweep knows the complete picture, so it replaces the list; a resolved
            // conflict that comes back later is then announced again.
            if (fullSweep) _notifiedConflicts = new HashSet<string>(plan.Conflicts, StringComparer.Ordinal);
            else _notifiedConflicts.UnionWith(plan.Conflicts);

            var skips = plan.Skipped.Select(kv => $"Instance '{kv.Key}' was skipped: {kv.Value}. Its rules were left as they are.").ToList();
            var newSkips = skips.Where(s => !_notifiedSkips.Contains(s)).ToList();
            foreach (var s in newSkips) _log.Warning($"UniFi sync: {s}");
            if (fullSweep) _notifiedSkips = new HashSet<string>(skips, StringComparer.Ordinal);
            else _notifiedSkips.UnionWith(skips);

            // A change UniFi keeps refusing would otherwise pop up on every sync. Each failure is
            // logged every time, but only shown as a dialog the first time.
            foreach (var f in plan.Failed) _log.Warning($"UniFi sync: change failed: {f}");
            var newFailures = plan.Failed.Where(f => !_notifiedFailures.Contains(f)).ToList();
            if (fullSweep) _notifiedFailures = new HashSet<string>(plan.Failed, StringComparer.Ordinal);
            else _notifiedFailures.UnionWith(plan.Failed);

            var problems = newConflicts.Concat(newFailures.Select(f => "Change failed: " + f)).ToList();
            if (problems.Count == 0) return;
            _messages.Push("portconflict", string.Join("\n\n", problems), 20, null, "UnifiPortSync");
            // Raise a failed task so it also shows in the activity bar while browsing the panel.
            var title = newFailures.Count > 0 ? "UniFi Port Sync: changes failed" : "UniFi Port Sync: port conflict";
            var task = _tasks.CreateTask(title, "", false, false, false, null, false, false);
            task.End(TaskState.Failed, string.Join(" | ", problems));
        }
    }
}
