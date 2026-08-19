using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using WebOverlay;

namespace WebOverlay.Demo
{
    /// <summary>
    /// Shows what the library is for: a panel written in HTML, with no web
    /// server anywhere, that talks to the game in both directions.
    ///
    /// Press F10 to toggle it. The page pushes button presses to the game and
    /// the game pushes a live value back every second; the panel also shows
    /// how to read a value back out of the page. F11 toggles the
    /// click-through HUD, F8 the interactive glass panel, and F7 a Three.js
    /// WebGL cube that follows the player camera.
    /// </summary>
    [BepInPlugin("com.anvil.weboverlay.demo", "Anvil-WebOverlayDemo", Branding.PluginVersion)]
    [BepInDependency(Branding.PluginGuid)]
    public class DemoPlugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyboardShortcut> toggleKey;
        private ConfigEntry<KeyboardShortcut> hudKey;
        private ConfigEntry<KeyboardShortcut> glassKey;
        private ConfigEntry<KeyboardShortcut> cubeKey;
        private IWebOverlay overlay;
        private IWebOverlay hud;
        private IWebOverlay glass;
        private IWebOverlay cube;
        private readonly Queue<string> fromPage = new Queue<string>();
        private float nextPush;

        private void Awake()
        {
            toggleKey = this.Config.Bind("Demo", "Toggle", new KeyboardShortcut(KeyCode.F10),
                "Shows the WebOverlay demo panel.");
            hudKey = this.Config.Bind("Demo", "Toggle HUD", new KeyboardShortcut(KeyCode.F11),
                "Shows the transparent HUD demo. Click-through: the game stays fully playable.");
            glassKey = this.Config.Bind("Demo", "Toggle glass panel", new KeyboardShortcut(KeyCode.F8),
                "Shows the interactive glass panel demo: real transparency AND clickable HTML.");
            cubeKey = this.Config.Bind("Demo", "Toggle 3D cube", new KeyboardShortcut(KeyCode.F7),
                "Shows the Three.js demo: a WebGL compass cube following the player camera.");
        }

        private void Update()
        {
            if (isPressed(toggleKey.Value))
                toggle();
            if (isPressed(hudKey.Value))
                toggleHud();
            if (isPressed(glassKey.Value))
                toggleGlass();
            if (isPressed(cubeKey.Value))
                toggleCube();

            drainPageMessages();
            pushLiveValue();
            pushCameraView();
        }

        /// <summary>
        /// BepInEx's KeyboardShortcut.IsDown blocks while ANY unrelated key is
        /// held, so a toggle would be swallowed whenever the player is walking.
        /// Honor configured modifiers, ignore everything else.
        /// </summary>
        private static bool isPressed(KeyboardShortcut shortcut)
        {
            if (!Input.GetKeyDown(shortcut.MainKey))
                return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
                if (!Input.GetKey(modifier))
                    return false;
            return true;
        }

        private void toggleCube()
        {
            if (cube != null)
            {
                cube.Toggle();
                return;
            }

            if (!WebOverlayPlugin.IsDisplayModeSupported)
            {
                this.Logger.LogWarning("Exclusive fullscreen cannot show an overlay; use borderless windowed.");
                return;
            }

            string page = loadCubePage();
            if (page == null)
                return;

            // Click-through like the F11 HUD; the WebGL canvas is just another
            // thing the page paints.
            cube = WebOverlays.Create("WebOverlay cube demo", new OverlayOptions
            {
                Transparent = true
            });

            if (cube == null)
            {
                this.Logger.LogWarning("Overlays are unavailable - is the WebView2 runtime installed?");
                return;
            }

            var created = cube;
            cube.Failed += () =>
            {
                this.Logger.LogWarning("The cube demo failed (" + created.Failure + "): " + created.FailureMessage);
                created.Dispose();
                if (ReferenceEquals(cube, created))
                    cube = null;
            };
            created.LoadHtml(page);
        }

        /// <summary>
        /// The cube page ships as two embedded resources: the page itself and
        /// the bundled three.min.js, spliced into it here. LoadHtml wants one
        /// self-contained string, and at ~620 KB the result stays well under
        /// NavigateToString's 2 MB ceiling.
        /// </summary>
        private string loadCubePage()
        {
            if (cubePage != null)
                return cubePage;
            try
            {
                string html = readResource("WebOverlay.Demo.cube.html");
                string three = readResource("WebOverlay.Demo.three.min.js");
                cubePage = html.Replace("/*!THREE_MIN_JS!*/", three);
            }
            catch (Exception ex)
            {
                this.Logger.LogWarning("Could not assemble the cube page: " + ex.Message);
            }
            return cubePage;
        }

        private static string cubePage;

