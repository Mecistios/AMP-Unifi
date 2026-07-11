using System.ComponentModel;
using ModuleShared;

namespace UnifiPortSync
{
    // Persisted plugin configuration. The [Description("Networking")] on the section
    // makes these settings appear under AMP's existing "Networking" tab.
    internal class PluginSettings : SettingStore
    {
        [Description("Networking")]
        public class UnifiSettings : SettingSectionStore
        {
            // Master on/off. When false the plugin does nothing (no calls to UniFi).
            [WebSetting("Enable UniFi Port Sync",
                "When enabled, AMP will keep UniFi port-forwards in sync with instance ports (create/update/remove).",
                false, "", "", "unifi,ports,sync", false, "", "", "", null, 10)]
            [InlineAction("UnifiPortSync", "RunSync", "Sync Now", "", false)]
            public bool Enabled = false;

            [WebSetting("UniFi Controller Host / IP",
                "Hostname or IP of the UniFi OS console (e.g. 192.168.1.1 or ui.example.com).",
                false, "", "", "unifi,host", false, "192.168.1.1", "", "", null, 20)]
            public string Host = "";

            [WebSetting("UniFi Controller Port",
                "HTTPS port of the UniFi OS console (usually 443, or a custom port).",
                false, "", "", "unifi,port", false, "443", "", "", null, 30)]
            public int Port = 443;

            [WebSetting("UniFi API Key",
                "API key created under UniFi OS > Settings > Admins & Users > (your admin) > Control Plane API Key.",
                false, "", "Password", "unifi,apikey", false, "", "", "", null, 40)]
            [StoreEncrypted(true)]
            public string ApiKey = "";

            [WebSetting("UniFi Site",
                "Site identifier (internalReference), e.g. 'default'. Use the Test button to list your sites.",
                false, "", "", "unifi,site", false, "default", "", "", null, 50)]
            public string SiteName = "default";

            [WebSetting("Forward Target LAN IP",
                "The LAN IP of this machine that game ports should be forwarded to (where AMP instances listen).",
                false, "", "", "unifi,target,ip", false, "192.168.1.50", "", "", null, 60)]
            public string TargetLanIp = "";

            [WebSetting("Sync Interval (minutes)",
                "How often to automatically reconcile ports. Set to 0 to only sync manually.",
                false, "", "", "unifi,interval", false, "5", "min", "", null, 70)]
            public int SyncIntervalMinutes = 5;

            [WebSetting("Rule Name Prefix",
                "Prefix for port-forward rules this plugin creates, so it only manages its own rules.",
                false, "", "", "unifi,prefix", false, "AMP:", "", "", null, 80)]
            public string RuleNamePrefix = "AMP:";

            [WebSetting("AMP Data Path (optional)",
                "Path to the AMP data folder that contains instances.json. Leave blank to auto-detect.",
                false, "", "", "unifi,path", false, "", "", "", null, 90)]
            public string AmpDataPath = "";
        }

        public UnifiSettings Unifi = new UnifiSettings();
    }
}
