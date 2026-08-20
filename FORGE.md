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

## Changelog v1.6.0 (for the Forge version field)

Covers v1.4.0 to v1.6.0 in one entry, since the two versions in between were
not published separately. Per-version wording is in the repository history.

```text
Nothing changes for players - this release is for the mods that use the library.

- Pages can be built like ordinary web apps: a mod can serve its own folder as https://yourmod.assets/, so scripts, fonts and images load normally and the page gets working storage.
- Named channels with request/reply: page and mod can ask each other a question and await the answer, instead of every mod inventing its own convention. A question is always answered, so neither side can leave the other hanging.
- A mod can read values back out of its page, and gets events for "my page is live" and for real visibility changes, plus the option to receive everything on the game's main thread.
- Failures now say why, so a mod can tell you "install the WebView2 runtime" instead of showing a generic error.
- Interactive HUDs can be cut down to the rectangles they actually use, so a HUD can cover the screen while the game stays clickable everywhere else; windows can also be moved and resized at runtime.
```

## Changelog v1.3.0 (for the Forge version field)

```text
- Demo: F7 shows a Three.js WebGL compass cube coupled to the player camera - overlays run full WebGL2, so 3D HUDs are possible. The library itself is unchanged.
- README now documents measured performance: ~0.5 ms message round trip, ~9,600 messages/s, visible changes within 1-2 display frames.
```

## Changelog v1.2.1 (for the Forge version field)

```text
- Fixed: the transparent display-only HUD had stopped being click-through in 1.2.0; it ignores the mouse again.
```

## Changelog v1.2.0 (for the Forge version field)

```text
- HUDs are now composition hosted (Windows 8+, 2021+ WebView2): true per-pixel alpha - rgba() glass, soft shadows and clean antialiasing blend with the game. Older systems keep the chroma-key fallback.
- New Interactive option: a transparent HUD can receive mouse input - HTML buttons, hovers and wheel scrolling work while the game keeps the keyboard.
- Demo: F11 shows the glass HUD, F8 an interactive glass panel.
```

## Changelog v1.1.0 (for the Forge version field)

```text
- Windows now remember their position and size: toggling no longer recenters, and the spot survives restarts. A spot that ends up off-screen (monitor changes) falls back to the centered default. Mods can opt out or set their own storage key (RememberBounds / PersistenceKey).
```
