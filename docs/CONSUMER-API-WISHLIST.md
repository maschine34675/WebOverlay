# Consumer API wishlist

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Many of its findings were addressed in the releases
> that followed - see `CHANGELOG.md`; the ones still open recur in newer
> reports until a release closes them. It is kept as evidence, not as a
> description of the current library.

Written 2026-08-18 from the ScopeRangefinder side, while designing a "Style
Studio" overlay (concept: `ScopeRangefinder/docs/WEB-STYLE-STUDIO.md`). The
input is a full read of the current API plus how CraftQueue, QuestMarkers and
ModProfiler actually use it.

Nothing here is a bug report. Several entries are deliberate design decisions
(no `file://`, creation-time-only geometry) — they are listed because a consumer
runs into them and has to work around them, so they are worth a conscious
"still no" as much as a "yes".

Priority is from a consumer's view: how much library-shaped work every new mod
has to redo without it.

---

## 1. Local asset loading (virtual host mapping)

**Today:** no `SetVirtualHostNameToFolderMapping`, no `WebResourceRequested`, and
`originOf()` rejects anything that is not http/https — so `file://`, `data:` and
`about:` are out. A page must be one self-contained string through `LoadHtml`,
capped at 2 MB, or a consumer has to run its own HTTP server.

**Why it hurts:** the demo splices a 620 KB `three.min.js` into the page.
CraftQueue keeps a ~28 KB UI as a C# raw string literal. Both are the same
workaround. It also rules out real assets: web fonts, icons, images — exactly
what a *styling* UI wants (I would like to show a font gallery rendered in the
actual `.otf` files that ship in the mod's `fonts/` folder; today I would have to
base64 every font into the HTML).

**Sketch:**

```csharp
public sealed class OverlayOptions
{
    // Maps https://<host>/ to a folder. Read-only, deny cross-origin.
    public (string Host, string Folder)[] VirtualHosts { get; set; }
}
```

Implementation is one `ICoreWebView2_3::SetVirtualHostNameToFolderMapping` call
plus adding the synthetic origin to the existing trust list. It keeps the current
security model intact (still https, still origin-filtered) and removes the
biggest single piece of consumer friction.

**Alternative if that is unwanted:** raise/remove the 2 MB `LoadHtml` cap and say
so — but that only fixes size, not assets.

---

## 2. `Failed` carries no reason

**Today:** `public event Action Failed;` — no payload, and the same event covers
"WebView2 runtime missing", "composition unavailable for an interactive HUD",
"renderer crashed twice", and "environment start failed".

