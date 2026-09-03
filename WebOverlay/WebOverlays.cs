using System;

namespace WebOverlay
{
    /// <summary>
    /// Shows web pages in windows over Escape From Tushonka, so a mod can build a
    /// user interface in HTML instead of an immediate-mode toolkit.
    ///
    /// Everything is safe to call from Unity's thread and nothing blocks it:
    /// the browser starts asynchronously on its own thread. Nothing here
    /// throws: when overlays are already known to be unusable,
    /// <see cref="Create"/> returns null; failures that surface later (no
    /// WebView2 runtime, the browser will not start) raise the handle's
    /// <see cref="IWebOverlay.Failed"/> event - dispose the handle there and
    /// fall back.
    /// </summary>
    public static class WebOverlays
    {
        /// <summary>
        /// Kicks off the browser start and reports whether overlays are still
        /// plausible. False only when a previous start already failed; a true
        /// does not yet guarantee success - that is what
        /// <see cref="IWebOverlay.Failed"/> is for.
        /// </summary>
        public static bool IsAvailable => OverlayHost.EnsureStarted();

        /// <summary>The installed WebView2 runtime, once known.</summary>
        public static string RuntimeVersion => OverlayHost.RuntimeVersion;

        /// <summary>
        /// Creates an overlay. Returns null when overlays are unavailable, in
        /// which case the caller should use its own fallback.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static IWebOverlay Create(string title, OverlayOptions options = null)
        {
            if (!OverlayHost.EnsureStarted())
                return null;

            // NoInlining keeps GetCallingAssembly honest: the caller's name
            // namespaces the default persistence key, so equal titles from
            // different mods do not trade window positions.
            string ownerName;
            try
            {
                ownerName = System.Reflection.Assembly.GetCallingAssembly().GetName().Name;
            }
            catch
            {
                ownerName = "unknown";
            }

            // A private copy: the overlay reads its options for its whole
            // lifetime, and a caller mutating or reusing the object must not
            // half-transform a live overlay.
            var handle = new OverlayHandle(title ?? "Overlay", ownerName, snapshot(options));
            return handle.Start() ? handle : null;
        }

        private static OverlayOptions snapshot(OverlayOptions options)
        {
            if (options == null)
                return new OverlayOptions();
            return new OverlayOptions
            {
                Width = options.Width,
                Height = options.Height,
                Frame = options.Frame,
                CloseKeys = options.CloseKeys == null ? null : (int[])options.CloseKeys.Clone(),
                ContextMenu = options.ContextMenu,
                DevTools = options.DevTools,
                Opacity = options.Opacity,
                Transparent = options.Transparent,
                Interactive = options.Interactive,
                AllowedOrigins = options.AllowedOrigins == null ? null : (string[])options.AllowedOrigins.Clone(),
                RememberBounds = options.RememberBounds,
                PersistenceKey = options.PersistenceKey,
                DispatchOnMainThread = options.DispatchOnMainThread,
                // One decision, two ways of expressing it: the older flag wins
                // only where the newer one says nothing.
                Dispatch = options.Dispatch != EventDispatch.OverlayThread ? options.Dispatch
                    : options.DispatchOnMainThread ? EventDispatch.MainThread
                    : EventDispatch.OverlayThread,
                InjectTheme = options.InjectTheme,
                FreeCursorWhileShown = options.FreeCursorWhileShown,
                ClickThroughWhenUnfocused = options.ClickThroughWhenUnfocused,
                AllowDownloads = options.AllowDownloads,
                VirtualHosts = copy(options.VirtualHosts),
            };
        }

        private static VirtualHost[] copy(VirtualHost[] hosts)
        {
            if (hosts == null)
                return null;
            // Deep: the entries are mutable objects, and a caller reusing one
            // must not be able to retarget a live overlay's folder mapping.
            var result = new VirtualHost[hosts.Length];
            for (int i = 0; i < hosts.Length; i++)
                result[i] = hosts[i] == null ? null : new VirtualHost(hosts[i].Host, hosts[i].Folder)
                {
                    Access = hosts[i].Access,
                };
            return result;
        }
    }

    /// <summary>
    /// Why an overlay reported <see cref="IWebOverlay.Failed"/>. The cases are
    /// grouped by what the consumer can tell its user to do about them, not by
    /// where in the library the error happened; the exact sentence is in
    /// <see cref="IWebOverlay.FailureMessage"/> and the log.
    /// </summary>
    public enum OverlayFailure
    {
        /// <summary>No failure, or a cause the library could not classify.</summary>
        Unknown = 0,

        /// <summary>
        /// `WebView2Loader.dll` is missing next to the library - the
        /// installation is incomplete, so reinstalling it is the fix.
        /// </summary>
        LibraryIncomplete,

        /// <summary>
        /// No WebView2 runtime on this machine. The user has to install it;
        /// current Windows 10/11 installations already have it.
        /// </summary>
        RuntimeMissing,

        /// <summary>
        /// The runtime is there but the shared browser environment did not
        /// start (or timed out). Usually transient or a broken user-data
        /// folder; overlays stay unavailable for this game session.
        /// </summary>
        EnvironmentFailed,

        /// <summary>The overlay's own window could not be created.</summary>
        WindowFailed,

        /// <summary>
        /// The browser view for this overlay could not be created or brought
        /// into a usable, secure state.
        /// </summary>
        ViewFailed,

        /// <summary>
        /// Transparency was requested and cannot be delivered here - an
        /// interactive HUD without composition support (Windows 8+ and a 2021+
        /// runtime), or a transparent background the runtime refused. A
        /// non-transparent overlay would still work.
        /// </summary>
        CompositionUnavailable,

        /// <summary>
        /// The browser or its renderer process died (the renderer only after
        /// bounded reload attempts). Creating the overlay again may well work.
        /// </summary>
        RendererCrashed,

        /// <summary>
        /// A folder in <see cref="OverlayOptions.VirtualHosts"/> could not be
        /// served - a missing folder, a malformed host name, or a runtime too
        /// old to map folders at all. The overlay refuses to continue rather
        /// than let the page's own host name reach the network.
        /// </summary>
        VirtualHostFailed,
    }

    /// <summary>
    /// Which thread an overlay's events arrive on.
    /// </summary>
    public enum EventDispatch
    {
        /// <summary>
        /// The library's own thread, as events have always arrived. Queue what
        /// a handler learns and touch game state from your own `Update`.
        /// </summary>
        OverlayThread = 0,

        /// <summary>
        /// The game's main thread, delivered from the library plugin's
        /// `Update`, so a handler may touch Unity objects directly. Costs up
        /// to one frame - and note whose frame: the work happens inside the
        /// library's `Update`, so a profiler bills it there and its place in
        /// the frame follows plugin load order.
        /// </summary>
        MainThread,

        /// <summary>
        /// The game's main thread, but on your terms: events wait until you
        /// call <see cref="IWebOverlay.PumpEvents"/>, so they run inside your
        /// own `Update`, at the point you choose, on your own frame budget.
        /// An overlay set to this and never pumped receives nothing - the
        /// queue fills and then drops, with one warning.
        /// </summary>
        Manual,
    }

