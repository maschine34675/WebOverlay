using System.Reflection;
using System.Runtime.InteropServices;

// A classic project does not turn csproj version properties into assembly
// attributes, so without this file the DLL would carry 0.0.0.0. Branding is
// the single source; nothing here repeats a literal.
[assembly: AssemblyTitle(WebOverlay.Branding.PluginName)]
[assembly: AssemblyProduct(WebOverlay.Branding.PluginName)]
[assembly: AssemblyDescription("HTML overlays over Escape From Tarkov for BepInEx mods")]
[assembly: AssemblyCompany("https://github.com/maschine34675/WebOverlay")]
[assembly: AssemblyVersion(WebOverlay.Branding.PluginVersion)]
[assembly: AssemblyFileVersion(WebOverlay.Branding.PluginVersion)]
[assembly: ComVisible(false)]
