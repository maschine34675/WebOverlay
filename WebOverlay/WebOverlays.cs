using System;

namespace WebOverlay
{
    /// <summary>
    /// Shows web pages in windows over Escape From Tarkov, so a mod can build a
    /// user interface in HTML instead of an immediate-mode toolkit.
    ///
    /// Everything is safe to call from Unity's thread; the work is handed to the
    /// overlay's own thread internally. Nothing here throws: when the overlay
    /// cannot be used - typically because no WebView2 runtime is installed -
    /// <see cref="IsAvailable"/> is false and <see cref="Create"/> returns null,
    /// so a mod can fall back to its own behaviour.
    /// </summary>
    public static class WebOverlays
    {
        /// <summary>
        /// Whether overlays can be shown at all. Starts the browser on first
        /// call, which takes a moment, so call it when the player asks for an
        /// overlay rather than during start-up.
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

            var handle = new OverlayHandle(title ?? "Overlay", options ?? new OverlayOptions());
            return handle.Start() ? handle : null;
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
    }

    internal sealed class OverlayHandle : IWebOverlay
    {
        private readonly OverlayWindow window;
        private bool disposed;

        public OverlayHandle(string title, OverlayOptions options)
        {
            window = new OverlayWindow(title, options);
            window.MessageReceived = message => MessageReceived?.Invoke(message);
            window.KeyPressed = key => KeyPressed?.Invoke(key);
            window.Closed = () => Closed?.Invoke();
        }

        public event Action<string> MessageReceived;
        public event Action<int> KeyPressed;
        public event Action Closed;

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
            if (window.IsVisible)
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
            if (disposed)
                return;
            disposed = true;
            OverlayHost.Post(() => window.CloseFromHost());
        }

        private void post(Action action)
        {
            if (disposed)
                return;
            OverlayHost.Post(action);
        }
    }
}
