# Answers to the consumer API wishlist

**Status:** entries 1, 2, 3 and the `PageLoaded`/`IsPageLoaded` half of 9
shipped in v1.4.0; entries 5 and 6 in v1.5.0; entries 4 and 7 in v1.6.0 - each
verified by the probe host (`vhost`, `dispatch`, `failure-kind`,
`script-result`, `visibility`, `channels`, `shape`, `bounds-api` modes).
Entry 8 and the throwing half of 9 stay declined for the reasons given.

Entries 10-22 arrived later, from ModProfiler and QuestMarkers; they are
answered at the end of this document. Of those, 10, 12, 13, 18, 19 and 21
shipped in v1.7.0, and 11, 16 and 17 in v1.8.0 - 17 as the two halves the
library can honestly deliver. What remains is documentation (14, 22) and
giving the probe host a home (20).

**One correction to this document:** entry 7 below proposes hit-test regions
as the better answer, keeping the picture and giving up only the mouse. That
does not work, and the measurement is in the entry: `HTTRANSPARENT` passes a
click only to windows of the same thread, which the game never is. What
shipped instead is `SetShape`, which delivers the same use case honestly -
picture and mouse together.

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
over its whole rectangle - deserves a better fix than resizing the window.
**The hit-test regions proposed here do not deliver it, and that was measured
rather than assumed** (`regions` probe, since replaced by `shape`):

- `WM_NCHITTEST` returning `HTTRANSPARENT` is delivered and evaluated
  correctly - the log shows the right decision for every point - but the click
  then reaches *nothing*. That matches the documented rule: `HTTRANSPARENT`
  passes the message to windows **of the same thread**, and the game is never
  the same thread as the overlay.
- `SetWindowRgn` does route clicks to whatever is behind, across processes -
  but it clips the composed picture to the same region (measured: the block
  outside the shape stopped being painted).

So Windows offers picture-or-mouse, not both, and the honest feature is the
one that says so: **`SetShape`** cuts the overlay down to a set of rectangles
for picture and mouse together, driven either by the mod or by the page
(`overlay.setShape([element, ...])`). For a HUD whose content *is* its
interactive area - a panel with widgets - that is exactly the wanted result;
for one that wants to paint outside what it can be clicked in, it is not
achievable at all, and no API should pretend otherwise.

`SetBounds` shipped as proposed, with the persistence rule above.

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

---

# Answers to the second and third rounds

Written 2026-08-22 against entries 10-22, from ModProfiler and QuestMarkers.
Same rules as above: a verdict, the reason, and - where it is a yes - what the
implementation actually costs. Three entries assume something the library does
not have, and those corrections are the useful part.

## Verdicts at a glance

| # | Wish | Verdict |
|---|---|---|
| 10 | Deferred request replies | **Yes**, an overload |
| 11 | Who pays for dispatched callbacks, manual pump | **Yes to both halves**, the pump needs a per-overlay queue |
| 12 | Free the cursor while a framed window has focus | **Yes**, in the plugin, under a clearer name |
| 13 | `VirtualKey(KeyCode)` helper | **Yes**, trivial |
| 14 | Soft-dependency guide | **Yes** - and the version gate is the part worth writing down |
| 15 | Checked, needs nothing | Agreed; one README sentence |
| 16 | Retained messages | **Yes** |
| 17 | Latest-wins delivery | **Partly** - the library owns no queue where the entry assumes one |
| 18 | `Show()` refuses exclusive fullscreen | **Yes** |
| 19 | Tell the page which transparency it got | **Yes**, same mechanism as 21 |
| 20 | The probe host belongs in the repo | **Yes** - it also closes a gap this repo has been carrying |
| 21 | Shared design tokens | **Yes**, bundled with 19 |
| 22 | HUD lifecycle traps | **Yes**, as documentation |

---

## 10. Deferred request replies - yes

`OnRequest(channel, Func<string, string>)` does force the answer out of the
dispatched callback, and the page side has been asynchronous from the start.
The window already hands the routing layer a `reply` action with once-only
semantics; only the public shape assumes a return value.

Add an overload rather than change the existing one:

```csharp
void OnRequest(string channel, Action<string /*payload*/, Action<string> /*reply*/> handler);
```