**Why it hurts:** the consumer is the one who must tell the user what to do, and
the four causes need four different messages ("install WebView2" vs "your
Windows is too old for glass HUDs" vs "the page crashed, reopen it"). Right now
every consumer either stays vague or, like CraftQueue, just shells out to the
external browser and hopes.

**Sketch:**

```csharp
public enum OverlayFailure { RuntimeMissing, EnvironmentFailed, CompositionUnavailable,
                             RendererCrashed, Navigation, Disposed, Unknown }

public event Action<OverlayFailure, string> Failed;   // reason + one log-ready line
```

Keep the parameterless overload for source compatibility if that matters. This
is small and unlocks genuinely better UX in every consumer.

---

## 3. Optional main-thread event dispatch

**Today:** `MessageReceived`, `KeyPressed` and `Closed` fire on the library's STA
thread; `Ready`/`Failed` are latched and can run on the subscribing thread. Every
consumer therefore writes the same `ConcurrentQueue` + drain-in-`Update()`
boilerplate, and the failure mode when you forget is nasty and intermittent
(Unity API from a foreign thread).

**Sketch:**

```csharp
public bool DispatchOnMainThread { get; set; }   // OverlayOptions, default false
```

With it set, the library queues callbacks and raises them from a hidden
`MonoBehaviour`'s `Update`. Costs one frame of latency, removes an entire class
of consumer bugs. Guaranteeing a *fixed* thread for `Ready`/`Failed` (the
regression review's REG finding) would fall out of the same mechanism.

---

## 4. Channel-based messaging, optionally with correlation

**Today:** one untyped `string` in each direction. Every consumer has
independently invented `prefix:payload` — `markers:`/`cfg:` (QuestMarkers),
`snap:`/`status:`/`cmd:` (ModProfiler), `fps:`/`view:` (demo) — and hand-rolls
the JSON on both sides. There is no request/response correlation at all, so
"page asks the mod a question" has no pattern.

**Sketch (no JSON dependency in the library, just framing):**

```csharp
void Post(string channel, string payload);
event Action<string /*channel*/, string /*payload*/> MessageReceived;

// and the piece nobody can build cheaply on top:
void Request(string channel, string payload, Action<string> reply, int timeoutMs = 5000);
```

Wire format stays a plain string (`channelpayloadid`), with a tiny
JS shim injected via `AddScriptToExecuteOnDocumentCreated` exposing
`overlay.on(channel, fn)` / `overlay.send(channel, payload)` / `overlay.reply(...)`.
That shim is the part every consumer currently copies by hand.

---

## 5. `ExecuteScript` discards the result

**Today:** the completion handler exists and logs a failing HRESULT, but the
result JSON never reaches the consumer.

**Why it hurts:** anything "read the page's current state" needs a round trip
through `postMessage` plus a hand-built correlation. With #4 this is less
pressing, but it is nearly free given the handler is already there:

```csharp
void ExecuteScript(string script, Action<string> result = null);
```

---

## 6. `Closed` fires on every `Hide()`

**Today:** `Hide()` raises `Closed`, so the event cannot distinguish "user closed
the window" from "we hid it ourselves" — matching the open finding A11.

**Why it hurts:** ModProfiler reconciles `IsVisible()` against its own flag every
single frame rather than trusting the event; that is a workaround for exactly
this. Any consumer wanting "persist my UI state when the user closes it" has no
clean hook.

**Sketch:** keep `Closed` for the real close (user, `CloseKeys`, `Dispose`), add
`event Action<bool /*visible*/> VisibilityChanged`. If the current semantics must
stay for compatibility, documenting them prominently would already help.

---

## 7. Geometry is creation-time only

**Today:** no runtime resize/move/`SetBounds`, no `Opacity` setter, no z-order
API. Size comes from `OverlayOptions.Width/Height`, position is centered or
remembered.

**Why it hurts:** less than it sounds for framed windows (the user drags them),
but a consumer cannot offer a "compact / wide" toggle, cannot fit the window to
its content after the page has laid itself out, and an interactive HUD — which
swallows the mouse over its whole rectangle — cannot shrink to what it actually
draws.

**Sketch:** `void SetBounds(int? x, int? y, int? width, int? height)` marshalled
like every other call, plus (for HUDs) a way for the page to ask for its own
content size via #4. Lower priority than 1–4, but the interactive-HUD case is a
real limitation for QuestMarkers-style overlays.

---

## 8. Shared browser profile across all mods

**Today:** one WebView2 environment per process, user data under
`%LOCALAPPDATA%\WebOverlay\BrowserData`, shared by every consumer (open finding
A14).

**Why it hurts:** `localStorage`/`sessionStorage` are per origin, and every
`LoadHtml` page shares the same opaque origin — so two mods that both remember
UI state collide, silently. I would like the Style Studio to remember which
panels are open; today that is not safe without prefixing every key by hand.

**Sketch:** either per-consumer profiles (`CoreWebView2ControllerOptions.ProfileName`,
needs a newer SDK level — check what the hand-built vtables support) or, much
cheaper, document the collision and hand each consumer a namespace it can prefix
with, e.g. expose `IWebOverlay.StorageNamespace` derived from the calling
assembly.

---

## 9. Smaller things worth a line

- **Null/empty validation on public methods** (A10): `Create(null)`,
  `LoadHtml(null)`, `Post(null)` should fail loudly at the call site instead of
  somewhere on the overlay thread.
- **Outbox capacity is fixed at 100 and silently lossy after a warning.** A
  consumer that streams (thumbnails, live previews) can hit that before `Ready`.
  Either make it an option or expose `bool IsReady` so the consumer can hold off
  instead of over-filling.
- **No "document loaded" signal distinct from `Ready`.** `Ready` means the
  browser is up; consumers actually want "my page is live" (FUR-02 touches this).
  With #4's shim the page can just announce itself — but then that shim needs to
  exist.
- **Interactive HUDs get no keyboard** (already on the roadmap). Worth keeping
  there; a HUD with a text field is otherwise impossible.
- **`WebView2Api.Method<T>` allocates a delegate per call** (A15): irrelevant at
  UI rates, but a consumer streaming frames at 60 Hz through `Post` pays it.

---

## What the Style Studio actually needs

Ordered by how much it would change that design:

1. **#3 main-thread dispatch** and **#4 channel messaging** — these are pure
   boilerplate removal; without them I write the same ~120 lines every consumer
   already has.
2. **#2 `Failed` reason** — needed to tell the user *why* the studio will not
   open, since the IMGUI fallback message should differ per cause.
3. **#1 virtual hosts** — would let the font gallery use the real font files and
   drop the "inline everything" constraint. Nice-to-have for v1, decisive if the
   UI grows.
4. **#8 storage namespace** — only if the studio should remember its own UI
   state.

Everything else I can live without as designed.

---

# Second round (2026-08-22, from the ModProfiler side)

Written after moving ModProfiler's profiler window onto the library as it is
after 1.6 - channels, request/reply, main-thread dispatch, failure causes -
with the IMGUI overlay kept as the fallback. Entries 1-9 above are answered in
`CONSUMER-API-WISHLIST-ANSWERS.md`; this round starts at 10 so the two
documents keep one numbering. As before: nothing here is a bug report, and
several entries may deserve a conscious "no".

## 10. Deferred (asynchronous) request replies

**Today:** `OnRequest(channel, Func<string, string>)` must answer inside the
dispatched callback. The page side already resolves a promise; only the mod
side lacks the asynchronous half.

**Why it hurts:** a request whose answer takes real work - ModProfiler's
rescan (seconds of Harmony patching) or its CSV write - either blocks inside
the dispatch or does what ModProfiler now does: reply `"scheduled"` and send
the real outcome later on a separate `status` channel. One question becomes
two channels, and the page's promise resolves with a placeholder instead of
the answer.

**Sketch:**

```csharp
void OnRequest(string channel, Action<string /*payload*/, Action<string> /*reply*/> handler);
```

Reply exactly once, later, from wherever the consumer likes; the existing
timeout still bounds the page's wait (it can raise it through the shim's third
argument), and a consumer that never replies is answered `null` by the timeout
as today. Same guarantee, one more shape.

## 11. Who pays for dispatched callbacks - and a manual pump

**Today:** with `DispatchOnMainThread` every consumer callback - `ChannelMessage`,
`OnRequest` handlers, the events - runs inside `WebOverlayPlugin.Update`.

**Why it hurts:** measured in ModProfiler, which instruments every mod's
`Update` including the library's: a rescan executed inside the request handler
was booked to the *Anvil-WebOverlay* row - hundreds of milliseconds of Max and
total, even spike-log blame. Any profiler-like consumer sees the same; every
other consumer has its handler cost land in the library's frame slice at a
point in the frame it does not control (before or after its own `Update`,
depending on plugin load order). ModProfiler works around it by keeping the
handlers trivial and doing the work from its own `Update`.

**Sketch:** (a) say it in the `DispatchOnMainThread` docs; (b) offer a manual
pump so a consumer can drain from its own `Update` and own both the cost and
the ordering:

```csharp
public enum EventDispatch { OverlayThread, MainThread, Manual }
public EventDispatch Dispatch { get; set; }     // OverlayOptions
void PumpEvents();                              // IWebOverlay, Manual only
```

## 12. Free the cursor while a framed window holds the focus (in raid)

**Today:** a framed overlay takes the foreground on `Show`. In a raid EFT keeps
the cursor locked, hidden and clipped even though a foreign window of the same
process now has the focus (measured in ModProfiler's in-game test): the pointer
can never reach the window, the game is unfocused, and the player is stuck
until Escape. Every consumer that opens a framed window mid-raid hits this.

**Sketch:** Unity-plugin layer only, so the core stays Unity-free:

```csharp
public bool FreeCursorWhileFocused { get; set; }   // OverlayOptions, default false
```

While such an overlay is visible and `!Application.isFocused`, the plugin sets
`Cursor.lockState = None` / `Cursor.visible = true` in `Update` **and**
`LateUpdate` (EFT relocks from late components), and stops the moment the game
window is focused again - EFT relocks on its own. ModProfiler ships exactly
this logic privately. (It also mutes EFT's input commands meanwhile through a
Harmony prefix on `InputManager` - that part is game-specific and does not
belong in the library.)

## 13. `VirtualKey(KeyCode)` helper for `CloseKeys`

**Today:** `CloseKeys` are Win32 virtual-key codes; a consumer with a
configurable BepInEx hotkey (`KeyboardShortcut` -> Unity `KeyCode`) maps
`KeyCode` -> VK itself.

**Why it hurts:** CraftQueue and ModProfiler carry the same ~20-line table
(F1-F15, A-Z, 0-9, Escape/Tab/Home/End/Insert/Delete/PageUp/PageDown/Pause) -
and the second copy only exists because a review caught the close key
hard-coded to F10 while the toggle key was rebindable. The next consumer will
make the same mistake.

**Sketch:** a static helper next to the plugin (Unity side, same assembly):

```csharp
public static int VirtualKey(KeyCode key);                 // 0 when unmapped
public static int[] CloseKeysFor(KeyboardShortcut shortcut); // Escape + the key
```

documented beside `CloseKeys`. Both consumers would delete their copy.

## 14. A soft-dependency guide (documentation)

**Today:** every consumer re-derives the same rules, and since 1.4+ there is a
new one. The proven set: all library-touching members `NoInlining` and called
only behind a presence check; closure-captured handles typed `object` (a field
of a library type in a compiler-generated closure class makes
`Assembly.GetTypes()` over the plugin throw for every other mod when the
library is absent); and now a **minimum-version gate** - additive APIs mean a
gate body that uses a 1.6 member fails at JIT time on a 1.3 install, so the
check is `Chainloader.PluginInfos[guid].Metadata.Version >= new Version(1, 6, 0)`
rather than mere presence, with a log line and a fallback below it. Worth
adding: `[BepInDependency(guid, DependencyFlags.SoftDependency)]` so the
library loads - and therefore `Update`s - before the consumer, and a build-time
check (Mono.Cecil: no field, base type, interface or method signature in the
plugin may reference the library).

**Sketch:** `docs/SOFT-DEPENDENCY.md` with that pattern and a pointer to the two
shipping gates (CraftQueue `UI/WebOverlayGate.cs`, ModProfiler
`UI/WebOverlayGate.cs`), plus one sentence in the changelog contract: minors are
additive, so consumers gate on "at least X.Y".

## 15. Checked, needs nothing

- Request-handler exceptions are isolated: caught, logged, and the page is
  answered `null` - a throwing consumer never hangs a page.
- The shim's `request(channel, payload, timeoutMs)` already lets a page wait
  longer for a slow answer; fine for long actions once entry 10 exists.
- `window.overlay` is absent outside the library (a page opened in a plain
  browser); a one-line guard in the page is enough, and a README sentence
  would spare the first confused console.

---

# Third round (2026-08-22, from the QuestMarkers side)

Written after building QuestMarkers on 1.6.0 - a display-only HUD that
streams one projected marker frame per rendered frame over a channel - and
running its adversarial review. Entries 1-9 are answered in
`CONSUMER-API-WISHLIST-ANSWERS.md`, 10-15 are the ModProfiler round; this one
starts at 16 and repeats nothing from either. As before, nothing here is a bug
report - several are traps every HUD consumer walks into and then fixes on its
own side, which is exactly the kind of work a library exists to absorb.

## 16. Retained messages on a channel

**Today:** the library reloads the same page silently after a renderer crash
(bounded attempts, no `Failed`), and `PageLoaded` fires again. The fresh
document starts with its built-in defaults. QuestMarkers' review found that its
display options would have quietly reverted mid-raid - the dirty-check on the
mod side saw no change and never resent them. The fix was "re-post the config
from `PageLoaded`", which every consumer with page-side configuration needs and
none would think of until a GPU reset hits a player.

**Sketch:**

```csharp
void Post(string channel, string payload, bool retain);   // retain: replay on (re)load
```

The library keeps the last retained payload per channel and replays it to every
document that loads for the same target - a reload after a crash included - in
channel order, before anything else. A retarget via `LoadHtml`/`Navigate`
clears the set, since the page changed. This is the MQTT "retained message"
idea, and it turns a trap into a one-word flag.

## 17. Latest-wins delivery for streaming channels

**Today:** every `Post` is queued in order and delivered in order. At the
measured ~9,600 messages/s that is invisible - until it is not: a GC pause, a
renderer busy with a reload, or a hidden-then-shown overlay leaves a queue of
per-frame marker frames that are all stale on arrival, and the markers
visibly trail the camera while the page catches up. A consumer cannot fix this
from outside; by the time it could notice, the frames are already queued.

**Sketch:**

```csharp
void Post(string channel, string payload, bool latestOnly);   // or PostLatest(...)
```

A message flagged latest-only replaces any still-undelivered payload on the
same channel instead of being appended - in the pre-load outbox and in the
delivery queue alike. Per-frame telemetry (markers, the demo's `view` feed,
ModProfiler's snapshots) is exactly the traffic this is for; ordinary messages
keep their ordering guarantee untouched. Combines naturally with 16 as two
flags on the same overload.

## 18. `Show()` should refuse exclusive fullscreen itself

**Today:** `WebOverlayPlugin.IsDisplayModeSupported` is the consumer's job to
check before *every* `Show()`, including the re-show of an overlay created
earlier. QuestMarkers had the check on its creation path only; the review
caught the re-show path, where a player who switched to exclusive fullscreen
mid-session would have had the game minimised at the next raid start. Every
consumer has at least two Show paths and gets this wrong in one of them.

**Sketch:** the plugin already registers a main-thread dispatcher into the
Unity-free core (entry 3). Register a display-mode probe the same way, and let
`Show()` log once and stay hidden - `VisibilityChanged(false)` if anything - when
it reports exclusive fullscreen. The consumer's own check becomes optional
instead of load-bearing.

## 19. Tell the page which transparency it got

**Today:** a `Transparent` HUD is composition hosted or falls back to the
chroma key, and the README explains how differently `rgba()` panels and soft
shadows look in the two modes. Nothing exposes which one the overlay actually
got - not on the handle, not to the page - so a page cannot adapt its styles,
and a mod cannot even log it. QuestMarkers' ink panels and drop shadows are
designed for glass and would need solid variants on chroma; today that is a
guess.

**Sketch:**

```csharp
enum OverlayTransparency { None, Composition, ChromaKey }
OverlayTransparency Transparency { get; }      // valid once Ready
```

plus the same fact for the page without any mod code: a class on the root
element set by the injected shim (`wo-composed` / `wo-chroma`) or
`overlay.env.transparency`. A stylesheet can then say
`.wo-chroma .panel { background: #1b1c18 }` and be done.

## 20. A versioned page-preview tool - the probe host belongs in the repo

**Today:** the empirical probe host (the net9 program that drives the real
`Anvil-WebOverlay.dll` outside the game and proved every vtable slot) lives in
a session scratchpad, not in the repository. QuestMarkers verified its page by
adding a mode to it - and that render is what found the marker anchor bug (the
pin tip sat a label's height below the target) before any raid. A consumer has
no official way to see its page inside a real composed HUD without starting the
game, and the library's own evidence base can be lost with a cleaned temp
folder.

**Sketch:** `tools/PagePreview` (or the probe host itself under `tools/Probe`)
in the repo, with one consumer-facing mode:

```
PagePreview <page.html> [--backdrop dark|<image.png>] [--post <channel> <payload>]...
            [--screenshot out.png]
```

It loads the page into a real transparent HUD over the backdrop, sends the
given messages, and saves a screenshot. That is the whole QuestMarkers test
loop, and it would serve the Style Studio's font gallery just as well.

## 21. Shared design tokens

**Today:** the demo HUD, the cube page, the glass panel and QuestMarkers all
declare the same palette by copy-paste: gold `#c2ad6d`, ink
`rgba(16,17,13,.72-.78)`, text `#d0cdbd`, dim `#918e7e`, the Segoe UI stack.
Four copies already, and every new consumer makes a fifth - and a palette
change never propagates.

**Sketch, cheapest first:** a `docs/STYLE.md` listing the tokens and the panel
recipe. Nicer: an opt-in `OverlayOptions.InjectTheme` that has the shim set CSS
custom properties on `:root` (`--wo-gold`, `--wo-ink`, `--wo-text`, `--wo-dim`,
`--wo-font`), so mod pages look like one family and a consumer writes
`border-color: var(--wo-gold)` instead of a hex value. Pure consistency, no
priority.

## 22. HUD lifecycle traps worth a README section (docs only)

All of these came out of the QuestMarkers review; each is one line in the code
once known and invisible until a player reports it:

- A HUD is an OS window above the game's *own* full-screen interfaces - it
  floats over the inventory, the map, the menu and the death sequence unless
  the consumer gates visibility on game state (EFT:
  `EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI)` and
  the player's `HealthController.IsAlive`).
- The hideout registers a `GameWorld`, a game that reaches
  `GameStatus.Started`, and a player flagged `IsYourPlayer` - every "am I in a
  raid" check passes there unless the world is excluded by type
  (`HideoutGameWorld`).
- Page-side configuration must be re-sent on every `PageLoaded` until 16
  exists; the library reloads the page after a renderer crash.
- The exclusive-fullscreen guard belongs on every Show path until 18 exists.
- A page that drives visibility through both a CSS class and an inline
  `style.opacity` will find the inline value always wins - pick one mechanism.
  (Not a library matter, but it cost a review round, and the README's HUD
  section is where page authors look.)

## What QuestMarkers actually needs

1. **16 retained messages** and **17 latest-wins** - the two that cannot be
   built on the consumer side and directly affect how the markers feel.
2. **18 Show guard** and **22 docs** - the traps the review had to find.
3. **20 preview tool** - it already exists; it just needs a home.
4. **19 transparency mode** and **21 tokens** - whenever convenient.

---

# Fourth round (2026-08-26, from the ScopeRangefinder side)

Written after actually building the Style Studio on 1.8 - the design the first
round was written for - and shipping it. Entries 10-22 are answered in
`CONSUMER-API-WISHLIST-ANSWERS.md`; this round starts at 23 so the numbering
stays continuous. Everything here came out of building against the library
rather than reading it, so each entry names the concrete place it cost
something. As before, several may deserve a conscious "no".

## 23. Choose the access kind per virtual host

**Today:** every mapping is created with `HostResourceAccessDenyCors`
(`OverlayWindow.cs`, at the `SetVirtualHostNameToFolderMapping` call). The
constant's own comment explains why not `DENY` - an inline `LoadHtml` page has
an opaque origin and could then not reach its own assets - but there is no way
to ask for `ALLOW` for a host that belongs to the same overlay.

**Why it hurts:** web fonts are CORS-checked. A page served from `mod.studio`
that declares `@font-face` against a second host `mod.fonts` has every face
refused - silently. The page renders, nothing reports an error, and every
string is drawn in the fallback font. That is what the Style Studio did on its
first in-game test, and it looked like a font-loading bug for a round.

The only fix a consumer has is to serve everything from ONE host, which means
pointing that host at a folder containing both the page and the fonts. For
ScopeRangefinder that is the plugin root - so the WebView can also see the
mod's DLL, its layout JSON and its config. A review of the mod flagged exactly
that as avoidable exposure, and today it is not avoidable.

**Sketch:**

```csharp
public sealed class VirtualHost
{
    public string Host { get; set; }
    public string Folder { get; set; }
    // Deny cross-origin (default, today's behaviour), or allow it between
    // hosts that belong to the same overlay.
    public bool AllowCrossOrigin { get; set; }
}
```

One argument on a call that already exists. The trust model is unchanged: a mod
can only declare its own hosts, so "cross-origin" here means "between two
folders the same mod asked for".

**Alternative:** several path prefixes under one host (`/web/` and `/fonts/`
mapped separately). Nicer for the consumer, but WebView2 maps one folder per
host name, so it needs `WebResourceRequested` interception - far more library
than the flag above buys.

## 24. Set an option by name

**Today:** setting a newer option under the soft-dependency pattern takes three
parts: a version constant, a comparison against the found version, and a
separate `[MethodImpl(NoInlining)]` body that takes `object` - because a
parameter type belongs to the signature and resolves when something reflects
over the type's methods, not when the method is called.

**Why it hurts:** three moving parts per option, and each fails hard rather
than gracefully when it is wrong - a `TypeLoadException` on a player's machine,
not a missing feature. It also does not compose: two new options in a release
means two more bodies. `tools/Audit-SoftDependency.ps1` exists because this is
easy to get wrong, and it caught ScopeRangefinder's own first attempt.

**Sketch:**

```csharp
// Ignored by a library that does not know the key.
options.Set("ClickThroughWhenUnfocused", true);
```

It trades compile-time checking for string keys, which is a real loss. For a
library whose whole design assumes consumers run against versions that predate
the member, it looks like the better trade: one version gate for `Set` itself,
and then never again.

## 25. Page diagnostics behind the switch that already exists

**Today:** nothing forwards the page's console output, or a sub-resource
request that the origin filter or a mapping refused. `Failed` and
`FailureMessage` describe the window; inside the document there is no signal at
all.

**Why it hurts:** the font problem in 23 produced no output anywhere - not in
the game log, not in the library's. The page looked correct and used the wrong
font. DevTools finds it in a minute, but DevTools is a developer's switch on a
developer's machine, while a player's report says "the fonts look wrong" and
nothing more.

**Sketch:** the same shape as the cursor report added in 1.8.4 - one switch,
off by default, rate-limited, one line per event. Console messages with their
level, and refused requests with the reason. WebView2 exposes both.

## 26. Say whether channel messages keep their order

**Today:** the guarantee is not written down anywhere.

**Why it hurts:** the studio's page sends a `set` and then asks for a fresh
preview, which is only correct if the second message is handled after the
first. I satisfied myself from the source that it is; the next consumer will
assume it without looking. Either answer is fine - "ordered per channel" or
"no ordering, correlate yourself" - but only one of them is written.

**Sketch:** one sentence in the README's channel section.

## 27. Smaller things worth a line

- **Binary payloads.** Preset thumbnails travel as base64 PNG inside the JSON
  of a channel message, fifteen and more of them. It works; it doubles the
  bytes and forces a string round trip. Publishing bytes under the virtual host
  at runtime would be the natural home for anything a mod renders itself.
- **The default geometry is the trap.** A window with no size of its own gets
  80% of the screen, centred - which is exactly where a first-person game reads
  mouse movement from. 1.8.6-1.8.8 fixed the consequence; a line in the
  consumer documentation would keep the next mod from meeting it at all.
  (ScopeRangefinder should also just set its own size.)

## What the Style Studio actually needed

1. **23 access kind** - the only entry that forced a compromise still present
   in the shipped mod: the whole plugin folder is mapped, because splitting it
   would cost the fonts.
2. **25 page diagnostics** - would have turned that same bug from a testing
   round into a log line, and it is the general answer for anything that goes
   wrong inside the document.
3. **24 option by name** - pure ergonomics, but it is the ergonomics of the
   pattern this library asks every consumer to follow.
4. **26 ordering** - one sentence, and consumers are already relying on
   whatever the answer is.

What the 1.8 series already answered needed no workaround and is worth saying
so: channels with `OnRequest`, retained posts, the manual pump, the version
gate, `Failed` with a cause, and `FreeCursorWhileShown` from 1.8.5 on - all
used exactly as documented.
