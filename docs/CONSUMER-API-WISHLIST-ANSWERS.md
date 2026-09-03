# Answers to the consumer API wishlist

**Status:** entries 1, 2, 3 and the `PageLoaded`/`IsPageLoaded` half of 9
shipped in v1.4.0; entries 5 and 6 in v1.5.0; entries 4 and 7 in v1.6.0 - each
verified by the probe host (`vhost`, `dispatch`, `failure-kind`,
`script-result`, `visibility`, `channels`, `shape`, `bounds-api` modes).
Entry 8 and the throwing half of 9 stay declined for the reasons given.

Entries 10-22 arrived later, from ModProfiler and QuestMarkers; they are
answered at the end of this document. Of those, 10, 12, 13, 18, 19 and 21
shipped in v1.7.0, and 11, 16 and 17 in v1.8.0 - 17 as the two halves the
library can honestly deliver. The three without an API half followed as
repository work: 20 as `tools/Probe`, 14 as `docs/SOFT-DEPENDENCY.md`, and 22
as the README's "What a HUD has to decide for itself". Entries 23-27 are
answered below; 23, 25 (page side) and 26 shipped in v1.9.0. Entries 28 and
29 arrived later from CombatLog and are answered at the end - together with
two things found underneath them that neither entry asked about. All four
shipped in v1.11.0.

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

**Done.** The guide also carries a table of which member arrived in which
version, since that is what a consumer actually needs to set `MinimumVersion`
from, and the changelog now states the additive rule in its own contract.

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

**Done**, as `tools/Probe` - the name says what it mostly is, and `preview` is
one mode of it rather than the whole program. The modes that drove QuestMarkers'
and ModProfiler's own pages stayed behind, where they belong. Two things
surfaced during the move that the throwaway version had been hiding: the two
modes that test a missing `WebView2Loader.dll` were only passing because that
harness happened not to copy the loader, so they now stage the incomplete
folder themselves and put it back afterwards; and a page that listens to raw
`chrome.webview` messages *and* uses channels sees every channel envelope
twice, which the sample page now demonstrates stepping over.

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

**Done**, as "What a HUD has to decide for itself" in the README's HUD
section. Two of the five entries did not survive the wait: re-sending page
configuration is now `PostOptions.Retain` (1.8.0) and the fullscreen guard is
inside `Show()` (1.7.0), so both are recorded there as closed rather than as
advice.

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

---

# Answers to the fourth round

Written against 1.8.10. Two of these were checked against the WebView2 IDL
itself (`WebView2.idl` from the 1.0.3485.44 SDK package, which is on this
machine) rather than against our own comments, because both entries turn on
what the browser actually does.

## Verdicts at a glance

| | | |
|---|---|---|
| 23 | access kind per virtual host | **yes** - but as a three-value enum, not a flag |
| 24 | set an option by name | **no** - and the real gap is documentation plus tooling |
| 25 | page diagnostics | **partly** - the page-side half now, the browser-side half later |
| 26 | channel ordering | **yes**, documentation - the guarantee is stronger than the entry assumes |
| 27 | binary payloads / geometry trap | **documentation** for both |

## 23. Access kind per virtual host - yes, as an enum

Every factual claim in the entry checks out, and the primary source settles the
part our own comment only asserted. `WebView2.idl:629-632` gives the table:

| access context | DENY | ALLOW | DENY_CORS |
|---|---|---|---|
| from DOM - `src` of img, script, iframe | deny | allow | allow |
| from script - fetch or XHR | deny | allow | deny |

A `@font-face` is fetched in CORS mode by specification, so under `DENY_CORS`
every face from a second host is refused. The diagnosis is right, and the
consequence - one host for everything, which for ScopeRangefinder is the whole
plugin folder including its DLL and its user JSON - is exactly what that mod's
own review flagged and could not fix.

**But not as sketched.** `public bool AllowCrossOrigin` can only reach ALLOW.
There are three kinds, and this repository has already been asked once for the
*stricter* one: WOV-1812 in the 2026-08-22 code review (a dated report kept outside the
repository) recommended
DENY. A bool forecloses that half of the design space permanently, under a
contract that says minor releases never change what a consumer depended on. So:

```csharp
public enum HostAccess { DenyCors = 0, Deny, Allow }   // DenyCors = today
public sealed class VirtualHost { ...; public HostAccess Access { get; set; } }
```

Two corrections before any of this becomes documentation:

- ALLOW is a property of the host being **read**, not of a pair of hosts.
  Setting it on the page's own host does nothing, and a name like
  `AllowCrossOrigin` invites precisely that mistake.
- "The trust model is unchanged" is not true. ALLOW is
  `Access-Control-Allow-Origin: *` for that folder - to every origin in the
  overlay, including any remote origin the mod put in `AllowedOrigins`. Small,
  but not nothing, and it belongs in the XML doc.

**The strongest thing in the entry is not the feature.** `docs/FAULT-TESTS.md`
has no row on the cross-origin behaviour of a mapping in either direction, and
the reason we serve DENY_CORS rather than DENY has never been measured - it is
a comment. Both are cheap to settle with the probe's existing two-host support,
and both should be settled *before* the API is chosen, not after: if DENY turns
out to work for a `LoadHtml` page's own assets, the default itself is wrong.

Cost, all additive: one property, one enum, the deep copy in `WebOverlays.cs`
(which must carry the new field or it never reaches the window), the call site
in `applyVirtualHosts`, a mode registered in both `tools/Probe/Program.cs` and
`NewApi.cs`. No new interface and no new slot - the mapping call already takes
the access kind, on a slot proven by row 12.

## 24. Set an option by name - no

The pattern the entry describes is real, and it cost me a mistake in this very
release: ScopeRangefinder's soft-dependency audit rejected a helper of mine that
took `OverlayOptions` as a parameter, correctly, because a parameter type is
part of the signature. So the complaint is earned. The proposed cure does not
work:

- **`Set` is itself a member subject to the rule it wants to retire.** A
  consumer with a 1.7.0 floor calling `Set` needs a floor of at least whatever
  version introduced `Set` - which is *higher* than the 1.8.8 the separate body
  was there to avoid. It cannot help the case that motivated it, and it can
  never help retroactively.
- **It answers the wrong question.** `Set("ClickThroughWhenUnfocused", true)`
  asks whether the key exists. That option existed in 1.8.6 and only *behaved*
  from 1.8.8; on 1.8.6 the call would have succeeded and handed the player a
  panel that could not be clicked. The version comparison is what prevents that,
  and `Set` does not replace it - so it removes one of the three moving parts,
  not three.
- It also freezes every property name as public API that no compiler checks,
  under a contract promising minors break nothing.

And the cost is smaller than the entry thinks: the straddle body is **one per
version tier, not one per option**. Every consumer carries exactly one today,
and would still carry one if 1.9 added five options.

What the entry is really reporting is that the pattern is undocumented. That
part is accepted:

1. `docs/SOFT-DEPENDENCY.md` gains a rule naming the straddle body, with the
   `object` parameter and the reason for it - the three consumers converged on
   the same ten lines independently, which is a sign it should have been written
   down rather than rediscovered.
2. Its version table extends to patch granularity, and says which versions were
   never released - the 1.8.4-1.8.7 gap is exactly the kind of thing a consumer
   gets wrong.
3. `Audit-SoftDependency.ps1` moves into this repository beside `tools/Probe`.
   It lives in one consumer today and it caught a real defect here; a rule the
   library asks every consumer to follow should ship with the check for it.

## 25. Page diagnostics - the page-side half, now

The premise needs one correction: **the library cannot see the refused font at
all.** That was Chromium's own CORS check on a `DENY_CORS` mapping, a decision
that never leaves the browser. `NavigationStarting` fires for document and frame
navigations, so our origin filter never sees a sub-resource. (That last sentence
is the likely answer rather than a measured one - it should be settled with a
row, not asserted, and the row is cheap.)

So the console half of the entry would not have caught its own example either -
`document.fonts` would.

