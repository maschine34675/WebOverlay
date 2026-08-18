# Consumer API wishlist

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
