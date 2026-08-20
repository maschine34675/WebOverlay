# WebOverlay

Show web pages in windows over Escape From Tushonka, so a mod can build its user
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
fallback. **`Failure`** says which of those it was, so you can tell the user
what to do about it, and `FailureMessage` carries the exact sentence:

```csharp
overlay.Failed += () =>
{
    switch (overlay.Failure)
    {
        case OverlayFailure.RuntimeMissing: /* "install the WebView2 runtime" */ break;
        case OverlayFailure.CompositionUnavailable: /* "no glass HUD on this system" */ break;
        case OverlayFailure.RendererCrashed: /* "the page died - reopen it" */ break;
        default: /* overlay.FailureMessage has the details */ break;
    }
};
```

`Ready` fires once the view is fully set up; **`PageLoaded`** (and
`IsPageLoaded`) fires once the page you targeted is live, on every navigation.
Messages or scripts sent before that wait in a bounded outbox, so posting right
after `Create` is fine - only a consumer that streams should hold off while
`IsPageLoaded` is false rather than fill the outbox.

Events arrive on the overlay thread - except a latched `Ready`/`Failed`
subscribed after the fact, which runs on the subscribing thread. Either queue
what a handler learns and touch game state from `Update()`, or set
**`DispatchOnMainThread`** and skip that boilerplate: the library then delivers
every event from its own `Update`, so handlers may touch Unity objects
directly. The cost is up to one frame of delay - a late `Ready`/`Failed`
subscription included, which then also arrives from the next frame instead of
inside the `+=` - and events still queued when you `Dispose` the handle are
dropped.

When another mod ships alongside this library, reference it with
`<Private>false</Private>` and do **not** copy `Anvil-WebOverlay.dll` into your
own release zip - it is a shared dependency the user installs once.

Install the demo plugin to see a working panel: press **F10** in game,
**F11** for the transparent HUD demo, **F8** for the interactive glass
panel, and **F7** for a Three.js WebGL cube that follows the player camera.

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
| `Transparent` | false | HUD: unpainted pixels show the game (see below). |
| `Interactive` | false | The HUD receives mouse input - clickable glass (see below). |
| `AllowedOrigins` | null | Extra origins allowed for navigation and messages. |
| `RememberBounds` | true | Reopen at the position and size the player left the window at, across sessions. |
| `PersistenceKey` | assembly/title | Storage key for the remembered bounds. |
| `DispatchOnMainThread` | false | Raise this overlay's events from the game's main thread (see below). |
| `VirtualHosts` | null | Folders served as `https://<host>/`, so the page can load real files (see below). |

`IWebOverlay`:

| Member | Meaning |
|---|---|
| `Show()`, `Hide()`, `Toggle()` | Visibility. `IsVisible` reads the current state. |
| `Navigate(url)`, `LoadHtml(html)` | Set the page; the URL's origin becomes trusted. |
| `Post(message)`, `ExecuteScript(script)` | Send to the page; buffered until it finished loading. |
| `Post(channel, payload)` | Send on a named channel; arrives at the page's `overlay.on(channel, ...)`. |
| `Request(channel, payload, answer)` | Ask the page a question; answered exactly once, `null` on timeout. |
| `OnRequest(channel, handler)` | Answer questions the page asks with `overlay.request(...)`; null removes the handler. |
| `ExecuteScript(script, result)` | Same, and hands back what the script evaluated to, as JSON. |
| `OpenDevTools()` | Opens the browser developer tools (with `DevTools = true`). |
| `IsPageLoaded` | Whether the page you targeted has finished loading. |
| `Failure`, `FailureMessage` | Why `Failed` fired, as a cause you can act on plus the exact sentence. |
| `MessageReceived` | The page called `postMessage` with something that is not channel traffic. Overlay thread. |
| `ChannelMessage` | The page called `overlay.send(channel, payload)`. Overlay thread. |
| `KeyPressed` | A key pressed in the overlay that did not close it. Overlay thread. |
| `Closed` | Fires on every hide or close - not only on destruction. Overlay thread. |
| `VisibilityChanged` | The overlay became visible or invisible; only on real changes. Overlay thread. |
| `PageLoaded` | Your page is live; fires again on every navigation. Overlay thread. |
| `Ready`, `Failed` | Latched creation outcome - see above for threading. |
| `Dispose()` | Destroys the overlay window. |

