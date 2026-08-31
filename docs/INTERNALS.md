# Internals

Why the library is built the way it is. Nothing here is needed to use it -
this is for the curious, for reviewers, and for anyone building something
similar.

## How it works, and why it looks like this

- **The bundled `WebView2Loader.dll` is a bootstrapper, not the browser.** It is
  160 KB that locates the WebView2 runtime already installed on the machine,
  loads its client DLL and forwards to it; every browser feature comes from that
  runtime. The runtime itself is a full Chromium build of several hundred
  megabytes which Microsoft distributes rather than letting apps ship it: it is
  in-box on Windows 11, was rolled out to Windows 10 through Microsoft Edge, and
  updates itself through the Edge updater, not through this library. That is
  also why the loader's own version matters so little - it is version-agnostic
  by design and only skips runtimes below its minimum. Which features exist is
  decided per interface at `QueryInterface` time, so an older runtime does not
  fail to load; individual capabilities simply fall back, exactly as HUD
  transparency does when composition support is missing.
- **One browser for the whole game, almost always.** Every WebView2 environment
  starts its own browser process tree and wants its user-data folder to itself,
  so the library keeps a single environment and gives out as many overlay
  windows as mods ask for. There is one exception, forced by the browser: a
  browser that is hosting a transparent overlay refuses to create a windowed
  one (`ERROR_INVALID_STATE`), and environments sharing a user data folder
  share the browser. So when a mod opens a window while another mod's
  transparent overlay is up, that window gets a second browser of its own -
  about six processes and a quarter of a gigabyte, measured, which is why it is
  created only for that case and not up front. Pages in it have their own
  browser profile, so per-origin storage does not carry across.
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
  Members of versioned interfaces (`ICoreWebView2Controller2` and later) are
  reached only via an explicit `QueryInterface` plus an absolute slot counted
  through every inherited member - and each such slot must be proven by an
  observable effect before it is trusted; see `WebOverlay/Interop/WebView2Api.cs`.
- **A HUD is composed, not drawn into its window.** The browser renders into a
  DirectComposition visual instead of a child window
  (`WS_EX_NOREDIRECTIONBITMAP`), which is what makes true per-pixel alpha and
  forwarded mouse input possible at all. One trap worth knowing if you build
  something similar: `WS_EX_TRANSPARENT` only takes a window out of hit-testing
  when `WS_EX_LAYERED` is set as well, so a display-only HUD carries both -
  learned from a release where it swallowed every click.
- **The chroma key is what older systems fall back to.** DWM applies
  `LWA_COLORKEY` to a window's classic redirection surface, which Chromium's
  GPU compositing bypasses - so keying the page's own pixels does not work
  (measured). What does work: `DefaultBackgroundColor` alpha 0 makes the
  browser render nothing where the page paints nothing, those pixels show the
  window's key-color background brush, and the chroma key replaces exactly
  them with the game. Hit-testing reads the same surface, which is why such a
  HUD is click-through everywhere and cannot be selective.

One caution: WebView2 transparency has regressed before in runtime updates
(opaque instead of transparent, runtime 145.x, fixed since). If a HUD suddenly
shows a dark background after a Windows update, suspect the runtime first.

## The proof behind it

[`tools/Probe`](../tools/Probe) is a host that drives the built DLL outside
the game. It is the proof behind every hand-bound vtable slot in this library
and the source of every row in [FAULT-TESTS.md](FAULT-TESTS.md); it ships in
no release. A slot number read off a header is a guess until something observable
changes because of it. `tools/Audit-SoftDependency.ps1` is the same idea
pointed at consumers: the soft-dependency rules are mechanical, so the build
enforces them.

## Review history

The library was reviewed repeatedly during development - code reviews,
regression reviews per release, and two outside first-contact assessments.
Those reports are dated snapshots of the commits named in their headers and
live outside the repository; what came of them is recorded where it belongs:
fixes in [CHANGELOG.md](../CHANGELOG.md), measurements in
[FAULT-TESTS.md](FAULT-TESTS.md). The `CONSUMER-API-WISHLIST` pair in this
folder is the one piece of that history kept here, because its answers
explain several API decisions. None of these files describes the
current state. What is true now lives in three places: this
documentation, [FAULT-TESTS.md](FAULT-TESTS.md) for what is measured, and
[CHANGELOG.md](../CHANGELOG.md) for how it got here.
