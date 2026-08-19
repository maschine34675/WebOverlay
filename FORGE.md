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
- **Transparent HUDs**: pixels your page does not paint show the game; the window ignores mouse and keyboard entirely, so the game stays fully playable.
- **Opacity**: fade the whole window; combine with the HUD for a faded HUD.
- **Two-way messaging** between page and mod, plus `ExecuteScript`, inline HTML without any web server, and optional DevTools while building a page.
- **A real browser engine**: WebGL2 works, so Three.js scenes can run inside a HUD - the demo includes a 3D compass cube coupled to the player camera.
- **Safe by default**: navigation is locked to origins the mod itself asked for, popups are suppressed, permission prompts denied, password saving and autofill disabled.
- **One shared browser** for all mods, on its own thread - nothing blocks the game, and failures report through a `Failed` event instead of crashing anything.

## Requirements

- Microsoft WebView2 runtime - current Windows 10/11 installations already include it.
- Borderless windowed or windowed mode. Exclusive fullscreen cannot show a window over the game.

## Demo

The optional demo plugin shows the modes in game: **F10** an interactive panel, **F11** a transparent click-through glass HUD with live values, **F8** an interactive glass panel with working buttons, **F7** a Three.js WebGL compass cube that follows the player camera. Source included - it is the reference for how to use the library.

Full API documentation and the technical write-up (why raw COM vtables, why a chroma key) are in the README on GitHub.

## About the name

Anvil is the library branding of **maschine** (the author of CraftQueue and other mods): shared infrastructure gets a neutral name that other mods can depend on without carrying one modder's personal tag. Same author, same source account, same support.

## License

MIT. `WebView2Loader.dll` is part of the Microsoft WebView2 SDK, redistributed under BSD 3-Clause; the WebView2 runtime itself is not redistributed.

## Changelog v1.4.0 (for the Forge version field)

```text
- For mod authors: overlays can now serve a folder of real files as https://yourmod.assets/ - scripts, fonts and images load normally, and such a page also gets working localStorage (an inline page has none).
- Failed now says why: a cause a mod can act on plus the exact message, so users get "install the WebView2 runtime" instead of a generic failure.
- New PageLoaded event and IsPageLoaded for "my page is live", and an option to receive all events on the game's main thread.
- Nothing changes for players; the demo is unchanged apart from showing the new API.
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