The reply may be called from any thread, at any later time. What already holds
keeps holding: a handler that throws answers `null`, a handler that never
replies leaves the page to its own timeout, and a reply that arrives after that
timeout is dropped by the shim rather than resolving a stale promise. Worth
documenting explicitly, since with an asynchronous handler a late reply stops
being a theoretical case.

## 11. Who pays for dispatched callbacks - yes, and the pump is worth it

The measurement is right, and it is right by construction: `DispatchOnMainThread`
delivers from `WebOverlayPlugin.Update`, so a consumer's handler runs inside the
library's frame slice and any profiler bills it there. That is a documentation
failure first - the option's own text talks about threading and never about
whose budget the work lands in.

The manual pump is worth having, with one correction to the sketch: the queue
is **one process-wide queue in the host**, not per overlay, so `PumpEvents()`
cannot drain "this overlay's events" today. It needs a per-overlay queue, with
the plugin's pump draining the overlays that asked for main-thread delivery.
That is a contained refactor of the dispatch path, not a redesign.

Second correction, on the API: shipping `Dispatch` next to the existing
`DispatchOnMainThread` would leave two knobs for one decision. The bool stays
(it is released API) and becomes a documented alias that sets the enum, with
the enum as the way it is described from now on.

One thing the entry does not mention but the implementation must: in `Manual`
mode nothing may be delivered until the consumer pumps, so an overlay that is
never pumped queues forever. The existing cap (4096, then drop with one
warning) already covers it; that cap should be documented rather than left as a
surprise.

## 12. Free the cursor - yes, in the plugin, under a better name

This is the library fixing its own side effect: a framed overlay takes the
foreground on `Show`, and the game keeps the cursor captured while it is not
focused. Every consumer that opens a framed window mid-raid hits it, and each
would write the same `Update`/`LateUpdate` pair.

It belongs in the plugin, where `Cursor` and `Screen` already live, so the core
stays free of Unity - which the whole empirical test suite depends on. The core
needs to expose only "is an overlay that asked for this currently visible",
which is the same small bridge entry 18 needs in the other direction.

`FreeCursorWhileFocused` reads as "while the overlay is focused", which is not
what it does. `FreeCursorWhileShown` says it: while this overlay is visible and
the game window is not focused, the cursor is released; the moment the game has
focus again the library stops touching it and the game relocks on its own.

Muting the game's input commands stays out, as proposed - that is a Harmony
patch against a game type and has no place in a library that references no game
assembly.

## 13. `VirtualKey(KeyCode)` - yes

Two consumers carrying the same twenty-line table, the second copy written only
because a review caught a hard-coded close key, is exactly the argument. It is
Unity-typed, so it lives in the plugin next to `IsDisplayModeSupported`, and it
is a table plus two lines of code.

## 14. Soft-dependency guide - yes, and the version gate is the new part

The closure trap has been folklore in this codebase since CraftQueue's stack
sizes silently collapsed to 1; writing it down is overdue. The genuinely new
rule is the version gate, and it is a direct consequence of how these four
releases were built: minors are additive, so a gate body that touches a 1.6
member fails at JIT time on a 1.3 install - presence is no longer the question,
`Metadata.Version >= new Version(1, 6, 0)` is.

`docs/SOFT-DEPENDENCY.md` with the pattern, the two shipping gates as
references, the Mono.Cecil build check, and one sentence in the changelog
contract saying minors are additive so consumers gate on "at least X.Y".

## 15. Checked, needs nothing - agreed

All three hold. The third deserves the README sentence: a page opened in an
ordinary browser has no `window.overlay`, so a page meant to be openable both
ways needs one guard line.

## 16. Retained messages - yes

The trap is real and the library causes it: after a renderer crash the page is
reloaded silently, `PageLoaded` fires again, and a mod whose configuration is
guarded by a dirty check never resends. A consumer *can* fix this itself - the
fix is one `PageLoaded` handler - but nobody writes it before a player reports
that a HUD reverted mid-raid.

```csharp
void Post(string channel, string payload, bool retain);
```

Two implementation notes that are not in the sketch. The replay has to happen
**before the outbox is flushed** on load, or a message the mod sent while the
page was loading would be overwritten by the older retained one. And the
retained set must be cleared by `LoadHtml`/`Navigate`, which already clear the
outbox for the same reason: the page changed, the old state is not its state.

