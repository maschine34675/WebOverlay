# Fault-injection matrix

Executed against the built `Anvil-WebOverlay.dll`, driven by a standalone host
outside the game. Every scenario runs the real library code path; the probe
verifies outcomes through the public API and the library's log lines.

Rows 1-9 were run on 2026-08-01 for v1.0.0 (WebView2 runtime 151.0.4129.59)
and re-run unchanged for v1.4.0; rows 10-31 were added for v1.3.0 to v1.7.0
(runtime 151.0.4129.93).

| # | Scenario | Expectation | Result |
|---|---|---|---|
| 1 | `WebView2Loader.dll` missing | first handle raises `Failed` (latched), no hang; later `Create` returns null | PASS |
| 2 | Inline HTML over the 2 MB WebView2 limit | navigation rejected with a log, buffered sends dropped, no crash | PASS |
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

Two modes record a defect rather than a guarantee: `mixed` shows that a
windowed overlay cannot be created while the only live overlays are transparent
ones, and `mixed-reverse` shows the same combination working in the other
order. They are expected to fail until that is fixed; see the changelog.

Not automated (manually covered in game during development, or accepted):

- Renderer crash / unresponsive renderer (recovery + terminal `Failed` paths
  are code-reviewed; deliberately killing a renderer needs browser internals).
- Selective mouse transparency without clipping the picture: measured as
  impossible on Windows, see `SetShape` in the README and entry 7 of
  `docs/CONSUMER-API-WISHLIST-ANSWERS.md`.
- A failing browser-environment HRESULT (the classification and the "first
  cause wins" rule are code-reviewed; the callback cannot be forced to fail
  from outside).
- Shutdown during environment start (guarded by `stopping` checks; exercised
  implicitly at every game exit).
- Freeing the cursor while the game holds it: needs Unity and a game that
  captures the mouse, so it is covered in game rather than here.
- The `KeyCode` to virtual-key table, which is a table.

The probe source lives outside the repository (a throwaway harness); re-create
it from this table when re-running the matrix for a future release. Its modes
map to the rows above: `fault-loader`, `fault-bightml`, `fault-dispose-race`,
`fault-redirect`, `script-roundtrip`, `bounds-save`/`bounds-verify`, `glass`,
`glass-click`, `normal`, `cube`, `storage`, `vhost`, `vhost-fail`,
`nav-reject`, `dispatch`, `script-result`, `visibility`, `close-race`,
`shutdown-quiet`, `channels`, `shape`, `bounds-api`, `shape-guards`, `api17`.
