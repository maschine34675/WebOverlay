namespace WebOverlay
{
    /// <summary>
    /// Duck-typed attribute bag read by reflection from BepInEx's
    /// ConfigurationManager (the in-game F12 settings menu). No reference to
    /// that assembly is needed: ConfigurationManager looks for fields with
    /// these exact names on any object passed through the Tags of a
    /// <see cref="BepInEx.Configuration.ConfigDescription"/>, and quietly
    /// ignores the object when that menu is not installed.
    ///
    /// The names and shape match the copy consumers already carry, so a
    /// setting can be given a friendly label or hidden behind Advanced while
    /// the .cfg key and section stay as they are for existing config files.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public string DispName;
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public bool? HideDefaultButton;
        public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    }
}
