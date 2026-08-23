# Fault-injection matrix

Executed against the built `Anvil-WebOverlay.dll`, driven by a standalone host
outside the game. Every scenario runs the real library code path; the probe
verifies outcomes through the public API and the library's log lines.

Rows 1-9 were run on 2026-08-01 for v1.0.0 (WebView2 runtime 151.0.4129.59)
and re-run unchanged for v1.4.0; rows 10-49 were added for v1.3.0 to v1.8.3
(runtime 151.0.4129.93).

The Result column says how the row is evidenced. `PASS` means the probe
asserts it automatically; anything else names what was actually done, because a
row that claims more than the automation delivers is worse than no row.

| # | Scenario | Expectation | Result |
|---|---|---|---|
| 1 | `WebView2Loader.dll` missing | first handle raises `Failed` (latched), no hang; later `Create` returns null | PASS |
| 2 | Inline HTML over the 2 MB WebView2 limit | navigation rejected with a log, no crash; nothing that was buffered or retained is lost, since the rejected page never became the target (see rows 40-41) | PASS |
| 3 | `Create` immediately followed by `Dispose`, 10 rounds | no crash, no leak-induced failure; a normal overlay works afterwards | PASS |
| 4 | Redirect from target origin A to a *different allowed* origin B with a send buffered for A | B loads and can message the host, the buffered send is dropped with a log, nothing reaches B | PASS |
| 5 | `ExecuteScript` (with its real completion handler) | script executes in the page, verified by a message round-trip | PASS |
| 6 | Bounds persistence: move+resize, hide/show, then a fresh process | the spot survives the toggle and the restart; the store file holds one namespaced entry | PASS |
| 7 | Composed glass HUD (library path): unpainted, solid and rgba() pixels over a backdrop | true alpha: backdrop shows through, solids stay, rgba blends arithmetically | PASS |
| 8 | Interactive glass HUD: a real SendInput click on an HTML button | the click travels window proc -> SendMouseInput -> page button -> message | PASS |
| 9 | Normal path: `Create` → `LoadHtml` → page renders, page→mod message | `Ready` fires, page paints (pixel-verified when the session compositor was alive), message passes the source filter | PASS |
| 10 | WebGL: the demo's Three.js page in a composed HUD | context created, render loop advances, cube pixels on screen, transparency and click-through intact, camera-coupling orientation correct | PASS |
| 11 | Web storage in an inline page | opaque origin: `localStorage`, `sessionStorage` and `document.cookie` all throw `SecurityError` - documented, not a defect | PASS |
| 12 | Virtual host: folder mapped, page navigates to it | page and its sub-resources load from disk, origin is the mapped host, `localStorage` works there | PASS |
| 13 | Virtual host mapping fails (missing folder) on a name that resolves publicly | overlay fails with `VirtualHostFailed`, nothing is loaded, and navigating anyway is refused by the origin filter - never a network page under the mod's own origin | PASS |
| 14 | Rejected `LoadHtml` (over 2 MB) after a page was already live | the previous document stays the target: `IsPageLoaded` remains true and sends still reach it | PASS |
| 15 | Main-thread dispatch with a pump | nothing is delivered until the pump runs, then on the pumping thread; late latched handlers likewise; nothing after `Dispose` | PASS |
| 16 | Main-thread dispatch without a pump (non-Unity host) | events fall back to the overlay thread, with one warning | PASS |
| 17 | `ExecuteScript` with results, including 200 in a burst and a script that cannot run | every caller answered exactly once with its own value, one-shot COM handlers freed, no crash | PASS |
| 18 | Show/Hide/Show, redundant calls, then destruction | `VisibilityChanged` reports only real transitions and the final false | PASS |
| 19 | A script occupying the renderer for seconds, then `Dispose` before it completes | the close answers the caller with null, exactly once, and the late completion changes nothing | PASS |
| 20 | `ExecuteScript` with a result, handle disposed before the main-thread pump runs | the result is still delivered - unlike an event, which is dropped on purpose - and a call on an already disposed handle is answered too | PASS |
| 21 | Host shutdown with a visible overlay | no `VisibilityChanged`, so nothing wakes a consumer fallback while the game quits | PASS |
| 22 | Named channels end to end: both directions of send, both directions of request, a promise answer, a channel nobody answers, a page that never answers | every request answered exactly once (null on timeout), payloads with quotes, newlines, backslashes and non-ASCII survive verbatim | PASS |
| 23 | A page using `window.overlay` in its first script, and a page sending plain text and its own JSON | the shim is there before page script runs; non-envelope messages arrive at `MessageReceived` untouched and no protocol traffic leaks into it | PASS |
| 24 | An interactive HUD shaped down to one rectangle, from the page and from the mod, then cleared | inside the shape it paints and takes real clicks; outside, the click reaches the window behind and nothing is painted; clearing restores both | PASS |
| 25 | `SetBounds` with all four values and with two of them null | the window moves and resizes; null arguments keep what they were | PASS |
| 26 | A page sending a shape the library cannot read, while a shape is already set | the old shape stays - picture and mouse both - instead of falling back to the whole window | PASS |
| 27 | A page sending on, and requesting from, a reserved `__wo.` channel | neither reaches the mod, and the request is answered with null rather than left open | PASS |
| 28 | `SetShape` on a framed window | the region starts below the title bar, so the frame stays usable whatever the page asks for | PASS |
| 29 | A deferred request answered from a background thread a second later, and one whose handler throws | both reach the page - the late answer with its value, the throwing one with null | PASS |
| 30 | The transparency a page is told about, with and without the theme | `wo-composed` / `wo-opaque` on the root element, `overlay.env` agreeing with the handle, palette variables only when asked for | PASS |
| 31 | `Show()` while the display-mode probe reports exclusive fullscreen, then not | refused and logged, then shown again - without a `Failed` in between | PASS |
| 32 | A transparent overlay first, then a windowed one, then more of both | all come up: the windowed one gets a second browser, since a browser hosting composed views refuses windowed ones | PASS |
| 33 | The same overlays in the other order | all come up in one browser - no second browser is created when the first one can still serve windowed views | PASS |
| 34 | Browser processes and memory with a window, then with a HUD added | one extra process and 53 MB, because that order needs no second browser (the collision order costs about six processes and 258 MB) | PASS |
| 35 | Talking to an overlay that is already up while a second browser starts for another one | the message arrives - only overlay creation waits for a browser, commands do not | PASS |
| 37 | A page that reloads with retained state set, then a retarget | the fresh page gets the newest retained payload per channel and nothing else; a page the mod retargeted to starts clean | PASS |
| 38 | 200 latest-only messages before the page can receive any, plus a page asking for `{ latest: true }` | the library delivers one - the newest - while an ordinary message beside them is untouched; the page gets a handful instead of fifty | PASS |
| 39 | `Dispatch = Manual` | nothing arrives, including `PageLoaded`, until `PumpEvents()`; then on the pumping thread | PASS |
| 40 | `Post(..., Retain)` with a page live, then a rejected retarget, then a page-initiated reload | the retained payload survives the rejection and replays to the reloaded page exactly once - the page that stays on screen keeps the state that belongs to it | PASS |
| 41 | A page named before the browser exists, followed by a real page - whether or not the first one is ever attempted | a target the browser never took is not a page being left, so state set up beforehand still reaches the page that does load, and the answer does not depend on which of the two happened first | PASS |
| 42 | `Navigate` to a file that is not in the mapped folder, and to a connection nothing answers | a warning naming the page and the browser's error status - one per completed attempt, and the browser may retry a target by itself, so a single `Navigate` can produce more than one; `IsPageLoaded` stays false, the target stands, and a working page afterwards still loads | PASS |
| 43 | The web-error-status slot on the NavigationCompleted args | the neighbouring slot returns sequential navigation ids while this one returns differing statuses - `UNKNOWN` (0) for the missing file, `CONNECTION_ABORTED` (9) for the refused connection - which no wrong slot could produce | **measured by hand**, once, during v1.8.1: the probe reads no navigation id, so it asserts only that a status arrives inside the documented range |
| 44 | `Dispatch = Manual` with a script answer already waiting in the queue when the handle is disposed | the answer is handed over rather than dropped with the events; events stay droppable, answers do not | PASS |
| 45 | The same with a script the renderer is still running when the overlay closes | the caller is answered exactly once, whichever of the two paths gets there first | PASS |
| 46 | A send buffered while the target page fails to load, then the mod naming a different page | the send does not follow the mod elsewhere - it was addressed to the page that was the target when it was made, and moving away is a retarget | PASS |
| 47 | `Navigate` to the page already showing, with retained state set | treated as a reload rather than a retarget: the state survives, as it does when the page reloads itself | PASS |
| 48 | A page asks a question, the document changes while the mod is still holding the answer, and the new page asks its own | the new page gets its own answer and never the one owed to its predecessor, although both questions carry the same page-side id | PASS |
| 49 | `Show()` refused by the display-mode probe on an already hidden overlay | no `VisibilityChanged` is invented for the refusal, so the event stays trustworthy as state | PASS |
| 36 | A second browser whose data folder cannot be created | the library refuses the folder itself and logs it, so the browser never shows the player its own modal error box; the overlay fails cleanly, nothing is remembered, and the next one succeeds | PASS |

