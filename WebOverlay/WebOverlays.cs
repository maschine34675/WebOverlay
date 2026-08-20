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

        /// <summary>
        /// A folder in <see cref="OverlayOptions.VirtualHosts"/> could not be
        /// served - a missing folder, a malformed host name, or a runtime too
        /// old to map folders at all. The overlay refuses to continue rather
        /// than let the page's own host name reach the network.
        /// </summary>
        VirtualHostFailed,
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

        /// <summary>
        /// Sends a string on a named channel, where it arrives at every
        /// `window.overlay.on(channel, ...)` handler in the page. Saves both
        /// sides the prefix-and-split every consumer wrote for itself.
        /// </summary>
        void Post(string channel, string payload);

        /// <summary>
        /// Asks the page a question and takes its answer, from the page's
        /// `window.overlay.onRequest(channel, ...)` handler - which may return
        /// a value or a promise. Answered exactly once: with the page's reply,
        /// or with null if no answer arrives within five seconds, so a page
        /// that cannot answer never hangs the mod.
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

        /// <summary>Runs JavaScript in the page, for pushing live values.</summary>
        void ExecuteScript(string script);

        /// <summary>
        /// Runs JavaScript and hands back what it evaluated to, as the JSON the
        /// browser produced ("42", "\"text\"", "null" for no value). The
        /// callback is answered exactly once - with null when the script could
        /// not run at all: no page, a page that is no longer the mod's target,
        /// an overlay that closed, or a script the browser rejected. So a
        /// caller waiting for a value is never left waiting forever - the
        /// answer arrives even after the handle was disposed. The one
        /// exception is the game shutting down, where the library delivers
        /// nothing at all rather than wake a fallback on the way out.
        ///
        /// Threading follows the events: the overlay thread, or the game's
        /// main thread with
        /// <see cref="OverlayOptions.DispatchOnMainThread"/>.
        /// </summary>
        void ExecuteScript(string script, Action<string> result);

        /// <summary>Opens the browser developer tools, if enabled in the options.</summary>
        void OpenDevTools();

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
        /// includes the mod's own <see cref="Hide"/>, so it cannot tell "the
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
        /// "any thread". With
        /// <see cref="OverlayOptions.DispatchOnMainThread"/> it is queued like
        /// every other event instead, so even a late subscription is answered
        /// from the game's next frame rather than inside the Add call.
        /// </summary>
        event Action Ready;

        /// <summary>
        /// Raised when the overlay cannot work - browser start failed, the
        /// browser process died, HUD transparency unavailable. The overlay
        /// stays hidden; dispose the handle and use a fallback.
        /// <see cref="Failure"/> and <see cref="FailureMessage"/> say why, and
        /// are set before this fires. Latched exactly like
        /// <see cref="Ready"/>, including how
        /// <see cref="OverlayOptions.DispatchOnMainThread"/> changes when a
        /// late subscription is answered.
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
            window.ChannelMessage = (channel, payload) => raise(() => ChannelMessage?.Invoke(channel, payload));
            window.RequestReceived = (channel, payload, reply) =>
            {
                Func<string, string> handler;
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
                // the page is answered whatever the handler does.
                raiseResult(() =>
                {
                    string value = null;
                    try
                    {
                        value = handler(payload);
                    }
                    catch (Exception ex)
                    {
                        OverlayHost.LogWarning("a request handler threw ("
                            + ex.GetType().Name + ": " + ex.Message + ").");
                    }
                    reply(value);
                });
            };
            window.KeyPressed = key => raise(() => KeyPressed?.Invoke(key));
            window.Closed = () => raise(() => Closed?.Invoke());
            window.VisibilityChanged = visible => raise(() => VisibilityChanged?.Invoke(visible));
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

        /// <summary>
        /// Delivers a script result. Unlike an event, this is a promise made to
        /// one caller, so it is handed over even after the handle was disposed:
        /// whoever asked for the value is still waiting for it, and dropping it
        /// would break the "answered exactly once" contract that makes the API
        /// safe to await. Only a game that is shutting down swallows it, like
        /// every other callback.
        /// </summary>
        private void raiseResult(Action invoke)
        {
            if (!dispatchOnMainThread || !OverlayHost.DispatchToMainThread(() => invokeIsolated(invoke)))
                invokeIsolated(invoke);
        }

        public event Action<string> MessageReceived;
        public event Action<string, string> ChannelMessage;
        public event Action<int> KeyPressed;

        // One responder per channel, set from the consumer's thread and read
        // on the overlay thread.
        private readonly System.Collections.Generic.Dictionary<string, Func<string, string>> responders =
            new System.Collections.Generic.Dictionary<string, Func<string, string>>(StringComparer.Ordinal);
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

        public void Post(string channel, string payload) => post(() => window.PostToChannel(channel, payload));

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
            OverlayHost.Post(() => window.RequestFromPage(channel, payload,
                value => raiseResult(() => answer(value)), timeoutMilliseconds));
        }

        public void OnRequest(string channel, Func<string, string> handler)
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
            OverlayHost.Post(() => window.ExecuteScript(script, value => raiseResult(() => result(value))));
        }

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