**Recommended scope, now:** page-side reporting through the shim that already
exists. A reserved channel, an `else if` beside the shape branch in
`routeChannelMessage`, a rate limiter in `LogDiagnostic`, and a documented
snippet consumers paste into their page - `window.onerror`, a console hook,
`document.fonts.ready`. About thirty lines, no interop, one probe mode. One
honest bound: `isMessageAllowed` gates it, so this reports from documents the
origin filter already trusts and cannot be "anything that goes wrong inside the
document".

It needs a **new** config key. Renaming the released `Log cursor state` would
orphan every existing `.cfg` entry.

**Deferred, and cheaper than it first looked:** the DevTools route
(`CallDevToolsProtocolMethod` plus `GetDevToolsProtocolEventReceiver`). It is
four or five new slots and two new IIDs, but the slot numbers do not have to be
guessed - the SDK's own raw interfaces are in `Microsoft.Web.WebView2.Core.Raw.*`,
and member order in that metadata is the vtable order. I checked one:
`ICoreWebView2DevToolsProtocolEventReceiver` gives `add_` then `remove_`, i.e.
slots 3 and 4. They still need proving by a row - that rule does not bend - but
the risk is derivation-then-confirmation, not a guess that can kill the process.

Also: rejecting `WebResourceResponseReceived` outright was wrong. The event args
carry the response headers, so a missing `Access-Control-Allow-Origin` is
detectable host-side. Worth weighing against the CDP route when this is picked
up.

## 26. Channel ordering - yes, and it is stronger than assumed

Established by reading the send path; it should get a row before it goes in the
README, because a documented guarantee is a promise.

Every `Post` - on any thread - goes through `OverlayHost.Post`, one queue
drained on the overlay thread. There is no per-channel queue. So:

> Messages a mod posts are delivered to the page in the order it posted them,
> across channels and not merely within one. Before the page is ready they wait
> in a queue and are flushed in the same order. `Retain` values replay first,
> when the page loads, in the order the mod first set each channel.
> `LatestOnly` is the one exception: a newer message replaces the one still
> waiting on that channel and takes its place in the queue, so the page sees the
> newest payload at the position of the first one that was waiting. If the queue
> overflows, sends are dropped with a warning rather than reordered.

The studio's `set`-then-ask sequence is therefore correct as written.

## 27. Smaller things

**Binary payloads - documentation, not API.** A virtual host is a folder, read
at request time. A mod that wants to hand the page a PNG can write it into the
folder it already maps and reference it by URL; nothing in the library needs to
change, and it avoids the base64 round trip the entry is complaining about.
Publishing bytes that never touch disk would need `WebResourceRequested`
interception - a real feature, worth its own entry if the file route proves
inadequate.

**The default geometry trap - yes, documentation.** A window with no size of its
own gets 80% x 85% of the game's client area, centred, which is exactly where a
first-person game reads mouse movement from. That cost the 1.8.6-1.8.8 series to
find and is invisible from inside the library. It goes in two places: the
`Width`/`Height` doc comments, where someone deciding not to set them will read
it, and the README beside `ClickThroughWhenUnfocused`. Agreed too that
ScopeRangefinder should simply set its own size.

## Suggested order

1. **26** and **27b** - documentation, both true today, both cheap.
2. **The two missing probe rows behind 23** - the cross-origin behaviour of a
   mapping in each direction. They decide whether 23's default is even right.
3. **23** once those rows exist.
4. **24 as documentation and tooling** - the audit belongs here.
5. **25 page-side**, then the browser-side half if the page-side proves too
   narrow.

---

# Answers to the fifth round

Written against 1.10.0. Every factual claim in both entries was checked
against the code, and the first design for entry 29 was taken apart before it
reached this page - the refuted version is recorded below, because the next
person to think of it will think of it for the same good-sounding reasons.
Two findings that neither entry asked about are answered at the end: the
command queue both entries lean on is shared by every mod in the process, and
the exclusive-fullscreen refusal that entry 29 wants reported very probably
never fires in the game.

## Verdicts at a glance

