// A transparent, click-through HUD - the QuestMarkers shape. The page paints,
// the game stays fully playable underneath. Copy, rename, replace the HTML.
//
// This file compiles against the installed library on every release (the
// packaging check builds it), so if it is in your clipboard, it is current.

using BepInEx;
using UnityEngine;
using WebOverlay;

[BepInPlugin("com.you.yourhud", "You-YourHud", "1.0.0")]
[BepInDependency("com.anvil.weboverlay", "1.11.0")]
public class HudPlugin : BaseUnityPlugin
{
    private IWebOverlay hud;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F11))
            Toggle();

        // A HUD floats over EVERYTHING the game shows - the inventory, the
        // map, the death sequence, and the hideout, which looks exactly like
        // a raid to every obvious test (a GameWorld exists, the game says
        // Started, a player is IsYourPlayer). A HUD that should only exist in
        // combat has to decide that itself, roughly:
        //
        //   bool inBattle = EftScreenManager.Instance.CheckCurrentScreen(
        //           EEftScreenType.BattleUI)
        //       && !(gameWorld is HideoutGameWorld);
        //   if (!inBattle) hud.Hide();
        //
        // checked on a timer or a screen event - not every frame.
    }

    private void Toggle()
    {
        if (hud != null)
        {
            hud.Toggle();
            return;
        }

        hud = WebOverlays.Create("My HUD", new OverlayOptions
        {
            // Transparent: unpainted pixels show the game, the window never
            // takes focus or mouse, and it covers the whole game picture -
            // the PAGE decides where on it something appears. Without
            // Interactive it needs none of the cursor options a panel needs.
            Transparent = true,

            // Handlers may touch Unity objects - see PanelPlugin.cs.
            DispatchOnMainThread = true,
        });
        if (hud == null)
        {
            Logger.LogWarning("overlays are unavailable (is the WebView2 runtime installed?)");
            return;
        }

        var created = hud;
        created.Failed += () =>
        {
            // CompositionUnavailable here means this machine cannot do the
            // glass HUD; a solid panel would still work.
            Logger.LogWarning("HUD failed (" + created.Failure + "): " + created.FailureMessage);
            created.Dispose();
            if (ReferenceEquals(hud, created))
                hud = null;
        };

        created.LoadHtml("<!doctype html><html><body style='margin:0;background:transparent'>"
            + "<div style='position:absolute;top:24px;right:24px;padding:8px 14px;"
            + "background:rgba(16,17,13,.74);color:#d0cdbd;font:14px sans-serif;"
            + "border:1px solid rgba(194,173,109,.35);border-radius:8px'>my HUD</div>"
            + "</body></html>");
    }

    private void OnDestroy()
    {
        hud?.Dispose();
    }
}
