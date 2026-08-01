using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;
using WebOverlay;

namespace WebOverlay.Demo
{
    /// <summary>
    /// Shows what the library is for: a panel written in HTML, with no web
    /// server anywhere, that talks to the game in both directions.
    ///
    /// Press F10 to toggle it. The page pushes button presses to the game and
    /// the game pushes a live value back every second.
    /// </summary>
    [BepInPlugin("com.anvil.weboverlay.demo", "Anvil-WebOverlayDemo", "1.0.0")]
    [BepInDependency(Branding.PluginGuid)]
    public class DemoPlugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyboardShortcut> toggleKey;
        private ConfigEntry<KeyboardShortcut> hudKey;
        private IWebOverlay overlay;
        private IWebOverlay hud;
        private readonly Queue<string> fromPage = new Queue<string>();
        private float nextPush;

        private void Awake()
        {
            toggleKey = this.Config.Bind("Demo", "Toggle", new KeyboardShortcut(KeyCode.F10),
                "Shows the WebOverlay demo panel.");
            hudKey = this.Config.Bind("Demo", "Toggle HUD", new KeyboardShortcut(KeyCode.F11),
                "Shows the transparent HUD demo. Click-through: the game stays fully playable.");
        }

        private void Update()
        {
            if (toggleKey.Value.IsDown())
                toggle();
            if (hudKey.Value.IsDown())
                toggleHud();

            drainPageMessages();
            pushLiveValue();
        }

        private void toggleHud()
        {
            if (hud != null)
            {
                hud.Toggle();
                return;
            }

            if (!WebOverlayPlugin.IsDisplayModeSupported)
            {
                this.Logger.LogWarning("Exclusive fullscreen cannot show an overlay; use borderless windowed.");
                return;
            }

            hud = WebOverlays.Create("WebOverlay HUD demo", new OverlayOptions
            {
                Transparent = true
            });

            if (hud == null)
            {
                this.Logger.LogWarning("Overlays are unavailable - is the WebView2 runtime installed?");
                return;
            }

            var createdHud = hud;
            hud.Failed += () =>
            {
                this.Logger.LogWarning("The demo HUD failed; see the WebOverlay log lines above.");
                createdHud.Dispose();
                if (ReferenceEquals(hud, createdHud))
                    hud = null;
            };
            createdHud.LoadHtml(HudPage);
        }

        private void toggle()
        {
            if (overlay != null)
            {
                overlay.Toggle();
                return;
            }

            if (!WebOverlayPlugin.IsDisplayModeSupported)
            {
                this.Logger.LogWarning("Exclusive fullscreen cannot show an overlay; use borderless windowed.");
                return;
            }

            overlay = WebOverlays.Create("WebOverlay demo", new OverlayOptions
            {
                Width = 720,
                Height = 460,
                DevTools = true,
                CloseKeys = new[] { 0x1B, 0x79 } // Escape and F10
            });

            if (overlay == null)
            {
                this.Logger.LogWarning("Overlays are unavailable - is the WebView2 runtime installed?");
                return;
            }

            // Events arrive on the overlay thread, so only queue here and touch
            // game state from Update().
            overlay.MessageReceived += message =>
            {
                lock (fromPage)
                    fromPage.Enqueue(message);
            };
            // Creation is asynchronous; this is how a failure comes back. The
            // dead handle is dropped so the next F10 can try again.
            var created = overlay;
            overlay.Failed += () =>
            {
                this.Logger.LogWarning("The demo overlay failed; see the WebOverlay log lines above.");
                created.Dispose();
                if (ReferenceEquals(overlay, created))
                    overlay = null;
            };

            // Through the local: the latched Failed handler may already have
            // run during the subscription and nulled the field.
            created.LoadHtml(Page);
        }

        private void drainPageMessages()
        {
            while (true)
            {
                string message;
                lock (fromPage)
                {
                    if (fromPage.Count == 0)
                        return;
                    message = fromPage.Dequeue();
                }

                this.Logger.LogInfo("The page said: " + message);
            }
        }

        private void pushLiveValue()
        {
            if (Time.time < nextPush)
                return;
            nextPush = Time.time + 1f;
            string value = "fps:" + Mathf.RoundToInt(1f / Mathf.Max(0.0001f, Time.deltaTime));
            if (overlay != null && overlay.IsVisible)
                overlay.Post(value);
            if (hud != null && hud.IsVisible)
                hud.Post(value);
        }

        private void OnDestroy()
        {
            overlay?.Dispose();
            hud?.Dispose();
        }

        private const string Page = @"<!doctype html>
<meta charset='utf-8'>
<style>
  body { margin:0; padding:24px; background:#171816; color:#d0cdbd;
         font-family:'Segoe UI',system-ui,sans-serif; }
  h1 { color:#c2ad6d; font-size:1.3rem; margin:0 0 6px; }
  p { color:#918e7e; font-size:.85rem; margin:0 0 18px; }
  button { background:#c2ad6d; color:#191a17; border:0; border-radius:4px;
           padding:9px 16px; font-weight:650; cursor:pointer; margin-right:8px; }
  #live { margin-top:20px; font-size:2rem; color:#72ba80; }
</style>
<h1>Hello from HTML</h1>
<p>This panel is a string inside a mod - no web server involved.</p>
<button onclick=""say('button one')"">Send to game</button>
<button onclick=""say('button two')"">Send something else</button>
<div id='live'>waiting for the game...</div>
<script>
  function say(what) { window.chrome.webview.postMessage(what); }
  window.chrome.webview.addEventListener('message', e => {
    document.getElementById('live').textContent = e.data;
  });
</script>";

        // Everything the page does not paint shows the game. Elements sit on
        // solid dark panels: semi-transparent pixels would blend towards the
        // near-black transparency key rather than the game, so panels are the
        // look that works, glass is not.
        private const string HudPage = @"<!doctype html>
<meta charset='utf-8'>
<style>
  body { margin:0; font-family:'Segoe UI',system-ui,sans-serif; overflow:hidden; }
  .panel { position:absolute; background:#14150f; border:1px solid #3a3b30;
           border-radius:6px; color:#d0cdbd; padding:10px 16px; }
  #top { top:16px; left:50%; transform:translateX(-50%);
         font-weight:650; letter-spacing:.12em; color:#c2ad6d; }
  #corner { right:16px; bottom:16px; text-align:right; }
  #fps { font-size:1.6rem; color:#72ba80; }
  .label { font-size:.7rem; color:#918e7e; text-transform:uppercase; letter-spacing:.1em; }
</style>
<div class='panel' id='top'>WEBOVERLAY HUD - CLICK-THROUGH</div>
<div class='panel' id='corner'>
  <div class='label'>frames per second</div>
  <div id='fps'>-</div>
</div>
<script>
  window.chrome.webview.addEventListener('message', e => {
    const value = String(e.data);
    if (value.startsWith('fps:'))
      document.getElementById('fps').textContent = value.slice(4);
  });
</script>";
    }
}