    /// <summary>
    /// How a channel message should be treated beyond sending it once.
    /// </summary>
    [Flags]
    public enum PostOptions
    {
        /// <summary>Sent once, to whoever is listening now.</summary>
        None = 0,

        /// <summary>
        /// Remembered as the current value of this channel and re-sent to
        /// every page that loads afterwards, before anything else reaches it.
        /// The library reloads a page by itself after a renderer crash, and a
        /// fresh document starts from its own defaults - so configuration a
        /// mod sent once would quietly be lost mid-session. Only the newest
        /// retained payload per channel is kept, and retargeting the overlay
        /// with <see cref="IWebOverlay.LoadHtml"/> or
        /// <see cref="IWebOverlay.Navigate"/> forgets them all: the page
        /// changed, so its state is not the new page's state.
        /// </summary>
        Retain = 1,

        /// <summary>
        /// Worth sending only while it is the newest: if the page has not
        /// received the previous payload on this channel yet, that one is
        /// dropped and this takes its place. For per-frame telemetry - marker
        /// positions, a camera feed - where an older frame has no value once a
        /// newer one exists.
        ///
        /// This applies while the library still holds the message. Once it has
        /// handed a message to the browser there is no queue here to collapse,
        /// so a page that consumes slower than the mod sends should also ask
        /// for `{ latest: true }` on its side of the channel.
        /// </summary>
        LatestOnly = 2,
    }

    /// <summary>
    /// How an overlay ended up doing transparency, which decides how a page
    /// should be styled: composition gives true per-pixel alpha, the chroma
    /// key does not. Available once <see cref="IWebOverlay.Ready"/> has fired;
    /// the page learns the same thing without any mod code from the class the
    /// library puts on its root element.
    /// </summary>
    public enum OverlayTransparency
    {
        /// <summary>An ordinary opaque window.</summary>
        None = 0,

        /// <summary>
        /// True per-pixel alpha: `rgba()` glass, soft shadows and clean
        /// antialiasing blend with the game. Page class `wo-composed`.
        /// </summary>
        Composition,

        /// <summary>
        /// The fallback on older systems: transparency is binary and
        /// semi-transparent pixels blend towards near-black, so a page wants
        /// solid panels here. Page class `wo-chroma`.
        /// </summary>
        ChromaKey,
    }