Not automated (manually covered in game during development, or accepted):

- Renderer crash / unresponsive renderer (recovery + terminal `Failed` paths
  are code-reviewed; deliberately killing a renderer needs browser internals).
- Selective mouse transparency without clipping the picture: measured as
  impossible on Windows, see `SetShape` in the README and entry 7 of
  `docs/CONSUMER-API-WISHLIST-ANSWERS.md`.
- A failing browser-environment HRESULT (the classification and the "first
  cause wins" rule are code-reviewed; the callback cannot be forced to fail
  from outside). The same applies to the second browser failing to start: an
  invalid folder is either accepted and fails later somewhere else, or rejected
  synchronously, so what the probe would exercise is not the path that matters.
  That a failure is not remembered - the next windowed overlay tries again -
  is covered by row 36, which fails the folder rather than the browser.
- A renderer crash and the paths hanging off it: settling the scripts that were
  running in the dead renderer, and a reload the browser refuses. Killing a
  renderer on demand needs browser internals; the code paths are reviewed and
  share their settle helper with row 19, which does exercise it.
- A second-browser attempt that times out and is then retried, where the first
  attempt's completion handler must stay callable. Forcing a 30-second timeout
  from outside means making the WebView2 loader hang, which nothing here can
  do; the handler's lifetime is instead guaranteed structurally - the slot is
  a `ref` parameter that disposes what it replaces, so a call site cannot
  forget - and by `ComCallback`'s own rule that `Dispose` roots an instance
  native code still holds.
