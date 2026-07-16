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
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _pendingLock = new object();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public PluginMain(ILogger log, IConfigSerializer config, IAMPInstanceInfo info, IPluginMessagePusher messages, IRunningTasksManager tasks)
        {
            _log = log;
            _messages = messages;
            _tasks = tasks;
            _settings = config.Load<PluginSettings>("", true, null);
        }

        // The plugin ships a small WebRoot/Plugin.js to render conflict pop-ups.
        public override bool HasFrontendContent => true;

        public override IEnumerable<SettingStore> SettingStores => new List<SettingStore> { _settings };

        public override Task<WebMethodsBase> InitAsync()
            => Task.FromResult<WebMethodsBase>(new WebMethods(this, _settings, _log));

        // Kick off the periodic sync once everything is up, and start watching instance port
        // config so a create/port-change/remove reconciles right away instead of at the next tick.
        public override Task PostInitAsync()
        {
            var mins = _settings.Unifi.SyncIntervalMinutes;
            if (mins > 0)
            {
                var period = TimeSpan.FromMinutes(mins);
                // small initial delay so we don't fire mid-startup
                _timer = new Timer(_ => { _ = Tick(); }, null, TimeSpan.FromSeconds(30), period);
            }
            StartWatcher();
            return Task.CompletedTask;
        }

        // AMP has no plugin-facing event for a port/config change (only version upgrades raise
        // one), but every change rewrites the instance's <Module>.kvp on disk. Create and remove
        // add or drop those files too, so we watch the instances folder and reconcile on change.
        // The timer stays on as a full-sweep fallback in case the folder can't be located.
        private void StartWatcher()
        {
            try
            {
                var dir = new UnifiReconciler(ToConfig(_settings.Unifi)).ResolveInstancesDir();
                if (dir == null || !Directory.Exists(dir))
                {
                    _log.Info("UniFi sync: instances folder not found, relying on the timer.");
                    return;
                }
                // Coalesce the burst of writes a single config save produces into one reconcile.
                _debounce = new Timer(_ => { _ = FlushPending(); }, null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(dir, "*.kvp")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                _watcher.Changed += OnPortConfigChanged;
                _watcher.Created += OnPortConfigChanged;
                _watcher.Deleted += OnPortConfigChanged;
                _watcher.Renamed += OnPortConfigChanged;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
                _log.Info("UniFi sync: watching instance port config for changes.");
            }
            catch (Exception ex)
            {
                _log.Warning($"UniFi sync: couldn't watch instance config ({ex.Message}); relying on the timer.");
            }
        }

        // A .kvp under instances/<name>/ changed, so queue that instance for a targeted reconcile.
        private void OnPortConfigChanged(object sender, FileSystemEventArgs e)
        {
            if (!_settings.Unifi.Enabled) return;
            var name = InstanceFromKvpPath(e.FullPath);
            if (string.IsNullOrEmpty(name)) return;
            lock (_pendingLock) _pending.Add(name);
            _debounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        // instances/<name>/<Module>.kvp -> <name>
        private static string InstanceFromKvpPath(string fullPath)
        {
            var folder = Path.GetDirectoryName(fullPath);
            if (folder == null) return null;
            var name = new DirectoryInfo(folder).Name;
            return string.Equals(name, "instances", StringComparison.OrdinalIgnoreCase) ? null : name;
        }

        // The watcher's event buffer can overflow when a lot changes at once. Events got
        // lost at that point, so catch up with a full sweep instead of waiting for the timer.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _log.Warning($"UniFi sync: file watcher error ({e.GetException()?.Message}); running a full sweep to catch up.");
            _ = Tick();
        }

        private async Task FlushPending()
        {
            List<string> names;
            lock (_pendingLock) { names = _pending.ToList(); _pending.Clear(); }
            if (!_settings.Unifi.Enabled) return;
            foreach (var name in names)
            {
                try { await RunReconcileAsync(apply: true, name); }
                catch (Exception ex) { _log.Warning($"UniFi sync for '{name}' errored: {ex.Message}"); }
            }
        }

        private async Task Tick()
        {
            if (!_settings.Unifi.Enabled) return;
            try { await RunReconcileAsync(apply: true); }
            catch (Exception ex) { _log.Warning($"Scheduled UniFi sync errored: {ex.Message}"); }
        }

        internal UnifiClient CreateClient()
        {
            var u = _settings.Unifi;
            return new UnifiClient(u.Host, u.Port, u.ApiKey, u.SiteName);
        }

        private static UnifiConfig ToConfig(PluginSettings.UnifiSettings u) => new UnifiConfig
        {
            Host = u.Host, Port = u.Port, ApiKey = u.ApiKey,
            SiteName = u.SiteName, TargetLanIp = u.TargetLanIp,
            RuleNamePrefix = u.RuleNamePrefix, AmpDataPath = u.AmpDataPath
        };

        // Shared entry point used by the timer, the Sync button and the file watcher. Only one
        // runs at a time. When instanceName is set, only that instance's rules are touched.
        internal async Task<SyncPlan> RunReconcileAsync(bool apply, string instanceName = null)
        {
            await _gate.WaitAsync();
            try
            {
                var plan = await new UnifiReconciler(ToConfig(_settings.Unifi)).ReconcileAsync(apply, instanceName);
                if (plan.Error != null)
                {
                    _log.Warning($"UniFi sync: {plan.Error}");
                    return plan;
                }
                // Surface every conflict as its own warning so it shows up in the console/log.
                foreach (var c in plan.Conflicts)
                    _log.Warning($"UniFi port conflict: {c}");
                // Push a short-lived pop-up to anyone on the ADS view.
                if (plan.Conflicts.Count > 0)
                {
                    _messages.Push("portconflict", string.Join("\n\n", plan.Conflicts), 20, "", "UnifiPortSync");
                    // Raise a failed task so it also shows in the activity bar while browsing the panel.
                    var task = _tasks.CreateTask("UniFi Port Sync: port conflict", "", false, false, false, null, false, false);
                    task.End(TaskState.Failed, string.Join(" | ", plan.Conflicts));
                }
                var scope = instanceName == null ? "all" : $"'{instanceName}'";
                _log.Info($"UniFi sync {scope} ({(apply ? "applied" : "dry-run")}): +{plan.ToCreate.Count} ~{plan.ToUpdate.Count} -{plan.ToDelete.Count}, conflicts {plan.Conflicts.Count}, ok {plan.AlreadyForwarded.Count}");
                return plan;
            }
            finally { _gate.Release(); }
        }
    }
}
