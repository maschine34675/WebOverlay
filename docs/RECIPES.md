# Recipes

How to build the things the library is for, one task per section. The member
tables and the threading rules live in [API.md](API.md); everything here
assumes them.

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
  rectangle, so either size an interactive overlay to its content or cut it
  down with `SetShape` (see the next section). `CloseKeys` do
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

The page can read which of the two it got - `IWebOverlay.Transparency` on the
mod side, `overlay.env.transparency` and a `wo-composed` / `wo-chroma` /
`wo-opaque` class on the root element in the page - so one stylesheet can
serve both.

### What a HUD has to decide for itself

An overlay is an operating-system window sitting above the game. That is what
makes it work at all, and it is also the source of every surprise below. None
of these are library behaviour the mod can turn off; they are decisions the
mod has to make. Each is one line once known, and invisible until a player
reports it.

- **It floats over the game's own screens too** - the inventory, the map, the
  menus, the death sequence. A HUD that should only exist during play has to
  say so: in EFT that is
  `EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI)` plus
  the player's `HealthController.IsAlive`, checked on a timer or a screen
  event, with `Hide()` when it no longer holds.
- **The hideout looks exactly like a raid** to every obvious test: it registers
  a `GameWorld`, the game reaches `GameStatus.Started`, and there is a player
  flagged `IsYourPlayer`. Exclude it by world type (`HideoutGameWorld`) or your
  HUD will greet the player over their workbench.
- **Pick one mechanism for showing and hiding the page.** A page that toggles a
  CSS class *and* writes `style.opacity` will find the inline value wins every
  time - this is ordinary CSS precedence, not the overlay, and it has cost more
  than one review round.

Two traps that used to belong here have been closed by the library: page-side
configuration no longer has to be re-sent on every `PageLoaded` - send it with
`PostOptions.Retain` and the library replays it after its own reload (1.8.0) -
and `Show()` refuses exclusive fullscreen by itself, logging once, rather than
leaving each caller to guard it (1.7.0).

## Shaping a HUD, and moving a window

An `Interactive` HUD takes the mouse over its whole rectangle, which is fine
for a small panel and useless for one that covers the screen. `SetShape` cuts
the overlay down to the rectangles it actually uses:

```csharp
overlay.SetShape(new[] { new OverlayRegion(20, 20, 260, 120) });
overlay.SetShape(null);                                  // whole window again
```

```js
overlay.setShape([document.querySelector('#panel')]);    // elements or {x,y,w,h}
```

Inside those rectangles the overlay draws and takes the mouse; outside them
the game gets the click and nothing is painted. Rectangles are measured from
the top-left of the page, so a framed window keeps its title bar, and a shape
the library cannot read is ignored rather than applied as "no shape" - losing
a shape would hand a full-screen HUD back the whole mouse. **Both halves are the same
mechanism and cannot be separated** - what is cut away is cut away for both -
so pad the rectangles a little if your content has soft shadows, and call it
again when the layout changes. Windows offers no way to keep the picture and
give up only the mouse: answering the hit test with "not me" passes clicks
only to windows of the same thread, which the game never is (measured: the
click reaches nothing at all), and the mechanism that does route clicks across
processes is the one used here, which clips.

`SetBounds(x, y, width, height)` moves or resizes a window at runtime, in
screen coordinates; any argument left null keeps its current value. It is not written to the
remembered-bounds store - that belongs to the player - but it does win over a
remembered spot for the rest of the session. HUDs follow the game picture, so
this is for panels.

## Pages with real files

`LoadHtml` takes one self-contained string, which is enough for a panel but
awkward once a UI has scripts, fonts or images - and the document itself is
capped at 2 MB by the browser. `VirtualHosts` serves a folder of yours under a
host name instead:

```csharp
// The folder travels with the mod: next to the DLL, wherever that is.
string assetFolder = Path.Combine(
    Path.GetDirectoryName(typeof(YourPlugin).Assembly.Location), "web");

var overlay = WebOverlays.Create("Studio", new OverlayOptions
{
    VirtualHosts = new[] { new VirtualHost("yourmod.assets", assetFolder) },
});
overlay.Navigate("https://yourmod.assets/index.html");
```

The other two thirds of this recipe are outside the code. In the `.csproj`,
copy the page files to the build output so a local run has them:

```xml
<ItemGroup>
  <Content Include="web\**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

And in the release zip, ship the folder inside the plugin directory, exactly
where the code above will look for it:

```
BepInEx/plugins/YourMod/YourMod.dll
BepInEx/plugins/YourMod/web/index.html
BepInEx/plugins/YourMod/web/style.css
```

Map `web/`, not the plugin folder itself: everything under the mapped folder
is reachable from the page, and your DLL and the player's config files are
nobody's sub-resources.

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

The mapped folder is served read-only, and the mapping exists only inside your
overlay's own browser view - nothing outside it can reach the files. Inside it,
`fetch` and XHR from another origin are denied; a different origin you allow in
the same overlay can still load files as a script, image or iframe, so map a
folder that holds only what your interface serves. (That is
`DENY_CORS` rather than the stricter `DENY`, deliberately: an inline `LoadHtml`
page has an opaque origin, and under `DENY` even your own markup could not
reach its assets - measured, rows 66 and 67 of [FAULT-TESTS.md](FAULT-TESTS.md).)

**Web fonts are the trap.** They are fetched in CORS mode by specification, so
the default refuses every face: the page renders in a fallback and nothing
reports it. Two shapes meet this, and the second is easy to miss:

- A page on one host of yours loading a face from another host of yours. The
  face is cross-origin, so it is refused.
- **An inline `LoadHtml` page loading a face from your only host.** A
  `LoadHtml` document has an opaque origin, so it is cross-origin to *every*
  mapped host - including the single one you gave it. Having one host is not
  protection here.

Either way, say what the folder holding the fonts may serve:

```csharp
VirtualHosts = new[]
{
    new VirtualHost("yourmod.ui", pageFolder),
    new VirtualHost("yourmod.fonts", fontFolder) { Access = HostAccess.Allow },
},
```

`Access` is a property of the host being **read**, not of a pair - it belongs on
the host the fonts come from, never on the page's own. And it is not free:
`Allow` is the equivalent of that folder answering
`Access-Control-Allow-Origin: *` to every origin in the overlay, including any
remote origin you put in `AllowedOrigins`. So point it at a folder holding only
what the page may read - which is the reason to split the hosts rather than map
your whole plugin directory.

The mapped origin is trusted for navigation and messages exactly like a
`Navigate` target; pick a host name unique to your mod, since it is also the key
its storage belongs to.

Mapping is all-or-nothing on purpose. If a folder is missing, a host name is
malformed, or the runtime is too old to map folders at all, the overlay fails
with `VirtualHostFailed` instead of starting, and navigation to that host stays
refused. Otherwise a host name that happens to resolve would quietly fetch a
real site from the internet under an origin your page - and this library's
message bridge - treat as your own folder.

## Previewing a page without starting the game

Building a HUD by launching a raid to look at it gets old fast. The repository
carries the host this library was tested with, and one of its modes shows your
page in a real overlay - same window, same transparency, same message bridge:

```bash
dotnet build WebOverlay/WebOverlay.csproj -c Release -p:SptRoot=<your SPT folder> -p:DeployToSpt=false
dotnet run --project tools/Probe -c Release -- preview path/to/page.html --transparent
```

(`SptRoot` names the SPT installation whose BepInEx and Unity assemblies the
build borrows; a working copy inside one needs neither argument, and without
`DeployToSpt=false` a successful build also deploys into it - see the
README's Building section.)

It serves the file from its own folder, so relative assets and storage behave
as they will in the mod; `--post <channel> <text>` feeds the page messages,
whatever the page sends back is printed, and `--screenshot` saves what it
looks like. [`tools/Probe/sample-page.html`](../tools/Probe/sample-page.html) is a worked example to start from,
and [`tools/Probe/README.md`](../tools/Probe/README.md) has the rest.

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
- Downloads are blocked, with a warning naming the URL -
  `OverlayOptions.AllowDownloads` opts back in for a mod that really wants
  files. (On a runtime from before 2021 there is no download control;
  downloads then stay browser-managed and the log says so.)
- Popups are suppressed, permission prompts (camera, location, ...) are
  denied, `alert()`-style script dialogs are off, and the browser's password
  saving and form autofill are disabled on runtimes that support those
  settings (2021 or newer; older ones keep their defaults) - the browser
  profile is shared by every mod using the library (one per Windows user
  under `%LOCALAPPDATA%\WebOverlay`), so nothing sensitive should be stored
  in it.
- Browser accelerator keys (print, find, refresh) are off unless the overlay
  was created with `DevTools = true` - same runtime caveat.
- One script of the library's own runs in every document, before the page's
  scripts: it provides `window.overlay` for named channels. It only wraps the
  message bridge the page already had, so it grants no new reach; the name
  `overlay` and the `__wo` prefix are reserved for it.