| | | |
|---|---|---|
| 28 | `TryPost` | **yes** - three additive overloads; two answers differ from the sketch |
| 29 | visibility settlement | **yes** - as `Show(cb)`/`Hide(cb)`; the deeper fix that suggests itself is refuted |
| - | the shared command queue | **found underneath both** - process-wide, the 1.10.0 comment over-promises; a per-window share is recommended |
| - | the fullscreen refusal | **probably dead in the game** - one in-game check, then a cheap fix |

## 28. `TryPost` - yes, with two corrections to the sketch

Every claim checks out. The three `Post` overloads route through the handle's
private `post()` (`WebOverlays.cs:1272-1280`), which discards the bool the host
returns. `remember()` runs *inside* the queued action (`OverlayWindow.cs:1895-1924`),
so a command the queue refused never reaches the retained set - the entry's
"Retain cannot repair it" is exactly right, and it is worse than the entry
says: after the silent renderer-crash reload the page is replayed the
*previous* payload, so the consumer's bookkeeping and the page diverge for the
rest of the session. CombatLog's gate returns true whenever a handle exists
(`WebOverlayGate.cs:212-226`), `_lastPayload` is set, and the equality check
suppresses every retry.

Two facts the entry does not know, and which decide the shape of the answer:

- **The queue is one static queue for every handle of every mod**
  (`OverlayHost.cs:24`). CombatLog posts one `LatestOnly` frame per second and
  cannot fill 4,096 slots on its own; the flood that drops its post is another
  mod's, or a stall of the overlay thread. See "Underneath both" below.
- **The overflow warning fires once per process and is never reset**
  (`OverlayHost.cs:224, 265`). A second flood - a different mod, an hour later
  - logs nothing. A return value would be the only per-call signal there is.

**Shape:** `bool TryPost(string message)`, `bool TryPost(string channel,
string payload)`, `bool TryPost(string channel, string payload, PostOptions
options)`, mirroring the three `Post` overloads. Adding members to
`IWebOverlay` in a minor has precedent (`PumpEvents` 1.8.0, `ChannelsFailed`
and `ChannelsAvailable` 1.10.0); the only implementer is `OverlayHandle`, and
no shipping consumer reflects over the interface's members - every gate casts
`as WebOverlay.IWebOverlay` inside a NoInlining body.

**Where the answer differs from the sketch:**

1. **Shutdown answers `true`, not `false`.** Every accept-and-swallow path in
   the library answers true while `Stopping` (`OverlayHost.TryPost:260`,
   `DispatchToMainThread:472`), and that true is load-bearing: it is what keeps
   `Request` and `ExecuteScript` from answering null - and waking a fallback -
   during teardown (`WebOverlays.cs:1181-1189, 1226-1227`). A second `TryPost`
   with the opposite answer to the same question is a trap for whoever aligns
   them later, and a consumer has no "later" at shutdown anyway. Documented as:
   during shutdown the library accepts everything and does nothing, like every
   other call. Pinned by a row through the shutdown seam the probe already has.
2. **"Failed" is not in the false set.** `Failure` is a plain auto-property
   written on the overlay thread (`OverlayWindow.cs:389`) and would be read
   unsynchronised; a stale `Unknown` answers true and the command is then
   refused silently by `refuseAfterFailure` - which is just the "true is not
   delivery" case the entry already concedes. `Failed` is latched and the
   consumer already knows. So false means exactly two things: the handle was
   disposed before the call, or the command queue refused it.

**What the documentation of `true` has to say,** because consumers will read
it as delivery: a post that entered the queue can still be lost to the
100-entry outbox before the page loads (non-retained sends), to a document
that is not the mod's target, to a retarget (`LoadHtml`/`Navigate` forgets
both outbox and retained set), to a renderer crash without `Retain`, to a
failure or close that lands before the command runs, and to the browser
itself - `PostMessageToPage` discards the HRESULT of `PostWebMessageAsString`
(`OverlayWindow.cs:2057`), so the library does not know either. `Retain` is
what makes a true durable across reloads. False means retry later, never fall
back; a retry is a new send at a new position, so a consumer that posts on
several channels and retries one has reordered its own traffic (the library's
cross-channel guarantee is untouched). Until the per-window share exists, a
false may be another mod's doing.

