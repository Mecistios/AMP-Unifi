using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ModuleShared;

namespace UnifiPortSync
{
    // Anything tagged [JSONMethod] here becomes an endpoint the panel can call.
    internal class WebMethods : WebMethodsBase
    {
        private readonly PluginMain _plugin;
        private readonly PluginSettings _settings;
        private readonly ILogger _log;

        public WebMethods(PluginMain plugin, PluginSettings settings, ILogger log)
        {
            _plugin = plugin;
            _settings = settings;
            _log = log;
        }

        [JSONMethod("Test the connection to the UniFi controller and list available sites.",
            "Object with success flag, message and the discovered sites.")]
        public async Task<object> TestUnifiConnection()
        {
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey))
                return new Dictionary<string, object> { ["success"] = false, ["message"] = "Set Host and API key first." };
            try
            {
                using var c = _plugin.CreateClient();
                var sites = await c.ListSitesAsync();
                _log.Info($"UniFi test OK: {sites.Count} site(s).");
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["message"] = $"Connected. {sites.Count} site(s) found.",
                    ["sites"] = sites.Select(s => new Dictionary<string, string>
                    {
                        ["internalReference"] = s.InternalReference,
                        ["name"] = s.Name
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _log.Error($"UniFi test connection failed: {ex.Message}");
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }

        [JSONMethod("Reconcile AMP instance ports with UniFi port-forwards. Set apply=true to make changes; otherwise a dry-run plan is returned.",
            "Object with the sync plan (create/update/delete/conflicts).")]
        public async Task<object> SyncNow(bool apply = false)
        {
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey) || string.IsNullOrWhiteSpace(u.TargetLanIp))
                return new Dictionary<string, object> { ["success"] = false, ["message"] = "Set Host, API key and Target LAN IP first." };
            // A dry-run is harmless, but applying changes while the sync is switched off would
            // contradict the master switch.
            if (apply && !u.Enabled)
                return new Dictionary<string, object> { ["success"] = false, ["message"] = "UniFi Port Sync is disabled. Enable it before applying changes (a dry-run works without it)." };
            try
            {
                var plan = await _plugin.RunReconcileAsync(apply);
                if (plan.Error != null)
                    return new Dictionary<string, object> { ["success"] = false, ["message"] = plan.Error };
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["applied"] = apply,
                    ["create"] = plan.ToCreate,
                    ["update"] = plan.ToUpdate,
                    ["delete"] = plan.ToDelete,
                    ["conflicts"] = plan.Conflicts,
                    ["alreadyForwarded"] = plan.AlreadyForwarded.Count,
                    ["unchanged"] = plan.Unchanged.Count
                };
            }
            catch (Exception ex)
            {
                _log.Error($"UniFi sync failed: {ex.Message}");
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }

        // Backs the "Test Connection" button. Connects and lists the sites so you can pick one.
        // The outcome is pushed as a dialog too, since the panel tends to swallow the
        // ActionResult text and clicking a button that seems to do nothing is no fun.
        [JSONMethod("Test the UniFi connection and list the available sites.", "Success/failure with the site list.")]
        public async Task<ActionResult> TestConnection()
        {
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey))
            {
                _plugin.PushPopup("testfail", "Set Host and API key first.");
                return ActionResult.FailureReason((FormattableString)$"Set Host and API key first.", "", (FormattableString)null);
            }
            try
            {
                using var c = _plugin.CreateClient();
                var sites = await c.ListSitesAsync();
                var list = string.Join("\n", sites.Select(s => $"{s.InternalReference} ({s.Name})"));
                _plugin.PushPopup("testok", $"Connected. {sites.Count} site(s) found:\n{list}\n\nUse the internalReference (first value) as the site setting.");
                return new ActionResult(true, (FormattableString)$"Connected. {sites.Count} site(s) found.", "", (FormattableString)null);
            }
            catch (Exception ex)
            {
                _plugin.PushPopup("testfail", $"Connection failed: {ex.Message}");
                return ActionResult.FailureReason((FormattableString)$"Connection failed: {ex.Message}", "", (FormattableString)null);
            }
        }

        // Backs the "Sync Now" button in the settings tab. Applies and reports a summary,
        // both as the action result and as a dialog (same reason as TestConnection).
        [JSONMethod("Run a sync now and apply any changes to UniFi.", "Success/failure with a short summary.")]
        public async Task<ActionResult> RunSync()
        {
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey) || string.IsNullOrWhiteSpace(u.TargetLanIp))
            {
                _plugin.PushPopup("syncfail", "Set Host, API key and Target LAN IP first.");
                return ActionResult.FailureReason((FormattableString)$"Set Host, API key and Target LAN IP first.", "", (FormattableString)null);
            }
            if (!u.Enabled)
            {
                _plugin.PushPopup("syncfail", "Enable UniFi Port Sync first, then run the sync.");
                return ActionResult.FailureReason((FormattableString)$"Enable UniFi Port Sync first, then run the sync.", "", (FormattableString)null);
            }

            var plan = await _plugin.RunReconcileAsync(apply: true);
            if (plan.Error != null)
            {
                _plugin.PushPopup("syncfail", $"Sync failed: {plan.Error}");
                return ActionResult.FailureReason((FormattableString)$"Sync failed: {plan.Error}", "", (FormattableString)null);
            }

            var summary = $"Sync complete. Created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count}, removed {plan.ToDelete.Count}, {plan.AlreadyForwarded.Count} already OK.";
            // Conflicts get their own dialog from the reconcile itself, so just count them here.
            if (plan.Conflicts.Count > 0)
                summary += $"\n\n{plan.Conflicts.Count} conflict(s) were skipped; a separate warning lists them.";
            _plugin.PushPopup(plan.Conflicts.Count > 0 ? "syncfail" : "syncok", summary);
            return new ActionResult(true, (FormattableString)$"{summary}", "", (FormattableString)null);
        }

        [JSONMethod("List the port-forward rules on the configured UniFi site.", "Object with the current forwards.")]
        public async Task<object> ListUnifiPortForwards()
        {
            try
            {
                using var c = _plugin.CreateClient();
                var fwds = await c.GetPortForwardsAsync();
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["count"] = fwds.Count,
                    ["forwards"] = fwds.Select(f => new Dictionary<string, object>
                    {
                        ["name"] = f.Name,
                        ["proto"] = f.Proto,
                        ["dstPort"] = f.DstPort,
                        ["fwd"] = f.Fwd,
                        ["enabled"] = f.Enabled
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["success"] = false, ["message"] = ex.Message };
            }
        }
    }
}
