# Changelog

Version-specific changes live here and nowhere else: the GitHub release notes
and the Forge version field are filled from these entries, and `README.md`
describes the library as it is now rather than how it got there.

Each version starts with the short player-facing copy for the Forge version
field, followed by the detailed record for anyone reading the source later.

Minor releases are additive: they add members and change nothing a consumer
depended on. So a mod gates on "at least X.Y" rather than on presence, and
every entry below names the version a member arrived in - see
[`docs/SOFT-DEPENDENCY.md`](docs/SOFT-DEPENDENCY.md).

## [Unreleased]

## [1.8.10]

### Forge version notes

- Fixed: a half-transparent panel turned solid the first time the mouse was
  handed back to the game, and stayed solid.

### Fixed

- `ClickThroughWhenUnfocused` discarded `Opacity`. Letting the mouse through
  means making the window layered, and a layered window paints nothing until
  its attributes are set - so they were set, to fully opaque. Nothing writes
  the alpha again after the window is created, so the fade was gone for the
  session. It now writes the alpha the mod asked for.

- A navigation the origin filter refused threw away the target of the page
  that was still on screen. The browser accepts the request and only the
  filter turns it down, so the old document stays - but the overlay then
  reported `IsPageLoaded` false about a window that was showing a page,
  buffered every send into nothing, and counted the next `LoadHtml` as a first
  page rather than a retarget, which let that buffer run into it. It now
  restores the previous target, exactly as it already did when the browser
  refused the request outright. A first navigation still has nothing to come
  back to and still forgets.

- The diagnostic line that names the foreground window asked whether that
  window wanted the cursor, not whether it was one of ours. A panel that had
  only asked for click-through was reported as some other application's
  window - in the one line written to identify it.

### Notes

- Rows 59-62. Both fixes were checked by putting the fault back: without them
  row 59 reports `IsPageLoaded=False` with sends vanishing, and rows 61-62
  report alpha 255 for a panel that asked for 128 and never get it back.

## [1.8.9]

### Forge version notes

- The one setting this library shows is now behind the settings menu's Advanced
  switch, where a diagnostic belongs. Players see nothing from it.

### Changed

- `Diagnostics / Log cursor state` is marked advanced. It answers one kind of
  bug report - "the mouse stopped working while a panel was open" - and is of
  no use to anyone not writing that report. It is also the only setting this
  library binds, so an ordinary player's settings menu now shows nothing from
  it at all, which is the right amount for a library that consumers install on
  a player's behalf.

  It stays rather than being removed: this release series exists because that
  bug could not be told apart from correct behaviour without knowing which
  window the system had in front, and the switch is what answered it.

## [1.8.8]

### Forge version notes

- Fixed: with a panel open, a menu that shows the mouse pointer made the
  library rewrite the window twice per frame for as long as the menu was up.

### Fixed

- `ClickThroughWhenUnfocused` acted on a contested reading of the cursor. When
  two parties write it in the same frame - a configuration menu showing the
  pointer, the game hiding it again - the answer alternates at frame rate, and
  1.8.7 dutifully rewrote the window's extended style each time: two
  `SetWindowLongPtr` calls plus a `SetLayeredWindowAttributes`, about a hundred
  times a second, on a window owned by another thread.

  Neither reading is wrong. The state is contested, and the honest answer to a
  contested state is to keep the last settled one, so the belief now changes
  only after the same answer has held for eight frames - about a tenth of a
  second, below noticing, and an alternating answer never gets a run that long.

- The per-frame pass ran twice per frame. `Update` and `LateUpdate` both called
  the same routine, and the click-through check, the mouse observation and the
  diagnostic line rode along with it. `LateUpdate` exists only for the fallback
  that keeps writing the cursor; a second look in the same frame learns nothing
  and doubled the cost of everything above.

### Changed

- The cursor diagnostic holds itself to one line per second and says how many
  changes it skipped. It printed on every change, which is correct until
  something changes every frame - and then the instrument that was supposed to
  explain the flood becomes it. The switch stays off by default.

### Notes

- Rows 57 and 58: an answer that alternates on every frame leaves the window
  style untouched, and a change that settles still takes effect. The probe
  gained a way to ask for frames, because a rule expressed in frames cannot be
  tested by a single call.

## [1.8.7]

### Forge version notes