**The alternative that was weighed and is not the answer:**
`ExecuteScript(script, result)` is *already* an exactly-once delivery that
answers null on queue refusal, failure, outbox overflow, non-target document,
renderer crash and dispose, and returns the page's own value when it ran -
strictly stronger than "entered the queue", with no library change. CombatLog
could ship one page function today on 1.10.0 and call it with the report. It
is not the answer because it needs a page-side function (the entry rejects a
protocol in the page), loses `Retain`'s replay after the silent crash reload
(recoverable by clearing `_lastPayload` on `PageLoaded`, which fires on that
reload too), and because it makes the consumer route around a fact the
library already holds and throws away. But it is the bridge until 1.11.0.

**Rejected:** coalescing `Retain` posts into a per-handle map synced by one
work item. A retained post merging into an already-queued sync item is
delivered before a plain post queued in between - a cross-channel reorder
against entry 26's guarantee. An ordering-preserving per-handle FIFO variant
exists, but it still needs a bound, hence a refusal, hence this entry; it
belongs with the queue work below, not in place of `TryPost`.

**Rows:** extend `flood` - during the stall, count trues and falses (falses
above zero, trues at most 4,096, one warning); after the stall the page's
echoes equal the trues exactly, which is the first assertion that ties true to
delivery on a healthy page and false to non-delivery; `TryPost` on a disposed
handle is false; the shutdown seam pins the true. A second row proves the
disclaimer: a fresh overlay, 101 raw `TryPost`s before any page, all true plus
"the outbox is full", then `LoadHtml` and exactly 100 echoes. Counterproof in
the ledger's convention: make the handle discard the host's answer - falses
drop to zero and echoes fall below trues, both assertions fail.

**Consumer side:** CombatLog's floor rises to the version that ships this, in
the straddle body that calls it.

## 29. Visibility settlement - yes, as `Show(cb)`/`Hide(cb)`, not as un-droppable visibility

Every claim checks out. The refusal path (`OverlayWindow.cs:2255-2270`) warns
once per overlay, resets `desiredVisible`, raises nothing; a dropped command
is equally silent; `IsVisible` is one volatile bool with no request identity.
One thing the entry understates: a `Show()` refused while the window is
*visible* takes the same branch, leaves it visible and flips `desiredVisible`
to false - so the next `Toggle` calls `Show()` again, refused again, now
without a log line. No row covers that case.

**CombatLog's hazard is real, and consumer-fixable today.** Traced: the
raid-start `Hide()` is a bare static call; if the queue refuses it, the next
`Update` re-issues once through `RequestVisibility(false)`; if that is refused
too, the in-raid predicate `_open || (_awaitingVisibility && _requestedVisible)`
is false for a pending Hide, so nothing retries; at the 30-second deadline
`_open = nativeVisible && _requestedVisible` is false regardless of the native
state, pumping stops, and an 1100x720 window sits over the raid while CombatLog
believes it is closed - until the next post-raid toggle, which consults
`IsVisible()` and heals it. Adding `|| WebOverlayGate.IsVisible()` to the
in-raid predicate closes that today, on 1.10.0. Which is the entry's own
framing: 29 is the boilerplate item; 28 is the correctness item. Noted in
passing: `LoadHtml` is one-shot state on the same droppable path
(`WebOverlayGate.cs:129`); under the same flood at first open the page never
loads and nothing re-issues it. Neither `TryPost` nor `Show(cb)` closes that
class - the queue work below does.

**Shape:** `public enum VisibilityOutcome { Applied, AlreadyThere,
RefusedFullscreen, Superseded, Failed, Disposed, QueueRefused }`, plus
`void Show(Action<VisibilityOutcome> completed)` and
`void Hide(Action<VisibilityOutcome> completed)`. The parameterless three are
unchanged, `Toggle` gets no overload, `VisibilityChanged` stays exactly as
truthful as entry 18 made it, and **the window keeps `desiredVisible`**.

**The refuted deeper fix.** The obvious next step is to make visibility
un-droppable: let the handle own `desiredVisible`, hold at most one pending
"apply" work item per handle on the never-refused path, and have later calls
merely flip the flag. It is wrong on four counts:

1. The window changes its own desired state without any handle call - the
   close key (`OverlayWindow.cs:1504`), `WM_CLOSE` (`:2960`), and the fullscreen
   refusal (`:2268`). Every `Toggle` consumer registers its toggle key as a
   close key, so the canonical interaction is press-to-close through the
   window, press-to-reopen through the handle. Today `Toggle` reads the window's
   flag on the overlay thread and shows; with a handle-owned flag it would Hide
   an already hidden window and the player would press twice.
2. Two Hides coalesced into one apply fire `Closed` once; row B5 promises
   "on every hide, unchanged until 2.0".
3. The apply runs at the *first* call's queue position, ahead of a `LoadHtml`
   or `SetBounds` posted in between - a new ordering class that appears exactly
   under the stall it was meant to survive.
4. It puts a lock on the caller's thread on a path that has none, while
   `Show()`/`Hide()` call `SetForegroundWindow` across threads to the game
   window - a deadlock class the probe host cannot reproduce, because its
   `GameWindow` is zero.

`Show(cb)` through the droppable path, with the share below making refusal
self-inflicted, is simpler and equally robust for the real hazard.

**Settlement rules** - the part the entry rightly called more important than
the surface:

- Settled exactly once, or - only once shutdown has begun - not at all. Never
  dropped for a full queue, never dropped because the handle was disposed.
- Delivered like an `ExecuteScript` answer: inline on the overlay thread, on
  the game's main thread through the non-droppable queue, or on the next
  `PumpEvents` - and delivered by `Dispose` too. It may run synchronously
  inside the call on the calling thread when refused at call time, and after
  `Dispose` it may arrive on the overlay thread whatever the dispatch mode.
- Precedence at every settle point: `Disposed` > `Failed` > `RefusedFullscreen`
  > `Superseded` > `Applied`/`AlreadyThere`. The work item re-checks the
  handle's `disposed` before touching the window: a queued `Show(cb)` that runs
  after `Dispose` would otherwise show the window, have its `VisibilityChanged`
  dropped by the disposed handle, and report `Applied` for a transition the
  consumer can never observe.
- **A parked request is the mainline, not an edge.** `drainWork` drains
  commands before creations (`OverlayHost.cs:955-967`), so every consumer's
  first `Show(cb)` runs before `Create()` does, is parked, and is settled from
  inside `configure()` before `Ready` fires. `Applied` means the native window
  and precedes `Ready` and `PageLoaded` on every first open. `Hide(cb)` on a
  failed or closed window settles `Failed`/`Disposed` rather than parking:
  `Hide()` tests only `window == IntPtr.Zero`, and a creation that fails before
  `createWindow` leaves it zero forever - the "creation will call this again"
  comment at `:2250` is false for the `Failed` state.
- Mechanism mirrors `ScriptCall`: a once-only `VisibilityCall` list on the
  window, walked by the creation tail, by a newer request (`Superseded`), by
  `fail()` and by `CloseFromHost`, with the handle wrapping the completion in
  its own once flag so on-the-spot and window-side settles cannot both fire.
- **No ordering promise against `VisibilityChanged`.** Under Manual dispatch
  `PumpEvents` drains answers *first* (`WebOverlays.cs:955-965`), so the
  completion arrives before the event of the same transition in the same pump;
  under main-thread dispatch both ride one FIFO but the event is droppable and
  the completion is not. `Applied`/`AlreadyThere` are self-sufficient: they
  assert the state at settlement. `VisibilityChanged` is for transitions the
  consumer did not ask for - player close, failure, destroy. CombatLog's
  reconcile tolerates the inversion by accident (it collapses to the last
  value); a consumer that reacts inside the completion and reads the later
  event as "it came back" is the failure mode, and the doc has to say so.
- A parked completion has no bound of its own. The environment wait is bounded
  (30 s / 10 s); the controller request in `OverlayWindow` has no timer at
  all. So this cannot fully replace CombatLog's deadline unless creation is
  bounded - add `fail(ViewFailed, "the browser view did not arrive within N
  seconds")` as a separate row, or say in the rule text that a parked request
  waits for creation.

