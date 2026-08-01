# WebOverlay

Show web pages in windows over Escape From Tarkov, so a mod can build its user
interface in HTML instead of an immediate-mode toolkit.

A page can be a URL, or just a string of markup - **no web server needed** -
and it can talk to the game in both directions.

## For mod authors

Reference `Anvil-WebOverlay.dll` and declare the dependency:

```csharp
[BepInPlugin("com.you.yourmod", "You-YourMod", "1.0.0")]
[BepInDependency("com.anvil.weboverlay")]
public class YourPlugin : BaseUnityPlugin
{
    private IWebOverlay overlay;

    private void Open()
    {
        overlay = WebOverlays.Create("My panel", new OverlayOptions { DevTools = true });
        if (overlay == null)
            return;                       // no runtime: use your own fallback

        overlay.LoadHtml("<h1>Hello</h1>");
        overlay.MessageReceived += text => { /* the page called postMessage */ };
        overlay.Post("hello page");       // arrives as a message event
    }
}
```

From the page:

```js
window.chrome.webview.postMessage('button pressed');
window.chrome.webview.addEventListener('message', e => console.log(e.data));
```

`WebOverlays.Create` returns `null` rather than throwing whenever overlays are
unavailable, so a fallback is always a null check. Events arrive on the overlay
thread - queue them and touch game state from `Update()`.

Install the demo plugin to see a working panel: press **F10** in game, and
**F11** for the transparent HUD demo.

## Translucency and HUDs

Two options in `OverlayOptions` control how much of the game shows through:

- **`Opacity`** (0.15 to 1.0) fades the whole window - content included -
  evenly. The overlay stays a normal interactive window; this suits panels that
  should not completely cover the game.
- **`Transparent`** turns the overlay into a display-only HUD. Pixels the page
  leaves unpainted show the game; painted content floats over it. The window
  ignores the mouse and never takes focus, so the game stays fully playable.
  Unless a size is set the HUD covers the game's whole picture, and the page
  decides where on it something appears. Both can be combined for a faded HUD.

HUD rules that follow from how it works (chroma key, see below):

- **Interaction:** none - clicks go to the game everywhere, and `CloseKeys`
  cannot apply. Hide the HUD from the mod's own hotkey (`Hide`/`Toggle`).
- **Per-pixel transparency is binary.** A pixel either shows the game or shows
  page content. Semi-transparent page pixels blend towards near-black rather
  than the game, and antialiased edges pick up a hint of that - design HUD
  elements on solid dark panels (see the demo's HUD page), not as glass.
- The page must not paint exactly `rgb(3,1,3)`; that color is the reserved
  transparency key.

## Requirements and limits

- Needs the Microsoft WebView2 runtime, which current Windows 10 and 11
  installations already include. Without it `WebOverlays.Create` returns null.
- Needs borderless windowed or windowed mode. In exclusive fullscreen a window
  over the game would minimise it, so check `WebOverlayPlugin.IsDisplayModeSupported`.
- While the overlay holds the keyboard the game does not see key presses, and
  the other way round. That is why the window has a title bar with a close
  button by default, and why `OverlayOptions.CloseKeys` exists.

## How it works, and why it looks like this

- **One browser for the whole game.** Every WebView2 environment starts its own
  browser process tree and wants its user-data folder to itself, so the library
  keeps a single environment and gives out as many overlay windows as mods ask
  for.
- **Its own thread.** WebView2 is COM and needs a thread that is STA and pumps
  messages. The game's main thread is neither, so the library runs one.
- **Owned popup windows, not child windows.** Unity presents through a
  flip-model swapchain, which does not composite child windows.
- **Hand-built COM vtables instead of Microsoft's managed wrapper.** The wrapper
  cannot be used under Unity's Mono: the SDK marks inherited vtable slots with
  `_VtblGap`, Mono ignores those markers, and native calls then land on the
  wrong function - measured, it kills the process with no managed exception.
  Function pointers taken from delegates work reliably, so every interface used
  here is bound by explicit slot number, taken from the official `WebView2.h`.
  Members of versioned interfaces (`ICoreWebView2Controller2` and later) are
  reached only via an explicit `QueryInterface` plus an absolute slot counted
  through every inherited member - and each such slot must be proven by an
  observable effect before it is trusted; see `Interop/WebView2Api.cs`.
- **HUD transparency is a chroma key.** DWM applies `LWA_COLORKEY` to a window's
  classic redirection surface, which Chromium's GPU compositing bypasses - so
  keying the page's own pixels does not work (measured). What does work:
  `DefaultBackgroundColor` alpha 0 makes the browser render nothing where the
  page paints nothing, those pixels show the window's key-color background
  brush, and the chroma key replaces exactly them with the game. Hit-testing
  reads the same surface, which is why the whole HUD is click-through - and why
  that cannot be selective in this mode.

One caution: WebView2 transparency has regressed before in runtime updates
(opaque instead of transparent, runtime 145.x, fixed since). If a HUD suddenly
shows a dark background after a Windows update, suspect the runtime first.

## Roadmap

- **True per-pixel alpha with interaction** (glass panels, soft shadows over
  the game, clickable HUD elements) is possible but is a different hosting
  model: composition hosting via `ICoreWebView2Environment3` /
  `ICoreWebView2CompositionController`, a DirectComposition visual tree in a
  `WS_EX_NOREDIRECTIONBITMAP` window, and manual forwarding of all mouse input
  plus cursor handling. The interop fits this library's hand-built-vtable
  approach; the work is roughly a week for the core and is planned as a second
  overlay mode. Microsoft's `WebView2APISample` (ViewComponent.cpp) shows the
  canonical native implementation.

## Third-party components

`WebView2Loader.dll` belongs to the Microsoft WebView2 SDK and is redistributed
under BSD 3-Clause; see `WebView2-LICENSE.txt` and `WebView2-NOTICE.txt`. The
WebView2 runtime itself is not redistributed.

## License

MIT.