- Shutdown during environment start (guarded by `stopping` checks; exercised
  implicitly at every game exit).
- Freeing the cursor while the game holds it: needs Unity and a game that
  captures the mouse, so it is covered in game rather than here.
- The `KeyCode` to virtual-key table, which is a table.

## Running it

The probe lives in the repository at [`tools/Probe`](../tools/Probe), and each
row above is one of its modes:

```bash
dotnet build WebOverlay/WebOverlay.csproj -c Release
dotnet run --project tools/Probe -c Release -- channels
```

Modes: `fault-loader`, `fault-bightml`, `fault-dispose-race`,
`fault-redirect`, `script-roundtrip`, `bounds-save`/`bounds-verify`, `latency`,
`glass`, `glass-click`, `cube`, `storage`, `vhost`, `vhost-fail`,
`nav-reject`, `dispatch`, `failure-kind`, `script-result`, `visibility`,
`close-race`, `shutdown-quiet`, `channels`, `shape`, `bounds-api`,
`shape-guards`, `api17`, `mixed`, `mixed-reverse`, `dcomp-first`, `footprint`,
`spare-browser`, `spare-folder`, `retained`, `latest-only`, `manual-pump`,
`failed-nav`, `generation`; no mode at all is the normal path.

`glass`, `glass-click`, `cube`, `shape` and `shape-guards` sample real screen
pixels and send real mouse input, so they need a desktop that is actually being
composited. Over
RDP both a disconnected session *and* an `Active` session whose window is
merely minimised return `rgb(0,0,0)` for every sample and swallow every click.
See [`tools/Probe/README.md`](../tools/Probe/README.md) for that and for
`preview`, the mode meant for building your own pages rather than for testing
the library.
