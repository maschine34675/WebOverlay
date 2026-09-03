# WebOverlay

Show web pages in windows over Escape From Tushonka, so a mod can build its user
interface in HTML instead of an immediate-mode toolkit.

A page can be a URL, or just a string of markup - **no web server needed** -
and it can talk to the game in both directions.

![The demo plugin: an HTML panel, a transparent HUD and a WebGL cube over the game](https://raw.githubusercontent.com/maschine34675/WebOverlay/main/assets/demo.gif)

## At a glance

| | |
|---|---|
| Current version | see [Releases](https://github.com/maschine34675/WebOverlay/releases) - the changelog names what each one changed |
| Tested with | SPT 4.1.x, BepInEx 5, in game |
| Game coupling | none at compile time - the library references only BepInEx and Unity; its one game integration (asking EFT for the cursor) is reflective and falls back harmlessly |
| Needs | Windows with the WebView2 runtime (in-box on current Windows 10/11 - and any machine that has run the SPT launcher has it, the launcher's own UI is WebView2) |
| Window modes | borderless windowed or windowed; exclusive fullscreen is refused with a log line |
| Not for | untrusted or arbitrary remote content - the security defaults assume pages the mod itself provides |

Links in this file point at the repository rather than at neighbouring files,
because this README also ships inside the release zip, where nothing else does.

## Installation

Grab `Anvil-WebOverlay-v<version>.zip` from the
[latest release](https://github.com/maschine34675/WebOverlay/releases/latest)
and extract it over the SPT folder; it places
`BepInEx/plugins/Anvil-WebOverlay/` with the library, `WebView2Loader.dll` and
the license texts. One installation serves every mod that uses the library.
Players only need it when a mod lists it as a dependency. The demo plugin is a
separate zip and purely optional.

## Quickstart

A complete plugin - reference block, hotkey, failure handling, shutdown. It
compiles and runs as pasted (a build check in this repository holds it to
that).

In your `.csproj`, next to your usual BepInEx and Unity references
(`$(SptRoot)` is your SPT folder):

<!-- quickstart-csproj:begin -->
```xml
<ItemGroup>
  <Reference Include="BepInEx">
    <HintPath>$(SptRoot)\BepInEx\core\BepInEx.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine">
    <HintPath>$(SptRoot)\EscapeFromTarkov_Data\Managed\UnityEngine.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine.CoreModule">
    <HintPath>$(SptRoot)\EscapeFromTarkov_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="UnityEngine.InputLegacyModule">
    <HintPath>$(SptRoot)\EscapeFromTarkov_Data\Managed\UnityEngine.InputLegacyModule.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="Anvil-WebOverlay">
    <HintPath>$(SptRoot)\BepInEx\plugins\Anvil-WebOverlay\Anvil-WebOverlay.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```
<!-- quickstart-csproj:end -->

`<Private>false</Private>` on the library matters: do **not** copy
`Anvil-WebOverlay.dll` into your own release zip - it is a shared dependency
the user installs once.

The plugin - press F10 in game and the panel appears:

<!-- quickstart-plugin:begin -->
```csharp
using BepInEx;
using UnityEngine;
using WebOverlay;

[BepInPlugin("com.you.yourmod", "You-YourMod", "1.0.0")]
// With a version: your code fails at JIT time if a member is missing, long
// after BepInEx would have called a bare GUID dependency satisfied.
[BepInDependency("com.anvil.weboverlay", "1.11.0")]
public class YourPlugin : BaseUnityPlugin
{
    private IWebOverlay overlay;

    private void Update()
    {
        // Hardcoded for brevity. For a configurable hotkey, poll the key
        // yourself as the demo plugin does - BepInEx's KeyboardShortcut.IsDown
        // blocks while any unrelated key is held, walking included.
        if (Input.GetKeyDown(KeyCode.F10))
            Toggle();
    }

    private void Toggle()
    {
        if (overlay != null)
        {
            overlay.Toggle();
            return;
        }

        // Asynchronous and non-blocking: null means overlays are already known
        // to be unusable, anything later arrives through Failed.
        overlay = WebOverlays.Create("My panel", new OverlayOptions
        {
            // A size of your own, always: the default is 80% of the picture,
            // centred - exactly where the game reads the mouse while the
            // player turns.
            Width = 720,
            Height = 460,
            // A framed window takes the foreground while the game keeps the
            // mouse captured; these two hand the cursor back and forth so
            // both sides stay usable.
            FreeCursorWhileShown = true,
            ClickThroughWhenUnfocused = true,
            // Events arrive on the game's main thread, so a handler may touch
            // Unity objects. Leave this off and they arrive on the library's
            // own thread instead - where touching Unity is a crash.
            DispatchOnMainThread = true,
        });
        if (overlay == null)
        {
            Logger.LogWarning("overlays are unavailable (is the WebView2 runtime installed?)");
            return;
        }

        var created = overlay;
        created.Failed += () =>
        {
            Logger.LogWarning("overlay failed (" + created.Failure + "): " + created.FailureMessage);
            created.Dispose();
            if (ReferenceEquals(overlay, created))
                overlay = null;
        };
        created.MessageReceived += text => Logger.LogInfo("the page says: " + text);

        created.LoadHtml("<!doctype html><h1>Hello</h1>"
            + "<button onclick=\"window.chrome.webview.postMessage('clicked')\">Click me</button>");
        created.Post("hello page");
    }

    private void OnDestroy()
    {
        overlay?.Dispose();
    }
}
```
<!-- quickstart-plugin:end -->

From the page, the other direction:

```js
window.chrome.webview.postMessage('button pressed');
window.chrome.webview.addEventListener('message', e => console.log(e.data));
```

Three things to know before going further:

- Events arrive on the overlay's own thread by default. Queue what a handler
  learns, or set `DispatchOnMainThread = true` and touch Unity directly - the
  threading rules are at the top of
  [`docs/API.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/API.md).
- The example above makes the library a **hard** dependency, which is the
  simple and usually right choice. If your mod should also work when the
  library is *not* installed, that takes more than a `try`/`catch` on Mono -
  and getting it slightly wrong breaks other people's mods rather than yours.
  The rules, the shipping gates that follow them, and the build-time check for
  both are in
  [`docs/SOFT-DEPENDENCY.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/SOFT-DEPENDENCY.md).
- Instead of `LoadHtml`, a page can be real files served from your plugin
  folder - with storage, fonts and no size limit; see
  [`docs/RECIPES.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/RECIPES.md).

## The demo

Install the demo zip to see all of it working: **F10** toggles the HTML panel
above, **F11** a click-through transparent HUD, **F8** an interactive glass
panel, and **F7** a Three.js WebGL cube that follows the player camera. Its
source, [`WebOverlay.Demo/DemoPlugin.cs`](https://github.com/maschine34675/WebOverlay/blob/main/WebOverlay.Demo/DemoPlugin.cs),
is the reference consumer - every pattern in it is there because something
needed it.

## Templates

[`examples/`](https://github.com/maschine34675/WebOverlay/tree/main/examples)
holds three files sized like a real first plugin, compiled verbatim by the
same check that holds the quickstart:
[`PanelPlugin.cs`](https://github.com/maschine34675/WebOverlay/blob/main/examples/PanelPlugin.cs)
(a hotkey window with raid-suitable options),
[`HudPlugin.cs`](https://github.com/maschine34675/WebOverlay/blob/main/examples/HudPlugin.cs)
(a transparent click-through HUD, with the hideout decision as a comment), and
[`WebOverlayGate.cs`](https://github.com/maschine34675/WebOverlay/blob/main/examples/WebOverlayGate.cs)
(the complete soft-dependency gate - copy it rather than inventing the
pattern).

## Used by

Shipping mods built on this library, each a worked answer to "how do I":

- [CraftQueue](https://github.com/maschine34675/CraftQueue) - a hideout craft
  queue whose web panel is an optional dependency with a browser fallback.
- [ModProfiler](https://github.com/maschine34675/ModProfiler) - a per-mod CPU
  profiler; web window when the library is there, IMGUI overlay when not.
- [QuestMarker](https://github.com/maschine34675/QuestMarkers) - world-anchored
  quest markers in a transparent HUD; a hard dependency.
- [RaidReviewOverlay](https://github.com/maschine34675/RaidReviewOverlay) -
  Raid Review's web interface in a window over the game.
- ScopeRangefinder's Web Style Studio uses it next (in development).

## Documentation

| | |
|---|---|
| [`docs/API.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/API.md) | every member, the options, events, failure causes, channels and request/reply, threading, ordering |
| [`docs/RECIPES.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/RECIPES.md) | HUDs and transparency, shaping, real files and web fonts, previewing without the game, performance, security defaults |
| [`docs/TROUBLESHOOTING.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/TROUBLESHOOTING.md) | by symptom: dead mouse, flickering cursor, wrong fonts, missing window - and what a useful bug report contains |
| [`docs/SOFT-DEPENDENCY.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/SOFT-DEPENDENCY.md) | using the library as an optional dependency without breaking anyone, plus the version table |
| [`docs/STYLE.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/STYLE.md) | the shared design tokens `InjectTheme` provides |
| [`docs/INTERNALS.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/INTERNALS.md) | how it works and why it looks like this; the review history |
| [`docs/FAULT-TESTS.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/FAULT-TESTS.md) | the measured evidence - one row per proven behaviour |
| [`CHANGELOG.md`](https://github.com/maschine34675/WebOverlay/blob/main/CHANGELOG.md) | what changed between versions; this file describes the library as it is now |

## Requirements

- Windows with the Microsoft WebView2 runtime. Without it, the first
  `WebOverlays.Create` still returns a handle whose `Failed` event fires
  shortly after; later calls return null.
- Borderless windowed or windowed mode - in exclusive fullscreen a window over
  the game would minimise it, so `Show()` refuses and logs.
- The library runs a browser of its own, with its own user data folder, so it
  neither disturbs nor is disturbed by any other application's WebView2 - the
  SPT launcher included.

The sharp edges of a window over a game's cursor and keyboard are in
[`docs/TROUBLESHOOTING.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/TROUBLESHOOTING.md);
what a HUD has to decide for itself - menus, the hideout, showing and hiding -
is in
[`docs/RECIPES.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/RECIPES.md#translucency-and-huds).

## Reporting a problem

Say the library version (the `Anvil-WebOverlay <version> ready.` line in
`BepInEx/LogOutput.log`), the SPT version and window mode, and attach that log
from a session where it happened. For mouse or focus problems, switch on
**Diagnostics / Log cursor state** first (F12 menu, behind Advanced); for a
page that looks wrong, **Diagnostics / Log page problems**. The full checklist
is at the end of
[`docs/TROUBLESHOOTING.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/TROUBLESHOOTING.md#reporting-a-problem).
Reports go to
[GitHub issues](https://github.com/maschine34675/WebOverlay/issues); for
anything security-sensitive, use GitHub's private *Report a vulnerability*
on the repository's Security tab instead of a public issue.

## Building

Classic net472 projects referencing BepInEx and Unity assemblies from an SPT
installation:

```bash
dotnet build WebOverlay/WebOverlay.csproj -c Release -p:SptRoot=<your SPT folder>
```

`SptRoot` defaults to three folders above the project, so a working copy at
`<SPT>/Development/WebOverlay` needs no argument. **A successful build deploys
the DLL into that SPT installation** - only into a folder that really is one,
and `-p:DeployToSpt=false` makes a build that touches nothing.
[`scripts/New-ReleasePackage.ps1`](https://github.com/maschine34675/WebOverlay/blob/main/scripts/New-ReleasePackage.ps1)
produces the release zips including the license files, verified against a
manifest allowlist; it also compiles the README's quickstart exactly as a
reader would paste it, and refuses to package if it does not build.
[`tools/Probe`](https://github.com/maschine34675/WebOverlay/blob/main/tools/Probe)
is the test host behind
[`docs/FAULT-TESTS.md`](https://github.com/maschine34675/WebOverlay/blob/main/docs/FAULT-TESTS.md);
it ships in no release, and its `preview` mode shows your own page in a real
overlay without starting the game.

## If you are an AI coding agent

Read [`AGENTS.md`](https://github.com/maschine34675/WebOverlay/blob/main/AGENTS.md),
then stop reading documentation and copy a file from
[`examples/`](https://github.com/maschine34675/WebOverlay/tree/main/examples).
The quickstart above is already raid-suitable; the classic generated-code
mistakes are listed there as imperatives.

## Third-party components

`WebView2Loader.dll` belongs to the Microsoft WebView2 SDK and is redistributed
under BSD 3-Clause; see `WebView2-LICENSE.txt` and `WebView2-NOTICE.txt`. The
WebView2 runtime itself is not redistributed.

The demo plugin embeds Three.js (r149, MIT) for its WebGL cube; see
`WebOverlay.Demo/web/three-LICENSE.txt`, which also ships in the demo zip.

## License

MIT.
