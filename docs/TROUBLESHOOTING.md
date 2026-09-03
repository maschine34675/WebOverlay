# Troubleshooting

By symptom. Almost everything here has the same shape: nothing reports an
error, because nothing is in error - an overlay is an operating-system window
over a game that was not expecting one, and the surprises live in the seams.

## The two diagnostic switches

**Diagnostics / Log cursor state** is the mouse-and-focus instrument - the
whole cursor chapter below was measured with it. It reports which
window the system has in front, what the cursor is doing, and whether Unity
sees the mouse move at all. Off by default, and of no use except while
writing a bug report.

Its neighbour, **Diagnostics / Log page problems**, answers the other kind of
report: a script error, a rejected promise, a console error or a font that
would not load, from inside the page itself. Nothing in there reaches the log
otherwise - a refused font renders as a silent fallback, and the report says
"it looks wrong" and nothing more. Also off by default. The hooks are part
of the script injected when a window is created, so switching it on reaches
windows opened afterwards - not one already on screen, and not merely the
next page in it.

Both sit in the F12 configuration menu under **Diagnostics**, behind its
Advanced toggle, so players never meet them. Everything below assumes you can
switch them on when a symptom has no other trace.

## The window never appears

Needs borderless windowed or windowed mode. In exclusive fullscreen a window
over the game would minimise it, so `Show()` refuses and logs there - from
1.11.0 on; before that the plugin's probe read the display mode off the main
thread and the refusal never fired in the game. Current Escape From Tushonka
appears to stay borderless even when set to fullscreen, so this is insurance
rather than a case you are likely to meet;
`WebOverlayPlugin.IsDisplayModeSupported` is still public for a mod that
wants to explain the situation in its own interface, and a mod that asks
`Show(cb)` hears `RefusedFullscreen`.

And the failure that arrives late rather than never:

Needs the Microsoft WebView2 runtime, which current Windows 10 and 11
installations already include - and which any machine that has started the
SPT launcher demonstrably has, since the launcher's own interface is a web UI
hosted in WebView2. Without it the failure surfaces asynchronously: the first
`WebOverlays.Create` still returns a handle whose `Failed` event fires
shortly after; later calls return null.

## The mouse stops turning the player

**A window over the middle of the screen takes the mouse away from the
game.** While the game has focus it locks the cursor to the centre of its own
window, and Windows delivers mouse movement to whatever window sits under the
pointer - so a panel covering that point receives the movement instead, and
the player cannot turn. Nothing reports an error: the cursor state is
correct, the game has the foreground, and the input simply never arrives.
Holding a mouse button restores it, because that gives the game window a
capture.

It is worth knowing while choosing a size and a position, and the default is
the trap: a window that sets no `Width` and `Height` gets 80% by 85% of the
game's picture, centred on it, which cannot avoid that point. A smaller one
placed to the side never meets the problem at all, and setting a size of your
own is the cheapest way past this whole section.

`ClickThroughWhenUnfocused` is the answer for a panel that cannot avoid it:
while the game is in front, the mouse passes straight through the overlay to
the game. The panel stays visible and keeps updating; what it loses is being
clickable, so bring it back with its hotkey rather than by clicking it. Off
by default, because a panel that does not cover the centre wants no such
thing - and pointless for a HUD, which never holds the foreground anyway.
It engages only while the game actually holds the mouse, so in menus the
panel behaves like any other window.

## The cursor flickers, or a framed window cannot be pointed at

A framed overlay takes the foreground, and a game that captures the mouse
keeps capturing it - which leaves the window unreachable mid-raid. Set
`FreeCursorWhileShown` and the library hands the cursor back while such an
overlay is the window in front.

It does that by asking the game to want the cursor, not by overruling it.
The game decides once per frame what the cursor state should be and writes
it only when the live state disagrees - so a mod that simply sets
`Cursor.visible` creates that disagreement every frame, and the two
alternate at frame rate. That is the flickering cursor every overlay mod
runs into, and it is worse than it looks: the game's write also swaps the
cursor bitmap for a transparent one, which a mod forcing the property never
restores. Setting the game's own "show the cursor" flag instead means there
is nothing to disagree about, and the single write the game then performs
restores visibility, lock mode and bitmap together.

The flag is reached by reflection, so this library still references nothing
but BepInEx and Unity. A game that does not have it falls back to setting the
properties directly, which flickers - better than an unreachable window. Note
that the flag is global and has no counter: a second mod releasing it takes
the cursor from the first. A mod doing this itself, rather than through this
library, should raise `EFT.GlobalEvents.ToggleShowInGameCursorEvent` the way
the game does - or register an input node whose `ShouldLockCursor()` returns
`ECursorResult.ShowCursor`, which composes properly because the input tree
takes the maximum across nodes.

One flicker is not yours: the game's own F12 configuration menu writes the
cursor from three places per frame while it is open, the game writes it back,
and the two alternate. That happens in a vanilla installation with no overlay
anywhere - close the menu and it stops.

## Fonts look wrong

A page rendering in a fallback face means a `@font-face` was refused, and web
fonts are refused by default across origins - including a `LoadHtml` page's
own only host, because an inline page has an opaque origin. The fix is
`VirtualHost.Access` on the host serving the fonts; the whole mechanism is in
[RECIPES.md](RECIPES.md#pages-with-real-files). **Diagnostics / Log page
problems** turns this exact silence into a log line naming the family.

## A HUD suddenly shows a dark background

WebView2 transparency has regressed before in runtime updates (opaque instead
of transparent, runtime 145.x, fixed since). If it happens right after a
Windows update, suspect the runtime before suspecting the mod.

## Keys go to the window, or to the game, unexpectedly

While the overlay holds the keyboard the game does not see key presses, and
the other way round. That is why the window has a title bar with a close
button by default, and why `OverlayOptions.CloseKeys` exists. The title bar
is recolored to a dark game-appropriate grey (Windows 11 exact, Windows 10
dark mode, older keeps the stock look); `OverlayOptions.Frame = false`
removes it entirely - then the close keys are the only way out, so make sure
they are set.

`WebOverlayPlugin.VirtualKey(KeyCode)` and `CloseKeysFor(KeyboardShortcut)`
turn a configurable hotkey into the virtual-key codes `CloseKeys` wants.

## Reporting a problem

A report that can be acted on has:

- the library version (the log line `Anvil-WebOverlay <version> ready.` in
  `BepInEx/LogOutput.log`), the mod's version, and the SPT version;
- the window mode (borderless windowed, windowed, fullscreen);
- `BepInEx/LogOutput.log` from a session where the problem happened -
  ideally with the matching **Diagnostics** switch on: *Log cursor state* for
  anything mouse- or focus-shaped, *Log page problems* for anything that
  looks wrong inside the page;
- for a page problem, whether the same page misbehaves in the
  [preview tool](RECIPES.md#previewing-a-page-without-starting-the-game),
  which takes the game out of the picture.