- Fixed: a panel that lets the mouse through could not be clicked again
  afterwards. It now only does so while the game has the mouse, so in menus it
  behaves like any other window.

### Changed

- `ClickThroughWhenUnfocused` now engages only while the game actually holds
  the mouse - cursor hidden or locked, the way it is while the player is
  looking around.

  As shipped in 1.8.6 the option cost a clickable window outright: once the
  panel lost the foreground, nothing could give it back but its hotkey. That
  was never the intent. The trap it exists for is the game reading mouse
  movement at the centre of its own window, and a game showing a free cursor -
  a menu, the stash, a trader - is not doing that. There is nothing to hand
  over then, so nothing is handed over and the panel stays a mouse target.

  The library cannot see Unity, so the plugin installs the test as
  `OverlayHost.CursorCapturedProbe`, the same bridge shape as
  `MainThreadPumpAvailable` and `DisplayModeProbe`. No probe means "assume
  captured": the state that keeps the player able to turn, which is the failure
  worth defaulting against.

### Notes

- Row 56: another window in front but the cursor free - nothing passes through
  and the panel is usable on the next click.
- Rows 54 and 55 measured less than they claimed. The probe focused the other
  window, which also raised it *above* the overlay, so the click landed behind
  because nothing was in the way - not because the overlay let it past. Both
  rows passed while proving nothing. The probe now raises the other window
  without focus, so the overlay stays on top exactly as an owned window sits
  above the game, and the rows fail if the click stops passing through.

## [1.8.6]

### Forge version notes

- Fixed: a mod's panel covering the middle of the screen could stop the mouse
  from turning the player, with no way to tell why. Mods can now let the mouse
  through to the game while the game is in front.

### Added

- `OverlayOptions.ClickThroughWhenUnfocused` - while the game is the window in
  front, the mouse passes through the overlay to the game instead of landing on
  it. Off by default.

  It exists for a trap that took a whole evening to find and is invisible from
  inside the library: the game locks the cursor to the centre of its own window
  while it has focus, and Windows delivers mouse movement to whatever window
  sits under the pointer - so a panel covering that point receives the movement
  and the player cannot turn. Nothing reports an error, because nothing is in
  error: the cursor state is correct, the game has the foreground, the input
  simply arrives somewhere else. Holding a mouse button restores it, because
  that gives the game window a capture, which is what made it look like a
  cursor problem for so long.

  The cost is that the panel can no longer be clicked to bring it back to the
  front, since a click is mouse input like any other; its hotkey still works.
  Pointless for a HUD, which never takes the foreground.

### Notes

- Probe mode `click-through`, rows 53-55: with the overlay in front the page
  gets a real click; with another window in front the same click reaches the
  window behind and the page does not see it; focusing the overlay again gives
  it back. That last pair also settles a doubt the implementation raised - the
  browser's own child window does not swallow the click.

## [1.8.5]

### Forge version notes

- Fixed: with a mod's panel open the mouse could stop working - no cursor, and
  the game would not turn with it either - until the panel was closed again.

### Fixed

- `FreeCursorWhileShown` never fired for a panel that was shown and focused in
  one go, which is what opening one normally does. Whether an overlay wants the
  cursor was decided by `window == foreground` **and** the library's own record
  of having shown it - and `Show()` makes the window visible and takes the
  foreground *before* it writes that record. Asked once per frame, the gap in
  between is easy to hit: the library then said "not one of mine" about a
  window sitting in front of the player, never asked for the cursor, and the
  cursor stayed captured for as long as the panel was up. A mod muting game
  input meanwhile - the usual thing to do while a panel has focus - left the
  mouse dead in both directions.

  Being the foreground window is now the whole test. Windows does not give the
  foreground to a hidden window, so the second condition never added anything
  except the race.

  Found by reporting rather than reading: three passes over this code produced
  three wrong answers, and the switch added in 1.8.4 printed the window handles
  and the lifecycle that made it obvious. That reporting stays.

## [1.8.4]

### Forge version notes

- Fixed: after closing an overlay, the mouse could be left invisible without
  the game taking it back, so looking around stopped working until the game
  was refocused.

### Fixed

