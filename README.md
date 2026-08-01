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

`WebOverlays.Create` returns `null` when overlays are known to be unavailable,
and otherwise a handle whose browser is still starting: creation is
asynchronous and never blocks Unity's thread. Failures that surface later -
no WebView2 runtime, a browser that will not start, a dead browser process -
raise the handle's **`Failed`** event; dispose the handle there and use your
fallback. `Ready` fires once the view is fully set up, and messages or
scripts sent before the page finished loading wait in a bounded outbox, so
posting right after `Create` is fine. Events arrive on the overlay thread -
queue them and touch game state from `Update()`.

When another mod ships alongside this library, reference it with
`<Private>false</Private>` and do **not** copy `Anvil-WebOverlay.dll` into your
own release zip - it is a shared dependency the user installs once.

Install the demo plugin to see a working panel: press **F10** in game, and
**F11** for the transparent HUD demo.

## Security defaults

The overlay is meant for pages the mod itself provides, and the defaults
enforce that:

- Navigation is allowed only to origins the mod itself asked for (each
  `Navigate` URL's origin, plus `OverlayOptions.AllowedOrigins`); redirects
  and followed links to anywhere else are cancelled.
- Messages are dropped unless they come from an allowed origin, so a foreign
  page never reaches the message bridge.
- Popups are suppressed, permission prompts (camera, location, ...) are
  denied, `alert()`-style script dialogs are off, and the browser's password
  saving and form autofill are disabled - the browser profile is shared by
  every mod using the library (one per Windows user under
  `%LOCALAPPDATA%\WebOverlay`), so nothing sensitive should be stored in it.
- Browser accelerator keys (print, find, refresh) are off unless the overlay
  was created with `DevTools = true`.

## Translucency and HUDs

Two options in `OverlayOptions` control how much of the game shows through:

- **`Opacity`** (0.15 to 1.0) fades the whole window - content included -
  evenly. The overlay stays a normal interactive window; this suits panels that
  should not completely cover the game.
- **`Transparent`** turns the overlay into a display-only HUD. Pixels the page
  leaves unpainted show the game; painted content floats over it. The window
  ignores the mouse and never takes focus, so the game stays fully playable.
  Unless a size is set the HUD covers the game's whole picture, and the page
  decides where on it something appears (a sized HUD sits at the picture's
  top-left corner - prefer the full-size default and place elements with CSS).
  Both can be combined for a faded HUD.

HUD rules that follow from how it works (chroma key, see below):

- **Interaction:** none - clicks go to the game everywhere, and `CloseKeys`
  cannot apply. Hide the HUD from the mod's own hotkey (`Hide`/`Toggle`).
- **Per-pixel transparency is binary.** A pixel either shows the game or shows
  page content. Semi-transparent page pixels blend towards near-black rather
  than the game, and antialiased edges pick up a hint of that - design HUD
  elements on solid dark panels (see the demo's HUD page), not as glass.
- `rgb(3,1,3)` is the reserved transparency key. A page pixel of exactly that
  color normally just shows as near-black (page pixels are not keyed on the
  GPU path), but avoid it anyway: under software rendering such pixels can
  land on the keyed surface and vanish.

## Requirements and limits

- Needs the Microsoft WebView2 runtime, which current Windows 10 and 11
  installations already include. Without it the failure surfaces
  asynchronously: the first `WebOverlays.Create` still returns a handle whose
  `Failed` event fires shortly after; later calls return null.
- Needs borderless windowed or windowed mode. In exclusive fullscreen a window
  over the game would minimise it, so check `WebOverlayPlugin.IsDisplayModeSupported`.
- While the overlay holds the keyboard the game does not see key presses, and
  the other way round. That is why the window has a title bar with a close
  button by default, and why `OverlayOptions.CloseKeys` exists. The title bar
  is recolored to a dark game-appropriate grey (Windows 11 exact, Windows 10
  dark mode, older keeps the stock look); `OverlayOptions.Frame = false`
  removes it entirely - then the close keys are the only way out, so make sure
  they are set.

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