`OverlayFailure`: `RuntimeMissing` (no WebView2 runtime - the user installs
it), `LibraryIncomplete` (`WebView2Loader.dll` missing next to this library -
reinstall it), `EnvironmentFailed` (the shared browser did not start - no
overlays this session), `WindowFailed` / `ViewFailed` (this overlay could not
be built), `CompositionUnavailable` (transparency cannot be delivered here - a
solid panel would still work), `RendererCrashed` (the browser or its renderer
died after bounded reload attempts - creating it again may work).

Every event above is delivered from the game's main thread instead when the
overlay was created with `DispatchOnMainThread = true`.

`ExecuteScript(script, result)` answers the callback exactly once - with the
JSON the script evaluated to, or `null` when it could not run at all (no page,
a page that is no longer your target, an overlay that closed, a rejected
call). That holds even if you dispose the handle while the script is still
running: closing the overlay answers whoever is waiting, rather than leaving
them waiting forever. The one case where nothing is delivered is the game
shutting down. A script that throws is reported by the browser as the JSON
`"null"`, which is indistinguishable from a script that really evaluated to
null.

`VisibilityChanged` is the event to use for "is my overlay showing": it fires
only on real transitions, including the `false` when a failure hides the
overlay - but not while the game is shutting down, where the library stays
quiet so nothing starts a fallback on the way out. `Closed` also fires for your own `Hide()`, so it cannot tell a player
closing the window from the mod closing it; that will narrow in a future major
version.

Windows keep the spot the player gave them: toggling does not recenter, and
the position and size survive restarts (`%LOCALAPPDATA%\WebOverlay\window-bounds.txt`).
A remembered spot that is no longer on any screen falls back to the centered
default, HUDs always follow the game window instead, and `RememberBounds =
false` restores the old center-on-every-show behaviour.

## Named channels and request/reply

One untyped string in each direction works, but every mod ends up inventing
the same `prefix:payload` convention and writing the page half by hand. The
library provides it instead, as `window.overlay` - injected before any page
script runs, on every document:

```csharp
overlay.Post("fps", "144");                        // mod -> page, on a channel
overlay.ChannelMessage += (channel, payload) => { ... };  // page -> mod
overlay.Request("zoom", "1.5", answer => { ... });        // mod asks, page answers
overlay.OnRequest("stash", query => LookUp(query));       // page asks, mod answers
```

```js
overlay.on('fps', value => show(value));           // mod -> page
overlay.send('button', 'reload');                  // page -> mod
overlay.onRequest('zoom', v => applyZoom(v));      // mod asks; may return a promise
overlay.request('stash', 'ammo').then(json => ...) // page asks, resolves with the answer
```

A request is answered **exactly once**: with the other side's reply, with
`null` when nothing answers that channel, and with `null` when the deadline
(five seconds by default, `Request(..., timeoutMilliseconds)` to change it)
passes. So neither side can hang the other, whatever the page does.

The plain `Post` / `MessageReceived` pair is untouched and keeps working:
anything that is not a well-formed envelope reaches `MessageReceived`
verbatim, including a page's own JSON. The protocol reserves exactly one
name - a top-level `__wo` key in a JSON message - and channel names beginning
with `__wo.` belong to the library.

## Pages with real files

`LoadHtml` takes one self-contained string, which is enough for a panel but
awkward once a UI has scripts, fonts or images - and the document itself is
capped at 2 MB by the browser. `VirtualHosts` serves a folder of yours under a
host name instead:

```csharp
var overlay = WebOverlays.Create("Studio", new OverlayOptions
{
    VirtualHosts = new[] { new VirtualHost("yourmod.assets", assetFolder) },
});
overlay.Navigate("https://yourmod.assets/index.html");
```

Navigating there, rather than pushing markup in with `LoadHtml`, is what makes
the difference - the page then has a **real origin**, and with it:

- assets load from disk as ordinary relative URLs, fonts included;
- `localStorage` and `sessionStorage` work, isolated per host name, so your UI
  can remember its own state;
- no 2 MB document limit;
- real file paths in the developer tools instead of one giant inline document.

An **inline page has none of that**: `LoadHtml` documents run in an opaque
origin, where `localStorage`, `sessionStorage` and `document.cookie` each throw
`SecurityError` on first touch (measured). A page that reads `localStorage`
without a `try`/`catch` therefore aborts the surrounding script and leaves a
half-built UI with no visible error - if your page wants storage, give it a
virtual host.