- Handing the cursor back is checked, one frame later, and repaired if the game
  did not complete it. Asking the game for the cursor rather than overruling it
  is what stopped the flicker in 1.8.3, and it works because the game writes
  the cursor state only when the live state disagrees with what it wants. The
  cost of that is a state the game agrees with by accident, which it therefore
  never corrects - and "hidden but not captured" is exactly such a state: the
  pointer is invisible and the mouse moves it instead of turning the player, so
  the game appears frozen to the mouse while the keyboard still works. It is
  reachable because the lock mode and the visibility are set by different
  parties at different moments; the game reapplies the lock from the *current*
  visibility when its window regains focus, which is the same moment this
  release happens. The library now looks once, after giving the cursor back,
  and captures the mouse itself if nothing else did. One write, only when that
  state is actually observed, and a warning the first time - so a report of it
  says whether this is what happened rather than leaving it to be inferred.

## [1.8.3]

### Forge version notes

- The mouse cursor no longer flickers while an overlay that frees it is open.

### Fixed

- `FreeCursorWhileShown` asks the game to show the cursor instead of overruling
  it every frame. The game decides once per frame what the cursor state should
  be and writes it only when the live state disagrees, so setting
  `Cursor.visible` is precisely what makes it disagree - the two then alternate
  at frame rate, which is the flicker. Worse, the game's write swaps the cursor
  bitmap for a transparent one, which forcing the property never restores. The
  library now sets the game's own "show the cursor" flag, once per change of
  state rather than once per frame; the game then agrees, stops writing, and
  the single write it did perform restored visibility, lock mode and bitmap
  together. Released again when no overlay wants it and when the plugin shuts
  down.

  Found and read out of the game's own code: it uses the same flag where its
  world needs a cursor mid-raid. Reached by reflection, so this library still
  references nothing but BepInEx and Unity, and a game without it falls back to
  the previous behaviour rather than breaking. The flag is global and has no
  counter - two mods using it will take the cursor from each other, which the
  README says.

- A page the mod asks for once, not twice. The usual consumer shape is a
  `Ready` handler that calls `LoadHtml`, and that work item runs before the
  browser confirms the channel shim - so the deferred first navigation started
  the same document a second time: two generations, `PageLoaded` twice, the
  page's own first scripts twice. The debt to the shim is settled by whichever
  navigation actually reaches the browser first.
- Page state is dropped when a navigation *starts*, not when it is asked for.
  The browser can accept a request that this library's own origin filter then
  refuses - a URL with no origin to trust, a host whose folder mapping failed -
  and the page on screen stays exactly where it was. Forgetting its retained
  state and its outbox at the moment of the request threw that away anyway,
  which is the same silent loss as a synchronously rejected navigation, reached
  through the asynchronous refusal instead. A refused navigation now leaves
  neither the target nor the state behind, and the overlay stops waiting for a
  start that will never come.
- Should the game refuse to take the cursor back, `FreeCursorWhileShown` falls
  back to setting it directly instead of believing the game still holds it -
  which would have left the player without a cursor, the exact opposite of the
  fallback the bridge documents.
- Retargeting means the browser had actually taken the previous page, not
  merely that a target had been recorded. A page named while the browser is
  still starting is only written down, and replacing a note is not leaving a
  page - so state the mod set up beforehand still belongs to whichever page
  finally loads. This surfaced as a race introduced by the change above: with
  the first navigation now waiting for the shim, whether that state survived
  depended on which of the two won, and that is not something a consumer
  should be able to observe.

### Notes

- Probe rows C0-C2 in `api17` cover the fallback: outside the game the bridge
  reports itself unavailable and refuses quietly instead of throwing at a
  caller that only wanted a cursor. Row 41 covers the retarget rule, and is
  what caught the race; rows 50-52 and the new `ready-load` mode cover the
  refused navigation and the single first page. Both were verified by watching
  them fail first - `ready-load` reports two `PageLoaded` without the fix.

## [1.8.2]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Fixed: an answer the game was too busy to deliver could be dropped instead of
  arriving late.
- Fixed: a page that reloads no longer receives an answer meant for the page
  before it.

### Fixed

- `EventDispatch.MainThread` could drop a script result or a page answer
  silently. The main-thread queue reports a full queue as delivered, and the
  result path believed it - so a stalled frame loop swallowed the answer. This
  is the same broken promise 1.8.1 closed for `EventDispatch.Manual`, in the
  half that was missed: answers now pass the limit, which cannot run away
  because the number outstanding is bounded by the calls that asked.