**Three pre-existing holes the design would inherit, to carry with it:**

- Quiet shutdown leaks on the settle paths. `Show()`/`Hide()` have no
  `Stopping` gate and `Shutdown()`'s `WM_APP_WORK` drains the whole queue;
  `DispatchToMainThread` tests `!mainThreadPumpAvailable` *before* `stopping`,
  and the plugin clears the pump before it calls `Shutdown()`
  (`WebOverlayPlugin.cs:364-368`), so after that point main-thread events and
  answers fall through to inline delivery on the overlay thread;
  `CloseFromHost` raises `Closed` ungated. Row 21 proves only the
  `closeEverything` path. Fix: test `stopping` first, early-return
  `Show()`/`Hide()` while stopping, gate `Closed` like `VisibilityChanged`,
  and a row that queues a `Show(cb)` on a visible overlay in all three modes
  and counts zero callbacks through shutdown.
- `Show()` on a failed window is swallowed forever with `desiredVisible` left
  true, so the next `Toggle` calls `Hide()` on the surviving HWND and fires
  `Closed`. Settle `Failed` in that branch and fix the comment.
- Between `createWindow` and `configure()` the HWND exists while the browser
  does not; a `Show()` drained in that sub-gap shows an empty popup and raises
  `VisibilityChanged(true)` before `Ready`. Park on `state == Creating` rather
  than `window == IntPtr.Zero`, which also removes the flash.

**Rows:** queued `Show(cb)` then `Dispose` before it drains - one `Disposed`,
never `Applied`, no `VisibilityChanged` (counterproof: without the run-time
disposed check it reports `Applied`); creation gap Show, Hide, Show -
`Superseded`, `Superseded`, `Applied`, one transition; creation gap Hide only -
`AlreadyThere`; creation failure with a parked request, then `Dispose` from the
`Failed` handler - exactly one `Failed`; `DisplayModeProbe` false - `Show(cb)`
gives `RefusedFullscreen`, `IsVisible` false, no event, no `Failed`, and the
following `Toggle` issues `Show` again; environment-missing failure, then
`Show(cb)` and `Hide(cb)` - both `Failed` on the spot (counterproof: without the
Failed-first check the Hide parks forever); Manual mode - record and assert the
documented completion-before-event order; `Show(cb)` during the H13 stall -
`QueueRefused` exactly once, on the calling thread; shutdown in all three modes
as above. The first-open row is the primary proof, not an edge row: its
counterproof (the tail does not settle) makes every consumer's very first
completion never arrive.

**Consumer side:** CombatLog keeps `_awaitingVisibility` armed until the
completion instead of a deadline, routes the raid-start Hide through the same
state machine (today it bypasses it), and keeps pumping while a completion is
outstanding - its current pump predicate strands a completion for a Hide issued
while the panel is closed.

## Underneath both: the command queue is shared by every mod

Verified: `work` is one static `ConcurrentQueue` with one static counter and
one static warn latch (`OverlayHost.cs:24, 222-224`), and nothing in `TryPost`
or `Post` knows which window a command belongs to. The comment at
`OverlayHost.cs:214-221` - "a consumer posting in a hot loop must cost itself,
not every mod in the process" - and the 1.10.0 changelog sentence are true
only of the heap bound. Once mod A holds the 4,096 slots, mod B's next
`Show()`, `Hide()`, `LoadHtml()` or `Post()` is refused, and after the first
warning nobody is named.

Honest scope: this is latent, not observed. Every shipping streamer throttles
or gates on `IsPageLoaded` - QuestMarkers, ModProfiler at one snapshot per half
second, CombatLog at one per second, ScopeRangefinder on user actions - and the
only measured rate anywhere is the probe's 9,600 per second. No player has met
it. But the library is public, and a third party's hot loop drops everyone
else's one-shot commands; entries 28 and 29 would then merely *report* that.

