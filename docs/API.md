# API reference

Everything a consuming mod calls, and the rules that make calling it safe.
The task-oriented walkthroughs are in [RECIPES.md](RECIPES.md); this file is
the contract. Minor releases are additive - the version each member arrived in
is in [SOFT-DEPENDENCY.md](SOFT-DEPENDENCY.md), together with the pattern for
using the library as an optional dependency.

## Lifecycle and threading

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

One thing to know before doing real work in a dispatched handler: it runs
inside **this library's** `Update`, so a profiler bills the time to the library
rather than to your mod, and where it lands in the frame depends on plugin load
order. Keep such handlers short - or take
`Dispatch = EventDispatch.Manual` and call `PumpEvents()` from your own
`Update`, which delivers the same events at the point you choose and on your
own frame budget. Nothing arrives until you pump, so a mod that stops pumping
stops hearing.

## The types


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
| `Transparent` | false | HUD: unpainted pixels show the game (see [RECIPES.md](RECIPES.md#translucency-and-huds)). |
| `Interactive` | false | The HUD receives mouse input - clickable glass (see [RECIPES.md](RECIPES.md#translucency-and-huds)). |
| `AllowedOrigins` | null | Extra origins allowed for navigation and messages. |
| `RememberBounds` | true | Reopen at the position and size the player left the window at, across sessions. |
| `PersistenceKey` | assembly/title | Storage key for the remembered bounds. |
| `DispatchOnMainThread` | false | Raise this overlay's events from the game's main thread (see above). |
| `Dispatch` | OverlayThread | Where events arrive: `OverlayThread`, `MainThread`, or `Manual` with `PumpEvents()`. |
| `InjectTheme` | false | Put the library palette on the page as CSS variables (see [`docs/STYLE.md`](STYLE.md)). |
| `FreeCursorWhileShown` | false | Hand the cursor back while this overlay is up and the game is unfocused. |
| `ClickThroughWhenUnfocused` | false | Let the mouse reach the game while the game is in front - needed for a panel over the middle of the screen (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md#the-mouse-stops-turning-the-player)). |
| `VirtualHosts` | null | Folders served as `https://<host>/`, so the page can load real files (see [RECIPES.md](RECIPES.md#pages-with-real-files)). |
| `AllowDownloads` | false | Let pages start downloads. Blocked by default with a warning naming the URL: a page over a game has no business writing files to the player's disk. On a pre-2021 runtime downloads stay browser-managed either way, and the log says so. 1.10.0. |

`IWebOverlay`:

| Member | Meaning |
|---|---|
| `Show()`, `Hide()`, `Toggle()` | Visibility. `IsVisible` reads the current state. |
| `Navigate(url)`, `LoadHtml(html)` | Set the page; the URL's origin becomes trusted. |
| `Post(message)`, `ExecuteScript(script)` | Send to the page; buffered until it finished loading. |
| `Post(channel, payload)` | Send on a named channel; arrives at the page's `overlay.on(channel, ...)`. |
| `Post(channel, payload, options)` | The same, `Retain`ed for pages that load later or only worth sending while `LatestOnly` (see below). |
| `PumpEvents()` | Deliver this overlay's waiting events, for `Dispatch = Manual`. |
| `Request(channel, payload, answer)` | Ask the page a question; answered exactly once, `null` on timeout. |
| `OnRequest(channel, handler)` | Answer questions the page asks with `overlay.request(...)`; null removes the handler. Take `(payload, reply)` instead of returning a value when the answer is not ready yet. |
| `Transparency` | Which transparency this overlay got: `Composition`, `ChromaKey` or `None`. |
| `ExecuteScript(script, result)` | Same, and hands back what the script evaluated to, as JSON. |
| `OpenDevTools()` | Opens the browser developer tools (with `DevTools = true`). |
| `SetBounds(x, y, w, h)` | Move or resize at runtime; null keeps a value. Not persisted. |
| `SetShape(regions)` | Cut the overlay down to these rectangles - picture and mouse both (see [RECIPES.md](RECIPES.md#shaping-a-hud-and-moving-a-window)). |
| `IsPageLoaded` | Whether the page you targeted has finished loading. |
| `Failure`, `FailureMessage` | Why `Failed` fired, as a cause you can act on plus the exact sentence. |
| `MessageReceived` | The page called `postMessage` with something that is not channel traffic. Overlay thread. |
| `ChannelMessage` | The page called `overlay.send(channel, payload)`. Overlay thread. |
| `KeyPressed` | A key pressed in the overlay that did not close it. Overlay thread. |
| `Closed` | Fires on every hide or close - not only on destruction. Overlay thread. |
| `VisibilityChanged` | The overlay became visible or invisible; only on real changes. Overlay thread. |
| `PageLoaded` | Your page is live; fires again on every navigation. Overlay thread. |
| `Ready`, `Failed` | Latched creation outcome - see above for threading. |
| `ChannelsFailed` | The channel shim could not be installed: everything built on `window.overlay` is dead, while the window, raw `Post`/`MessageReceived` and scripts keep working. Latched like `Failed`. 1.10.0. |
| `ChannelsAvailable` | Whether `window.overlay` will exist in this overlay's pages: null until known, then true or false for the overlay's lifetime. 1.10.0. |
| `Dispose()` | Destroys the overlay window. |

`OverlayFailure`: `RuntimeMissing` (no WebView2 runtime - the user installs
it), `LibraryIncomplete` (`WebView2Loader.dll` missing next to this library -
reinstall it), `EnvironmentFailed` (the shared browser did not start - no
overlays this session), `VirtualHostFailed` (a folder in
`OverlayOptions.VirtualHosts` could not be served - the overlay refuses to
continue rather than let the page's host name reach the network), `WindowFailed` / `ViewFailed` (this overlay could not
be built), `CompositionUnavailable` (transparency cannot be delivered here - a
solid panel would still work), `RendererCrashed` (the browser or its renderer
died after bounded reload attempts - creating it again may work).

Every event above is delivered from the game's main thread instead when the
overlay was created with `DispatchOnMainThread = true`, and waits for your own
`PumpEvents()` call with `Dispatch = EventDispatch.Manual`. That includes the
latched `Ready` and `Failed`: outside the default overlay-thread mode, even a
late subscription is queued rather than run inside the `+=`, so do not read
your fallback flag on the frame you subscribed.

`ExecuteScript(script, result)` answers the callback exactly once - with the
JSON the script evaluated to, or `null` when it could not run at all (no page,
a page that is no longer your target, an overlay that closed, a rejected
call, a renderer that crashed under it). That holds even if you dispose the
handle while the script is still running: closing the overlay answers whoever
is waiting, rather than leaving them waiting forever - with
`EventDispatch.Manual` too, where the answers owed at that moment go out on the
spot rather than into a queue nobody will pump again. While the handle is
alive, though, a Manual overlay hands you its answers on `PumpEvents()` like
everything else, so keep pumping while you wait for one. The one case where
nothing is delivered is the game shutting down. A script that throws is reported by the browser as the JSON
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

Two things a channel message can be beyond "send this now":

```csharp
overlay.Post("config", json, PostOptions.Retain);       // every page that loads gets it
overlay.Post("frame", data, PostOptions.LatestOnly);    // only while it is the newest
```

**Retained** is the answer to a trap: the library reloads a page by itself
after a renderer crash, and the fresh document starts from its own defaults -
so configuration a mod sent once is quietly gone mid-session, and the mod's own
dirty-check sees no change to re-send. A retained payload is remembered per
channel and handed to every page that loads afterwards, before anything else
reaches it. Retargeting the overlay with `LoadHtml` or `Navigate` forgets them:
the page changed, so its state is not the new page's state. (Setting state up
*before* naming the first page is fine - that page is the one it was meant
for.)

**Latest-only** drops a payload that has not been sent yet when a newer one on
the same channel arrives, which is what per-frame telemetry wants. It applies
while the library still holds the message; once it has gone to the browser
there is no queue here to collapse. The other half is in the page, which is
where a backlog actually forms:

```js
overlay.on('frame', draw, { latest: true });   // newest payload, once per frame
```

A request is answered **exactly once**: with the other side's reply, with
`null` when nothing answers that channel, with `null` when the deadline
(five seconds by default, `Request(..., timeoutMilliseconds)` to change it)
passes - and with an immediate `null` in the one pathological case of the
overlay command queue being full under a message flood, rather than a caller
waiting five seconds for a timeout on a question nobody was ever asked.
`ExecuteScript(script, result)` answers `null` in that same case. So neither side can hang the other, whatever the page does.

When an answer is not ready yet, take the deferred form of `OnRequest` and
call `reply` later, from wherever the answer arrives:

```csharp
overlay.OnRequest("rescan", (payload, reply) => StartCoroutine(Rescan(reply)));
```

The page can raise its own deadline with `overlay.request(channel, payload,
timeoutMs)` when it expects to wait. A reply that arrives after the page gave
up is dropped rather than resolving a stale promise, and a handler that throws
before replying answers `null`.

Pages also learn what they are running in without asking the mod: the library
puts `wo-composed`, `wo-chroma` or `wo-opaque` on the root element (and
`overlay.env.transparency` says the same), so a stylesheet can adapt to the
kind of transparency it actually got. `OverlayOptions.InjectTheme` additionally
sets the library palette as CSS variables - see [`docs/STYLE.md`](STYLE.md).

`window.overlay` exists only inside an overlay. A page that should also open in
an ordinary browser wants one guard line (`if (window.overlay) { ... }`) rather
than a console full of errors.

**Order.** Messages a mod posts arrive at the page in the order it posted
them - across channels, not merely within one, because every send goes through
a single queue rather than one queue per channel. Before the page is ready they
wait in that queue and are flushed in the same order; `Retain` values replay
first, when the page loads, in the order the mod first set each channel. So
posting a value and then asking the page about it is safe as written.

`LatestOnly` is the one exception, and deliberately: a newer message replaces
the one still waiting on that channel and takes its place, so the page sees the
newest payload at the position of the first one that was waiting. If the queue
overflows the extra sends are dropped with a warning, never reordered.

The plain `Post` / `MessageReceived` pair is untouched and keeps working:
anything that is not a well-formed envelope reaches `MessageReceived`
verbatim, including a page's own JSON. The protocol reserves exactly one
name - a top-level `__wo` key in a JSON message - and channel names beginning
with `__wo.` belong to the library.
