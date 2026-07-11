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

        // Backs the "Sync Now" button in the settings tab — applies and reports a summary.
        [JSONMethod("Run a sync now and apply any changes to UniFi.", "Success/failure with a short summary.")]
        public async Task<ActionResult> RunSync()
        {
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey) || string.IsNullOrWhiteSpace(u.TargetLanIp))
                return ActionResult.FailureReason((FormattableString)$"Set Host, API key and Target LAN IP first.", "", (FormattableString)null);

            var plan = await _plugin.RunReconcileAsync(apply: true);
            if (plan.Error != null)
                return ActionResult.FailureReason((FormattableString)$"Sync failed: {plan.Error}", "", (FormattableString)null);

            return new ActionResult(true,
                (FormattableString)$"Sync complete — created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count}, removed {plan.ToDelete.Count}, {plan.AlreadyForwarded.Count} already OK, {plan.Conflicts.Count} conflict(s).",
                "", (FormattableString)null);
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
