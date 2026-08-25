namespace WebOverlay
{
    /// <summary>
    /// The single place the published identity lives. The Forge requires the
    /// plugin name to read "Username-ModName" and the GUID "com.username.modname",
    /// so publishing under a different account only changes these constants.
    /// </summary>
    public static class Branding
    {
        public const string Account = "Anvil";
        public const string ModName = "WebOverlay";

        public const string PluginGuid = "com.anvil.weboverlay";
        public const string PluginName = Account + "-" + ModName;
        public const string PluginVersion = "1.8.9";
    }
}