## 17. Latest-wins - partly, and the entry assumes a queue that does not exist

The symptom is real, the mechanism is not. Once the page is loaded, `Post` does
not queue anywhere in the library: it calls `PostWebMessageAsString` straight
through to the browser. The only queue this library owns for outbound traffic
is the pre-load outbox. So "replace the still-undelivered payload in the
delivery queue" cannot be honoured for the case the entry describes - the
frames are already in Chromium's IPC queue, and reaching into it is not
something a caller can be given.

What can be delivered, honestly:

- **Collapsing in the outbox.** A latest-only message replaces an undelivered
  one on the same channel while the page is still loading. That is a real case
  (an overlay created with a per-frame feed already running), just not the one
  the entry describes.
- **Collapsing in the page**, which is where the queue actually is. The shim
  can offer `overlay.on(channel, fn, { latest: true })`: buffer the newest
  payload and deliver it once per animation frame. That fixes the visible
  symptom - markers trailing the camera - because the page stops rendering
  stale frames it has already superseded.

Both are worth having and they compose with 16 as flags on the same overload.
What should not be shipped is an API that promises flow control the library
cannot perform. Worth saying in the same place: a consumer streaming per frame
should send from `Update` and let the page coalesce, rather than assume delivery
is free.

## 18. `Show()` refuses exclusive fullscreen - yes

The argument is the one that decides it: every consumer has at least two show
paths and will get one of them wrong, and the failure mode is the game
minimising itself at raid start. The library knows about the window; the
consumer's check should be a courtesy, not load-bearing.

The mechanism already exists in shape: the plugin registers the main-thread
pump into the Unity-free core, and a display-mode probe registers the same way.
`Show()` then refuses, logs once per overlay, and leaves the overlay hidden. It
must not raise `Failed` - exclusive fullscreen is a setting the player can
change back, not a broken overlay - and `IsDisplayModeSupported` stays public
for consumers that want to explain the situation in their own UI.

## 19. Tell the page which transparency it got - yes

The window knows this before it injects anything (`usesComposition` is decided
before the window exists), so both halves are cheap: a `Transparency` property
on the handle, valid once `Ready`, and the shim stamping
`wo-composed` / `wo-chroma` on the root element so a stylesheet can adapt with
no mod code at all. The shim is one constant today; parameterising it at
injection is a string replacement.

## 20. The probe host in the repo - yes, and it closes a gap of ours

This one is not really a wish, it is a finding. `docs/FAULT-TESTS.md` currently
ends with "the probe source lives outside the repository - re-create it from
this table", which means the evidence behind twenty-eight rows and every proven
vtable slot lives in a temp folder. That is a bad place for the thing that
proves the library works.

Bringing it in as `tools/PagePreview` with the consumer-facing mode described
gives a consumer a way to see its page in a real composed HUD without starting
the game, and gives this repository its own test harness back. It is a net9
console project that is not part of any release zip, and it needs a pass to
drop session-specific modes and paths to other mods' pages before it moves.

## 21. Shared design tokens - yes, with 19

`docs/STYLE.md` is worth writing on its own. The injected variables are the
same mechanism as 19 - the shim already has to be parameterised - so they ship
together or not at all. Opt-in, because a mod that wants its own look should
not have to fight a theme it did not ask for.

## 22. HUD lifecycle traps - yes, as documentation

Most of these are game-specific, which is fine: this library exists for one
game and its README already says so. They belong in the HUD section, where a
page author looks, with the two general ones - re-sending page configuration
until 16 exists, and the exclusive-fullscreen guard until 18 exists - marked as
temporary so they can be deleted when those ship.

The inline-`style.opacity`-versus-class trap is not a library matter, as the
entry says, but it cost a review round and belongs with the rest.

## Suggested order

1. **10**, **13**, **19**, **21**, **15**'s README line, and the documentation
   half of **11** - all small, none of them touching the dispatch or delivery
   paths.
2. **18** and **12** - both are the same small bridge from the plugin into the
   core, so they are one piece of work.
3. **16** and **17** - the delivery-path change, with 17 scoped to what the
   library can actually promise.
4. **11**'s manual pump - the per-overlay queue refactor, worth doing after 16
   and 17 have settled how delivery is described.
5. **14**, **22**, **20** - the documentation and the probe host, which is the
   one that also pays this repository back.