The mapped folder is served read-only, and cross-origin requests to it are
denied, so nothing outside your overlay can reach the files. The mapped origin
is trusted for navigation and messages exactly like a `Navigate` target; pick a
host name unique to your mod, since it is also the key its storage belongs to.

Mapping is all-or-nothing on purpose. If a folder is missing, a host name is
malformed, or the runtime is too old to map folders at all, the overlay fails
with `VirtualHostFailed` instead of starting, and navigation to that host stays
refused. Otherwise a host name that happens to resolve would quietly fetch a
real site from the internet under an origin your page - and this library's
message bridge - treat as your own folder.

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
- **`Transparent`** turns the overlay into a HUD. Pixels the page leaves
  unpainted show the game; painted content floats over it. Without
  `Interactive` the window ignores the mouse and never takes focus, so the
  game stays fully playable. Unless a size is set the HUD covers the game's
  whole picture, and the page decides where on it something appears (a sized
  HUD sits at the picture's top-left corner).
- **`Interactive`** (on a `Transparent` overlay) forwards mouse input to the
  page: HTML buttons, hover states and wheel scrolling work while the game
  keeps the keyboard. The window swallows mouse input over its whole
  rectangle, so size an interactive overlay to its content. `CloseKeys` do
  not apply (no keyboard); hide it from the mod's own hotkey. Wheel scrolling
  over the unfocused overlay relies on Windows' "scroll inactive windows"
  setting (default on in Windows 10/11).

On Windows 8+ with a 2021+ WebView2 runtime, HUDs are **composition hosted**:
transparency is true per-pixel alpha, so `rgba()` glass, soft shadows and
clean antialiasing all blend with the game (`Opacity` is ignored there - fade
in the page's CSS instead). On older systems a display-only HUD falls back to
a **chroma key** with these rules; an interactive one fails instead:

- Per-pixel transparency is binary: semi-transparent page pixels blend towards
  near-black rather than the game - solid dark panels are the safe look.
- `rgb(3,1,3)` is the reserved transparency key; avoid painting it.

## Performance

Numbers from the library's empirical probe host (WebView2 runtime 151) on a
machine that only has Windows' software rasterizer - one measured data
point, not a guarantee for other hardware. The method: a page that echoes
every message straight back, timed from `Post` to the answering
`MessageReceived`.

- **Round trip** (game → page → game): median 0.48 ms, 95th percentile
  0.71 ms over 200 round trips.
- **Throughput**: a burst of 1000 round trips finished in 104 ms - about
  9,600 messages per second. One `Post` per rendered frame, as the demo's
  cube HUD does, uses a fraction of that even at high refresh rates.
- **Visible latency**: a message that changes what the page shows becomes
  visible within one to two display frames.
- The browser renders in its own process tree, so the page's layout,
  painting and JavaScript never run on the game's thread; a `Post` costs the
  game only assembling and queueing the string. The browser processes still
  share the machine's CPU and GPU, though - budget a heavy page like any
  program running alongside the game.

### 3D content: WebGL

Pages get Chromium's regular **WebGL2** (ANGLE over Direct3D 11), so
libraries like Three.js just work - the demo's F7 cube is one, fed by
per-frame `Post` messages. Even the probe machine's software rasterizer held
~30 fps at 340×340; with a GPU present Chromium hardware-accelerates the
same path. WebGPU is not exposed by the tested runtime (`navigator.gpu` is
absent) - target WebGL.

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

- **The bundled `WebView2Loader.dll` is a bootstrapper, not the browser.** It is
  160 KB that locates the WebView2 runtime already installed on the machine,
  loads its client DLL and forwards to it; every browser feature comes from that
  runtime. The runtime itself is a full Chromium build of several hundred
  megabytes which Microsoft distributes rather than letting apps ship it: it is
  in-box on Windows 11, was rolled out to Windows 10 through Microsoft Edge, and
  updates itself through the Edge updater, not through this library. That is
  also why the loader's own version matters so little - it is version-agnostic
  by design and only skips runtimes below its minimum. Which features exist is
  decided per interface at `QueryInterface` time, so an older runtime does not
  fail to load; individual capabilities simply fall back, exactly as HUD
  transparency does when composition support is missing.
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

- Keyboard forwarding for interactive HUDs (text fields in glass panels).
- Touch/pen input via `SendPointerInput`.

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

The demo plugin embeds Three.js (r149, MIT) for its WebGL cube; see
`WebOverlay.Demo/web/three-LICENSE.txt`, which also ships in the demo zip.

## License

MIT.
