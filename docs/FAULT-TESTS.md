# Fault-injection matrix

Executed against the built `Anvil-WebOverlay.dll`, driven by a standalone host
outside the game. Every scenario runs the real library code path; the probe
verifies outcomes through the public API and the library's log lines.

Rows 1-9 were run on 2026-08-01 for v1.0.0 (WebView2 runtime 151.0.4129.59)
and re-run unchanged for v1.4.0; rows 10-92 were added for v1.3.0 to v1.11.0
(runtime 151.0.4129.93). Row 21 grew two assertions in v1.11.0 (Q3, Q4) and
row 78 was re-proven there against a changed mechanism.

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
| 21 | Host shutdown with a visible overlay | no `VisibilityChanged` and, since 1.11.0, no `Closed` either, and a script asked for afterwards is never answered - so nothing wakes a consumer fallback while the game quits | PASS |
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
| 50 | A `Navigate` the library's own origin filter cancels - a URI with no origin to trust | blocked and logged, the overlay stops waiting for a start that will never come, and the page that loads next is heard normally | PASS |
| 51 | The same, with retained state set for the page that stays on screen | the refused page is not left as the target and its state is not thrown away: it reaches the page that does load | PASS |
| 52 | A `Ready` handler that asks for its page immediately, while the channel shim is still being confirmed | exactly one `PageLoaded` and one run of the page's own scripts - the deferred first navigation does not start the same document again | PASS |
| 53 | A panel with `ClickThroughWhenUnfocused` while it is the foreground window | a real click reaches the page, as for any other panel | PASS |
| 54 | The same panel while another window holds the foreground and the game holds the mouse | the click passes through to the window behind and the page never sees it - including the browser's own child window, which does not swallow it. The panel is raised above the other window without taking focus first, the way an owned window sits above the game; without that the click would land behind for the wrong reason and the row would prove nothing | PASS |
| 55 | Focusing the panel again | the mouse belongs to the page once more; the option is a state, not a one-way door | PASS |
| 56 | Another window in front but the cursor free, as in a menu | nothing passes through: with no captured cursor there is nothing to hand over, so the panel stays a mouse target and is usable again on the next click | PASS |
| 57 | Whether the game holds the mouse answered differently on every frame, as it is while a configuration menu and the game write the cursor in turn | the window's extended style is not touched at all: a contested answer keeps the last settled one rather than being acted on a hundred times a second | PASS |
| 58 | The same answer held steady afterwards | it takes effect, so holding still is not the same as being stuck | PASS |
| 59 | A page already showing, then a retarget the origin filter refuses | the visible page stays the target: `IsPageLoaded` stays true and sends still reach it, the same answer the browser's own refusal already got. Verified by putting the fault back - without the fix the row reports `IsPageLoaded=False` and the send vanishes | PASS |
| 60 | A panel that asked for click-through and nothing else, in front | the library still knows it as one of ours; the line that names the foreground window used to ask what the window wanted rather than which window it was | PASS |
| 61 | A panel with `Opacity = 0.5` while the mouse passes through it | it is still drawn at the alpha it asked for. Verified by putting the fault back - without the fix it is drawn at 255 | PASS |
| 62 | The same panel once the mouse comes back | still at its own alpha; nothing rewrites it after creation, so a wrong value here would last the session | PASS |
| 63 | Two channels interleaved, a latest-only channel written twice and a retained value, all posted before the page exists | the page sees `r1,a1,b1,l2,a2` - retained first, then the queue in the order it was filled, with the latest-only payload at the position of the first one that was waiting. Order holds ACROSS channels, which is what the README now promises | PASS |
| 64 | A page on one mapped host reading a second mapped host set to `DenyCors` | the CORS-checked read is refused and the ordinary sub-resource still loads - which is why a `@font-face` on a second host fails while an `img` from it works | PASS |
| 65 | The same with `Allow` | both pass | PASS |
| 66 | The same with `Deny` | both refused, including the ordinary sub-resource | PASS |
| 67 | An inline `LoadHtml` page - opaque origin - against a mapped folder | it cannot read a `Deny` folder and can read a `DenyCors` one. This is the measurement behind the default: `DenyCors` rather than `Deny` was a comment for eight releases, and it is correct | PASS |
| 67b | The same inline page's CORS-checked read of its ONLY mapped host, and then of one set to `Allow` | refused, then allowed. An opaque origin is cross-origin to every mapped host including its own, so having a single host is no protection from the web-font problem - the documentation said the opposite until this row was written | PASS |
| 68 | A page that throws, rejects and logs errors, with page diagnostics off | nothing is written. The switch is the whole contract | PASS |
| 69 | The same page with the switch on | the console error, the thrown error and the unhandled rejection each get a line, with the window named | PASS |
| 70 | A `@font-face` pointing at a host that refuses CORS-checked reads | `fonts failed to load: ProbeFont`. This is the bug the entry was written from: before it, the page rendered in a fallback face and nothing anywhere said so | PASS |
| 71 | A page reporting forty times in one burst | four lines and a notice that further reports are held back, written the moment the limit is passed rather than left for whoever speaks next - an instrument that floods the log is not one anybody reads, and a burst that stops must not take its own count with it | PASS |
| 72 | The bounds store held by another thread for longer than the two-second wait | one warning, no write, and no release of a lock never taken; the next save, with the store free, goes through quietly. Verified by putting the fault back: without the fix the write proceeds without the lock and the warning never appears | PASS |
| 73 | A retarget away from a navigation hanging on a non-routable address, three times | the new page loads every time and the hanging navigation's cancellation is never pinned on it. On runtime 151 the cancelled completion was measured to arrive BEFORE the replacing Starting, so the positional guard already covers this ordering - the row is regression coverage for an ordering the API permits, not proof of a fixed defect | PASS |
| 74 | The page itself navigating to an origin the filter refuses, while its own document stays on screen | the refused navigation's cancellation is not reported as the visible page having failed to load, and `IsPageLoaded` stays true. This is the deterministic flavour of the NavigationId defect, and the counterproof bites: without the id check the log shows a false failure report naming the healthy page | PASS |
| 75 | The channel shim rejected (via the test seam - a real browser never rejects the library's own shim on demand) | `ChannelsFailed` fires, `ChannelsAvailable` answers false, a late subscription still hears it, and the raw message bridge keeps working. The seam fakes the browser's ANSWER, so these rows prove the signal path, not the browser's ability to fail | PASS |
| 76 | A healthy overlay, same questions | `ChannelsAvailable` answers true and the event stays quiet | PASS |
| 77 | A page-initiated download (`a[download]`, clicked by script) | blocked, with a warning naming the URL, and the overlay is unharmed; with `AllowDownloads` the library stays out of it. Slot 75 on ICoreWebView2_4 and the args slots are thereby proven - a blocked download is an effect no wrong slot could fake | PASS |
| 78 | Six thousand posts while the overlay thread is deliberately stalled | the overlay is refused past its share of the queue with a warning naming it; a script answer owed during the flood is still delivered exactly once - refused-null, since the flooder's own share is what is full; the overlay works normally afterwards. Re-proven for 1.11.0 against the per-overlay share; the 1.10.0 row described the queue-wide bound, which row 83 now covers | PASS |
| 79 | `TryPost` under the same flood, six thousand times | the caller is told: the accepted count is at most the overlay's share (1,024) and the rest answer false, with the warning naming the overlay; after the stall the page receives exactly the accepted ones; a disposed handle answers false; during shutdown the answer is true, like every other call's silent acceptance. Counterproof: with the handle discarding the host's answer, no post answers false and the page receives fewer than were accepted | PASS |
| 80 | A hundred and one `TryPost`s before the page exists | all hundred and one enter the queue - true is admission, not delivery - and the page then receives the outbox's hundred, the extra one announced as dropped | PASS |
| 81 | One overlay flooding while a second, from the same process, keeps working | the flooder is refused at its share and the warning names it; the neighbour's `TryPost` still answers true, its `ExecuteScript` answers the real value rather than refused-null, its `Hide(cb)` answers `Applied`, its message reaches its page once the backlog has drained, and no warning names the neighbour. Counterproof: with the share raised to the ceiling the neighbour's commands are refused - 1.10.0's behaviour | PASS |
| 82 | The flooder disposed while over its share, after a second flood seconds after the first | it still closes - a Dispose is an obligation, outside the share - and the second flood is not warned about again: one warning per overlay, then at most one a minute. That it is named again an hour later is the same stamp read the other way, not spent an hour of the probe's time on | PASS |
| 83 | Five overlays flooding together | the ceiling above every share is reached and refused with the queue-wide warning | PASS |
| 84 | A `Show(cb)` queued behind a stall, and the handle disposed before it ran | answers `Disposed`, never `Applied`, and the window is never shown for a handle that can no longer see it. Counterproof: without the run-time disposed check the window is shown and its transition raised for the disposed handle | PASS |
| 85 | Show, Hide, Show with answers queued before the creation runs - the ordering every consumer's first open takes when commands are drained before creations | `Superseded`, `AlreadyThere`, `Applied`, exactly one visibility transition, and the window showing after creation. The other ordering - a Show that lands while the creation is waiting for its view, where 1.10.0 showed a bare popup - takes the same deferral but is not exercised here: nothing in the public API tells a window shown before its view from one shown with it | PASS |
| 86 | A `Hide(cb)` alone in the creation gap | `AlreadyThere`, no transition, and the window stays hidden through creation | PASS |
| 87 | A `Show(cb)` refused by the display mode | `RefusedFullscreen`, no transition, no failure - and `Toggle` afterwards shows, because the refusal reset the window's own desired state, which is what `Toggle` reads | PASS |
| 88 | A `Show(cb)` parked on a creation that then fails, the handle disposed from the `Failed` handler | `Failed`, exactly once - the parked request is answered before the handler runs, so the handler's Dispose cannot answer it again | PASS |
| 89 | `Show(cb)` and `Hide(cb)` on an overlay that has already failed | both answer `Failed` at once rather than wait for a view that will never come. Counterproof: without the failed-first precedence the Show parks on a view that will never come - the row's own Hide is what answers it, as `Superseded` - and the Hide answers `AlreadyThere` for a dead window | PASS |
| 90 | A `Hide(cb)` under manual dispatch, pumped once both are waiting | the pump delivers the answer and the event, the answer first - the documented order, asserted so it cannot drift unnoticed. A pump racing the two enqueues can find only the event, which is why the contract promises no order | PASS |
| 91 | A `Show(cb)` while the overlay's share of the queue is full | `QueueRefused` without entering the queue - synchronously on the calling thread in this host, which has no main-thread pump; under main-thread dispatch it arrives on the next frame like any answer | PASS |
| 92 | A `Show(cb)` in flight when shutdown begins, on a visible overlay | never answered; the close on the way out raises neither `Closed` nor `VisibilityChanged`; a script asked for afterwards is never answered. Counterproof for the `Closed` half: with the gate removed the row and row 21's Q3 see the event. The "never answered" half is guarded twice - the window's settle helper and the handle's - and was not counterproven on its own | PASS |
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
- The exclusive-fullscreen refusal as the game exercises it. Rows 31, 49 and
  87 replace the display-mode probe in a non-Unity host and prove the
  library's half; the plugin's half - reading `Screen.fullScreenMode` on the
  main thread and answering from the cache on the overlay thread - needs the
  game in exclusive fullscreen, which current Escape From Tushonka appears
  never to enter. Before 1.11.0 the probe read the property on the overlay
  thread, where Unity's binding is not thread-safe, and the throw was
  swallowed as "supported"; reasoned from the binding metadata, and a probe
  that throws now says so under Diagnose.

## Running it

The probe lives in the repository at [`tools/Probe`](../tools/Probe), and each
row above is one of its modes:

```bash
dotnet build WebOverlay/WebOverlay.csproj -c Release
dotnet run --project tools/Probe -c Release -- channels
```

Modes, in the order `tools/Probe/Program.cs` lists them: `fault-loader`,
`fault-bightml`, `fault-dispose-race`, `fault-redirect`, `script-roundtrip`,
`bounds-save`/`bounds-verify`, `latency`, `glass`, `glass-click`, `cube`,
`storage`, `vhost`, `vhost-cors`, `ordering`, `page-diag`, `bounds-locked`,
`nav-race`, `channels-dead`, `downloads`, `flood`, `dispatch`, `failure-kind`,
`vhost-fail`, `nav-reject`, `script-result`, `visibility`, `close-race`,
`shutdown-quiet`, `channels`, `shape`, `bounds-api`, `shape-guards`, `api17`,
`mixed`, `mixed-reverse`, `dcomp-first`, `footprint`, `spare-browser`,
`spare-folder`, `retained`, `latest-only`, `manual-pump`, `failed-nav`,
`generation`, `ready-load`, `click-through`, `trypost`, `share`,
`visibility-result`; no mode at all is the normal path, and `preview` is not
a row but the mode for looking at your own page.

`glass`, `glass-click`, `cube`, `shape` and `shape-guards` sample real screen
pixels and send real mouse input, so they need a desktop that is actually being
composited. Over
RDP both a disconnected session *and* an `Active` session whose window is
merely minimised return `rgb(0,0,0)` for every sample and swallow every click.
See [`tools/Probe/README.md`](../tools/Probe/README.md) for that and for
`preview`, the mode meant for building your own pages rather than for testing
the library.
