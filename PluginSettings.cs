using System.ComponentModel;
using ModuleShared;

namespace UnifiPortSync
{
    // Persisted plugin configuration. [Description(...)] is the Configuration menu item;
    // the WebSetting subcategory (below) is the tab shown within it.
    // Which WAN the forwards should listen on. Values map to UniFi's pfwd_interface.
    public enum WanInterface
    {
        [EnumDisplayName("WAN 1", "", "")]
        Wan1 = 0,
        [EnumDisplayName("WAN 2", "", "")]
        Wan2 = 10,
        [EnumDisplayName("All WANs", "", "")]
        All = 20
    }

    internal class PluginSettings : SettingStore
    {
        [Description("Instance Deployment")]
        public class UnifiSettings : SettingSectionStore
        {
            // Master on/off. When false no syncs run and nothing is changed on the gateway.
            [WebSetting("Enable UniFi Port Sync",
                "Master switch. When enabled, AMP keeps UniFi port-forwards in sync with instance ports (create/update/remove). When disabled no syncs run and no changes are made.",
                false, "", "", "unifi,ports,sync", false, "", "", "", "Unifi Network Automation",10)]
            [InlineAction("UnifiPortSync", "RunSync", "Sync Now", "", false)]
            public bool Enabled = false;

            [WebSetting("UniFi Controller Host / IP",
                "Hostname or IP of the UniFi OS console (e.g. 192.168.1.1 or ui.example.com).",
                false, "", "", "unifi,host", false, "192.168.1.1", "", "", "Unifi Network Automation",20)]
            public string Host = "";

            [WebSetting("UniFi Controller Port",
                "HTTPS port of the UniFi OS console (usually 443, or a custom port).",
                false, "", "", "unifi,port", false, "443", "", "", "Unifi Network Automation",30)]
            public int Port = 443;

            [WebSetting("UniFi API Key",
                "API key created under UniFi OS > Settings > Admins & Users > (your admin) > Control Plane API Key.",
                false, "", "Password", "unifi,apikey", false, "", "", "", "Unifi Network Automation",40)]
            [StoreEncrypted(true)]
            public string ApiKey = "";

            [WebSetting("UniFi Site",
                "Site identifier (internalReference), e.g. 'default'. Use the Test Connection button to list your sites.",
                false, "", "", "unifi,site", false, "default", "", "", "Unifi Network Automation",50)]
            [InlineAction("UnifiPortSync", "TestConnection", "Test Connection", "", false)]
            public string SiteName = "default";

            [WebSetting("Forward Target LAN IP",
                "The LAN IP of this machine that game ports should be forwarded to (where AMP instances listen).",
                false, "", "", "unifi,target,ip", false, "192.168.1.50", "", "", "Unifi Network Automation",60)]
            public string TargetLanIp = "";

            [WebSetting("WAN Interface",
                "Which WAN the forwards listen on. Only matters with a multi-WAN gateway; single WAN setups keep WAN 1. Changing this updates the existing rules on the next sync.",
                false, "", "", "unifi,wan,interface", false, "", "", "", "Unifi Network Automation",65)]
            public WanInterface Interface = WanInterface.Wan1;

            [WebSetting("Sync Interval (minutes)",
                "How often the fallback timer does a full sweep. Changes to instances are already picked up live, so this only catches drift. Set to 0 to turn the timer off. A new interval takes effect after the instance restarts.",
                false, "", "", "unifi,interval", false, "15", "min", "", "Unifi Network Automation",70)]
            public int SyncIntervalMinutes = 15;

            [WebSetting("Rule Name Prefix",
                "Prefix for port-forward rules this plugin creates, so it only manages its own rules. Must not be empty.",
                false, "", "", "unifi,prefix", false, "AMP:", "", "", "Unifi Network Automation",80)]
            public string RuleNamePrefix = "AMP:";

            [WebSetting("AMP Data Path (optional)",
                "Path to the AMP data folder that contains instances.json. Leave blank to auto-detect.",
                false, "", "", "unifi,path", false, "", "", "", "Unifi Network Automation",90)]
            public string AmpDataPath = "";
        }

        public UnifiSettings Unifi = new UnifiSettings();
    }
}