    /// <summary>
    /// A rectangle in the overlay's own pixels, measured from its top-left
    /// corner. Used by <see cref="IWebOverlay.SetShape"/>.
    /// </summary>
    public struct OverlayRegion
    {
        public OverlayRegion(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// Maps `https://&lt;Host&gt;/` to a local folder, so a page can load real
    /// files - scripts, fonts, images - instead of being one inlined string.
    /// See <see cref="OverlayOptions.VirtualHosts"/>.
    /// </summary>
    public sealed class VirtualHost
    {
        public VirtualHost()
        {
        }

        public VirtualHost(string host, string folder)
        {
            Host = host;
            Folder = folder;
        }

        /// <summary>
        /// The host name alone, without scheme or slashes - for example
        /// "yourmod.assets". Pick something unique to your mod: it is also the
        /// origin the page's `localStorage` belongs to.
        /// </summary>
        public string Host { get; set; }

        /// <summary>Absolute path to the folder served under that host.</summary>
        public string Folder { get; set; }

        /// <summary>
        /// How much of this folder other origins in the same overlay may read.
        /// The default is what every mapping has always had.
        /// </summary>
        /// <remarks>
        /// This is a property of the host being READ, not of a pair of hosts:
        /// it belongs on the host the assets come from. A page on host A that
        /// wants web fonts from host B has to loosen B.
        ///
        /// An inline <c>LoadHtml</c> page has an opaque origin and is
        /// therefore cross-origin to EVERY mapped host, including its only
        /// one - so a single host is not protection from this.
        /// </remarks>
        public HostAccess Access { get; set; } = HostAccess.DenyCors;
    }

    /// <summary>
    /// What other origins may read from a virtual host. The names follow
    /// WebView2's own COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND, whose table is:
    ///
    /// <list type="bullet">
    /// <item><description><c>Deny</c> - nothing cross-origin at all, not even an
    /// <c>img</c> or <c>script</c> source.</description></item>
    /// <item><description><c>DenyCors</c> - ordinary sub-resource loads pass;
    /// anything CORS-checked (fetch, XHR, and web fonts, which are fetched in
    /// CORS mode by specification) is refused.</description></item>
    /// <item><description><c>Allow</c> - everything, CORS-checked included.
    /// </description></item>
    /// </list>
    /// </summary>
    public enum HostAccess
    {
        /// <summary>The default, and what every mapping had before this existed.</summary>
        DenyCors = 0,

        /// <summary>Nothing cross-origin. Strictest, and it can break a page's own assets.</summary>
        Deny,

        /// <summary>
        /// Everything, CORS-checked included. Say this when a page on one of
        /// your hosts needs web fonts or fetch from another of them.
        /// </summary>
        /// <remarks>
        /// It is not free: this is the equivalent of the folder answering
        /// "Access-Control-Allow-Origin: *", to every origin in the overlay -
        /// including any remote origin the mod put in
        /// <see cref="OverlayOptions.AllowedOrigins"/>. Map a folder that holds
        /// only what the page may read, not a whole plugin directory.
        /// </remarks>
        Allow,
    }

    /// <summary>
    /// How an overlay window should look and behave.
    ///
    /// For a WINDOW the player uses in a raid: set <see cref="Width"/> and
    /// <see cref="Height"/> (never leave both 0 - the default is 80% of the
    /// picture, centred, exactly where the game reads the mouse while the
    /// player turns), set <see cref="FreeCursorWhileShown"/>, and consider
    /// <see cref="ClickThroughWhenUnfocused"/>. If your handlers touch Unity
    /// objects, set <see cref="DispatchOnMainThread"/>. For a HUD: set
    /// <see cref="Transparent"/> and none of the cursor options. The
    /// repository's examples/ folder holds both shapes, compiled verbatim on
    /// every release.
    /// </summary>
    public sealed class OverlayOptions
    {
        /// <summary>Width in pixels; 0 means 80% of the game's window.</summary>
        /// <remarks>
        /// Leaving both at 0 puts the window over the middle of the picture,
        /// and in a first-person game that is the exact point the mouse is
        /// read from while the player turns - Windows delivers the movement to
        /// whatever window is under the pointer, whoever has the foreground.
        /// A panel there stops the player turning, with nothing anywhere
        /// reporting an error, because nothing is in error. See
        /// <see cref="ClickThroughWhenUnfocused"/>, or simply give the window
        /// a size and a place of its own.
        /// </remarks>
        public int Width { get; set; }

        /// <summary>Height in pixels; 0 means 85% of the game's window.</summary>
        public int Height { get; set; }

        /// <summary>
        /// Draw a title bar with a close button. On by default, and worth
        /// keeping: while the overlay holds the keyboard the game cannot see a
        /// toggle key, so without a frame the close keys are the only way out.
        /// </summary>
        public bool Frame { get; set; } = true;

        /// <summary>Virtual key codes that close the overlay. Escape by default.</summary>
        public int[] CloseKeys { get; set; } = { 0x1B };

        /// <summary>Allow the browser's right-click menu. Off by default.</summary>
        public bool ContextMenu { get; set; }

        /// <summary>Allow F12 developer tools. Useful while building a page.</summary>
        public bool DevTools { get; set; }

        /// <summary>
        /// Overall window opacity from 0.15 to 1.0; values outside are clamped.
        /// The whole window - content included - fades evenly. The overlay stays
        /// interactive, so this suits panels that should not fully cover the game.
        /// </summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>
        /// HUD mode. Pixels the page leaves unpainted show the game; painted
        /// content floats over it. Without <see cref="Interactive"/> the
        /// window ignores the mouse and never takes focus, so the game stays
        /// fully playable - which also means <see cref="CloseKeys"/> cannot
        /// apply: hide the HUD from the mod's own hotkey via
        /// <see cref="IWebOverlay.Hide()"/> or Toggle. Unless a size is set, the
        /// HUD covers the game's whole client area, and <see cref="Frame"/>
        /// is ignored (<see cref="Opacity"/> too when composition hosted -
        /// fade in the page's CSS instead; the chroma-key fallback still
        /// applies it). A sized HUD sits at the game
        /// picture's top-left corner - prefer the full-size default and place
        /// elements with CSS.
        ///
        /// On Windows 8+ with a 2021+ WebView2 runtime the HUD is composition
        /// hosted: transparency is TRUE per-pixel alpha - rgba() glass, soft
        /// shadows and clean antialiasing all blend with the game. On older
        /// systems it falls back to a chroma key, where transparency is
        /// binary, semi-transparent pixels blend towards near-black, and
        /// rgb(3,1,3) is reserved - design for solid-ish panels if those
        /// systems matter to you.
        /// </summary>
        public bool Transparent { get; set; }

        /// <summary>
        /// Extra origins ("https://example.com") the overlay may navigate to
        /// and receive messages from. The origin of every URL passed to
        /// <see cref="IWebOverlay.Navigate"/> is trusted automatically; all
        /// other navigation - redirects, followed links - is blocked, so a
        /// foreign page never reaches the message bridge.
        /// </summary>
        public string[] AllowedOrigins { get; set; }

        /// <summary>
        /// Remember the window's position and size across sessions (stored in
        /// `%LOCALAPPDATA%\WebOverlay\window-bounds.txt`) and keep them while
        /// toggling. On by default. HUDs never persist - they follow the game
        /// window. A remembered spot that is no longer on any screen falls
        /// back to the centered default.
        /// </summary>
        public bool RememberBounds { get; set; } = true;

        /// <summary>
        /// Makes a <see cref="Transparent"/> HUD receive mouse input: HTML
        /// buttons, hovers and wheel scrolling work, forwarded to the page
        /// while the game keeps the keyboard. Requires composition support
        /// (Windows 8+ with a 2021+ WebView2 runtime) - creation fails
        /// otherwise, reported through <see cref="IWebOverlay.Failed"/>.
        /// The window then swallows mouse input over its WHOLE rectangle, so
        /// size such an overlay to its content instead of the full screen.
        /// Keyboard input is not forwarded (yet); <see cref="CloseKeys"/> do
        /// not apply. Ignored without <see cref="Transparent"/>.
        /// </summary>
        public bool Interactive { get; set; }

        /// <summary>
        /// The key the bounds are stored under; defaults to
        /// "&lt;calling assembly&gt;/&lt;title&gt;". Set it when the title changes
        /// between sessions, or when several overlays share a title but
        /// should remember separate spots.
        /// </summary>
        public string PersistenceKey { get; set; }

        /// <summary>
        /// Raise this overlay's events on the game's main thread instead of the
        /// overlay thread, so a handler may touch Unity objects directly and
        /// the usual queue-and-drain boilerplate disappears. Off by default.
        ///
        /// Events are queued and delivered from the library plugin's own
        /// Update, so they arrive up to one frame later and, after
        /// <c>Dispose()</c>, not at all. Outside the game -
        /// no plugin, no Update - there is nothing to dispatch to, so the
        /// overlay keeps its normal threading and says so once in the log.
        /// </summary>
        public bool DispatchOnMainThread { get; set; }

        /// <summary>
        /// Which thread this overlay's events arrive on, and who pays for
        /// them. <see cref="DispatchOnMainThread"/> is the older way of saying
        /// <see cref="EventDispatch.MainThread"/> and still works; setting
        /// either is enough.
        /// </summary>
        public EventDispatch Dispatch { get; set; }

        /// <summary>
        /// Puts the library's palette on the page as CSS custom properties -
        /// `--wo-gold`, `--wo-ink`, `--wo-text`, `--wo-dim`, `--wo-accent`,
        /// `--wo-font`, `--wo-border`, `--wo-radius` - so overlays from
        /// different mods can look like one family without copying hex values
        /// around. Off by default: a mod with its own look should not have to
        /// fight a theme it did not ask for. See `docs/STYLE.md`.
        /// </summary>
        public bool InjectTheme { get; set; }

        /// <summary>
        /// While this overlay is visible and the game window does not have the
        /// focus, hand the mouse cursor back to the player. A game that
        /// captures the cursor keeps it captured when a window of the same
        /// process takes the foreground, so a framed overlay opened mid-raid
        /// would otherwise be unreachable - this is the library undoing its own
        /// side effect. The moment the game has the focus again the library
        /// stops touching the cursor and the game takes it back.
        ///
        /// Needs the library's Unity plugin, so it does nothing in a non-Unity
        /// host. Off by default, and pointless for a HUD, which never takes
        /// the foreground.
        /// </summary>
        public bool FreeCursorWhileShown { get; set; }

        /// <summary>
        /// While the game is the window in front, let the mouse pass through
        /// this overlay to the game instead of landing on it.
        ///
        /// For a panel that covers the middle of the screen this is the
        /// difference between a playable game and a frozen one. The game locks
        /// the cursor to the centre of its own window, and Windows delivers
        /// mouse movement to whatever window sits under the pointer - so a
        /// panel over that point receives the movement and the player cannot
        /// turn, with nothing reporting an error: the cursor state is right and
        /// the game has the foreground.
        ///
        /// The cost is that the panel can no longer be clicked to bring it back
        /// to the front, because a click is mouse input like any other. Its
        /// hotkey still works, which is how it was opened. Off by default for
        /// that reason, and pointless for a HUD, which never takes the
        /// foreground in the first place.
        /// </summary>
        public bool ClickThroughWhenUnfocused { get; set; }

        /// <summary>
        /// Let pages start downloads. Off by default, and deliberately so: a
        /// page over a game has no business writing files to the player's
        /// disk, a download bar over the game is never wanted, and every page
        /// here is the mod's own - a mod that wants the bytes of something
        /// serves it from a virtual host or fetches it itself. Blocked
        /// attempts are logged with the URL, once each. On a runtime too old
        /// to expose download control (before 2021) downloads keep the
        /// browser's default behaviour, and the log says so. Arrived in
        /// 1.10.0, tightening what earlier versions left browser-managed.
        /// </summary>
        public bool AllowDownloads { get; set; }

        /// <summary>
        /// Folders served to the page as `https://&lt;host&gt;/`, so it can load
        /// real files instead of inlining everything. The mapped origins are
        /// trusted automatically, exactly like a
        /// <see cref="IWebOverlay.Navigate"/> target.
        ///
        /// A page that navigates to its own mapped host (rather than being
        /// pushed in through <see cref="IWebOverlay.LoadHtml"/>) gains what an
        /// inline page cannot have: same-origin assets - fonts included -
        /// working `localStorage` isolated per host name, no 2 MB document
        /// limit, and real file paths in the developer tools.
        ///
        /// The folder is served read-only, and the mapping exists only inside
        /// this overlay's own browser view - so nothing outside it can reach
        /// the files. Inside it, `fetch` and XHR from another origin are
        /// denied, but another origin you allow in the same overlay can still
        /// load files as a script, image or iframe. Map a folder that holds
        /// only what your interface serves.
        /// </summary>
        public VirtualHost[] VirtualHosts { get; set; }
    }

    /// <summary>
    /// How a <see cref="IWebOverlay.Show(Action{VisibilityOutcome})"/> or
    /// <see cref="IWebOverlay.Hide(Action{VisibilityOutcome})"/> request
    /// ended. Answered exactly once per request - or, only once the game is
    /// shutting down, not at all. Arrived in 1.11.0.
    /// </summary>
    public enum VisibilityOutcome
    {
        /// <summary>
        /// The overlay is now in the requested state and was not before. The
        /// matching <see cref="IWebOverlay.VisibilityChanged"/> was raised no
        /// later than this answer was queued; under
        /// <see cref="EventDispatch.Manual"/> a
        /// <see cref="IWebOverlay.PumpEvents"/> call that finds both waiting
        /// delivers the answer first - and one may find only the event.
        /// </summary>
        Applied = 0,

        /// <summary>
        /// The overlay already was in the requested state. Nothing changed
        /// and no <see cref="IWebOverlay.VisibilityChanged"/> was raised.
        /// </summary>
        AlreadyThere,

        /// <summary>
        /// A Show refused because the game is in exclusive fullscreen, where
        /// a window over it would minimise it. The overlay stays alive and
        /// hidden - the player can switch modes - so this is neither a
        /// visibility change nor a failure.
        /// </summary>
        RefusedFullscreen,

        /// <summary>
        /// A newer Show, Hide or Toggle on this overlay replaced the request
        /// before it was applied; the newer request reports the result. Only
        /// possible while the browser view is still being built, which is
        /// where a request waits.
        /// </summary>
        Superseded,

        /// <summary>
        /// The overlay had failed before the request could be applied - for a
        /// Hide as much as for a Show, since a dead window is not "there".
        /// </summary>
        Failed,

        /// <summary>
        /// The handle was disposed before, or while, the request was being
        /// applied. A disposed handle stops dispatching events, so whatever
        /// the request did is not reported to it - under
        /// <see cref="EventDispatch.OverlayThread"/> an event already in
        /// flight can still arrive inline before this answer - and this
        /// outcome wins over every other.
        /// </summary>
        Disposed,

        /// <summary>
        /// The library's command queue refused the request: this overlay had
        /// its share of commands waiting already, or the whole queue was at
        /// its ceiling. Nothing was queued and nothing will happen. Retry
        /// later; never fall back on it.
        /// </summary>
        QueueRefused,
    }

    /// <summary>A single overlay window.</summary>
    public interface IWebOverlay : IDisposable
    {
        bool IsVisible { get; }

        /// <summary>
        /// Whether the page the mod last targeted has finished loading. Sends
        /// before that are buffered rather than lost, so this is for a
        /// consumer that streams: hold off while it is false instead of
        /// filling the outbox. Pairs with <see cref="PageLoaded"/>.
        /// </summary>
        bool IsPageLoaded { get; }

        /// <summary>
        /// Which kind of transparency this overlay actually got. Meaningful
        /// once <see cref="Ready"/> has fired; the page can read the same fact
        /// from the `wo-composed` / `wo-chroma` class on its root element.
        /// </summary>
        OverlayTransparency Transparency { get; }

        /// <summary>
        /// Why <see cref="Failed"/> fired; <see cref="OverlayFailure.Unknown"/>
        /// while the overlay is healthy. Read it in the handler to tell the
        /// user what to do about it.
        /// </summary>
        OverlayFailure Failure { get; }

        /// <summary>
        /// The exact reason behind <see cref="Failure"/>, as one log-ready
        /// sentence; null while the overlay is healthy.
        /// </summary>
        string FailureMessage { get; }

        void Show();
        void Hide();
        void Toggle();

        /// <summary>
        /// <see cref="Show()"/> that says how it ended, through
        /// <paramref name="completed"/>, exactly once - or, only once the game
        /// is shutting down, not at all. The answer travels like an
        /// <see cref="ExecuteScript(string, Action{string})"/> result: the
        /// overlay thread, the game's main thread, or your next
        /// <see cref="PumpEvents"/>, and it is still delivered after
        /// <see cref="IDisposable.Dispose"/>. It may run synchronously inside
        /// this call when the request is refused at once, and after a
        /// dispose it may arrive on the overlay thread whatever the dispatch
        /// mode. Keep pumping a manual overlay while an answer is
        /// outstanding, and never wait for one in <c>OnDestroy</c>.
        ///
        /// A request that arrives before the browser view exists - every
        /// consumer's first does, whether it runs before the creation or
        /// while the creation is waiting for the view - waits for the view
        /// and is answered before <see cref="Ready"/>.
        /// <see cref="VisibilityOutcome.Applied"/> means the native window,
        /// not the page: it precedes <see cref="PageLoaded"/>. There is no
        /// order promise between the answer and the
        /// <see cref="VisibilityChanged"/> it caused: use the answer for the
        /// request you made and the event for transitions you did not ask
        /// for. Arrived in 1.11.0.
        /// </summary>
        void Show(Action<VisibilityOutcome> completed);

        /// <summary>
        /// <see cref="Hide()"/> that says how it ended; everything said for
        /// <see cref="Show(Action{VisibilityOutcome})"/> applies. A window
        /// whose browser view does not exist yet is already hidden and is
        /// answered <see cref="VisibilityOutcome.AlreadyThere"/> at once.
        /// Arrived in 1.11.0.
        /// </summary>
        void Hide(Action<VisibilityOutcome> completed);

        /// <summary>Loads a URL, for a mod that already serves pages.</summary>
        void Navigate(string url);

        /// <summary>Shows markup directly, so no web server is needed.</summary>
        void LoadHtml(string html);

        /// <summary>
        /// Sends a string to the page, where it arrives as a `message` event on
        /// `window.chrome.webview`.
        /// </summary>
        void Post(string message);

        /// <summary>
        /// Sends a string on a named channel, where it arrives at every
        /// `window.overlay.on(channel, ...)` handler in the page. Saves both
        /// sides the prefix-and-split every consumer wrote for itself.
        /// </summary>
        void Post(string channel, string payload);

        /// <summary>
        /// The same, with <see cref="PostOptions"/> - a value the page should
        /// get again after it reloads, or one that is only worth sending while
        /// it is the newest.
        /// </summary>
        void Post(string channel, string payload, PostOptions options);

        /// <summary>
        /// <see cref="Post(string)"/> that says whether the command entered
        /// the library's command queue. False means it did not and will not:
        /// the handle was disposed, or the queue refused it - this overlay
        /// had its share of commands waiting already, or the whole queue was
        /// at its ceiling. Retry later; never fall back on it. During
        /// shutdown the library accepts everything and does nothing, like
        /// every other call, so the answer is true then.
        ///
        /// True is not delivery. A queued message can still be lost to the
        /// outbox limit before the page loads (plain sends; a
        /// <see cref="PostOptions.Retain"/>ed one is kept), to a document
        /// that is not the mod's target, to a retarget - <see cref="LoadHtml"/>
        /// and <see cref="Navigate"/> forget both the outbox and the retained
        /// set - to a renderer crash without <see cref="PostOptions.Retain"/>,
        /// to a failure or close that lands first, and to the browser itself,
        /// which does not report whether it took the string. What
        /// <see cref="PostOptions.Retain"/> buys is that a true survives
        /// reloads. A page that must acknowledge still does so through
        /// <see cref="Request(string, string, Action{string})"/> or
        /// <see cref="ExecuteScript(string, Action{string})"/>, both of which
        /// answer null when refused. Arrived in 1.11.0.
        /// </summary>
        bool TryPost(string message);

        /// <summary>
        /// <see cref="Post(string, string)"/> that says whether the command
        /// entered the library's command queue; see
        /// <see cref="TryPost(string)"/> for what the answer means. Arrived in
        /// 1.11.0.
        /// </summary>
        bool TryPost(string channel, string payload);

        /// <summary>
        /// <see cref="Post(string, string, PostOptions)"/> that says whether
        /// the command entered the library's command queue; see
        /// <see cref="TryPost(string)"/> for what the answer means. Arrived in
        /// 1.11.0.
        /// </summary>
        bool TryPost(string channel, string payload, PostOptions options);

        /// <summary>
        /// Asks the page a question and takes its answer, from the page's
        /// `window.overlay.onRequest(channel, ...)` handler - which may return
        /// a value or a promise. Answered exactly once: with the page's reply,
        /// or with null if no answer arrives within five seconds, so a page
        /// that cannot answer never hangs the mod. The answer arrives where
        /// the events do - see
        /// <see cref="ExecuteScript(string, Action{string})"/> for what each
        /// <see cref="EventDispatch"/> mode means for one.
        /// </summary>
        void Request(string channel, string payload, Action<string> answer);

        /// <summary>
        /// Asks the page a question with an explicit deadline in
        /// milliseconds; see <see cref="Request(string, string, Action{string})"/>.
        /// </summary>
        void Request(string channel, string payload, Action<string> answer, int timeoutMilliseconds);

        /// <summary>
        /// Answers questions the page asks with `window.overlay.request(channel, ...)`.
        /// One handler per channel; a null handler removes it, and a channel
        /// without one is answered with null rather than left open. The
        /// handler runs where the events run - the overlay thread, or the
        /// game's main thread with
        /// <see cref="OverlayOptions.DispatchOnMainThread"/>.
        /// </summary>
        void OnRequest(string channel, Func<string, string> handler);

        /// <summary>
        /// The same, for an answer that is not ready yet: call `reply` once,
        /// whenever and from wherever the answer arrives - after a scan, a
        /// file write, a round trip of your own. A handler that throws before
        /// replying answers null, and a reply that arrives after the page gave
        /// up waiting is dropped rather than resolving a stale promise, so the
        /// page can raise its own deadline with
        /// `overlay.request(channel, payload, timeoutMs)` when it expects to
        /// wait.
        /// </summary>
        void OnRequest(string channel, Action<string, Action<string>> handler);

        /// <summary>Runs JavaScript in the page, for pushing live values.</summary>
        void ExecuteScript(string script);

        /// <summary>
        /// Runs JavaScript and hands back what it evaluated to, as the JSON the
        /// browser produced ("42", "\"text\"", "null" for no value). The
        /// callback is answered exactly once - with null when the script could
        /// not run at all: no page, a page that is no longer the mod's target,
        /// a renderer that crashed under it, an overlay that closed, or a
        /// script the browser rejected. So a caller waiting for a value is
        /// never left waiting forever - the answer arrives even after the
        /// handle was disposed. The one exception is the game shutting down,
        /// where the library delivers nothing at all rather than wake a
        /// fallback on the way out.
        ///
        /// Threading follows the events: the overlay thread, the game's main
        /// thread with <see cref="EventDispatch.MainThread"/>, or your own
        /// <see cref="PumpEvents"/> call with
        /// <see cref="EventDispatch.Manual"/> - where the answer waits for the
        /// pump like everything else, so keep pumping while you are waiting
        /// for one. Disposing the handle is the exception even there: nobody
        /// pumps a handle they have thrown away, so the answers owed at that
        /// point are delivered on the spot instead.
        /// </summary>
        void ExecuteScript(string script, Action<string> result);

        /// <summary>Opens the browser developer tools, if enabled in the options.</summary>
        void OpenDevTools();

        /// <summary>
        /// Delivers this overlay's waiting events, on the calling thread. Only
        /// for <see cref="EventDispatch.Manual"/>, where it is the one thing
        /// that makes events arrive at all; call it from your own `Update`.
        /// Harmless in the other modes, where it has nothing to deliver.
        /// </summary>
        void PumpEvents();

        /// <summary>
        /// Moves or resizes the overlay, in screen coordinates. Any argument
        /// left null keeps its current value. A window positioned this way is not written to the
        /// remembered-bounds store - that belongs to the player - but it does
        /// win over a remembered spot for the rest of the session.
        /// </summary>
        void SetBounds(int? x, int? y, int? width, int? height);

        /// <summary>
        /// Cuts the overlay down to these rectangles: it draws there and takes
        /// the mouse there, and everything outside belongs to the game. Null
        /// (the default) means the whole window. Rectangles are measured from
        /// the top-left of the page, so a framed overlay keeps its title bar
        /// whatever shape is set.
        ///
        /// This is how an <see cref="OverlayOptions.Interactive"/> HUD can
        /// cover the screen and still leave the game playable: give it the
        /// rectangles it actually draws in. Both halves - picture and mouse -
        /// come from the one mechanism Windows offers for this, so they cannot
        /// be separated: whatever is cut away is cut away for both. Pad the
        /// rectangles a little if your content has soft shadows.
        ///
        /// The page usually knows its own layout better than the mod does and
        /// can say so directly with `overlay.setShape([element, ...])`, which
        /// accepts elements or `{x, y, w, h}` objects, converts them to device
        /// pixels, and should be called again when the layout changes.
        /// </summary>
        void SetShape(OverlayRegion[] regions);

        /// <summary>
        /// Raised when the page calls `window.chrome.webview.postMessage(...)`.
        /// Runs on the overlay thread: hop to Unity's thread before touching
        /// game state. Channel traffic does not appear here - it has its own
        /// event - but anything else the page sends arrives verbatim.
        /// </summary>
        event Action<string> MessageReceived;

        /// <summary>
        /// Raised when the page calls `window.overlay.send(channel, payload)`.
        /// Same threading as <see cref="MessageReceived"/>.
        /// </summary>
        event Action<string, string> ChannelMessage;

        /// <summary>Raised for keys pressed in the overlay that did not close it.</summary>
        event Action<int> KeyPressed;

        /// <summary>
        /// Raised whenever the page the mod targeted has finished loading -
        /// on every navigation, so a reload after a renderer crash raises it
        /// again. This is "my page is live", as opposed to <see cref="Ready"/>,
        /// which only means the browser view exists.
        /// </summary>
        event Action PageLoaded;

        /// <summary>
        /// Raised whenever the overlay is hidden or closed. Note that this
        /// includes the mod's own <see cref="Hide()"/>, so it cannot tell "the
        /// player closed it" from "we closed it" - use
        /// <see cref="VisibilityChanged"/> for state, and expect this event to
        /// narrow to real closes in a future major version.
        /// </summary>
        event Action Closed;

        /// <summary>
        /// Raised when the overlay becomes visible or invisible, and only on
        /// an actual change - so it can be trusted as state instead of being
        /// reconciled against the consumer's own flag every frame. Fires false
        /// when a failure hides the overlay, and once more when a visible
        /// overlay is destroyed.
        /// </summary>
        event Action<bool> VisibilityChanged;

        /// <summary>
        /// Raised once the browser view is fully set up. Latched: a handler
        /// subscribed after the fact still runs - immediately, on the
        /// subscribing thread - otherwise on the overlay thread; treat it as
        /// "any thread". That immediacy belongs to
        /// <see cref="EventDispatch.OverlayThread"/> alone. Whenever the
        /// overlay dispatches elsewhere, a late subscription is queued like
        /// every other event rather than run inside the Add call: with
        /// <see cref="EventDispatch.MainThread"/> it arrives on the game's
        /// next frame, and with <see cref="EventDispatch.Manual"/> on your
        /// next <see cref="PumpEvents"/> - so a gate must not read its own
        /// fallback flag on the frame it subscribed.
        /// </summary>
        event Action Ready;

        /// <summary>
        /// Raised when the overlay cannot work - browser start failed, the
        /// browser process died, HUD transparency unavailable. The overlay
        /// stays hidden; dispose the handle and use a fallback.
        /// <see cref="Failure"/> and <see cref="FailureMessage"/> say why, and
        /// are set before this fires. Latched exactly like
        /// <see cref="Ready"/>, including when a late subscription is answered
        /// under each <see cref="EventDispatch"/> mode.
        /// </summary>
        event Action Failed;

        /// <summary>
        /// Raised when the channel shim could not be installed - and only
        /// then. The overlay itself keeps working: the window renders, raw
        /// <see cref="Post(string)"/> / <see cref="MessageReceived"/> and
        /// <see cref="ExecuteScript(string, Action{string})"/> are untouched.
        /// What is dead is everything built on <c>window.overlay</c>: named
        /// channels, request/reply, retained replay into the page. A consumer
        /// whose page depends on those should fall back the way it would for
        /// <see cref="Failed"/>; one that only uses the raw bridge may ignore
        /// this. Latched exactly like <see cref="Failed"/>, so a late
        /// subscription still hears it. Arrived in 1.10.0.
        /// </summary>
        event Action ChannelsFailed;

        /// <summary>
        /// Whether <c>window.overlay</c> is going to exist in this overlay's
        /// pages: null while the answer is not in yet (the shim installs
        /// during creation), then true or false for the overlay's lifetime.
        /// The false transition is the moment <see cref="ChannelsFailed"/>
        /// fires. Arrived in 1.10.0.
        /// </summary>
        bool? ChannelsAvailable { get; }
    }

    internal sealed class OverlayHandle : IWebOverlay
    {
        private readonly OverlayWindow window;
        private int disposed;

        public OverlayHandle(string title, string ownerName, OverlayOptions options)
        {
            dispatch = options.Dispatch;
            window = new OverlayWindow(title, ownerName, options);
            window.MessageReceived = message => raise(() => MessageReceived?.Invoke(message));
            window.ChannelMessage = (channel, payload) => raise(() => ChannelMessage?.Invoke(channel, payload));
            window.RequestReceived = (channel, payload, reply) =>
            {
                Action<string, Action<string>> handler;
                lock (responders)
                    responders.TryGetValue(channel, out handler);
                if (handler == null)
                {
                    // Nobody answers this channel; say so rather than let the
                    // page wait out its own timeout.
                    reply(null);
                    return;
                }
                // Like a script result, this is a promise to one caller - it
                // is delivered even if the consumer has since disposed, and
                // the page is answered whatever the handler does. `reply` is
                // once-only, so a handler that answered and then threw is
                // still fine.
                raiseResult(() =>
                {
                    try
                    {
                        handler(payload, reply);
                    }
                    catch (Exception ex)
                    {
                        OverlayHost.LogWarning("a request handler threw ("
                            + ex.GetType().Name + ": " + ex.Message + ").");
                        reply(null);
                    }
                });
            };
            window.KeyPressed = key => raise(() => KeyPressed?.Invoke(key));
            window.Closed = () => raise(() => Closed?.Invoke());
            window.VisibilityChanged = visible => raise(() => VisibilityChanged?.Invoke(visible));
            window.PageLoaded = () => raise(() => PageLoaded?.Invoke());
            window.Ready = () => fire(ref readyHandlers, ref readyAlready);
            window.Failed = () => fire(ref failedHandlers, ref failedAlready);
            window.ChannelsOk = () => System.Threading.Interlocked.CompareExchange(ref channelsState, 1, 0);
            window.ChannelsBroken = () =>
            {
                // The transition guards the raise: only the first outcome
                // counts, so a raced second report cannot fire the latch
                // twice or flip an answered question.
                if (System.Threading.Interlocked.CompareExchange(ref channelsState, 2, 0) == 0)
                    fire(ref channelsFailedHandlers, ref channelsFailedAlready);
            };
        }

        private readonly EventDispatch dispatch;

        // Only used by EventDispatch.Manual: the consumer's own queue, drained
        // when it says so.
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> manual =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private int manualQueued;
        private int manualOverflowWarned;

        // Far above a frame's worth of events at any sane rate; a consumer
        // that never pumps should notice, not grow without bound.
        private const int ManualQueueLimit = 4096;

        /// <summary>
        /// Hands one event to the consumer, on the game's main thread when the
        /// overlay asked for it. A dispatched event that is still queued when
        /// the handle is disposed is dropped: the consumer has already let go
        /// of the overlay, and calling it afterwards is how a fallback ends up
        /// running against a null field.
        /// </summary>
        private void raise(Action invoke)
        {
            if (dispatch == EventDispatch.Manual)
            {
                enqueueManual(() =>
                {
                    if (disposed == 0)
                        invokeIsolated(invoke);
                });
                return;
            }
            if (dispatch != EventDispatch.MainThread || !OverlayHost.DispatchToMainThread(() =>
                {
                    if (disposed == 0)
                        invokeIsolated(invoke);
                }))
            {
                invokeIsolated(invoke);
            }
        }

        private void enqueueManual(Action action)
        {
            if (System.Threading.Interlocked.Increment(ref manualQueued) > ManualQueueLimit)
            {
                System.Threading.Interlocked.Decrement(ref manualQueued);
                if (System.Threading.Interlocked.Exchange(ref manualOverflowWarned, 1) == 0)
                    OverlayHost.LogWarning("this overlay's event queue is full (" + ManualQueueLimit
                        + ") and events are being dropped - is anything calling PumpEvents?");
                return;
            }
            manual.Enqueue(action);
        }

        public void PumpEvents()
        {
            // Answers first: somebody is blocked on one, nobody is blocked on
            // an event.
            drainManualResults();
            while (manual.TryDequeue(out Action action))
            {
                System.Threading.Interlocked.Decrement(ref manualQueued);
                action();
            }
        }

        /// <summary>
        /// Delivers a script result. Unlike an event, this is a promise made to
        /// one caller, so it is handed over even after the handle was disposed:
        /// whoever asked for the value is still waiting for it, and dropping it
        /// would break the "answered exactly once" contract that makes the API
        /// safe to await. Only a game that is shutting down swallows it, like
        /// every other callback.
        /// </summary>
        // Answers wait in a queue of their own, separate from the events. A
        // full event queue may drop events and a disposed handle stops
        // delivering them - both documented - but an answer is a promise made
        // to one caller and neither may swallow it.
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> manualResults =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();

        private void drainManualResults()
        {
            while (manualResults.TryDequeue(out Action action))
                action();
        }

        private void raiseResult(Action invoke)
        {
            // Queued like everything else in Manual mode: a consumer that owns
            // the delivery point owns it for answers too, and must keep
            // pumping while it is waiting for one. Except once the handle is
            // disposed - nobody pumps a handle they have thrown away, so
            // queueing there would be the one case where "answered exactly
            // once" quietly became "never". Closing the overlay is precisely
            // when the outstanding promises are settled, so the answer has to
            // go out on the spot instead.
            if (dispatch == EventDispatch.Manual && disposed == 0)
            {
                manualResults.Enqueue(() => invokeIsolated(invoke));
                // Disposal may have started between the check and the enqueue,
                // in which case the drain there has already run and nothing
                // else would ever come for this one. Draining is a TryDequeue
                // loop, so doing it twice costs nothing.
                if (disposed != 0)
                    drainManualResults();
                return;
            }
            // droppable: false for the same reason Manual has a queue of its
            // own - a full queue may cost events, never an answer.
            if (dispatch != EventDispatch.MainThread
                || !OverlayHost.DispatchToMainThread(() => invokeIsolated(invoke), droppable: false))
            {
                invokeIsolated(invoke);
            }
        }

        public event Action<string> MessageReceived;
        public event Action<string, string> ChannelMessage;
        public event Action<int> KeyPressed;

        // One responder per channel, set from the consumer's thread and read
        // on the overlay thread.
        private readonly System.Collections.Generic.Dictionary<string, Action<string, Action<string>>> responders =
            new System.Collections.Generic.Dictionary<string, Action<string, Action<string>>>(StringComparer.Ordinal);
        public event Action Closed;
        public event Action<bool> VisibilityChanged;
        public event Action PageLoaded;

        // Ready and Failed are latched: creation runs on the overlay thread and
        // can finish - or fail - before the consumer had a chance to subscribe.
        // A handler added after the fact still runs - on the adding thread, or
        // from the pump when this overlay dispatches to the main thread; either
        // way each handler runs exactly once.
        private readonly object stateSync = new object();
        private Action readyHandlers;
        private Action failedHandlers;
        private bool readyAlready;
        private bool failedAlready;
        private Action channelsFailedHandlers;
        private bool channelsFailedAlready;
        // 0 unknown, 1 available, 2 failed. An int rather than bool?, because
        // it is read from consumer threads and Nullable<bool> cannot be
        // volatile - and a plain int rather than a volatile one, because the
        // writers go through Interlocked and a by-ref volatile would warn.
        // The getter pairs them with a volatile read.
        private int channelsState;

        public event Action Ready
        {
            add { addLatched(ref readyHandlers, ref readyAlready, value); }
            remove { lock (stateSync) readyHandlers -= value; }
        }

        public event Action Failed
        {
            add { addLatched(ref failedHandlers, ref failedAlready, value); }
            remove { lock (stateSync) failedHandlers -= value; }
        }

        public event Action ChannelsFailed
        {
            add { addLatched(ref channelsFailedHandlers, ref channelsFailedAlready, value); }
            remove { lock (stateSync) channelsFailedHandlers -= value; }
        }

        public bool? ChannelsAvailable
        {
            get
            {
                int state = System.Threading.Volatile.Read(ref channelsState);
                return state == 0 ? (bool?)null : state == 1;
            }
        }

        private void addLatched(ref Action handlers, ref bool already, Action value)
        {
            bool fireNow;
            lock (stateSync)
            {
                handlers += value;
                fireNow = already;
            }
            if (fireNow)
                raise(value);
        }

        private void fire(ref Action handlers, ref bool already)
        {
            Action snapshot;
            lock (stateSync)
            {
                already = true;
                snapshot = handlers;
            }
            if (snapshot == null)
                return;
            // Each subscriber on its own: one throwing handler must neither
            // silence the others nor unwind into the native callback frames.
            // The latch itself was set above, synchronously, so a handler
            // subscribing while these are still queued is not lost.
            foreach (Delegate handler in snapshot.GetInvocationList())
                raise((Action)handler);
        }

        private static void invokeIsolated(Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                OverlayHost.LogWarning("an event handler threw (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        public bool IsVisible => window.IsVisible;

        public bool IsPageLoaded => window.IsPageLoaded;

        public OverlayTransparency Transparency => window.Transparency;

        public OverlayFailure Failure => window.Failure;

        public string FailureMessage => window.FailureMessage;

        internal bool Start()
        {
            OverlayHost.Register(window);
            // Creation goes in its own queue: it is the one thing that may
            // have to wait for a browser, and everything else must keep
            // flowing while it does.
            OverlayHost.PostCreation(() => window.Create());
            return true;
        }

        public void Show() => post(() => window.Show());

        public void Hide() => post(() => window.Hide());

        public void Toggle() => post(() =>
        {
            // Against the desired state, not the visible one: while the browser
            // is still starting, IsVisible is false although the overlay is
            // about to show - a toggle in that gap means "keep it closed".
            if (window.DesiredVisible)
                window.Hide();
            else
                window.Show();
        });

        public void Show(Action<VisibilityOutcome> completed)
        {
            if (completed == null)
            {
                Show();
                return;
            }
            Action<VisibilityOutcome> settle = visibilitySettler(completed);
            if (disposed != 0)
            {
                settle(VisibilityOutcome.Disposed);
                return;
            }
            if (!OverlayHost.TryPost(window, () =>
                {
                    // Disposed between the call and the run: the window may
                    // still be alive - CloseFromHost is queued behind this -
                    // but nothing it does now is observable to the consumer,
                    // whose events stopped at Dispose.
                    if (disposed != 0)
                        settle(VisibilityOutcome.Disposed);
                    else
                        window.Show(settle);
                }))
            {
                settle(VisibilityOutcome.QueueRefused);
            }
        }

        public void Hide(Action<VisibilityOutcome> completed)
        {
            if (completed == null)
            {
                Hide();
                return;
            }
            Action<VisibilityOutcome> settle = visibilitySettler(completed);
            if (disposed != 0)
            {
                settle(VisibilityOutcome.Disposed);
                return;
            }
            if (!OverlayHost.TryPost(window, () =>
                {
                    if (disposed != 0)
                        settle(VisibilityOutcome.Disposed);
                    else
                        window.Hide(settle);
                }))
            {
                settle(VisibilityOutcome.QueueRefused);
            }
        }

        /// <summary>
        /// Wraps a visibility completion so that it settles exactly once
        /// wherever the answer comes from - the call itself, the window, a
        /// failure, a close - travels like a script result, stays quiet
        /// during shutdown, and reports Disposed whenever the handle is
        /// disposed by the time it settles, because the outcome must agree
        /// with what the consumer can still observe.
        /// </summary>
        private Action<VisibilityOutcome> visibilitySettler(Action<VisibilityOutcome> completed)
        {
            int once = 0;
            return outcome =>
            {
                if (System.Threading.Interlocked.Exchange(ref once, 1) != 0 || OverlayHost.Stopping)
                    return;
                if (disposed != 0)
                    outcome = VisibilityOutcome.Disposed;
                raiseResult(() => completed(outcome));
            };
        }

        public void Navigate(string url) => post(() => window.Navigate(url));

        public void LoadHtml(string html) => post(() => window.LoadHtml(html));

        public void Post(string message) => post(() => window.PostMessageToPage(message));

        public void Post(string channel, string payload) =>
            Post(channel, payload, PostOptions.None);

        public void Post(string channel, string payload, PostOptions options) =>
            post(() => window.PostToChannel(channel, payload, options));

        public bool TryPost(string message) => tryPost(() => window.PostMessageToPage(message));

        public bool TryPost(string channel, string payload) =>
            TryPost(channel, payload, PostOptions.None);

        public bool TryPost(string channel, string payload, PostOptions options) =>
            tryPost(() => window.PostToChannel(channel, payload, options));

        /// <summary>
        /// The same droppable path as <see cref="post"/>, with the host's
        /// answer handed back instead of discarded. A disposed handle answers
        /// false itself; the host answers true during shutdown by its own
        /// rule, and that answer is passed on unchanged - two methods giving
        /// opposite answers to one question would be the trap here.
        /// </summary>
        private bool tryPost(Action action)
        {
            if (disposed != 0)
                return false;
            return OverlayHost.TryPost(window, action);
        }

        public void Request(string channel, string payload, Action<string> answer) =>
            Request(channel, payload, answer, 5000);

        public void Request(string channel, string payload, Action<string> answer, int timeoutMilliseconds)
        {
            if (answer == null)
            {
                // No answer wanted: that is just a message on a channel.
                Post(channel, payload);
                return;
            }
            if (disposed != 0)
            {
                raiseResult(() => answer(null));
                return;
            }
            if (!OverlayHost.TryPost(window, () => window.RequestFromPage(channel, payload,
                value => raiseResult(() => answer(value)), timeoutMilliseconds)))
            {
                // The queue is full, so the question never left. Answering
                // null now keeps "answered exactly once" true even under a
                // flood - the alternative is a caller waiting five seconds
                // for a timeout on a question nobody was ever asked.
                raiseResult(() => answer(null));
            }
        }

        public void OnRequest(string channel, Func<string, string> handler) =>
            OnRequest(channel, handler == null
                ? (Action<string, Action<string>>)null
                : (payload, reply) => reply(handler(payload)));

        public void OnRequest(string channel, Action<string, Action<string>> handler)
        {
            if (channel == null)
                return;
            lock (responders)
            {
                if (handler == null)
                    responders.Remove(channel);
                else
                    responders[channel] = handler;
            }
        }

        public void ExecuteScript(string script) => post(() => window.ExecuteScript(script));

        public void ExecuteScript(string script, Action<string> result)
        {
            if (result == null)
            {
                ExecuteScript(script);
                return;
            }
            // The result travels the same way as every event, so a consumer
            // that asked for main-thread delivery gets it here too.
            if (disposed != 0)
            {
                raiseResult(() => result(null));
                return;
            }
            if (!OverlayHost.TryPost(window, () => window.ExecuteScript(script, value => raiseResult(() => result(value)))))
                raiseResult(() => result(null));
        }

        public void OpenDevTools() => post(() => window.OpenDevTools());

        public void SetBounds(int? x, int? y, int? width, int? height) =>
            post(() => window.SetBounds(x, y, width, height));

        public void SetShape(OverlayRegion[] regions)
        {
            // Converted here, and copied: the overlay reads these on its own
            // thread long after the caller moved on.
            Interop.WebView2Api.RECT[] rects = null;
            if (regions != null && regions.Length > 0)
            {
                rects = new Interop.WebView2Api.RECT[regions.Length];
                for (int i = 0; i < regions.Length; i++)
                {
                    rects[i] = new Interop.WebView2Api.RECT
                    {
                        left = regions[i].X,
                        top = regions[i].Y,
                        right = regions[i].X + Math.Max(0, regions[i].Width),
                        bottom = regions[i].Y + Math.Max(0, regions[i].Height),
                    };
                }
            }
            post(() => window.SetShape(rects));
        }

        public void Dispose()
        {
            // Atomic: the consumer's Failed handler (overlay thread) and a
            // game-shutdown Dispose (Unity thread) can race, and a double
            // CloseFromHost would double-free native resources.
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            // Nobody pumps a handle they have thrown away. Events queued for
            // one are dropped on purpose; answers owed to a caller are not, so
            // they go out here instead of waiting for a pump that will never
            // come.
            drainManualResults();
            OverlayHost.Post(() => window.CloseFromHost());
        }

        private void post(Action action)
        {
            if (disposed != 0)
                return;
            // Fire-and-forget commands are the flood vector, so they take the
            // droppable path; obligations - answers, disposal - go through
            // OverlayHost.Post directly and are never dropped.
            OverlayHost.TryPost(window, action);
        }
    }
}
