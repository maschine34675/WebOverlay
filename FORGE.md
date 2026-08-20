# Forge page

## Teaser (max 100 characters)

`Lets mods show HTML panels and click-through HUDs over the game. Install once, used by many mods.`

(97 characters)

## Description (markdown)

# Anvil-WebOverlay

A shared library that lets BepInEx mods show **web pages in windows over Escape From Tushonka** - interactive panels, and fully click-through transparent HUDs - written in plain HTML instead of an immediate-mode toolkit.

**For players:** you only need this installed when another mod lists it as a dependency. Extract the zip over your SPT folder, done. One installation serves every mod that uses it - a single shared browser, not one per mod.

**For modders:** reference `Anvil-WebOverlay.dll`, and a UI is this:

```csharp
var overlay = WebOverlays.Create("My panel");
if (overlay == null) return;                   // known-unavailable: fall back
overlay.Failed += () => { ... };               // async failure: dispose + fall back
overlay.MessageReceived += text => { ... };    // page -> mod
overlay.LoadHtml("<h1>Hello</h1>");            // or Navigate(url)
overlay.Post("live value");                    // mod -> page
```

## Features

- **Panels**: movable, resizable windows with a dark game-toned title bar (or frameless), closable by keys you choose.
- **Transparent HUDs**: pixels your page does not paint show the game, with true per-pixel alpha on Windows 8+ - and by default the window ignores mouse and keyboard entirely, so the game stays fully playable.
- **Clickable glass**: a HUD can take mouse input instead, and can be cut down to the rectangles it actually draws in, so it covers the screen while the game stays clickable around it.
- **Opacity**: fade the whole window; combine with the HUD for a faded HUD.
- **Two-way messaging** between page and mod: plain strings, or named channels where either side can ask the other a question and await the answer. Plus `ExecuteScript` with its result, inline HTML without any web server, and optional DevTools while building a page.
- **Pages with real files**: a mod can serve its own folder under its own host name, so scripts, fonts and images load normally and the page gets working browser storage.
- **A real browser engine**: WebGL2 works, so Three.js scenes can run inside a HUD - the demo includes a 3D compass cube coupled to the player camera.
- **Safe by default**: navigation is locked to origins the mod itself asked for, popups are suppressed, permission prompts denied, password saving and autofill disabled.
- **One shared browser** for all mods, on its own thread - nothing blocks the game, and failures report through a `Failed` event instead of crashing anything.

## Requirements

- Microsoft WebView2 runtime - current Windows 10/11 installations already include it.
- Borderless windowed or windowed mode. Exclusive fullscreen cannot show a window over the game.

## Demo

The optional demo plugin shows the modes in game: **F10** an interactive panel that reads a value back out of its page, **F11** a transparent click-through glass HUD with live values, **F8** an interactive glass panel whose buttons talk to the game over named channels and ask it for the current frame rate, **F7** a Three.js WebGL compass cube that follows the player camera. Source included - it is the reference for how to use the library.

Full API documentation and the technical write-up (why raw COM vtables, why a composed visual instead of a child window) are in the README on GitHub.

## About the name

Anvil is the library branding of **maschine** (the author of CraftQueue and other mods): shared infrastructure gets a neutral name that other mods can depend on without carrying one modder's personal tag. Same author, same source account, same support.

## License

MIT. `WebView2Loader.dll` is part of the Microsoft WebView2 SDK, redistributed under BSD 3-Clause; the WebView2 runtime itself is not redistributed.

## Changelog (for the Forge version field)

Version-specific text lives in `CHANGELOG.md`. Paste the `### Forge version
notes` block of the version being uploaded; the entry for 1.6.0 covers 1.4.0
to 1.6.0, because those were never uploaded separately.