        private static string readResource(string name)
        {
            using (Stream stream = typeof(DemoPlugin).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new FileNotFoundException("embedded resource " + name);
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Feeds the cube page one view sample per rendered frame - also a
        /// live demonstration that per-frame Post traffic is cheap. Uses the
        /// main camera only, so the demo needs no game-specific assemblies.
        /// </summary>
        private void pushCameraView()
        {
            // Through a local: the Failed handler nulls the field from the
            // overlay thread, and Update must not race it.
            IWebOverlay target = cube;
            if (target == null || !target.IsVisible)
                return;
            Camera gameCamera = Camera.main;
            if (gameCamera == null)
                return;
            Transform view = gameCamera.transform;
            Vector3 angles = view.eulerAngles;
            Vector3 position = view.position;
            float pitch = angles.x > 180f ? angles.x - 360f : angles.x;
            target.Post(string.Format(CultureInfo.InvariantCulture,
                "view:{0:F2};{1:F2};{2:F2};{3:F2};{4:F2};{5:F3}",
                angles.y, pitch, position.x, position.y, position.z, Time.time));
        }

        private void toggleGlass()
        {
            if (glass != null)
            {
                glass.Toggle();
                return;
            }

            if (!WebOverlayPlugin.IsDisplayModeSupported)
            {
                this.Logger.LogWarning("Exclusive fullscreen cannot show an overlay; use borderless windowed.");
                return;
            }

            // Interactive glass: sized to its content, because the window
            // swallows mouse input over its whole rectangle.
            glass = WebOverlays.Create("WebOverlay glass demo", new OverlayOptions
            {
                Transparent = true,
                Interactive = true,
                Width = 360,
                Height = 240,
                // The other threading style: with this the library raises
                // this overlay's events from the game's own Update, so the
                // handler below may touch game state directly - no queue, no
                // drain. Costs up to one frame.
                DispatchOnMainThread = true,
            });

            if (glass == null)
            {
                this.Logger.LogWarning("Overlays are unavailable - is the WebView2 runtime installed?");
                return;
            }

            var created = glass;
            glass.MessageReceived += message =>
                this.Logger.LogInfo("The glass panel said: " + message);
            glass.Failed += () =>
            {
                this.Logger.LogWarning("The glass demo failed (" + created.Failure + "): " + created.FailureMessage);
                created.Dispose();
                if (ReferenceEquals(glass, created))
                    glass = null;
            };
            created.LoadHtml(GlassPage);
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
                this.Logger.LogWarning("The demo HUD failed (" + createdHud.Failure + "): " + createdHud.FailureMessage);
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
                this.Logger.LogWarning("The demo overlay failed (" + created.Failure + "): " + created.FailureMessage);
                created.Dispose();
                if (ReferenceEquals(overlay, created))
                    overlay = null;
            };

            // State, not "something happened": this fires only when the panel
            // really appears or disappears, unlike Closed, which also fires
            // for the mod's own Hide.
            created.VisibilityChanged += visible =>
                this.Logger.LogDebug("The panel is now " + (visible ? "visible" : "hidden") + ".");

            // Reading state back out of the page: the result arrives as the
            // JSON the script evaluated to. Hooked to PageLoaded, because
            // that is when the page exists - the overlay becomes visible
            // before its first page has loaded.
            created.PageLoaded += () =>
                created.ExecuteScript("document.getElementById('live').textContent",
                    json => this.Logger.LogDebug("The panel's live line reads " + json + "."));

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
            glass?.Dispose();
            cube?.Dispose();
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

        // Everything the page does not paint shows the game. With composition
        // hosting this is true per-pixel alpha: rgba() glass and soft shadows
        // blend with the game. (On the chroma-key fallback - pre-Windows-8 or
        // ancient runtimes - semi-transparent pixels blend towards near-black
        // instead; solid panels would be the safe look there.)
        private const string HudPage = @"<!doctype html>
<meta charset='utf-8'>
<style>
  body { margin:0; font-family:'Segoe UI',system-ui,sans-serif; overflow:hidden; }
  .panel { position:absolute; background:rgba(16,17,13,0.72); border:1px solid rgba(194,173,109,0.35);
           border-radius:8px; color:#d0cdbd; padding:10px 16px;
           box-shadow:0 4px 18px rgba(0,0,0,0.45); }
  #top { top:16px; left:50%; transform:translateX(-50%);
         font-weight:650; letter-spacing:.12em; color:#c2ad6d; }
  #corner { right:16px; bottom:16px; text-align:right; }
  #fps { font-size:1.6rem; color:#72ba80; text-shadow:0 1px 6px rgba(0,0,0,0.6); }
  .label { font-size:.7rem; color:#918e7e; text-transform:uppercase; letter-spacing:.1em; }
</style>
<div class='panel' id='top'>WEBOVERLAY HUD - CLICK-THROUGH GLASS</div>
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

        // Interactive glass: real transparency AND working buttons. The
        // window swallows the mouse over its whole rectangle, so it is sized
        // to its content instead of covering the screen.
        private const string GlassPage = @"<!doctype html>
<meta charset='utf-8'>
<style>
  body { margin:0; font-family:'Segoe UI',system-ui,sans-serif; overflow:hidden; }
  .card { position:absolute; inset:12px; background:rgba(16,17,13,0.66);
          border:1px solid rgba(194,173,109,0.4); border-radius:10px;
          box-shadow:0 6px 24px rgba(0,0,0,0.5); color:#d0cdbd; padding:16px; }
  h1 { color:#c2ad6d; font-size:1rem; letter-spacing:.1em; margin:0 0 4px; }
  p { color:#918e7e; font-size:.78rem; margin:0 0 14px; }
  button { background:rgba(194,173,109,0.85); color:#191a17; border:0; border-radius:5px;
           padding:8px 14px; font-weight:650; cursor:pointer; margin-right:8px; }
  button:hover { background:#e0ca85; }
</style>
<div class='card'>
  <h1>INTERACTIVE GLASS</h1>
  <p>The game shows through this card - and the buttons still work.</p>
  <button onclick=""say('glass one')"">Send to game</button>
  <button onclick=""say('glass two')"">Send more</button>
</div>
<script>
  function say(what) { window.chrome.webview.postMessage(what); }
</script>";
    }
}
