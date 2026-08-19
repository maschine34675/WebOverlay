# Answers to the consumer API wishlist

Written 2026-08-19 against `CONSUMER-API-WISHLIST.md`. Each entry gets a
verdict, the reason, and - where the answer is "yes" - what the implementation
actually costs, including the interop facts needed for it.

Two things were checked empirically rather than argued, because they decide
entries on their own:

- **Web storage in an inline page.** The probe host asked a real `LoadHtml`
  page what it has: `origin=null`, `url=about:blank`,
  `localStorage=THROWS(SecurityError)`, `sessionStorage=THROWS(SecurityError)`,
  `cookie=THROWS(SecurityError)`, `indexedDB` present as an object. Inline
  pages run in an **opaque origin**, so there is no shared storage to collide
  in - there is none at all. This refutes the premise of entry 8 and changes
  the answer to entry 1.
- **Vtable slots** for every candidate, extracted from the official
  `WebView2.h` 1.0.3485.44 C bindings, cross-checked by having the same
  extraction reproduce every slot this library already proved empirically
  (`ExecuteScript`=29, `OpenDevToolsWindow`=51, `Controller::Close`=24,
  `Controller2::put_DefaultBackgroundColor`=27,
  `Environment3::CreateCoreWebView2CompositionController`=9 - all matched).
  The house rule still stands: a slot is trusted only once an observable
  effect proves it.

## Verdicts at a glance

| # | Wish | Verdict |
|---|---|---|
| 1 | Local assets via virtual host | **Yes** - and it answers 8 as well |
| 2 | `Failed` carries a reason | **Yes**, cheapest real win |
| 3 | Main-thread dispatch | **Yes**, with one constraint the sketch misses |
| 4 | Channel messaging + request/reply | **Yes for the shim and correlation**, no for replacing the raw API |
| 5 | `ExecuteScript` result | **Yes**, one lifetime detail |
| 6 | `Closed` fires on `Hide()` | **Yes to a new event, no to changing `Closed` before 2.0** |
| 7 | Runtime geometry | **Partly** - the underlying need has a better answer |
| 8 | Storage namespace | **Refuted as stated** - solved by 1 |
| 9 | Smaller things | Mixed; one contradicts a deliberate decision |

---

## 1. Local assets via virtual host mapping - yes

This does not conflict with the "no `file://`" decision. That decision is about
authority: `file:`, `data:` and `about:` have no host, so `originOf()` returns
null and they can never be allowlisted - otherwise every authority-less
document would collapse onto one origin and trust every other.
`SetVirtualHostNameToFolderMapping` produces a *real* `https://<host>/` origin,
which the existing allowlist covers unchanged. It is the sanctioned way to do
what the wishlist wants.

Interop: `QueryInterface(ICoreWebView2_3, {A0D6DF20-3B92-416D-AA0C-437A9C727857})`,
absolute slot **71** (`ClearVirtualHostNameToFolderMapping` is 72), signature
`(LPCWSTR hostName, LPCWSTR folderPath, COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND)`.

**The part the sketch understates:** mapping assets is only half the value. A
consumer with a virtual host should stop using `LoadHtml` and `Navigate` to
`https://<mod>.assets/index.html` instead. Then the page has a real origin, and
that single change also gives it:

- same-origin asset loading, so fonts work without the weakest access kind
  (fonts are CORS-anchored; an opaque-origin page fetching them cross-origin
  would force `ALLOW`, while a page living on the host itself keeps `DENY_CORS`);
- working `localStorage` / `sessionStorage`, isolated per mod by host name -
  entry 8, for free and better than a prefix convention;
- no 2 MB ceiling (that limit belongs to `NavigateToString`, not to this
  library - it cannot be raised from here);
- real file paths in DevTools instead of one giant inline document.

`Navigate` already trusts the URL's own origin, so the trust list needs no new
concept; the mapping call just has to happen before the first navigation.
Recommended shape, close to the sketch:

```csharp
public (string Host, string Folder)[] VirtualHosts { get; set; }
```

with `DENY_CORS` as the fixed access kind, absolute folder paths only, and the
mapped origin added to `allowedOrigins` at creation.

## 2. `Failed` reason - yes

Every one of the ~20 `fail(...)` call sites already builds an exact sentence;
only the classification is missing. Worth doing as proposed, with one change:
**do not add a second event.** Two events named `Failed` are not expressible in
C#, and a differently named one splits every consumer's handling. Set two
properties before raising the existing event instead:

```csharp
OverlayFailure Failure { get; }      // RuntimeMissing, EnvironmentFailed,
string FailureMessage { get; }       // CompositionUnavailable, RendererCrashed, ...
```

Source-compatible for every existing consumer, and a handler reads them
directly. `fail()` gains an enum argument at each call site.

## 3. Main-thread dispatch - yes, but not via a MonoBehaviour inside the core

The constraint the sketch misses: **the core is Unity-free on purpose.** Only
`WebOverlayPlugin.cs` references `UnityEngine`/`BepInEx`; `OverlayHost`,
`OverlayWindow`, `WebOverlays`, `BoundsStore` and the interop layer do not.
That is what lets the probe host drive the real DLL under net9.0 outside the
game - the entire empirical test suite depends on it. A hidden `MonoBehaviour`
in the core would end that.

The same feature without the coupling: the core keeps a dispatcher hook
(`Action<Action>` plus a queue and a `Pump()`), and the BepInEx plugin
registers it and drains it from its own `Update`. `DispatchOnMainThread` then
means "queue and hand to the registered dispatcher"; with no dispatcher
registered it falls back to current behavior and logs once, rather than
silently swallowing events in a non-Unity host.

Two details worth getting right: events must keep their per-overlay order, and
anything still queued at shutdown must be dropped deliberately - a fallback
browser window opening while the game quits is exactly what `fail()` already
guards against via `OverlayHost.Stopping`. This also delivers the regression
review's REG finding: `Ready`/`Failed` get a fixed thread.

## 4. Channel messaging and request/reply - yes for the parts consumers cannot build

Split the entry. The `channel:payload` framing itself is five lines per
consumer, and standardising only that would not be worth new API surface. What
consumers genuinely cannot build cheaply is the other two thirds:

- the **JS shim**, because it needs `AddScriptToExecuteOnDocumentCreated` -
  `ICoreWebView2` v1, absolute slot **27**, no QueryInterface needed, completion
  handler IID `{b99369f3-9b11-47b5-bc6f-8e7895fcea17}`. Note it injects into
  *every* document in the WebView, iframes included; the existing message
  source filter is what keeps that safe and must stay in front of it.
- **request/reply correlation**, which needs an id table and timeouts on the
  managed side.

Constraint on the design: the raw string API stays primary and unwrapped. A
message that is not a well-formed envelope must reach `MessageReceived`
verbatim, so existing consumers and hand-written pages keep working. That means
a reserved, versioned envelope key (`__wo`), not a bare prefix, plus a
documented guarantee that consumer payloads never see it.

## 5. `ExecuteScript` result - yes

Correct that the handler already exists. One detail the sketch cannot see: the
completion callback is currently a **single reused `ComCallback` field**, which
is fine while the result is discarded and wrong the moment results are routed -
two overlapping calls would resolve to one callback. Per-call callbacks are
required, and they must follow the existing lifetime rule (a `ComCallback` is
freed only when the native side holds no reference; freeing early crashes the
process). Otherwise a small change.

## 6. `Closed` on `Hide()` - yes to a new event, not to changing the old one

The finding is right and the workaround it describes (polling `IsVisible`) is
real. But `Closed` is public, released, and consumed outside this machine;
silently narrowing it in a minor version would break consumers that rely on it
firing for a hide. So add

```csharp
event Action<bool /*visible*/> VisibilityChanged;
```