- A question belongs to the document that asked it. The page numbers its
  questions from 1 again in every new document and matches an answer on that
  number alone, so a reply the mod took its time over - the deferred
  `OnRequest`, or any reply at all under main-thread dispatch - could resolve
  whichever question the *next* document happened to number the same. Replies
  are now bound to the document that asked, in the mod's hands and in the
  outbox, and dropped when that document is gone. The host-to-page direction
  was never affected: those ids never restart.
- A late `NavigationCompleted` from a navigation that had already been replaced
  was taken at face value. It could mark the page the mod is waiting for as
  loaded, flush the outbox into a document on its way out, and report a
  superseded navigation's cancellation as a failure of the new one. A
  completion arriving before its own navigation has started is now ignored.
- The first navigation waited for nothing: the channel shim is installed
  asynchronously, and the browser only promises it is in place once the
  completion has run. The mod's own first page could therefore have come up
  without `window.overlay` - which its first script may use. Only the
  navigation waits; `Ready` and the window do not, since `Ready` has never
  meant "the page is loaded".
- A request that timed out while still waiting for the page could be put to
  that page anyway once it loaded, running a handler with side effects for an
  answer nobody was listening for. The question is now withdrawn with its
  deadline.
- `ExecuteScript` and `Post` on an overlay whose window had already closed
  buffered into an outbox nobody would ever flush, leaving a script caller
  waiting until the handle was disposed. They are answered at once, as they
  already were on a failed overlay.
- Creating a windowed overlay may have to start a second browser, and waiting
  for one pumps messages - which can run that overlay's own close. Creation now
  asks again afterwards whether anyone still wants it, instead of building a
  view for a window that has gone.
- `Navigate` to the page already showing counted as a retarget and threw away
  the retained state, while the same page reloading itself kept it. Two routes
  to the same visible outcome, two different results. A navigation to the
  current target is a reload.
- The demo plugin declared its dependency on the library without a minimum
  version while using API from 1.7.0 - the failure would have arrived at JIT
  time, long after BepInEx called the dependency satisfied. It now names the
  version, from the library's own constant.

### Changed

- The virtual-host documentation said cross-origin requests to the mapped
  folder are denied. That is true of `fetch` and XHR and not of ordinary
  sub-resource loads: another origin allowed in the same overlay can still pull
  a file in as a script, image or iframe. The wording is corrected in all four
  places it appeared. The access kind stays `DENY_CORS` rather than the
  stricter `DENY`, deliberately - an inline `LoadHtml` page has an opaque
  origin, so under `DENY` even the mod's own markup could not reach its assets.
- The README's links point at the repository rather than at neighbouring files,
  since it also ships inside the release zip where nothing else does.

### Notes

- Probe mode `generation`, rows 48-49; two half-written assertions in existing
  modes finished rather than deleted. `docs/FAULT-TESTS.md` now says how each
  row is evidenced, because one row was claiming a proof the probe does not
  perform.

## [1.8.1]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Fixed: a mod's settings could still be lost after a page reload, in the one
  case 1.8.0 did not cover - when a page the mod asked for was refused by the
  browser.
- Fixed: a rare crash when the second browser had to be started more than once.
- Fixed: a page that simply is not there - a typo in a file name - now says so
  in the log instead of leaving the overlay to sit there looking slow.
- Fixed: an overlay that has died no longer reports itself as showing a page.

### Fixed

- A navigation the browser refuses now leaves the overlay exactly as it was.
  `Navigate` and `LoadHtml` used to drop the buffered sends and the retained
  state *before* the call that turned out to be rejected, so the page that
  stayed on screen lost the state belonging to it, and the next reload - the
  library's own after a renderer crash, or the page calling `location.reload()`
  - handed it its defaults while the mod still believed its configuration was
  up. That is precisely the silent loss `PostOptions.Retain` exists to prevent,
  reached through a failed attempt instead of a reload. A *successful* retarget
  still forgets, as documented.
- A page named before the browser exists is navigated to once the view is
  created, and that attempt could be refused too - the oversized inline page, a
  URL the browser will not take. Its result was never looked at, so the refused
  page stayed the overlay's target: every send buffered into nothing,
  `IsPageLoaded` stayed false for good, and the mod's next `LoadHtml` looked
  like a retarget away from it and threw out state that had never belonged to
  any page. The overlay now goes back to "no page named", which is what it is.