**Recommendation:** a per-window share under the kept global ceiling - 1,024
per window, 4,096 in total; the outbox limit of 100 is the honest anchor for
"no healthy consumer queues more than this". Owner token is the
`OverlayWindow` (every producer already holds it), the queue element becomes
typed, the per-window counter is decremented in `drainQueue` before the action
runs, the warn latch moves to the window and names `title` and `ownerName`, and
non-droppable items - `Dispose`, page answers - stay outside the share and are
never refused. Victims are then *admitted*, not unaffected: it is one FIFO on
one thread, so mod B's command waits behind mod A's backlog, about half a
second at the measured rate. Say so.

Cost: forty-odd internal lines and real probe work. The stall seam reflects
`OverlayHost.Post` by name without parameter types (`NewApi.cs:2720`) and needs
a typed lookup once the signature changes; row 78 describes the mechanism
being replaced and must be re-run, not carried forward; three rows are new -
a second overlay admitted and delivered during the first one's flood, with the
warning naming the flooder (counterproof: share disabled, the second overlay's
`ExecuteScript` answers null-by-refusal, which is today's behaviour); the
flooder disposed while over its share (`CloseFromHost` still runs, the counter
returns to zero); the global ceiling reached honestly. Fix the comment, and
add a changelog line saying plainly that 1.10.0's bound was shared.

Effect on the two entries: a false from `TryPost` or a `QueueRefused` becomes
self-inflicted, disposed or shutdown. `TryPost` stays right - it is the honest
per-call signal - but the advice next to false changes from "retry" to "you
flooded yourself".

## The fullscreen refusal is probably dead in the game

The probe the plugin registers is `Screen.fullScreenMode !=
FullScreenMode.ExclusiveFullScreen` (`WebOverlayPlugin.cs:85, 399-400`), and it
is evaluated only on the overlay thread - in `Show()` (`OverlayWindow.cs:2245-2247`,
eagerly inside the diagnostic string, and `:2255`) and in the creation tail
(`:648`), never from `Update`. Mono.Cecil over the game's
`UnityEngine.CoreModule.dll`: `Screen.get_fullScreenMode` is an internal call
carrying only `[NativeName("GetFullscreenMode")]`, while `get_width` and
`get_height` carry `[NativeMethod(IsThreadSafe = true)]`. Unity's binding
generator guards the former to the main thread, and
`OverlayHost.DisplayModeSupported` turns any exception into "supported"
(`OverlayHost.cs:514-521`). So the refusal that rows 31 and 49 prove - by
replacing the delegate in a non-Unity host - very likely never fires in the
game. Nobody noticed because every consumer still guards on the main thread
before calling `Show()`, and because the game apparently never reaches
exclusive fullscreen at all (`TROUBLESHOOTING.md:30-35`). The cursor probe, by
contrast, runs from `Update` and is fine.

This is reasoned from metadata, not measured in the game, so: one diagnostic
first - log the exception type inside that catch under `Diagnose` and toggle
the game's display mode once. If it throws, the fix is the shape
`MainThreadPumpAvailable` already has: the plugin's `Update` caches the answer
into a volatile field and the probe returns it; the ledger's manual section
records that the in-game row cannot be automated. Answer 18 and
`RECIPES.md:67-71` ("`Show()` refuses it by itself") are, on this evidence,
inverted in reality today; `RefusedFullscreen` stays in the enum because the
fix makes it real.

## Suggested order

1. **28** - three overloads, the two rows, the consumer floor.
2. **29** - the two overloads and the enum, carrying the three pre-existing
   fixes, the nine rows, and the creation-bound decision.
3. **The per-window share** - its own commit, row 78 re-proven, rows 79-81.
4. **The fullscreen probe** - after the in-game check.

All four in one minor. Documentation the release touches beyond the API table:
the `SOFT-DEPENDENCY.md` version table gains its row (it is what a gate author
copies from); `INTERNALS.md` currently describes the command queue nowhere and
must, before forty lines land in it; the `FAULT-TESTS.md` mode list is already
behind `Program.cs` and gets refreshed rather than extended; and `AGENTS.md`
gains the two rules the new members invite breaking - false means retry later,
never fall back, and never wait for a completion in `OnDestroy`.
