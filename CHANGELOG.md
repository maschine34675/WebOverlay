# Changelog

Version-specific changes live here and nowhere else: the GitHub release notes
and the Forge version field are filled from these entries, and `README.md`
describes the library as it is now rather than how it got there.

Each version starts with the short player-facing copy for the Forge version
field, followed by the detailed record for anyone reading the source later.

Minor releases are additive: they add members and change nothing a consumer
depended on. So a mod gates on "at least X.Y" rather than on presence, and
every entry below names the version a member arrived in - see
[`docs/SOFT-DEPENDENCY.md`](docs/SOFT-DEPENDENCY.md).

## [Unreleased]

Nothing in the shipped library. The repository gained the host it was tested
with:

- `tools/Probe` - the standalone host that drives the built DLL outside the
  game, previously a throwaway harness kept out of the tree. Every row of
  `docs/FAULT-TESTS.md` is one of its modes, and every hand-bound vtable slot
  in this library was proven by one of them. `fault-loader` and `failure-kind`
  now stage their own incomplete plugin folder instead of depending on how the
  harness happened to be laid out.
- `preview` - the mode for mod authors rather than for the library: it shows a
  page in a real overlay, feeds it channel messages, prints what it sends back
  and screenshots the result, so a HUD can be built without launching a raid.
  `tools/Probe/sample-page.html` is a worked example.

## [1.8.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- A mod's settings survive a page reload: after a browser hiccup the overlay
  comes back the way it was instead of falling back to its defaults.
- Overlays that stream live data (markers, telemetry) can keep up instead of
  falling behind when the game stutters.

### Added

- `Post(channel, payload, PostOptions.Retain)` - the payload is remembered per
  channel and replayed to every page that loads afterwards, before anything
  else reaches it. The library reloads a page by itself after a renderer crash,
  and a fresh document starts from its own defaults, so configuration sent once
  was quietly lost mid-session with the mod's dirty-check none the wiser.
  Retargeting the overlay forgets them; setting state up before naming the
  first page does not.
- `PostOptions.LatestOnly` - a payload still held by the library is replaced by
  a newer one on the same channel, and `overlay.on(channel, fn, { latest: true })`
  hands the page the newest payload once per frame rather than the backlog.
  Once a message has gone to the browser there is no queue here to collapse,
  which is why the page has a half of this.
- `OverlayOptions.Dispatch` with `EventDispatch.Manual` and
  `IWebOverlay.PumpEvents()`: events wait until the consumer asks, so they run
  inside its own `Update`, at the point it chooses, on its own frame budget.
  `DispatchOnMainThread` keeps working as the older way of saying
  `EventDispatch.MainThread`.

### Fixed

- Messages and retained state set up before the first `LoadHtml` or `Navigate`
  are no longer discarded by it. Only a real retarget - away from a page that
  existed - forgets what was meant for the page being left.

## [1.7.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Overlays can be told to hand the mouse cursor back while they are open, so a
  window opened during a raid can actually be used instead of leaving the
  cursor captured by the game.
- The library now refuses to open a window over a game in exclusive fullscreen
  rather than relying on every mod to check first, which is what used to
  minimise the game.
- Fixed: a mod's window failed to open while another mod's transparent overlay
  was on screen. Two mods that use the library at the same time no longer
  interfere with each other.

### Added

- `OnRequest(channel, (payload, reply) => ...)` for answers that are not ready
  yet: reply once, later, from wherever the answer arrives.
- `IWebOverlay.Transparency`, and the same fact for the page without any mod
  code - `wo-composed` / `wo-chroma` / `wo-opaque` on the root element and
  `overlay.env.transparency` - so a stylesheet can adapt to the kind of
  transparency it actually got.
- `OverlayOptions.InjectTheme`, putting the library palette on the page as CSS
  variables, documented in `docs/STYLE.md`.
- `OverlayOptions.FreeCursorWhileShown`, releasing the cursor while such an
  overlay is the window in front - the library undoing its own side effect,
  since a framed overlay takes the foreground while the game keeps the mouse
  captured. The condition is the foreground window as the system reports it,
  not Unity's idea of focus, which does not have to agree.
- `WebOverlayPlugin.VirtualKey(KeyCode)` and `CloseKeysFor(KeyboardShortcut)`,
  the table two consumers had each written for themselves.

### Fixed

