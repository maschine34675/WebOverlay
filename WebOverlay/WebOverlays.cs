using System;

namespace WebOverlay
{
    /// <summary>
    /// Shows web pages in windows over Escape From Tarkov, so a mod can build a
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
                result[i] = hosts[i] == null ? null : new VirtualHost(hosts[i].Host, hosts[i].Folder);
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
    }

    /// <summary>How an overlay window should look and behave.</summary>
    public sealed class OverlayOptions
    {
        /// <summary>Width in pixels; 0 means 80% of the game's window.</summary>
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
        /// <see cref="IWebOverlay.Hide"/> or Toggle. Unless a size is set, the
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
        /// <see cref="IWebOverlay.Dispose"/>, not at all. Outside the game -
        /// no plugin, no Update - there is nothing to dispatch to, so the
        /// overlay keeps its normal threading and says so once in the log.
        /// </summary>
        public bool DispatchOnMainThread { get; set; }

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
        /// The folder is served read-only and cross-origin requests to it are
        /// denied, so nothing outside the overlay can reach the files.
        /// </summary>
        public VirtualHost[] VirtualHosts { get; set; }
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

        /// <summary>Loads a URL, for a mod that already serves pages.</summary>
        void Navigate(string url);

        /// <summary>Shows markup directly, so no web server is needed.</summary>
        void LoadHtml(string html);

        /// <summary>
        /// Sends a string to the page, where it arrives as a `message` event on
        /// `window.chrome.webview`.
        /// </summary>
        void Post(string message);

        /// <summary>Runs JavaScript in the page, for pushing live values.</summary>
        void ExecuteScript(string script);

        /// <summary>Opens the browser developer tools, if enabled in the options.</summary>
        void OpenDevTools();

        /// <summary>
        /// Raised when the page calls `window.chrome.webview.postMessage(...)`.
        /// Runs on the overlay thread: hop to Unity's thread before touching
        /// game state.
        /// </summary>
        event Action<string> MessageReceived;

        /// <summary>Raised for keys pressed in the overlay that did not close it.</summary>
        event Action<int> KeyPressed;

        /// <summary>
        /// Raised whenever the page the mod targeted has finished loading -
        /// on every navigation, so a reload after a renderer crash raises it
        /// again. This is "my page is live", as opposed to <see cref="Ready"/>,
        /// which only means the browser view exists.
        /// </summary>
        event Action PageLoaded;

        /// <summary>Raised whenever the overlay is hidden or closed.</summary>
        event Action Closed;

        /// <summary>
        /// Raised once the browser view is fully set up. Latched: a handler
        /// subscribed after the fact runs immediately on the subscribing
        /// thread, otherwise on the overlay thread - treat it as "any thread".
        /// </summary>
        event Action Ready;

        /// <summary>
        /// Raised when the overlay cannot work - browser start failed, the
        /// browser process died, HUD transparency unavailable. The overlay
        /// stays hidden; dispose the handle and use a fallback.
        /// <see cref="Failure"/> and <see cref="FailureMessage"/> say why, and
        /// are set before this fires. Latched like <see cref="Ready"/>: may run
        /// on the overlay thread or, when subscribed after the fact, on the
        /// subscribing thread.
        /// </summary>
        event Action Failed;
    }

    internal sealed class OverlayHandle : IWebOverlay
    {
        private readonly OverlayWindow window;
        private int disposed;

        public OverlayHandle(string title, string ownerName, OverlayOptions options)
        {
            dispatchOnMainThread = options.DispatchOnMainThread;
            window = new OverlayWindow(title, ownerName, options);
            window.MessageReceived = message => raise(() => MessageReceived?.Invoke(message));
            window.KeyPressed = key => raise(() => KeyPressed?.Invoke(key));
            window.Closed = () => raise(() => Closed?.Invoke());
            window.PageLoaded = () => raise(() => PageLoaded?.Invoke());
            window.Ready = () => fire(ref readyHandlers, ref readyAlready);
            window.Failed = () => fire(ref failedHandlers, ref failedAlready);
        }

        private readonly bool dispatchOnMainThread;

        /// <summary>
        /// Hands one event to the consumer, on the game's main thread when the
        /// overlay asked for it. A dispatched event that is still queued when
        /// the handle is disposed is dropped: the consumer has already let go
        /// of the overlay, and calling it afterwards is how a fallback ends up
        /// running against a null field.
        /// </summary>
        private void raise(Action invoke)
        {
            if (!dispatchOnMainThread || !OverlayHost.DispatchToMainThread(() =>
                {
                    if (disposed == 0)
                        invokeIsolated(invoke);
                }))
            {
                invokeIsolated(invoke);
            }
        }

        public event Action<string> MessageReceived;
        public event Action<int> KeyPressed;
        public event Action Closed;
        public event Action PageLoaded;

        // Ready and Failed are latched: creation runs on the overlay thread and
        // can finish - or fail - before the consumer had a chance to subscribe.
        // A handler added after the fact runs immediately on the adding thread;
        // either way each handler runs exactly once.
        private readonly object stateSync = new object();
        private Action readyHandlers;
        private Action failedHandlers;
        private bool readyAlready;
        private bool failedAlready;

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

        public OverlayFailure Failure => window.Failure;

        public string FailureMessage => window.FailureMessage;

        internal bool Start()
        {
            OverlayHost.Register(window);
            OverlayHost.Post(() => window.Create());
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

        public void Navigate(string url) => post(() => window.Navigate(url));

        public void LoadHtml(string html) => post(() => window.LoadHtml(html));

        public void Post(string message) => post(() => window.PostMessageToPage(message));

        public void ExecuteScript(string script) => post(() => window.ExecuteScript(script));

        public void OpenDevTools() => post(() => window.OpenDevTools());

        public void Dispose()
        {
            // Atomic: the consumer's Failed handler (overlay thread) and a
            // game-shutdown Dispose (Unity thread) can race, and a double
            // CloseFromHost would double-free native resources.
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            OverlayHost.Post(() => window.CloseFromHost());
        }

        private void post(Action action)
        {
            if (disposed != 0)
                return;
            OverlayHost.Post(action);
        }
    }
}