- The completion handler of a second-browser attempt that timed out is handed
  to the leak list before the next attempt overwrites the field. It stays
  registered with a browser that may still answer, and the managed object it
  calls through was only kept alive by that field - so a retry could leave the
  first handler's thunks to be collected while native code could still call
  them. Reachable since 1.7.0, when a failed spare stopped being remembered so
  the next overlay would try again.
- The XML documentation for replaying retained state sat above
  `forgetPageState`, which does the opposite; `SetBounds` carried a second
  stray summary block in the same shape, from 1.6.0. Both merged into the
  member they describe.
- A navigation that fails outright - a file missing behind a mapped folder, a
  host that will not answer - produced no output whatsoever. A typo in a page
  name was indistinguishable from a slow load: `IsPageLoaded` stayed false, the
  outbox filled quietly, and nothing anywhere said why. It is now one warning
  naming the page and the browser's own error status. Only a warning: the
  target stands, because it still is the target, and the mod may fix the URL
  and try again.
- `fail()` left `pageReady` and `pageLoaded` set, so `IsPageLoaded` went on
  claiming a page was live on an overlay that had just died - the one flag a
  streaming consumer reads before sending. It now retires the page, answers
  every script caller still waiting (they will never get a value now), and
  refuses later sends with that same answer instead of buffering them into a
  page that no longer exists.
- A renderer crash left the scripts that were running in it unanswered until
  the handle was disposed, although the reload starts a document that never saw
  them. They are answered when the crash is handled.
- A reload after a renderer crash that the browser refuses outright ends the
  overlay with `RendererCrashed` instead of leaving it quietly blank with no
  `Failed` and no further attempt. The same refusal on the creation path is
  deliberately *not* a failure: the mod named a page the browser will not take,
  which is a content bug in the mod rather than a broken window, and the next
  `LoadHtml` loads fine. Failing there would destroy a working overlay over one
  bad page. The difference is what there is to go back to - on the crash path
  the page was live and now there is nothing.
- `EventDispatch.Manual` could swallow an answer. Results travelled the event
  queue, which is dropped when the handle is disposed - correct for events,
  which are documented as droppable, and a broken promise for an answer, which
  is documented as always arriving. Answers now have a queue of their own:
  never dropped on overflow, drained first by `PumpEvents()`, and handed over
  on the spot when the handle is disposed, since nobody pumps a handle they
  have thrown away.
- A `Create` and a `Dispose` posted in that order could arrive the other way
  round, because commands and creations have had separate queues since 1.7.0
  and commands are drained first. The overlay then built a window and a browser
  view whose owner was already gone and whose close had been and gone. Creation
  now stops when the handle is already closed.
- A second browser that can never start was asked for again by every windowed
  overlay, each time holding the creation queue for the full timeout. It is
  now asked for at most three times, and the wait for it is ten seconds rather
  than thirty - it is optional, and the browser it clones is already running.
- The documentation for the latched `Ready` and `Failed` claimed a late
  subscription runs inside the `+=`, carving out only `DispatchOnMainThread`.
  With `EventDispatch.Manual` it waits for the consumer's own pump, which
  matters to a soft-dependency gate deciding whether to fall back. Corrected
  here, in the README, and in `docs/SOFT-DEPENDENCY.md`, along with the
  threading sentences on `ExecuteScript` and `Request`.

### Repository

Nothing here changes the shipped DLL.

- `tools/Probe` - the standalone host that drives the built DLL outside the
  game, previously a throwaway harness kept out of the tree. Every row of
  `docs/FAULT-TESTS.md` is one of its modes, and every hand-bound vtable slot
  in this library was proven by one of them. `fault-loader` and `failure-kind`
  now stage their own incomplete plugin folder instead of depending on how the
  harness happened to be laid out.
- `preview` - the mode for mod authors rather than for the library: it shows a
  page in a real overlay, feeds it channel messages, prints what it sends back
  and screenshots the result, so a HUD can be built without launching a raid.
  `tools/Probe/sample-page.html` is a worked example.
- `docs/SOFT-DEPENDENCY.md` - the rules for depending on this library without
  requiring it, including the version gate that additive minors make necessary.
- The README says what a HUD has to decide for itself: it floats over the
  game's own screens, and the hideout passes every obvious "am I in a raid"
  test.

### Notes

