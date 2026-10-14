using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using ModuleShared;

namespace UnifiPortSync
{
    // Permission nodes for this plugin's endpoints, assigned under Role Management. Without
    // them any panel user who can reach the controller could change the gateway's port forwards
    // or read the site's forwarding setup.
    public enum UnifiPortSyncPermissions
    {
        [Description("View UniFi port sync status and test the connection")]
        ViewStatus,
        [Description("Run UniFi port syncs that change the gateway")]
        ApplyChanges
    }

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

        // Shown when an AMP update outdated this build; the settings cannot load then, so
        // every other guard would misreport the problem as missing configuration.
        private const string MismatchMessage = "This plugin build does not match the installed AMP version. Update the plugin from https://github.com/Mecistios/AMP-Unifi/releases";

        private static Dictionary<string, object> Fail(string message)
            => new Dictionary<string, object> { ["success"] = false, ["message"] = message };

        private static ActionResult FailResult(string message)
            => ActionResult.FailureReason((FormattableString)$"{message}", "", (FormattableString)null);

        // Shared pre-checks. Returns a message when the sync can't run.
        private string SyncPrecheck(bool apply)
        {
            if (_plugin.BuildMismatch) return MismatchMessage;
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey) || string.IsNullOrWhiteSpace(u.TargetLanIp))
                return "Set Host, API key and Target LAN IP first.";
            // A dry-run is harmless, but applying changes while the sync is switched off would
            // contradict the master switch.
            if (apply && !u.Enabled)
                return "UniFi Port Sync is disabled. Enable it before applying changes (a preview works without it).";
            return null;
        }

        [JSONMethod("Test the connection to the UniFi controller and list available sites.",
            "Object with success flag, message, the discovered sites and the certificate fingerprint.")]
        [RequiresPermissions(UnifiPortSyncPermissions.ViewStatus)]
        public async Task<object> TestUnifiConnection()
        {
            if (_plugin.BuildMismatch) return Fail(MismatchMessage);
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey))
                return Fail("Set Host and API key first.");
            UnifiClient c = null;
            try
            {
                c = _plugin.CreateClient();
                var sites = await c.ListSitesAsync();
                _log.Info($"UniFi test OK: {sites.Count} site(s).");
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["message"] = $"Connected. {sites.Count} site(s) found.",
                    ["certificateFingerprint"] = UnifiClient.Pretty(c.LastSeenFingerprint),
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
                return Fail(ConnectionFailure(c, ex));
            }
            finally { c?.Dispose(); }
        }

        [JSONMethod("Reconcile AMP instance ports with UniFi port-forwards. Set apply=true to make changes; otherwise a dry-run plan is returned.",
            "Object with the sync plan (create/update/delete/conflicts/failed/skipped).")]
        [RequiresPermissions(UnifiPortSyncPermissions.ApplyChanges)]
        public async Task<object> SyncNow(bool apply = false)
        {
            var pre = SyncPrecheck(apply);
            if (pre != null) return Fail(pre);
            try
            {
                var plan = await _plugin.RunReconcileAsync(apply);
                if (plan.Error != null) return Fail(plan.Error);
                return new Dictionary<string, object>
                {
                    ["success"] = plan.Failed.Count == 0,
                    ["applied"] = apply,
                    ["create"] = plan.ToCreate,
                    ["update"] = plan.ToUpdate,
                    ["delete"] = plan.ToDelete,
                    ["conflicts"] = plan.Conflicts,
                    ["failed"] = plan.Failed,
                    ["skipped"] = plan.Skipped,
                    ["alreadyForwarded"] = plan.AlreadyForwarded.Count,
                    ["unchanged"] = plan.Unchanged.Count
                };
            }
            catch (Exception ex)
            {
                _log.Error($"UniFi sync failed: {ex.Message}");
                return Fail(ex.Message);
            }
        }

        // When a TLS failure happens with a pinned fingerprint, say so plainly.
        private string ConnectionFailure(UnifiClient c, Exception ex)
        {
            var pinned = UnifiClient.NormalizeFingerprint(_settings.Unifi.CertFingerprint);
            if (pinned == UnifiClient.Invalid)
                return "The certificate fingerprint setting isn't a SHA-256 fingerprint (64 hex characters)"
                    + (c?.LastSeenFingerprint != null ? $". The console presented {UnifiClient.Pretty(c.LastSeenFingerprint)}" : "")
                    + ". Paste that value, or clear the setting.";
            if (pinned != null && c?.LastSeenFingerprint != null && !string.Equals(pinned, c.LastSeenFingerprint, StringComparison.OrdinalIgnoreCase))
                return $"The console's certificate doesn't match the pinned fingerprint. It presented {UnifiClient.Pretty(c.LastSeenFingerprint)}. If the console's certificate was renewed on purpose, put this new value in the fingerprint setting.";
            return ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
        }

        // Backs the "Test Connection" button. Connects and lists the sites so you can pick one.
        // The outcome is pushed as a dialog too, since the panel tends to swallow the
        // ActionResult text and clicking a button that seems to do nothing is no fun.
        [JSONMethod("Test the UniFi connection and list the available sites.", "Success/failure with the site list.")]
        [RequiresPermissions(UnifiPortSyncPermissions.ViewStatus)]
        public async Task<ActionResult> TestConnection()
        {
            if (_plugin.BuildMismatch)
            {
                _plugin.PushPopup("testfail", MismatchMessage);
                return FailResult(MismatchMessage);
            }
            var u = _settings.Unifi;
            if (string.IsNullOrWhiteSpace(u.Host) || string.IsNullOrWhiteSpace(u.ApiKey))
            {
                _plugin.PushPopup("testfail", "Set Host and API key first.");
                return FailResult("Set Host and API key first.");
            }
            UnifiClient c = null;
            try
            {
                c = _plugin.CreateClient();
                var sites = await c.ListSitesAsync();
                var list = string.Join("\n", sites.Select(s => $"{s.InternalReference} ({s.Name})"));
                var pinned = UnifiClient.NormalizeFingerprint(u.CertFingerprint) != null;
                var cert = pinned
                    ? "Certificate matches the pinned fingerprint."
                    : $"Certificate fingerprint (paste into the fingerprint setting to pin it):\n{UnifiClient.Pretty(c.LastSeenFingerprint)}";
                _plugin.PushPopup("testok", $"Connected. {sites.Count} site(s) found:\n{list}\n\nUse the internalReference (first value) as the site setting.\n\n{cert}");
                return new ActionResult(true, (FormattableString)$"Connected. {sites.Count} site(s) found.", "", (FormattableString)null);
            }
            catch (Exception ex)
            {
                var msg = "Connection failed: " + ConnectionFailure(c, ex);
                _plugin.PushPopup("testfail", msg);
                return FailResult(msg);
            }
            finally { c?.Dispose(); }
        }

        // Backs the "Preview Changes" button: a dry run shown as a dialog, nothing is changed.
        [JSONMethod("Show what a sync would change without changing anything.", "Success/failure with the planned changes.")]
        [RequiresPermissions(UnifiPortSyncPermissions.ViewStatus)]
        public async Task<ActionResult> PreviewSync()
        {
            var pre = SyncPrecheck(apply: false);
            if (pre != null)
            {
                _plugin.PushPopup("syncfail", pre);
                return FailResult(pre);
            }
            var plan = await _plugin.RunReconcileAsync(apply: false);
            if (plan.Error != null)
            {
                _plugin.PushPopup("syncfail", $"Preview failed: {plan.Error}");
                return FailResult($"Preview failed: {plan.Error}");
            }

            var lines = new List<string>();
            void Section(string title, IEnumerable<string> items)
            {
                var l = items.ToList();
                if (l.Count == 0) return;
                lines.Add($"{title}:");
                lines.AddRange(l.Select(i => "  • " + i));
                lines.Add("");
            }
            Section("Would create", plan.ToCreate);
            Section("Would update", plan.ToUpdate);
            Section("Would remove", plan.ToDelete);
            Section("Conflicts (left alone)", plan.Conflicts);
            Section("Skipped instances", plan.Skipped.Select(kv => $"{kv.Key}: {kv.Value}"));
            var nothing = plan.ToCreate.Count + plan.ToUpdate.Count + plan.ToDelete.Count == 0;
            lines.Add(nothing
                ? $"No changes needed. {plan.Unchanged.Count} rule(s) up to date, {plan.AlreadyForwarded.Count} covered by existing rules."
                : $"{plan.Unchanged.Count} rule(s) already up to date. Press Sync Now to apply.");
            var text = string.Join("\n", lines).Trim();
            _plugin.PushPopup("syncpreview", text);
            return new ActionResult(true, (FormattableString)$"{text}", "", (FormattableString)null);
        }

        // Backs the "Sync Now" button in the settings tab. Applies and reports a summary,
        // both as the action result and as a dialog (same reason as TestConnection).
        [JSONMethod("Run a sync now and apply any changes to UniFi.", "Success/failure with a short summary.")]
        [RequiresPermissions(UnifiPortSyncPermissions.ApplyChanges)]
        public async Task<ActionResult> RunSync()
        {
            var pre = SyncPrecheck(apply: true);
            if (pre != null)
            {
                _plugin.PushPopup("syncfail", pre);
                return FailResult(pre);
            }

            var plan = await _plugin.RunReconcileAsync(apply: true);
            if (plan.Error != null)
            {
                _plugin.PushPopup("syncfail", $"Sync failed: {plan.Error}");
                return FailResult($"Sync failed: {plan.Error}");
            }

            var summary = $"Sync complete. Created {plan.ToCreate.Count}, updated {plan.ToUpdate.Count}, removed {plan.ToDelete.Count}, {plan.AlreadyForwarded.Count} already OK.";
            if (plan.Failed.Count > 0)
                summary += $"\n\n{plan.Failed.Count} change(s) were refused by UniFi:\n" + string.Join("\n", plan.Failed.Select(f => "  • " + f));
            if (plan.Conflicts.Count > 0)
                summary += $"\n\n{plan.Conflicts.Count} conflict(s) were left alone:\n" + string.Join("\n", plan.Conflicts.Select(c => "  • " + c));
            if (plan.Skipped.Count > 0)
                summary += $"\n\n{plan.Skipped.Count} instance(s) skipped because their port config couldn't be read:\n" + string.Join("\n", plan.Skipped.Select(kv => $"  • {kv.Key}: {kv.Value}"));
            var problems = plan.Failed.Count + plan.Conflicts.Count + plan.Skipped.Count > 0;
            _plugin.PushPopup(problems ? "syncfail" : "syncok", summary);
            return new ActionResult(plan.Failed.Count == 0, (FormattableString)$"{summary}", "", (FormattableString)null);
        }

        [JSONMethod("List the port-forward rules on the configured UniFi site.", "Object with the current forwards.")]
        [RequiresPermissions(UnifiPortSyncPermissions.ViewStatus)]
        public async Task<object> ListUnifiPortForwards()
        {
            if (_plugin.BuildMismatch) return Fail(MismatchMessage);
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
                        ["interface"] = f.Interface,
                        ["enabled"] = f.Enabled
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }
    }
}
