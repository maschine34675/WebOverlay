// A window the player opens on a hotkey - the CraftQueue shape. Copy this
// file, rename the GUID and the class, replace the HTML, and you have a
// working panel with raid-suitable options.
//
// This file compiles against the installed library on every release (the
// packaging check builds it), so if it is in your clipboard, it is current.

using BepInEx;
using UnityEngine;
using WebOverlay;

[BepInPlugin("com.you.yourpanel", "You-YourPanel", "1.0.0")]
// A hard minimum version, not a bare GUID: a member your code uses fails at
// JIT time long after BepInEx would have called a bare dependency satisfied.
[BepInDependency("com.anvil.weboverlay", "1.10.0")]
public class PanelPlugin : BaseUnityPlugin
{
    private IWebOverlay overlay;

    private void Update()
    {
        // Poll the key yourself. BepInEx's KeyboardShortcut.IsDown blocks
        // while ANY unrelated key is held - walking included - so a raid
        // hotkey built on it goes dead whenever the player moves.
        if (Input.GetKeyDown(KeyCode.F9))
            Toggle();
    }

    private void Toggle()
    {
        if (overlay != null)
        {
            overlay.Toggle();
            return;
        }

        overlay = WebOverlays.Create("My panel - Escape closes", new OverlayOptions
        {
            // A size of your own, always. The default is 80% of the picture,
            // centred - which sits exactly on the point the game reads mouse
            // movement from while the player turns.
            Width = 720,
            Height = 460,

            // A framed window takes the foreground while the game keeps the
            // mouse captured; without this the player cannot point at it.
            FreeCursorWhileShown = true,

            // And the mirror image: while the GAME is in front, the mouse
            // must reach it even where the panel overlaps. Only matters for a
            // window that can sit over the middle of the screen in a raid.
            ClickThroughWhenUnfocused = true,

            // Events arrive on the game's main thread, so handlers may touch
            // Unity objects directly. Without this they arrive on the
            // library's own thread, and touching Unity there is a crash you
            // built yourself.
            DispatchOnMainThread = true,
        });
        if (overlay == null)
        {
            Logger.LogWarning("overlays are unavailable (is the WebView2 runtime installed?)");
            return;
        }

        // Capture the handle: Failed can outlive a field reassignment.
        var created = overlay;
        created.Failed += () =>
        {
            Logger.LogWarning("overlay failed (" + created.Failure + "): " + created.FailureMessage);
            created.Dispose();
            if (ReferenceEquals(overlay, created))
                overlay = null;
        };
        created.MessageReceived += text => Logger.LogInfo("the page says: " + text);

        created.LoadHtml("<!doctype html><h1>Hello</h1>"
            + "<button onclick=\"window.chrome.webview.postMessage('clicked')\">Click me</button>");
    }

    private void OnDestroy()
    {
        overlay?.Dispose();
    }
}