- A windowed overlay could not be created while the only live overlays were
  transparent ones - it failed with `ViewFailed` and `ERROR_INVALID_STATE`.
  A browser hosting visual (composed) views refuses to create a windowed one,
  and two environments sharing a user data folder share the browser, so the
  library now gives such a windowed overlay a browser of its own. Present since
  1.2.0; in practice one mod's HUD broke another mod's panel, which is exactly
  the QuestMarkers and ModProfiler combination.

  The second browser is created only when that collision actually happens and
  takes the windowed overlay, because it costs about six processes and a
  quarter of a gigabyte (measured). A game whose mods only use HUDs, only use
  windows, or open the window first never pays for it - measured at one extra
  process and 53 MB for a window plus a HUD in that order. While that browser
  starts, only overlay *creation* waits: an overlay that is already up keeps
  answering. A browser that fails to start is not remembered, so the next
  overlay tries again instead of inheriting the defect for the session.

- A browser data folder that cannot be created is refused by the library
  instead of by the browser. Told to use a folder it cannot create, WebView2
  puts a modal error box on the player's screen - not something a mod should be
  able to cause. Both folders are now made and checked here first, and a
  failure is a log line and a classified failure instead.

### Changed

- `Show()` refuses exclusive fullscreen itself, logging once, instead of
  leaving it to every consumer's every show path. `IsDisplayModeSupported`
  stays public for a mod that wants to explain the situation in its own
  interface.
- `DispatchOnMainThread` now says in its documentation whose frame budget a
  dispatched handler spends: it runs inside this library's `Update`, so a
  profiler bills it here and its position in the frame follows plugin load
  order.
- The demo derives its close keys from the configured hotkey rather than
  hard-coding them, frees the cursor, takes the theme, and shows a deferred
  answer.

## [1.6.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Pages can be built like ordinary web apps: a mod can serve its own folder as
  https://yourmod.assets/, so scripts, fonts and images load normally and the
  page gets working storage.
- Named channels with request/reply: page and mod can ask each other a question
  and await the answer, instead of every mod inventing its own convention. A
  question is always answered, so neither side can leave the other hanging.
- A mod can read values back out of its page, and gets events for "my page is
  live" and for real visibility changes, plus the option to receive everything
  on the game's main thread.
- Failures now say why, so a mod can tell you "install the WebView2 runtime"
  instead of showing a generic error.
- Interactive HUDs can be cut down to the rectangles they actually use, so a
  HUD can cover the screen while the game stays clickable everywhere else;
  windows can also be moved and resized at runtime.

(The entry above covers 1.4.0 to 1.6.0, because those two versions were never
published to the Forge separately.)

### Added

- Named channels and request/reply in both directions: `Post(channel, payload)`
  and `ChannelMessage`, `Request` and `OnRequest` on the mod side;
  `overlay.on`, `send`, `onRequest` and `request` in the page, provided by a
  shim injected before any page script runs
  (`AddScriptToExecuteOnDocumentCreated`, v1 slot 27). A request is answered
  exactly once - the reply, `null` when no handler takes that channel, `null`
  on timeout, `null` when the overlay closes with the question still open.
- `SetShape` and `overlay.setShape`, cutting an overlay down to a set of
  rectangles, and `SetBounds` for runtime move and resize.

### Changed

- Framing lives in a JSON envelope with one reserved key (`__wo`) and a
  reserved channel prefix (`__wo.`); anything that is not a well-formed
  envelope still reaches `MessageReceived` verbatim, a page's own JSON
  included.
- The demo's glass panel uses channels and asks the game for its frame rate;
  the F10 panel keeps using plain strings, so both styles are visible.

### Fixed

- A shape the library cannot read is ignored instead of clearing the shape,
  which would have handed a full-screen interactive HUD back the whole mouse.
- Reserved channels are filtered as a prefix rather than by one known name,
  and a request on one is answered rather than left open.
- Shape rectangles are offset from the client to the window origin, so a
  framed overlay keeps its title bar.

### Notes

- Selective mouse transparency - keeping the picture and giving up only the
  mouse - was measured to be impossible on Windows: `HTTRANSPARENT` passes a
  click only to windows of the same thread, and a window region routes clicks
  across processes but clips the picture. `SetShape` therefore governs picture
  and mouse together. See `docs/CONSUMER-API-WISHLIST-ANSWERS.md`, entry 7.

## [1.5.0]

### Forge version notes

- For mod authors: ExecuteScript can now hand back what the script evaluated
  to, so a mod can read state out of its page without building a round trip by
  hand.
- New VisibilityChanged event that reports only real show/hide changes - the
  existing Closed event also fires for a mod's own Hide and cannot tell the two
  apart.
- Nothing changes for players.

### Added

- `ExecuteScript(script, result)` returning the script's value as the JSON the
  browser produced, answered exactly once - including when the overlay is
  disposed while the script is still running.
