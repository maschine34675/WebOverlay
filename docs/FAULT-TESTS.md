# Fault-injection matrix

Executed against the built `Anvil-WebOverlay.dll` on 2026-08-01 (commit of the
v1.0.0 release), driven by a standalone host outside the game (WebView2
runtime 151.0.4129.59). Every scenario runs the real library code path; the
probe verifies outcomes through the public API and the library's log lines.

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

Not automated (manually covered in game during development, or accepted):

- Renderer crash / unresponsive renderer (recovery + terminal `Failed` paths
  are code-reviewed; deliberately killing a renderer needs browser internals).
- Shutdown during environment start (guarded by `stopping` checks; exercised
  implicitly at every game exit).
- Exclusive fullscreen (guarded by `WebOverlayPlugin.IsDisplayModeSupported`).

The probe source lives outside the repository (a throwaway harness); re-create
it from this table when re-running the matrix for a future release.
