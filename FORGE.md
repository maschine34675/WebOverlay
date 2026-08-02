# Forge page

## Teaser (max 100 characters)

`Lets mods show HTML panels and click-through HUDs over the game. Install once, used by many mods.`

(97 characters)

## Description (markdown)

# Anvil-WebOverlay

A shared library that lets BepInEx mods show **web pages in windows over Escape From Tarkov** - interactive panels, and fully click-through transparent HUDs - written in plain HTML instead of an immediate-mode toolkit.

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
- **Safe by default**: navigation is locked to origins the mod itself asked for, popups are suppressed, permission prompts denied, password saving and autofill disabled.
- **One shared browser** for all mods, on its own thread - nothing blocks the game, and failures report through a `Failed` event instead of crashing anything.

## Requirements

- Microsoft WebView2 runtime - current Windows 10/11 installations already include it.
- Borderless windowed or windowed mode. Exclusive fullscreen cannot show a window over the game.

## Demo

The optional demo plugin shows both modes in game: **F10** an interactive panel, **F11** a transparent click-through HUD with live values. Source included - it is the reference for how to use the library.

Full API documentation and the technical write-up (why raw COM vtables, why a chroma key) are in the README on GitHub.

## About the name

Anvil is the library branding of **maschine** (the author of CraftQueue and other mods): shared infrastructure gets a neutral name that other mods can depend on without carrying one modder's personal tag. Same author, same source account, same support.

## License

MIT. `WebView2Loader.dll` is part of the Microsoft WebView2 SDK, redistributed under BSD 3-Clause; the WebView2 runtime itself is not redistributed.

## Changelog v1.1.0 (for the Forge version field)

```text
- Windows now remember their position and size: toggling no longer recenters, and the spot survives restarts. A spot that ends up off-screen (monitor changes) falls back to the centered default. Mods can opt out or set their own storage key (RememberBounds / PersistenceKey).
```