- Probe rows 40-45. Rows 40-41 cover the delivery fixes, each with its control
  case beside it, so what the row measures is the rejection rather than the
  reload; 42-43 the reported navigation failure; 44-45 the answers a Manual
  overlay owes across a disposal.
- The error status on a failed navigation is read through a hand-bound vtable
  slot, proven the way every other slot here was: the neighbouring slot returns
  sequential navigation ids (2, then 3) while this one returns differing
  statuses - `UNKNOWN` (0) for a file missing behind a mapped folder,
  `CONNECTION_ABORTED` (9) for a connection nothing answers. A wrong slot could
  produce neither.


## [1.8.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- A mod's settings survive a page reload: after a browser hiccup the overlay
  comes back the way it was instead of falling back to its defaults.
- Overlays that stream live data (markers, telemetry) can keep up instead of
  falling behind when the game stutters.

### Added

- `Post(channel, payload, PostOptions.Retain)` - the payload is remembered per
  channel and replayed to every page that loads afterwards, before anything
  else reaches it. The library reloads a page by itself after a renderer crash,
  and a fresh document starts from its own defaults, so configuration sent once
  was quietly lost mid-session with the mod's dirty-check none the wiser.
  Retargeting the overlay forgets them; setting state up before naming the
  first page does not.
- `PostOptions.LatestOnly` - a payload still held by the library is replaced by
  a newer one on the same channel, and `overlay.on(channel, fn, { latest: true })`
  hands the page the newest payload once per frame rather than the backlog.
  Once a message has gone to the browser there is no queue here to collapse,
  which is why the page has a half of this.
- `OverlayOptions.Dispatch` with `EventDispatch.Manual` and
  `IWebOverlay.PumpEvents()`: events wait until the consumer asks, so they run
  inside its own `Update`, at the point it chooses, on its own frame budget.
  `DispatchOnMainThread` keeps working as the older way of saying
  `EventDispatch.MainThread`.

### Fixed

- Messages and retained state set up before the first `LoadHtml` or `Navigate`
  are no longer discarded by it. Only a real retarget - away from a page that
  existed - forgets what was meant for the page being left.

## [1.7.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Overlays can be told to hand the mouse cursor back while they are open, so a
  window opened during a raid can actually be used instead of leaving the
  cursor captured by the game.
- The library now refuses to open a window over a game in exclusive fullscreen
  rather than relying on every mod to check first, which is what used to
  minimise the game.
- Fixed: a mod's window failed to open while another mod's transparent overlay
  was on screen. Two mods that use the library at the same time no longer
  interfere with each other.

### Added

- `OnRequest(channel, (payload, reply) => ...)` for answers that are not ready
  yet: reply once, later, from wherever the answer arrives.
- `IWebOverlay.Transparency`, and the same fact for the page without any mod
  code - `wo-composed` / `wo-chroma` / `wo-opaque` on the root element and
  `overlay.env.transparency` - so a stylesheet can adapt to the kind of
  transparency it actually got.
- `OverlayOptions.InjectTheme`, putting the library palette on the page as CSS
  variables, documented in `docs/STYLE.md`.
- `OverlayOptions.FreeCursorWhileShown`, releasing the cursor while such an
  overlay is the window in front - the library undoing its own side effect,
  since a framed overlay takes the foreground while the game keeps the mouse
  captured. The condition is the foreground window as the system reports it,
  not Unity's idea of focus, which does not have to agree.
- `WebOverlayPlugin.VirtualKey(KeyCode)` and `CloseKeysFor(KeyboardShortcut)`,
  the table two consumers had each written for themselves.

### Fixed

- A windowed overlay could not be created while the only live overlays were
  transparent ones - it failed with `ViewFailed` and `ERROR_INVALID_STATE`.
  A browser hosting visual (composed) views refuses to create a windowed one,
  and two environments sharing a user data folder share the browser, so the
  library now gives such a windowed overlay a browser of its own. Present since
  1.2.0; in practice one mod's HUD broke another mod's panel, which is exactly
  the QuestMarkers and ModProfiler combination.

  The second browser is created only when that collision actually happens and
  takes the windowed overlay, because it costs about six processes and a
  quarter of a gigabyte (measured). A game whose mods only use HUDs, only use
  windows, or open the window first never pays for it - measured at one extra
  process and 53 MB for a window plus a HUD in that order. While that browser
  starts, only overlay *creation* waits: an overlay that is already up keeps
  answering. A browser that fails to start is not remembered, so the next
  overlay tries again instead of inheriting the defect for the session.

