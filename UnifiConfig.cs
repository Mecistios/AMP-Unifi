namespace UnifiPortSync
{
    // Kept separate from the AMP setting types so the reconciler and client stay
    // AMP-agnostic (and testable on their own).
    internal class UnifiConfig
    {
        public string Host = "";
        public int Port = 443;
        public string ApiKey = "";
        public string SiteName = "default";
        public string TargetLanIp = "";
        // UniFi pfwd_interface value: wan, wan2 or both.
        public string Interface = "wan";
        public string RuleNamePrefix = "AMP:";
        public string AmpDataPath = "";
    }
}
