# WebOverlay

Show web pages in windows over Escape From Tarkov, so a mod can build its user
interface in HTML instead of an immediate-mode toolkit.

A page can be a URL, or just a string of markup - **no web server needed** -
and it can talk to the game in both directions.

## Installation

Extract the release zip over the SPT folder; it places
`BepInEx/plugins/Anvil-WebOverlay/` with the library, `WebView2Loader.dll` and
the license texts. One installation serves every mod that uses the library.
Players only need it when a mod lists it as a dependency. The demo plugin is a
separate zip and purely optional.

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
except a latched `Ready`/`Failed` subscribed after the fact, which runs on the
subscribing thread. Treat handlers as "any thread": queue what they learn and
touch game state from `Update()`.

When another mod ships alongside this library, reference it with
`<Private>false</Private>` and do **not** copy `Anvil-WebOverlay.dll` into your
own release zip - it is a shared dependency the user installs once.

Install the demo plugin to see a working panel: press **F10** in game, and
**F11** for the transparent HUD demo.

## API reference

`WebOverlays` (static):

| Member | Meaning |
|---|---|
| `Create(title, options)` | New overlay handle, or null when overlays are already known to be unusable. Asynchronous - see above. |
| `IsAvailable` | Kicks off the browser start (side effect!) and reports whether overlays are still plausible. |
| `RuntimeVersion` | The installed WebView2 runtime version, once known. |

`OverlayOptions`:

| Option | Default | Meaning |
|---|---|---|
| `Width`, `Height` | 0 | Pixels; 0 means 80% / 85% of the game window (HUDs: the whole game picture). |
| `Frame` | true | Title bar with close button, recolored to a dark game tone. |
| `CloseKeys` | Escape | Virtual-key codes that hide the overlay while it has the keyboard. |
| `ContextMenu` | false | Allow the browser's right-click menu. |
| `DevTools` | false | Allow F12 developer tools and browser accelerator keys. |
| `Opacity` | 1.0 | Whole-window fade, 0.15-1.0. |
| `Transparent` | false | Display-only click-through HUD (see below). |
| `AllowedOrigins` | null | Extra origins allowed for navigation and messages. |
| `RememberBounds` | true | Reopen at the position and size the player left the window at, across sessions. |
| `PersistenceKey` | assembly/title | Storage key for the remembered bounds. |

`IWebOverlay`:

| Member | Meaning |
|---|---|
| `Show()`, `Hide()`, `Toggle()` | Visibility. `IsVisible` reads the current state. |
| `Navigate(url)`, `LoadHtml(html)` | Set the page; the URL's origin becomes trusted. |
| `Post(message)`, `ExecuteScript(script)` | Send to the page; buffered until it finished loading. |
| `OpenDevTools()` | Opens the browser developer tools (with `DevTools = true`). |
| `MessageReceived` | The page called `postMessage`. Overlay thread. |
| `KeyPressed` | A key pressed in the overlay that did not close it. Overlay thread. |
| `Closed` | Fires on every hide or close - not only on destruction. Overlay thread. |
| `Ready`, `Failed` | Latched creation outcome - see above for threading. |
| `Dispose()` | Destroys the overlay window. |

Windows keep the spot the player gave them: toggling does not recenter, and
the position and size survive restarts (`%LOCALAPPDATA%\WebOverlay\window-bounds.txt`).
A remembered spot that is no longer on any screen falls back to the centered
default, HUDs always follow the game window instead, and `RememberBounds =
false` restores the old center-on-every-show behaviour.

## Security defaults

The overlay is meant for pages the mod itself provides, and the defaults
enforce that:

- Navigation is allowed only to origins the mod itself asked for (each
  `Navigate` URL's origin, plus `OverlayOptions.AllowedOrigins`); redirects
  and followed links to anywhere else are cancelled.
- Messages are dropped unless they come from an allowed origin, so a foreign
  page never reaches the message bridge. Outgoing sends are bound to the
  mod's target at origin granularity: a redirect to a different path on the
  same origin still counts as the target, as in the classic origin model.
- Popups are suppressed, permission prompts (camera, location, ...) are
  denied, `alert()`-style script dialogs are off, and the browser's password
  saving and form autofill are disabled on runtimes that support those
  settings (2021 or newer; older ones keep their defaults) - the browser
  profile is shared by every mod using the library (one per Windows user
  under `%LOCALAPPDATA%\WebOverlay`), so nothing sensitive should be stored
  in it.
- Browser accelerator keys (print, find, refresh) are off unless the overlay
  was created with `DevTools = true` - same runtime caveat.

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
  approach and is planned as a second overlay mode. Microsoft's
  `WebView2APISample` (ViewComponent.cpp) shows the canonical native
  implementation.

## Building

The projects are classic net472 csproj files that reference BepInEx and Unity
assemblies from an SPT installation: build with
`dotnet build WebOverlay/WebOverlay.csproj -c Release -p:SptRoot=<your SPT folder>`
(default `C:\SPT`). `scripts/New-ReleasePackage.ps1` produces the release zips
including the license files, verified against a manifest allowlist.
`docs/FAULT-TESTS.md` records the fault-injection matrix run for a release.

## Third-party components

`WebView2Loader.dll` belongs to the Microsoft WebView2 SDK and is redistributed
under BSD 3-Clause; see `WebView2-LICENSE.txt` and `WebView2-NOTICE.txt`. The
WebView2 runtime itself is not redistributed.

## License

MIT.