- A browser data folder that cannot be created is refused by the library
  instead of by the browser. Told to use a folder it cannot create, WebView2
  puts a modal error box on the player's screen - not something a mod should be
  able to cause. Both folders are now made and checked here first, and a
  failure is a log line and a classified failure instead.

### Changed

- `Show()` refuses exclusive fullscreen itself, logging once, instead of
  leaving it to every consumer's every show path. `IsDisplayModeSupported`
  stays public for a mod that wants to explain the situation in its own
  interface.
- `DispatchOnMainThread` now says in its documentation whose frame budget a
  dispatched handler spends: it runs inside this library's `Update`, so a
  profiler bills it here and its position in the frame follows plugin load
  order.
- The demo derives its close keys from the configured hotkey rather than
  hard-coding them, frees the cursor, takes the theme, and shows a deferred
  answer.

## [1.6.0]

### Forge version notes

Nothing changes for players - this release is for the mods that use the
library.

- Pages can be built like ordinary web apps: a mod can serve its own folder as
  https://yourmod.assets/, so scripts, fonts and images load normally and the
  page gets working storage.
- Named channels with request/reply: page and mod can ask each other a question
  and await the answer, instead of every mod inventing its own convention. A
  question is always answered, so neither side can leave the other hanging.
- A mod can read values back out of its page, and gets events for "my page is
  live" and for real visibility changes, plus the option to receive everything
  on the game's main thread.
- Failures now say why, so a mod can tell you "install the WebView2 runtime"
  instead of showing a generic error.
- Interactive HUDs can be cut down to the rectangles they actually use, so a
  HUD can cover the screen while the game stays clickable everywhere else;
  windows can also be moved and resized at runtime.

(The entry above covers 1.4.0 to 1.6.0, because those two versions were never
published to the Forge separately.)

### Added

- Named channels and request/reply in both directions: `Post(channel, payload)`
  and `ChannelMessage`, `Request` and `OnRequest` on the mod side;
  `overlay.on`, `send`, `onRequest` and `request` in the page, provided by a
  shim injected before any page script runs
  (`AddScriptToExecuteOnDocumentCreated`, v1 slot 27). A request is answered
  exactly once - the reply, `null` when no handler takes that channel, `null`
  on timeout, `null` when the overlay closes with the question still open.
- `SetShape` and `overlay.setShape`, cutting an overlay down to a set of
  rectangles, and `SetBounds` for runtime move and resize.

### Changed

- Framing lives in a JSON envelope with one reserved key (`__wo`) and a
  reserved channel prefix (`__wo.`); anything that is not a well-formed
  envelope still reaches `MessageReceived` verbatim, a page's own JSON
  included.
- The demo's glass panel uses channels and asks the game for its frame rate;
  the F10 panel keeps using plain strings, so both styles are visible.

### Fixed

- A shape the library cannot read is ignored instead of clearing the shape,
  which would have handed a full-screen interactive HUD back the whole mouse.
- Reserved channels are filtered as a prefix rather than by one known name,
  and a request on one is answered rather than left open.
- Shape rectangles are offset from the client to the window origin, so a
  framed overlay keeps its title bar.

### Notes

- Selective mouse transparency - keeping the picture and giving up only the
  mouse - was measured to be impossible on Windows: `HTTRANSPARENT` passes a
  click only to windows of the same thread, and a window region routes clicks
  across processes but clips the picture. `SetShape` therefore governs picture
  and mouse together. See `docs/CONSUMER-API-WISHLIST-ANSWERS.md`, entry 7.

## [1.5.0]

### Forge version notes

- For mod authors: ExecuteScript can now hand back what the script evaluated
  to, so a mod can read state out of its page without building a round trip by
  hand.
- New VisibilityChanged event that reports only real show/hide changes - the
  existing Closed event also fires for a mod's own Hide and cannot tell the two
  apart.
- Nothing changes for players.

### Added

- `ExecuteScript(script, result)` returning the script's value as the JSON the
  browser produced, answered exactly once - including when the overlay is
  disposed while the script is still running.
- `VisibilityChanged`, raised only on real transitions.

### Changed

- Hand-built COM callbacks are freed when the last reference goes, instead of
  being leaked for the process lifetime, which is what makes per-call
  completion handlers affordable.
