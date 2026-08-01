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
        public static IWebOverlay Create(string title, OverlayOptions options = null)
        {
            if (!OverlayHost.EnsureStarted())
                return null;

            // A private copy: the overlay reads its options for its whole
            // lifetime, and a caller mutating or reusing the object must not
            // half-transform a live overlay.
            var handle = new OverlayHandle(title ?? "Overlay", snapshot(options));
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
                AllowedOrigins = options.AllowedOrigins == null ? null : (string[])options.AllowedOrigins.Clone(),
            };
        }
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
        /// Display-only HUD mode. Pixels the page leaves unpainted show the
        /// game; painted content floats over it. The window ignores the mouse
        /// and never takes focus, so the game stays fully playable - which also
        /// means <see cref="CloseKeys"/> cannot apply: hide the HUD from the
        /// mod's own hotkey via <see cref="IWebOverlay.Hide"/> or Toggle.
        /// Unless a size is set, the HUD covers the game's whole client area,
        /// and <see cref="Frame"/> is ignored. A sized HUD sits at the game
        /// picture's top-left corner - there is no placement option, so prefer
        /// the full-size default and place elements with CSS.
        ///
        /// Transparency is per pixel but binary: a pixel either shows the game
        /// or shows page content. Semi-transparent page pixels blend towards
        /// near-black instead of the game, and antialiased edges pick up a hint
        /// of that - design HUD elements on their own solid-ish backgrounds.
        /// rgb(3,1,3) is reserved as the transparency key; avoid painting it
        /// (normally it just shows as near-black, but under software rendering
        /// such pixels can vanish).
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
    }

    /// <summary>A single overlay window.</summary>
    public interface IWebOverlay : IDisposable
    {
        bool IsVisible { get; }

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
        /// stays hidden; dispose the handle and use a fallback. Latched like
        /// <see cref="Ready"/>: may run on the overlay thread or, when
        /// subscribed after the fact, on the subscribing thread.
        /// </summary>
        event Action Failed;
    }

    internal sealed class OverlayHandle : IWebOverlay
    {
        private readonly OverlayWindow window;
        private int disposed;

        public OverlayHandle(string title, OverlayOptions options)
        {
            window = new OverlayWindow(title, options);
            window.MessageReceived = message => MessageReceived?.Invoke(message);
            window.KeyPressed = key => KeyPressed?.Invoke(key);
            window.Closed = () => Closed?.Invoke();
            window.Ready = () => fire(ref readyHandlers, ref readyAlready);
            window.Failed = () => fire(ref failedHandlers, ref failedAlready);
        }

        public event Action<string> MessageReceived;
        public event Action<int> KeyPressed;
        public event Action Closed;

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
                invokeIsolated(value);
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
            foreach (Delegate handler in snapshot.GetInvocationList())
                invokeIsolated((Action)handler);
        }

        private static void invokeIsolated(Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                OverlayHost.LogWarning("WebOverlay: an event handler threw (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        public bool IsVisible => window.IsVisible;

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