- `VisibilityChanged`, raised only on real transitions.

### Changed

- Hand-built COM callbacks are freed when the last reference goes, instead of
  being leaked for the process lifetime, which is what makes per-call
  completion handlers affordable.
- Script results are delivered even after the handle is disposed - unlike an
  event, a result is a promise to one caller - and suppressed only while the
  game is shutting down, as `VisibilityChanged` now is too.

## [1.4.0]

### Forge version notes

- For mod authors: overlays can serve a folder of real files as
  https://yourmod.assets/ - scripts, fonts and images load normally, and such a
  page also gets working localStorage (an inline page has none).
- Failed now says why: a cause a mod can act on plus the exact message.
- New PageLoaded event and IsPageLoaded, and an option to receive all events on
  the game's main thread.
- Nothing changes for players.

### Added

- `OverlayOptions.VirtualHosts`, mapping folders to `https://<host>/`
  (`ICoreWebView2_3`, slot 71, `DENY_CORS`). A page that navigates there gets a
  real origin, and with it same-origin assets, working storage isolated per
  host, and no 2 MB document limit.
- `Failure` and `FailureMessage` on the handle, classified across every failure
  site, with the shared start recording its own cause.
- `PageLoaded` and `IsPageLoaded`, and `OverlayOptions.DispatchOnMainThread`.

### Changed

- The library core stays free of Unity: the main-thread queue lives in the
  host, and the plugin drains it from its own `Update`, so the empirical probe
  can still drive the real DLL outside the game.

### Fixed

- A virtual-host mapping that cannot be applied fails the overlay and keeps the
  origin filter closed, instead of letting the page's host name reach the
  network.
- A rejected `Navigate` or `LoadHtml` restores the previous target, so the
  overlay does not report "not loaded" forever while the old page is still on
  screen.

## [1.3.0]

### Forge version notes

- Demo: F7 shows a Three.js WebGL compass cube coupled to the player camera -
  overlays run full WebGL2, so 3D HUDs are possible. The library itself is
  unchanged.
- README now documents measured performance: ~0.5 ms message round trip,
  ~9,600 messages/s, visible changes within 1-2 display frames.

### Added

- Demo: a Three.js compass cube (F7) fed by one camera message per frame,
  with Three.js r149 embedded in the demo assembly.
- README sections on measured messaging performance and on WebGL support.

### Fixed

- Demo key toggles work while movement keys are held; BepInEx's
  `KeyboardShortcut.IsDown` blocks whenever any unrelated key is down.

## [1.2.1]

### Forge version notes

- Fixed: the transparent display-only HUD had stopped being click-through in
  1.2.0; it ignores the mouse again.

### Fixed

- `WS_EX_TRANSPARENT` only takes a window out of hit-testing when
  `WS_EX_LAYERED` is set as well, which the composed window was missing. The
  1.2.0 release was withdrawn in favour of this one; its tag stands.

## [1.2.0]

### Forge version notes

- HUDs are now composition hosted (Windows 8+, 2021+ WebView2): true per-pixel
  alpha - rgba() glass, soft shadows and clean antialiasing blend with the
  game. Older systems keep the chroma-key fallback.
- New Interactive option: a transparent HUD can receive mouse input - HTML
  buttons, hovers and wheel scrolling work while the game keeps the keyboard.

### Added

- Composition hosting through DirectComposition and
  `ICoreWebView2Environment3`, with the chroma key kept as the fallback.
- `OverlayOptions.Interactive`, forwarding mouse input to the page.

### Notes

- Superseded by 1.2.1, which fixes click-through for display-only HUDs.

## [1.1.0]

### Forge version notes

- Windows now remember their position and size: toggling no longer recenters,
  and the spot survives restarts. A spot that ends up off-screen (monitor
  changes) falls back to the centered default. Mods can opt out or set their
  own storage key (RememberBounds / PersistenceKey).

### Added

- Bounds persistence in `%LOCALAPPDATA%\WebOverlay\window-bounds.txt`, shared
  safely across mods and game instances, with `RememberBounds` and
  `PersistenceKey` to control it.

## [1.0.1]

### Forge version notes

- Packaging and logging polish; no functional changes.

### Fixed

- Every file in the release zip lives inside the plugin folder, so a blind
  extraction leaves nothing in the game root.
- Log lines no longer repeat the plugin name BepInEx already prints.

## [1.0.0]

### Forge version notes

- First release.

### Added

- Overlay windows over the game with HTML content, two-way messaging,
  transparent HUDs, and the security defaults the README describes.