- Script results are delivered even after the handle is disposed - unlike an
  event, a result is a promise to one caller - and suppressed only while the
  game is shutting down, as `VisibilityChanged` now is too.

## [1.4.0]

### Forge version notes

- For mod authors: overlays can serve a folder of real files as
  https://yourmod.assets/ - scripts, fonts and images load normally, and such a
  page also gets working localStorage (an inline page has none).
- Failed now says why: a cause a mod can act on plus the exact message.
- New PageLoaded event and IsPageLoaded, and an option to receive all events on
  the game's main thread.
- Nothing changes for players.

### Added

- `OverlayOptions.VirtualHosts`, mapping folders to `https://<host>/`
  (`ICoreWebView2_3`, slot 71, `DENY_CORS`). A page that navigates there gets a
  real origin, and with it same-origin assets, working storage isolated per
  host, and no 2 MB document limit.
- `Failure` and `FailureMessage` on the handle, classified across every failure
  site, with the shared start recording its own cause.
- `PageLoaded` and `IsPageLoaded`, and `OverlayOptions.DispatchOnMainThread`.

### Changed

- The library core stays free of Unity: the main-thread queue lives in the
  host, and the plugin drains it from its own `Update`, so the empirical probe
  can still drive the real DLL outside the game.

### Fixed

- A virtual-host mapping that cannot be applied fails the overlay and keeps the
  origin filter closed, instead of letting the page's host name reach the
  network.
- A rejected `Navigate` or `LoadHtml` restores the previous target, so the
  overlay does not report "not loaded" forever while the old page is still on
  screen.

## [1.3.0]

### Forge version notes

- Demo: F7 shows a Three.js WebGL compass cube coupled to the player camera -
  overlays run full WebGL2, so 3D HUDs are possible. The library itself is
  unchanged.
- README now documents measured performance: ~0.5 ms message round trip,
  ~9,600 messages/s, visible changes within 1-2 display frames.

### Added

- Demo: a Three.js compass cube (F7) fed by one camera message per frame,
  with Three.js r149 embedded in the demo assembly.
- README sections on measured messaging performance and on WebGL support.

### Fixed

- Demo key toggles work while movement keys are held; BepInEx's
  `KeyboardShortcut.IsDown` blocks whenever any unrelated key is down.

## [1.2.1]

### Forge version notes

- Fixed: the transparent display-only HUD had stopped being click-through in
  1.2.0; it ignores the mouse again.

### Fixed

- `WS_EX_TRANSPARENT` only takes a window out of hit-testing when
  `WS_EX_LAYERED` is set as well, which the composed window was missing. The
  1.2.0 release was withdrawn in favour of this one; its tag stands.

## [1.2.0]

### Forge version notes

- HUDs are now composition hosted (Windows 8+, 2021+ WebView2): true per-pixel
  alpha - rgba() glass, soft shadows and clean antialiasing blend with the
  game. Older systems keep the chroma-key fallback.
- New Interactive option: a transparent HUD can receive mouse input - HTML
  buttons, hovers and wheel scrolling work while the game keeps the keyboard.

### Added

- Composition hosting through DirectComposition and
  `ICoreWebView2Environment3`, with the chroma key kept as the fallback.
- `OverlayOptions.Interactive`, forwarding mouse input to the page.

### Notes

- Superseded by 1.2.1, which fixes click-through for display-only HUDs.

## [1.1.0]

### Forge version notes

- Windows now remember their position and size: toggling no longer recenters,
  and the spot survives restarts. A spot that ends up off-screen (monitor
  changes) falls back to the centered default. Mods can opt out or set their
  own storage key (RememberBounds / PersistenceKey).

### Added

- Bounds persistence in `%LOCALAPPDATA%\WebOverlay\window-bounds.txt`, shared
  safely across mods and game instances, with `RememberBounds` and
  `PersistenceKey` to control it.

## [1.0.1]

### Forge version notes

- Packaging and logging polish; no functional changes.

### Fixed

- Every file in the release zip lives inside the plugin folder, so a blind
  extraction leaves nothing in the game root.
- Log lines no longer repeat the plugin name BepInEx already prints.

## [1.0.0]

### Forge version notes

- First release.

### Added

- Overlay windows over the game with HTML content, two-way messaging,
  transparent HUDs, and the security defaults the README describes.