now, document `Closed`'s current meaning prominently, and narrow `Closed` to a
real close in **2.0**, bundled with whatever other breaking change is worth it.

## 7. Runtime geometry - partly, and the real need has a better answer

`SetBounds` itself is unobjectionable; the only design interaction is bounds
persistence, which assumes user-owned geometry. A rule that keeps it coherent:
a programmatic `SetBounds` is applied but never persisted (the store writes on
`WM_EXITSIZEMOVE`, which a programmatic move does not raise), and an explicit
`SetBounds` wins over a remembered spot for that session.

The case that actually motivates it - an interactive HUD swallowing the mouse
over its whole rectangle - deserves a better fix than resizing the window:
**hit-test regions.** The window procedure answers `WM_NCHITTEST` with
`HTTRANSPARENT` outside the rectangles the page declares as live, so clicks
pass to the game everywhere else while the HUD keeps covering the screen and
placing content with CSS. That is what a QuestMarkers-style overlay needs, and
no "compact/wide toggle" ever gets it. It needs a probe first: whether
`HTTRANSPARENT` routes correctly for the composed `WS_EX_NOREDIRECTIONBITMAP`
window has not been measured.

## 8. Storage namespace - refuted as stated

There is no collision, because there is no storage. Measured on a real
`LoadHtml` page: opaque origin, and `localStorage`, `sessionStorage` and
`document.cookie` each throw `SecurityError` on first touch.

Two consequences:

- A `StorageNamespace` property would hand consumers a prefix for an API they
  cannot call. Dropped.
- **Any consumer page touching `localStorage` without a `try`/`catch` dies at
  that line** - the exception aborts the surrounding script, which shows up as
  a silently half-initialised UI rather than a visible error. Worth stating in
  the README.

Consumers that want to persist UI state today have two working options: send it
to the mod and let the mod store it (what the bounds store does), or take entry
1 and navigate to a real origin, where `localStorage` works and is isolated per
mod by host name.

## 9. Smaller things

- **Null validation, throwing:** contradicts a deliberate decision. The library
  never throws into the game - `Create` returns null, everything else logs and
  degrades - because consumers call it from `Update()` behind a soft-dependency
  gate, where an exception is a broken raid rather than a helpful error. The
  useful half of the finding stands: silently ignoring `LoadHtml(null)` hides a
  programmer error, so those cases should **log a warning naming the call**
  instead of disappearing.
- **Outbox:** `IsReady` is trivial to expose (`pageReady` exists internally) and
  is the right answer for a streaming consumer - hold off rather than overfill.
  Making the limit an option is fine; the current behavior warns and drops the
  newest, which is at least not silent.
- **Document-loaded signal:** free. `add_NavigationCompleted` is already wired
  and already flips the internal `pageReady`, so a `PageLoaded` event needs no
  new interop. (`add_DOMContentLoaded` exists too - `ICoreWebView2_2`,
  `{9E8F0CF8-E670-4B5E-B2BC-73E061E3184C}`, slot 64 - but it is not needed for
  this.)
- **Keyboard for interactive HUDs:** agreed, stays on the roadmap.
- **`Method<T>` allocation:** measured in context this is noise - a per-frame
  `Post` allocates a larger string before it ever reaches the delegate. Worth
  caching if it becomes convenient, not as a goal.

## Suggested order

1. **2** (`Failed` reason) plus **9**'s `PageLoaded` and `IsReady` - hours, no
   new interop, immediately useful to every consumer.
2. **3** (main-thread dispatch) - removes the boilerplate the wishlist opens
   with, and the dispatcher hook is small.
3. **1** (virtual hosts) - the one structural change; also delivers 8 and the
   2 MB ceiling.
4. **5** (script results) and **6** (`VisibilityChanged`).
5. **4** (shim and request/reply) - biggest surface; do it once 1 and 3 have
   settled how pages are written.
6. **7** - hit-test regions after a probe, `SetBounds` whenever needed.
