# WebOverlay

Show web pages in windows over Escape From Tarkov, so a mod can build its user
interface in HTML instead of an immediate-mode toolkit.

A page can be a URL, or just a string of markup - **no web server needed** -
and it can talk to the game in both directions.

## For mod authors

Reference `Anvil-WebOverlay.dll` and declare the dependency:

```csharp
[BepInPlugin("com.you.yourmod", "You-YourMod", "1.0.0")]
[BepInDependency("com.anvil.weboverlay")]
public class YourPlugin : BaseUnityPlugin
{
    private IWebOverlay overlay;

    private void Open()
    {
        overlay = WebOverlays.Create("My panel", new OverlayOptions { DevTools = true });
        if (overlay == null)
            return;                       // no runtime: use your own fallback

        overlay.LoadHtml("<h1>Hello</h1>");
        overlay.MessageReceived += text => { /* the page called postMessage */ };
        overlay.Post("hello page");       // arrives as a message event
    }
}
```

From the page:

```js
window.chrome.webview.postMessage('button pressed');
window.chrome.webview.addEventListener('message', e => console.log(e.data));
```

`WebOverlays.Create` returns `null` rather than throwing whenever overlays are
unavailable, so a fallback is always a null check. Events arrive on the overlay
thread - queue them and touch game state from `Update()`.

Install the demo plugin to see a working panel: press **F10** in game.

## Requirements and limits

- Needs the Microsoft WebView2 runtime, which current Windows 10 and 11
  installations already include. Without it `WebOverlays.Create` returns null.
- Needs borderless windowed or windowed mode. In exclusive fullscreen a window
  over the game would minimise it, so check `WebOverlayPlugin.IsDisplayModeSupported`.
- While the overlay holds the keyboard the game does not see key presses, and
  the other way round. That is why the window has a title bar with a close
  button by default, and why `OverlayOptions.CloseKeys` exists.

## How it works, and why it looks like this

- **One browser for the whole game.** Every WebView2 environment starts its own
  browser process tree and wants its user-data folder to itself, so the library
  keeps a single environment and gives out as many overlay windows as mods ask
  for.
- **Its own thread.** WebView2 is COM and needs a thread that is STA and pumps
  messages. The game's main thread is neither, so the library runs one.
- **Owned popup windows, not child windows.** Unity presents through a
  flip-model swapchain, which does not composite child windows.
- **Hand-built COM vtables instead of Microsoft's managed wrapper.** The wrapper
  cannot be used under Unity's Mono: the SDK marks inherited vtable slots with
  `_VtblGap`, Mono ignores those markers, and native calls then land on the
  wrong function - measured, it kills the process with no managed exception.
  Function pointers taken from delegates work reliably, so every interface used
  here is bound by explicit slot number, taken from the official `WebView2.h`.
  **Only members of the v1 interfaces may be used**; see `Interop/WebView2Api.cs`.

## Third-party components

`WebView2Loader.dll` belongs to the Microsoft WebView2 SDK and is redistributed
under BSD 3-Clause; see `WebView2-LICENSE.txt` and `WebView2-NOTICE.txt`. The
WebView2 runtime itself is not redistributed.

## License

MIT.
