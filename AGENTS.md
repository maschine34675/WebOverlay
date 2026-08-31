# For AI coding agents building a mod that uses this library

You are probably not working in this repository - you are in a mod's
repository, and this library is a reference. This file is your contract.
Read it, copy a template, stop reading documentation.

## Do this

- Copy a file from `examples/` as your starting point: `PanelPlugin.cs` for a
  window, `HudPlugin.cs` for a transparent HUD, `WebOverlayGate.cs` when the
  library must be optional. They compile verbatim on every release.
- Declare a hard minimum version:
  `[BepInDependency("com.anvil.weboverlay", "<version you built against>")]`.
  A bare GUID dependency is satisfied by a library too old for your code.
- Reference the installed DLL with `<Private>false</Private>`:
  `$(SptRoot)\BepInEx\plugins\Anvil-WebOverlay\Anvil-WebOverlay.dll`.
- On `Failed`: log `Failure`/`FailureMessage`, `Dispose()` the handle, fall
  back. In `OnDestroy`: `Dispose()`.
- If a handler touches Unity objects, create the overlay with
  `DispatchOnMainThread = true`. Default events arrive on the library's own
  thread, where touching Unity crashes.
- For a window: set `Width`/`Height`, `FreeCursorWhileShown = true`, and -
  if it can sit over the middle of the screen in a raid -
  `ClickThroughWhenUnfocused = true`. The default size is 80% of the picture,
  centred, which sits exactly on the point the game reads the mouse from.
- For a HUD: `Transparent = true`, and decide yourself when it may show -
  the hideout looks exactly like a raid to every obvious test; see the
  comment in `examples/HudPlugin.cs`.
- Poll hotkeys with `Input.GetKeyDown` plus your own modifier check.
  BepInEx's `KeyboardShortcut.IsDown` blocks while any unrelated key is
  held - walking included.
- Serve page files through `OverlayOptions.VirtualHosts`, never `file://`.
  Map a subfolder (`web/`), not your whole plugin directory.

## Never do this

- Never copy `Anvil-WebOverlay.dll` into your mod's release zip. It is a
  shared dependency the player installs once.
- Never make the library optional with a `try`/`catch` around `Create`. On
  Mono, a type whose signatures mention the missing library breaks
  `Assembly.GetTypes()` for every mod that scans assemblies - OTHER mods
  fail, not yours. Copy `examples/WebOverlayGate.cs`; the rules are in
  `docs/SOFT-DEPENDENCY.md`, the build check in
  `tools/Audit-SoftDependency.ps1`.
- Never create an overlay per frame or per keypress. Create once, then
  `Toggle()`/`Show()`/`Hide()`.
- Never write `Cursor.visible`/`Cursor.lockState` yourself to free the mouse
  for an overlay - that is the flickering-cursor bug every overlay mod
  reinvents. `FreeCursorWhileShown` asks the game instead of fighting it.
- Never treat files under `docs/reviews/` or dated review reports as open
  findings. They are historical snapshots; the current contract is
  `docs/API.md`, and what was fixed is in `CHANGELOG.md`.

## If you are working on THIS repository

Different rules apply: every hand-bound vtable slot needs a probe row before
it is trusted, minors are additive, and `docs/FAULT-TESTS.md` is the ledger.
Start with `docs/INTERNALS.md` and the probe's README - and nothing in the
consumer contract above overrides them.
