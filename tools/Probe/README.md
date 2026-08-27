# The probe

A plain .NET host that drives the built `Anvil-WebOverlay.dll` outside the
game. It exists for two reasons:

- **Proof.** Every hand-bound vtable slot in this library was verified here
  before it was trusted. A slot number read off a header is a guess until
  something observable changes because of it - a pixel, a click, a message.
  Every row of [`docs/FAULT-TESTS.md`](../../docs/FAULT-TESTS.md) is one mode
  of this program.
- **Preview.** The `preview` mode shows *your* page in a real overlay, with
  the same window, the same transparency and the same message bridge as in a
  raid - so a layout can be worked on without starting the game.

It is not part of any release. It targets net9.0 rather than the net472 the
plugin needs, because nothing here runs inside Unity.

## Building

Build the library first: the probe references the built DLL rather than the
project, so that it tests exactly the binary that ships.

```bash
dotnet build WebOverlay/WebOverlay.csproj -c Release
dotnet build tools/Probe/Probe.csproj -c Release
```

`--no-build` on a later `dotnet run` reuses the DLL copied into the probe's
output folder, so after changing the library, build both again.

## Previewing a page

```bash
dotnet run --project tools/Probe -c Release -- preview path/to/page.html --transparent
```

| Option | |
|---|---|
| `--transparent` | no frame, alpha against a coloured backdrop |
| `--interactive` | transparent and clickable - a HUD that takes input |
| `--size WxH` | default 900x600 |
| `--host <name>` | serve the folder under this host name instead of `preview.local`; match your mod's own `VirtualHost` when the page uses absolute URLs or `localStorage` |
| `--theme` | inject the `--wo-*` colour tokens |
| `--post <ch> <text>` | send on a channel once the page has loaded (repeatable) |
| `--send <text>` | the same without a channel (repeatable) |
| `--screenshot <file>` | save a PNG of the window, then keep showing it |
| `--seconds <n>` | how long to leave it open (default 10) |
| `--devtools` | open the browser developer tools |

A local file is served from its own folder, so relative assets and storage
work as they do in a mod. Anything the page sends back is printed, and
`overlay.request('preview', ...)` is answered, so a page can be tried out
before the mod behind it exists.

A page the mod assembles at run time - one with placeholders spliced in before
`LoadHtml` - cannot be shown as it sits on disk. Preview the file the mod
would produce, or keep the parts separate and let the page fetch them.

[`sample-page.html`](sample-page.html) is a small worked example of the three
things every overlay page does: read the environment, take messages, answer
questions.

```bash
dotnet run --project tools/Probe -c Release -- preview tools/Probe/sample-page.html \
  --transparent --theme --post status "raid: Customs"
```

## Running the matrix

Each mode is one row of the fault table; it prints `PASS`/`FAIL` lines and
exits non-zero on a failure.

```bash
dotnet run --project tools/Probe -c Release -- channels
```

Modes: `fault-loader`, `fault-bightml`, `fault-dispose-race`,
`fault-redirect`, `script-roundtrip`, `bounds-save`, `bounds-verify`,
`latency`, `glass`, `glass-click`, `cube`, `storage`, `vhost`, `dispatch`,
`failure-kind`, `vhost-fail`, `nav-reject`, `script-result`, `visibility`,
`close-race`, `shutdown-quiet`, `channels`, `shape`, `bounds-api`,
`shape-guards`, `api17`, `mixed`, `mixed-reverse`, `dcomp-first`, `footprint`,
`spare-browser`, `spare-folder`, `retained`, `latest-only`, `manual-pump`,
`failed-nav`, `generation`, `ready-load`, `click-through`, `vhost-cors`,
`ordering`, `page-diag`, `bounds-locked`, `nav-race`, `channels-dead`,
`downloads`, `flood`.
Running the program with no mode takes the normal path: create, load, render,
message back.

Two things to know before reading a failure:

- **`glass`, `glass-click`, `cube`, `shape` and `shape-guards` need a desktop
  that is actually being composited.** They sample real screen pixels and send real mouse input, so
  they need more than a logged-in session: they need one whose desktop is
  being drawn. Over RDP that fails in two ways, and both look identical -
  every sample comes back `rgb(0,0,0)` and every click lands nowhere:

  - the session is disconnected (`query session` shows `Disc` rather than
    `Active`), or
  - the session is `Active` but the remote desktop **window is minimised**,
    which suspends composition just as thoroughly. This one is the trap:
    the session state looks perfectly healthy.

  Both were measured; restoring the window turns all five modes green again
  with no change to the library. Even with the desktop drawing, the compositor
  occasionally drops a frame during a capture, so re-run before believing a
  single pixel failure.
- **Do not use the machine while `glass`, `glass-click`, `cube`, `shape`,
  `shape-guards` or `click-through` are running.** They sample real screen
  pixels and send real mouse input at real desktop coordinates. A window of
  yours over the test area takes those clicks - which happened: a batch run
  clicked inside a running application and hit a button in it, then the
  application came to the front and the six modes failed together while each
  passed on its own afterwards. The failures were the interference, not a
  regression.

  Since then a click is refused outright unless that point belongs to a
  window the probe owns; the mode then prints `SKIP` and exits `3`, so "come
  back when the desktop is free" is distinguishable from "this broke". The
  guard asks about the *top-level* window rather than the one under the
  pointer, because the page lives in a child window owned by the WebView2
  browser process - checking the child refuses every real click instead.

  It cannot make these modes safe to run alongside your own work. It only
  stops them doing damage.
- **`fault-loader` and `failure-kind` move `WebView2Loader.dll` aside** for
  the length of the run, because they test what an incomplete plugin folder
  does. They put it back afterwards, including after an earlier run that was
  killed mid-block. If a build ran in between, the fresh copy wins.

`bounds-save` and `bounds-verify` are one row in two halves: run `bounds-save`,
then `bounds-verify` in a second process, which is the point - the store has to
survive the process, not just the window.
