using System;
using System.Collections.Generic;
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

        // Kick off the periodic sync once everything is up.
        public override Task PostInitAsync()
        {
            var mins = _settings.Unifi.SyncIntervalMinutes;
            if (mins > 0)
            {
                var period = TimeSpan.FromMinutes(mins);
                // small initial delay so we don't fire mid-startup
                _timer = new Timer(_ => { _ = Tick(); }, null, TimeSpan.FromSeconds(30), period);
            }
            return Task.CompletedTask;
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
            Enabled = u.Enabled, Host = u.Host, Port = u.Port, ApiKey = u.ApiKey,
            SiteName = u.SiteName, TargetLanIp = u.TargetLanIp,
            SyncIntervalMinutes = u.SyncIntervalMinutes, RuleNamePrefix = u.RuleNamePrefix,
            AmpDataPath = u.AmpDataPath
        };

        // Shared entry point used by both the timer and the Sync button. Only one runs at a time.
        internal async Task<SyncPlan> RunReconcileAsync(bool apply)
        {
            await _gate.WaitAsync();
            try
            {
                var plan = await new UnifiReconciler(ToConfig(_settings.Unifi)).ReconcileAsync(apply);
                if (plan.Error != null)
                {
                    _log.Warning($"UniFi sync: {plan.Error}");
                    return plan;
                }
                // Surface every conflict as its own warning so it shows up in the console/log.
                foreach (var c in plan.Conflicts)
                    _log.Warning($"UniFi port conflict — {c}");
                // Push a short-lived pop-up to anyone on the ADS view.
                if (plan.Conflicts.Count > 0)
                {
                    _messages.Push("portconflict", string.Join("\n\n", plan.Conflicts), 20, "", "UnifiPortSync");
                    // Raise a failed task so it also shows in the activity bar while browsing the panel.
                    var task = _tasks.CreateTask("UniFi Port Sync — port conflict", "", false, false, false, null, false, false);
                    task.End(TaskState.Failed, string.Join(" | ", plan.Conflicts));
                }
                _log.Info($"UniFi sync ({(apply ? "applied" : "dry-run")}): +{plan.ToCreate.Count} ~{plan.ToUpdate.Count} -{plan.ToDelete.Count}, conflicts {plan.Conflicts.Count}, ok {plan.AlreadyForwarded.Count}");
                return plan;
            }
            finally { _gate.Release(); }
        }
    }
}
