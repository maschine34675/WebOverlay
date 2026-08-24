using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WebOverlay.Interop;

namespace WebOverlay
{
    /// <summary>
    /// One overlay: a window over the game plus the browser view inside it.
    /// Everything here runs on the overlay thread; the public handle marshals
    /// calls onto it.
    /// </summary>
    internal sealed class OverlayWindow
    {
        // A window class outlives every window and cannot be unregistered
        // safely, so its WndProc must be a process-lifetime thunk - a
        // per-instance delegate here would dangle after that instance is
        // collected, and the next window of the class would crash the game.
        // Both fields are touched only on the overlay thread.
        private static readonly OverlayHost.WndProcDelegate classProc = staticWndProc;
        private static readonly Dictionary<IntPtr, OverlayWindow> byHandle =
            new Dictionary<IntPtr, OverlayWindow>();

        private enum CreationState
        {
            Creating,
            Ready,
            Failed,
        }

        private readonly string title;
        private readonly string ownerName;
        private readonly OverlayOptions options;

        // Only pages from these origins may be navigated to or send messages.
        // Filled from the mod's own Navigate calls and options.AllowedOrigins;
        // everything else - redirects, followed links, injected navigation -
        // is cancelled, so a foreign page never gains the message bridge.
        private readonly HashSet<string> allowedOrigins = new HashSet<string>(StringComparer.Ordinal);

        // Origins the mod asked to have served from a local folder. Until the
        // mapping is proven to be in place they are refused outright, so a
        // failed mapping cannot quietly become a real request to the internet.
        private readonly HashSet<string> unmappedOrigins = new HashSet<string>(StringComparer.Ordinal);

        // One FIFO for messages and scripts: their relative order is part of
        // what the consumer expressed. Cleared whenever the mod retargets the
        // overlay - buffered items belonged to the old page and must not leak
        // into the new one.
        private readonly List<Pending> outbox = new List<Pending>();

        /// <summary>
        /// A send waiting for the page. A script may carry the consumer's
        /// result callback, which has to be answered either way: dropping it
        /// silently would leave a caller waiting for a value that can never
        /// arrive.
        /// </summary>
        private struct Pending
        {
            public bool IsScript;
            public string Payload;
            public Action<string> Result;

            /// <summary>Set for a channel message that may be superseded.</summary>
            public string LatestChannel;

            /// <summary>
            /// The question this envelope asks, when it is one. A request that
            /// has already timed out - answered null - must not still be put
            /// to the page later, or a handler with side effects runs for an
            /// answer nobody is listening for any more.
            /// </summary>
            public int RequestId;

            /// <summary>
            /// The document this envelope belongs to, for the two kinds that
            /// belong to one: a question put to a page, and an answer owed to
            /// it. Zero for an ordinary send, which belongs to whatever page
            /// the mod is targeting rather than to a particular document.
            /// </summary>
            public int Generation;
        }

        // The current value of each retained channel, in the order the mod
        // first sent them, so a page that loads later - or reloads after a
        // renderer crash - starts from the state the mod thinks it is in.
        private readonly List<KeyValuePair<string, string>> retained =
            new List<KeyValuePair<string, string>>();

        // The shape the overlay is cut down to, or null for the whole window.
        private WebView2Api.RECT[] shape;

        // Requests this overlay sent to the page and is still waiting on. A
        // page that never answers - no handler, a script error, a page that
        // navigated away mid-question - must not leave the mod hanging, so
        // each one carries a deadline.
        private readonly Dictionary<int, PendingRequest> pendingRequests = new Dictionary<int, PendingRequest>();
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private System.Threading.Timer requestTimer;
        private int nextRequestId;

        private struct PendingRequest
        {
            public Action<string> Answer;
            public long DeadlineMilliseconds;
        }

        // Scripts whose result a consumer is waiting for. Kept until they are
        // answered so the browser never calls a collected delegate, and
        // retired right after so they do not accumulate.
        private readonly List<ScriptCall> pendingScripts = new List<ScriptCall>();

        // Whether the current target was ever actually handed to the browser.
        // A page named while the browser was still starting is only recorded,
        // and replacing a recording is not leaving a page - so the state the
        // mod set up beforehand still belongs to whichever page finally loads.
        // Without this the answer would depend on whether the deferred first
        // navigation happened to run before the mod's next call, which is a
        // race in something a consumer can observe.
        private bool targetHandedToBrowser;

        // Whether the navigation now in flight is leaving a page behind. Acted
        // on when that navigation starts rather than when it was requested,
        // because a request the browser takes can still be refused - by this
        // library's own origin filter - and then nothing was left at all.
        private bool forgetOnNavigationStart;

        // Set between handing a navigation to the browser and seeing it start.
        // Any completion in that window belongs to the navigation being
        // replaced, not to the one the mod is waiting for.
        private bool awaitingNavigationStart;

        // Bumped for every top-level document. A question asked by one
        // document must not be answered into the next.
        private int documentGeneration;

        // The channel shim is installed asynchronously; the first navigation
        // waits for the browser to confirm it rather than racing it.
        private bool shimSettled;
        private bool navigationOwedToShim;

        /// <summary>
        /// One script in flight. The consumer callback lives here rather than
        /// only inside the completion closure, because a close has to be able
        /// to answer it: once the controller is gone the completion may never
        /// arrive. Settling is once-only, so a completion that does arrive
        /// afterwards finds the call already answered.
        /// </summary>
        private sealed class ScriptCall
        {
            public ComCallback Callback;
            public Action<string> Result;
            private int settled;

            public bool Settle() => System.Threading.Interlocked.Exchange(ref settled, 1) == 0;
        }

        private IntPtr window;
        private IntPtr controller;
        private IntPtr webView;
        private IntPtr compositionController;
        private IntPtr dcompDevice;
        private IntPtr dcompTarget;
        private IntPtr dcompVisual;
        private ComCallback compositionCompleted;
        private ComCallback cursorChangedCallback;
        private bool usesComposition;
        private IntPtr currentCursor;
        private bool trackingMouseLeave;
        private int mouseButtonsDown;
        private ComCallback controllerCallback;
        private ComCallback keyCallback;
        private ComCallback messageCallback;
        private ComCallback navigationStartingCallback;
        private ComCallback frameNavigationCallback;
        private ComCallback navigationCompletedCallback;
        private ComCallback newWindowCallback;
        private ComCallback permissionCallback;
        private ComCallback processFailedCallback;
        private ComCallback scriptCompletedCallback;
        private ComCallback channelShimCallback;
        private string pendingUrl;
        private string pendingHtml;
        private bool desiredVisible = true;
        private volatile bool isVisible;
        private CreationState state = CreationState.Creating;
        private bool pageReady;
        private bool htmlLoaded;
        private bool expectInlineNavigation;
        private bool closed;
        private bool everPositioned;
        private bool warnedAboutFullscreen;
        private bool windowedInMainEnvironment;
        private int renderRecoveries;
        private int overflowDropped;

        /// <summary>
        /// Bounds are remembered per key. The default is namespaced with the
        /// calling mod's assembly, so two mods titling their window the same
        /// do not trade positions; an explicit PersistenceKey wins verbatim.
        /// </summary>
        private string persistenceKey => options.PersistenceKey ?? (ownerName + "/" + title);

        private bool remembersBounds => options.RememberBounds && !options.Transparent;

        public OverlayWindow(string title, string ownerName, OverlayOptions options)
        {
            this.title = title;
            this.ownerName = ownerName;
            this.options = options;
            if (options.AllowedOrigins != null)
                foreach (string origin in options.AllowedOrigins)
                    allowOrigin(origin);
        }

        private static IntPtr staticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (byHandle.TryGetValue(hwnd, out OverlayWindow target))
            {
                if (message == WM_NCDESTROY)
                    byHandle.Remove(hwnd);
                return target.wndProc(hwnd, message, wParam, lParam);
            }
            return OverlayHost.DefWindowProc(hwnd, message, wParam, lParam);
        }

        public bool IsVisible => isVisible;

        internal bool DesiredVisible => desiredVisible;

        public Action<string> MessageReceived;
        public Action<string, string> ChannelMessage;

        /// <summary>
        /// The page asked the mod something: channel, payload, and the reply
        /// to call exactly once. Left null, every request is answered with
        /// nothing rather than left open.
        /// </summary>
        public Action<string, string, Action<string>> RequestReceived;

        public Action<int> KeyPressed;
        public Action Closed;
        public Action<bool> VisibilityChanged;
        public Action PageLoaded;

        /// <summary>
        /// Raises VisibilityChanged only for real transitions, so a consumer
        /// can trust it as state rather than having to compare against its own
        /// flag - which is what Closed, firing on every Hide, forces today.
        /// </summary>
        private void setVisible(bool value, bool notify = true)
        {
            if (isVisible == value)
                return;
            OverlayHost.LogDiagnostic("overlay '" + title + "': shown -> " + value);
            isVisible = value;
            if (notify)
                VisibilityChanged?.Invoke(value);
        }
        public Action Ready;
        public Action Failed;

        /// <summary>
        /// Read from the consumer's thread, written here - both are single
        /// writes of a word, and the volatile keeps the reader from seeing a
        /// stale one.
        /// </summary>
        private volatile bool pageLoaded;

        public bool IsPageLoaded => pageLoaded;

        /// <summary>
        /// Which transparency this overlay ended up with - decided before the
        /// window exists, so it is already true when the page is injected.
        /// </summary>
        public OverlayTransparency Transparency => !options.Transparent
            ? OverlayTransparency.None
            : usesComposition ? OverlayTransparency.Composition : OverlayTransparency.ChromaKey;

        /// <summary>
        /// Read once per frame by the plugin. The question is whether this
        /// overlay is the window in front - not whether Unity thinks it lost
        /// focus, which is a different thing and, in a game that keeps the
        /// cursor, not the one that decides.
        /// </summary>
        /// <summary>Handle, title and the two conditions, for a diagnostic report.</summary>
        internal string Describe() =>
            window.ToString("X") + "(" + title + ",free=" + options.FreeCursorWhileShown
                + ",shown=" + isVisible + ")";

        /// <summary>
        /// Whether this overlay wants the cursor handed back right now.
        /// </summary>
        /// <remarks>
        /// Being the foreground window is the whole test: Windows does not
        /// give the foreground to a hidden window, so "is it shown" adds
        /// nothing - except a race it used to lose. Show() makes the window
        /// visible and takes the foreground BEFORE it records that the overlay
        /// is shown, so for the moment in between this said "not mine" about a
        /// window sitting in front of the player. Asked once per frame, that
        /// moment is easy to hit, and the cursor then stayed captured for as
        /// long as the panel was up.
        /// </remarks>
        // What was last applied, so the style is only touched on a change.
        private bool clickThroughApplied;

        /// <summary>
        /// Lets the mouse through to the game while the game is in front, for
        /// an overlay that asked for it. Called once per frame with the OS
        /// foreground; does nothing until the answer changes.
        /// </summary>
        internal void UpdateClickThrough(IntPtr foreground)
        {
            // A HUD is already click-through or deliberately interactive, and
            // it never holds the foreground, so this would only ever be on.
            if (!options.ClickThroughWhenUnfocused || options.Transparent || window == IntPtr.Zero)
                return;
            bool through = window != foreground;
            if (through == clickThroughApplied)
                return;
            clickThroughApplied = through;
            applyClickThrough(through);
        }

        private void applyClickThrough(bool through)
        {
            uint style = (uint)GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
            if (through)
            {
                // Layered as well as transparent: measured on the composed HUD,
                // hit-testing only skips a window when both are set. A layered
                // window also needs its attributes set or it is not painted at
                // all, so the alpha goes back to fully opaque immediately.
                style |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
                SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style));
                SetLayeredWindowAttributes(window, 0, byte.MaxValue, LWA_ALPHA);
                return;
            }
            style &= ~WS_EX_TRANSPARENT;
            // The layer itself stays when the mod asked for it through Opacity;
            // taking it away there would drop the fade with it.
            if (opacityAsAlpha() == byte.MaxValue)
                style &= ~WS_EX_LAYERED;
            SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(style));
        }

        internal bool WantsFreeCursor(IntPtr foreground) =>
            options.FreeCursorWhileShown && window != IntPtr.Zero && window == foreground;

        public OverlayFailure Failure { get; private set; } = OverlayFailure.Unknown;

        public string FailureMessage { get; private set; }

        /// <summary>
        /// Marks the overlay broken and tells the consumer, exactly once. The
        /// window is hidden rather than destroyed - the consumer still owns the
        /// handle and disposes it. The kind is what the consumer can act on;
        /// the reason is the exact sentence, logged and readable from the
        /// handle.
        /// </summary>
        private void fail(OverlayFailure kind, string reason)
        {
            if (state == CreationState.Failed)
                return;
            state = CreationState.Failed;
            // Before anything can raise Failed: a handler reads these.
            Failure = kind;
            FailureMessage = reason;
            OverlayHost.LogWarning(reason);
            // There is no page any more, so IsPageLoaded must stop saying there
            // is: a consumer that streams reads it to decide whether to send.
            pageReady = false;
            pageLoaded = false;
            // Whoever was waiting for a script result is owed an answer, and
            // this overlay will never produce one. Settled, not disposed, and
            // the list is left alone: unlike CloseFromHost this can run inside
            // the ProcessFailed callback with the controller still open, so a
            // late completion may yet arrive - Settle is once-only, and the
            // eventual close still disposes them exactly as before.
            foreach (ScriptCall pending in pendingScripts.ToArray())
            {
                if (pending.Settle())
                    answer(pending.Result, null);
            }
            // During game shutdown a failure is expected, and notifying the
            // consumer would trigger its fallback - picture a fallback browser
            // window popping up while the game quits. That applies to the
            // visibility change this hide causes just as much as to Failed.
            if (window != IntPtr.Zero && isVisible)
            {
                ShowWindow(window, SW_HIDE);
                setVisible(false, !OverlayHost.Stopping);
            }
            if (!OverlayHost.Stopping)
                Failed?.Invoke();
        }

        public bool Create()
        {
            try
            {
                return createCore();
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the consumer must hear a terminal state -
                // a handle stuck in Creating answers nothing forever.
                fail(OverlayFailure.Unknown, "creation threw (" + ex.GetType().Name + ": " + ex.Message + ").");
                return false;
            }
        }

        /// <summary>
        /// Whether building this overlay is still worth finishing. Read it
        /// again after anything that pumps messages: the consumer may have
        /// disposed the handle, or the host may have started shutting down,
        /// while this method was suspended inside such a wait.
        /// </summary>
        private bool stillWanted => !closed && !OverlayHost.Stopping;

        private bool createCore()
        {
            // Disposed before this ever ran. Commands and creations sit in
            // separate queues since 1.7.0, and commands are drained first, so a
            // Create and a Dispose posted in that order can arrive the other way
            // round. Building the window now would leave a window and a browser
            // view whose owner is already gone and whose CloseFromHost has been
            // and gone. No fail() either: the consumer threw the handle away,
            // and raising Failed would push it into a fallback it never asked
            // for.
            if (closed)
                return false;

            if (OverlayHost.Environment == IntPtr.Zero)
            {
                // Naming the cause, not the symptom: the consumer's user can
                // act on "no WebView2 runtime", never on "no environment".
                fail(OverlayHost.StartFailure == OverlayFailure.Unknown
                        ? OverlayFailure.EnvironmentFailed : OverlayHost.StartFailure,
                    OverlayHost.StartFailureMessage == null
                        ? "no browser environment; the overlay cannot be created."
                        : "the overlay cannot be created: " + OverlayHost.StartFailureMessage);
                return false;
            }

            // HUDs prefer composition hosting: true per-pixel alpha instead of
            // the binary chroma key, and the only mode that can forward mouse
            // input. Decided before the window exists because the two modes
            // need different window styles. Display-only HUDs fall back to the
            // proven chroma key when composition is unavailable; interactive
            // ones cannot (a chroma-keyed window never receives per-pixel
            // input), so they fail instead.
            if (options.Transparent)
            {
                usesComposition = tryPrepareComposition();
                if (!usesComposition && options.Interactive)
                {
                    fail(OverlayFailure.CompositionUnavailable, "an interactive HUD needs composition support (Windows 8+ with a 2021+ WebView2 runtime).");
                    return false;
                }
            }

            if (!createWindow())
            {
                fail(OverlayFailure.WindowFailed, "the overlay window could not be created.");
                return false;
            }

            if (usesComposition)
                return createComposedView();

            // Chosen up front, because the completion below needs to know
            // which browser this view ended up in.
            // A suspension point, not a getter: for a windowed view this may
            // have to start a second browser, and waiting for one pumps
            // messages - which can run this very window's CloseFromHost. So
            // whether anyone still wants this overlay has to be asked again.
            IntPtr host = OverlayHost.EnvironmentFor(false);
            if (!stillWanted)
                return false;
            controllerCallback = new ComCallback(WebView2Api.IID_ControllerCompleted, (int result, IntPtr pointer) =>
            {
                // Disposed while the browser was still starting: expected, not
                // a failure. Destroying the parent window makes WebView2 report
                // E_ABORT here, and publishing that as Failed would send the
                // consumer down its fallback during a normal shutdown. Storing
                // the controller would resurrect a closed overlay instead.
                if (closed)
                {
                    if (result == WebView2Api.S_OK && pointer != IntPtr.Zero)
                        WebView2Api.Method<WebView2Api.NoArgsDelegate>(pointer, WebView2Api.Controller_Close)(pointer);
                    return WebView2Api.S_OK;
                }

                if (result != WebView2Api.S_OK || pointer == IntPtr.Zero)
                {
                    fail(OverlayFailure.ViewFailed, "the browser view failed, hr=0x" + result.ToString("X8") + ".");
                    return WebView2Api.S_OK;
                }

                controller = pointer;
                Marshal.AddRef(controller);
                if (!usesComposition)
                {
                    windowedInMainEnvironment = host == OverlayHost.Environment;
                    OverlayHost.WindowedControllerOpened(windowedInMainEnvironment);
                }
                try
                {
                    configure();
                }
                catch (Exception ex)
                {
                    // The ComCallback thunk would swallow this silently and
                    // leave the handle in Creating forever.
                    fail(OverlayFailure.ViewFailed, "configuration threw (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.Method<WebView2Api.CreateControllerDelegate>(
                host, WebView2Api.Environment_CreateController)(
                host, window, controllerCallback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                fail(OverlayFailure.ViewFailed, "could not request a browser view, hr=0x" + hr.ToString("X8") + ".");
                return false;
            }

            return true;
        }

        private void configure()
        {
            WebView2Api.Method<WebView2Api.GetPointerDelegate>(controller, WebView2Api.Controller_GetCoreWebView2)(
                controller, out webView);
            if (webView == IntPtr.Zero)
            {
                fail(OverlayFailure.ViewFailed, "the browser view could not be obtained.");
                return;
            }

            // The security boundary is part of creation success: an overlay
            // whose navigation filter, message filter, popup suppression or
            // permission denial did not register must not present itself as
            // working - that would be fail-open.
            if (!applySettings() || !subscribeToMessages() || !subscribeToNavigation())
            {
                fail(OverlayFailure.ViewFailed, "a security-critical setting could not be applied.");
                return;
            }
            if (options.Transparent && !applyTransparentBackground())
            {
                // Without a transparent background the "HUD" would be a white
                // click-through sheet over the whole game. Staying invisible is
                // the only safe failure.
                fail(OverlayFailure.CompositionUnavailable, "the HUD stays hidden because transparency is unavailable.");
                return;
            }
            injectChannelShim();

            // Before any navigation: a page loaded first would already have
            // failed to find its own files. A mapping that did not take is
            // terminal - the consumer's page cannot work, and continuing is
            // what would send it to the network instead.
            if (!applyVirtualHosts())
            {
                fail(OverlayFailure.VirtualHostFailed,
                    "a virtual host folder could not be served; see the lines above.");
                return;
            }

            if (!subscribeToKeys())
            {
                // A frameless interactive overlay whose close keys did not
                // register would trap the player: no frame, no way out. A
                // framed one still has its close button; a HUD never has focus.
                if (!options.Frame && !options.Transparent)
                {
                    fail(OverlayFailure.ViewFailed, "the close keys could not register on a frameless overlay.");
                    return;
                }
                OverlayHost.LogWarning("the close keys could not register; use the close button.");
            }
            fitToClientArea();

            // Replays what the mod asked for while the browser was starting -
            // through an internal path, because the public methods clear the
            // outbox and would wipe messages posted right after Create. A
            // Hide() or Toggle() in the gap must win over the default Show.
            // A refusal here is deliberately NOT a failure of the overlay. The
            // mod named a page the browser will not take - oversized markup, a
            // malformed URL - which is a content bug in the mod, not a broken
            // window: everything else about this overlay works, and the next
            // LoadHtml will load fine. Failing it would destroy a working
            // overlay over one bad page and push the consumer into its fallback
            // for good. What startPendingNavigation does instead is clear the
            // target, so the overlay is honestly "no page named" rather than
            // pointing at something that can never load; the refusal itself is
            // logged. The crash-recovery path decides differently, because
            // there the page WAS live and its reload being refused leaves
            // nothing to go back to.
            // Only if the shim is in place. Otherwise the completion above
            // starts it, moments later and on this same thread.
            if (shimSettled)
                startPendingNavigation();
            else
                navigationOwedToShim = true;
            if (desiredVisible)
                Show();

            // Ready last: everything internal is done, and a consumer handler
            // that throws must not be able to leave the overlay half-built.
            state = CreationState.Ready;
            Ready?.Invoke();
        }

        /// <summary>
        /// Puts `window.overlay` into every document before its own scripts
        /// run, which is the half of named channels a consumer cannot write
        /// for itself. It only wraps the existing message bridge, so it hands
        /// a page nothing it did not already have.
        ///
        /// Installation is asynchronous, and the browser only promises the script is installed
        /// once the completion has run - so the first navigation waits for it
        /// rather than racing it. Without that, the mod's own first page could
        /// come up without the object its very first script uses.
        ///
        /// Only the navigation waits. `Ready` and the window itself do not:
        /// `Ready` has never meant "the page is loaded" - that is
        /// <see cref="PageLoaded"/> - so nothing a consumer can observe moves.
        /// </summary>
        private void injectChannelShim()
        {
            if (channelShimCallback == null)
                channelShimCallback = new ComCallback(WebView2Api.IID_AddScriptCompleted, (int hr, IntPtr id) =>
                {
                    if (hr != WebView2Api.S_OK)
                        OverlayHost.LogWarning("the channel shim was rejected, hr=0x" + hr.ToString("X8")
                            + "; named channels will not work on this overlay.");
                    shimSettled = true;
                    if (navigationOwedToShim)
                    {
                        navigationOwedToShim = false;
                        // The overlay may have been closed or failed while the
                        // browser was confirming the script; navigating a view
                        // in either state is pointless at best.
                        // Unless the mod got there first. The usual consumer
                        // shape is a Ready handler that posts LoadHtml, and
                        // that work item runs on this thread before the
                        // browser confirms the script - so the page is already
                        // on its way. Navigating again would start the same
                        // document twice: two generations, two PageLoaded, the
                        // page's own first scripts twice.
                        if (!closed && state != CreationState.Failed && webView != IntPtr.Zero
                            && !targetHandedToBrowser && !startPendingNavigation())
                        {
                            forgetTarget();
                        }
                    }
                    return WebView2Api.S_OK;
                });

            int result = WebView2Api.Method<WebView2Api.ExecuteScriptDelegate>(
                webView, WebView2Api.WebView_AddScriptToExecuteOnDocumentCreated)(
                webView, ChannelProtocol.ShimFor(Transparency, options.InjectTheme),
                channelShimCallback.Pointer);
            if (result != WebView2Api.S_OK)
            {
                // Refused outright: no completion is coming, so waiting for one
                // would hang the first page for good. The overlay goes on
                // without channels, which the warning says.
                OverlayHost.LogWarning("could not install the channel shim, hr=0x" + result.ToString("X8")
                    + "; named channels will not work on this overlay.");
                shimSettled = true;
            }
        }

        /// <summary>
        /// Serves the mod's own folders as `https://&lt;host&gt;/`, which is the
        /// only way a page gets real files - and, when it navigates there
        /// instead of being handed inline markup, a real origin with working
        /// storage. Read-only, and the mapping lives only in this overlay's
        /// view, so nothing outside it reaches the folder; inside it, CORS-
        /// checked access from another origin is denied while ordinary
        /// sub-resource loads are not.
        ///
        /// A bad mapping is a broken page, not a broken security boundary, so
        /// it is logged and skipped rather than failing the overlay - the
        /// consumer sees its own missing content.
        /// </summary>
        private bool applyVirtualHosts()
        {
            if (options.VirtualHosts == null || options.VirtualHosts.Length == 0)
                return true;

            // Every requested host is barred until its mapping is in place.
            // Without this a failed mapping would turn the mod's own
            // "https://yourmod.assets/index.html" into an ordinary internet
            // navigation - a foreign page under an origin the mod trusts, with
            // the message bridge open. The folder is what was asked for; the
            // network never is.
            foreach (VirtualHost requested in options.VirtualHosts)
            {
                string origin = requested == null ? null : originOf("https://" + requested.Host);
                if (origin != null)
                    unmappedOrigins.Add(origin);
            }

            Guid iid = WebView2Api.IID_WebView2_3;
            if (Marshal.QueryInterface(webView, ref iid, out IntPtr webView3) != WebView2Api.S_OK
                || webView3 == IntPtr.Zero)
            {
                OverlayHost.LogWarning("this WebView2 runtime cannot map folders to hosts.");
                return false;
            }

            bool allMapped = true;
            try
            {
                var mapping = WebView2Api.Method<WebView2Api.SetVirtualHostMappingDelegate>(
                    webView3, WebView2Api.WebView2_3_SetVirtualHostNameToFolderMapping);
                foreach (VirtualHost host in options.VirtualHosts)
                {
                    if (host == null || !isUsableHostName(host.Host))
                    {
                        OverlayHost.LogWarning("a virtual host name must be a bare host"
                            + " like \"yourmod.assets\" (got " + describe(host?.Host) + ").");
                        allMapped = false;
                        continue;
                    }
                    if (string.IsNullOrEmpty(host.Folder) || !System.IO.Directory.Exists(host.Folder))
                    {
                        OverlayHost.LogWarning("virtual host " + host.Host
                            + ": the folder " + describe(host.Folder) + " does not exist.");
                        allMapped = false;
                        continue;
                    }

                    int hr = mapping(webView3, host.Host, System.IO.Path.GetFullPath(host.Folder),
                        WebView2Api.HostResourceAccessDenyCors);
                    if (hr != WebView2Api.S_OK)
                    {
                        OverlayHost.LogWarning("mapping " + host.Host + " failed, hr=0x" + hr.ToString("X8") + ".");
                        allMapped = false;
                        continue;
                    }

                    // Served from disk now, so the mod may load it and talk to
                    // it - exactly like a Navigate target.
                    unmappedOrigins.Remove(originOf("https://" + host.Host));
                    allowOrigin("https://" + host.Host);
                }
            }
            finally
            {
                Marshal.Release(webView3);
            }
            return allMapped;
        }

        /// <summary>
        /// A host name only - no scheme, path, port or credentials. Anything
        /// else would silently map nothing, or map something other than what
        /// the caller wrote.
        /// </summary>
        private static bool isUsableHostName(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            foreach (char c in host)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '.')
                    continue;
                return false;
            }
            return host[0] != '.' && host[host.Length - 1] != '.';
        }

        private static string describe(string value) =>
            value == null ? "<null>" : "\"" + value + "\"";

        /// <summary>
        /// Nothing can reach a page that no longer exists. Without this a send
        /// after the failure would go into the outbox, and a script caller
        /// would wait there until the buffer overflowed or the handle was
        /// disposed - long after the answer was knowable.
        /// </summary>
        private bool refuseAfterFailure(Action<string> result)
        {
            if (state != CreationState.Failed && !closed)
                return false;
            answer(result, null);
            return true;
        }

        /// <summary>
        /// Returns false when the browser refused the page outright, which is
        /// terminal on this path: nothing here has an earlier target to fall
        /// back to, and no further attempt is coming.
        /// </summary>
        private bool startPendingNavigation()
        {
            if (pendingHtml != null)
            {
                pageReady = false;
                pageLoaded = false;
                expectInlineNavigation = true;
                awaitingNavigationStart = true;
                if (!checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                    webView, WebView2Api.WebView_NavigateToString)(webView, pendingHtml), "LoadHtml"))
                {
                    forgetTarget();
                    return false;
                }
                targetHandedToBrowser = true;
            }
            else if (pendingUrl != null)
            {
                pageReady = false;
                pageLoaded = false;
                awaitingNavigationStart = true;
                if (!checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                    webView, WebView2Api.WebView_Navigate)(webView, pendingUrl), "Navigate"))
                {
                    forgetTarget();
                    return false;
                }
                targetHandedToBrowser = true;
            }
            return true;
        }

        /// <summary>
        /// Back to "no page named". Only for a navigation the browser refused
        /// on this internal path, which has no earlier target to fall back to:
        /// a mod that asked for a page before the browser existed, or a reload
        /// after a renderer crash. Leaving the refused page as the target
        /// would point the overlay at something that can never load - every
        /// send would buffer into nothing, IsPageLoaded would stay false for
        /// good, and the mod's next LoadHtml would look like a retarget away
        /// from it and throw out state that never belonged to any page.
        /// </summary>
        private void forgetTarget()
        {
            pendingUrl = null;
            pendingHtml = null;
            htmlLoaded = false;
            expectInlineNavigation = false;
            // Nothing was ever handed to the browser, so no start is owed.
            awaitingNavigationStart = false;
            targetHandedToBrowser = false;
            forgetOnNavigationStart = false;
        }

        private bool applySettings()
        {
            WebView2Api.Method<WebView2Api.GetPointerDelegate>(webView, WebView2Api.WebView_GetSettings)(
                webView, out IntPtr settings);
            if (settings == IntPtr.Zero)
                return false;

            try
            {
                // Messaging is what turns a page into a control surface, so it is
                // on by default; the browser chrome is off because this should
                // not look like a browser. Script dialogs are off because a
                // page-triggered alert() would freeze the overlay UI - and they
                // are on by default, so this one is fail-open and must succeed.
                bool critical =
                    setSetting(settings, WebView2Api.Settings_PutIsWebMessageEnabled, true) == WebView2Api.S_OK
                    & setSetting(settings, WebView2Api.Settings_PutAreDefaultScriptDialogsEnabled, false) == WebView2Api.S_OK;
                setSetting(settings, WebView2Api.Settings_PutIsStatusBarEnabled, false);
                setSetting(settings, WebView2Api.Settings_PutAreDefaultContextMenusEnabled, options.ContextMenu);
                setSetting(settings, WebView2Api.Settings_PutAreDevToolsEnabled, options.DevTools);
                // No host objects are ever registered, but the default is
                // permissive - close the door explicitly.
                setSetting(settings, WebView2Api.Settings_PutAreHostObjectsAllowed, false);
                if (!critical)
                    return false;

                // Browser shortcuts (print, find, refresh) make an overlay feel
                // like a browser - off unless a developer wants F5/F12. The
                // password and form-fill stores are shared across every mod
                // using this library, so they stay off entirely. All three live
                // on versioned settings interfaces: QI, absolute slot, and old
                // runtimes simply keep their defaults.
                Guid settings3 = WebView2Api.IID_Settings3;
                if (Marshal.QueryInterface(settings, ref settings3, out IntPtr s3) == WebView2Api.S_OK && s3 != IntPtr.Zero)
                {
                    try
                    {
                        setSetting(s3, WebView2Api.Settings3_PutAreBrowserAcceleratorKeysEnabled, options.DevTools);
                    }
                    finally
                    {
                        Marshal.Release(s3);
                    }
                }

                Guid settings4 = WebView2Api.IID_Settings4;
                if (Marshal.QueryInterface(settings, ref settings4, out IntPtr s4) == WebView2Api.S_OK && s4 != IntPtr.Zero)
                {
                    try
                    {
                        setSetting(s4, WebView2Api.Settings4_PutIsPasswordAutosaveEnabled, false);
                        setSetting(s4, WebView2Api.Settings4_PutIsGeneralAutofillEnabled, false);
                    }
                    finally
                    {
                        Marshal.Release(s4);
                    }
                }
            }
            finally
            {
                Marshal.Release(settings);
            }
            return true;
        }

        private static int setSetting(IntPtr settings, int slot, bool value)
        {
            return WebView2Api.Method<WebView2Api.PutBoolDelegate>(settings, slot)(settings, value ? 1 : 0);
        }

        private bool subscribeToKeys()
        {
            keyCallback = new ComCallback(WebView2Api.IID_AcceleratorKeyPressed, (IntPtr sender, IntPtr args) =>
            {
                if (args == IntPtr.Zero)
                    return WebView2Api.S_OK;

                WebView2Api.Method<WebView2Api.GetIntDelegate>(args, WebView2Api.KeyArgs_GetKeyEventKind)(
                    args, out int kind);
                if (kind != WebView2Api.KeyEventKindKeyDown && kind != WebView2Api.KeyEventKindSystemKeyDown)
                    return WebView2Api.S_OK;

                WebView2Api.Method<WebView2Api.GetUIntDelegate>(args, WebView2Api.KeyArgs_GetVirtualKey)(
                    args, out uint key);
                bool consumed = onKey((int)key);
                // A consumed close key must not also reach the page or trigger
                // a browser accelerator - and auto-repeat would reopen chaos.
                if (consumed)
                    WebView2Api.Method<WebView2Api.PutBoolDelegate>(args, WebView2Api.KeyArgs_PutHandled)(args, 1);
                return WebView2Api.S_OK;
            });

            return WebView2Api.Method<WebView2Api.AddEventDelegate>(controller, WebView2Api.Controller_AddAcceleratorKeyPressed)(
                controller, keyCallback.Pointer, out _) == WebView2Api.S_OK;
        }

        private bool subscribeToMessages()
        {
            messageCallback = new ComCallback(WebView2Api.IID_WebMessageReceived, (IntPtr sender, IntPtr args) =>
            {
                // Not "no MessageReceived, nothing to do" any more: a
                // consumer may use only channels, and answers to its own
                // requests still have to find their way home.
                if (args == IntPtr.Zero)
                    return WebView2Api.S_OK;

                // The bridge only trusts pages the mod itself put there; after
                // any navigation this library failed to block, the sender would
                // be foreign and its messages must not reach the mod.
                string source = readString(args, WebView2Api.MessageArgs_GetSource);
                if (!isMessageAllowed(source))
                {
                    OverlayHost.LogWarning("dropped a message from " + (source ?? "<unknown>") + ".");
                    return WebView2Api.S_OK;
                }

                WebView2Api.Method<WebView2Api.GetPointerDelegate>(
                    args, WebView2Api.MessageArgs_TryGetWebMessageAsString)(args, out IntPtr text);
                if (text == IntPtr.Zero)
                    return WebView2Api.S_OK;

                string message = Marshal.PtrToStringUni(text);
                Marshal.FreeCoTaskMem(text);
                if (!routeChannelMessage(message))
                    MessageReceived?.Invoke(message);
                return WebView2Api.S_OK;
            });

            return WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddWebMessageReceived)(
                webView, messageCallback.Pointer, out _) == WebView2Api.S_OK;
        }

        /// <summary>
        /// The security boundary of the overlay: navigation is refused unless
        /// the target origin was put on the list by the mod itself, popups are
        /// suppressed, permission prompts (camera, location, ...) are denied,
        /// and a dead browser process is reported instead of silently showing
        /// a corpse. Returns false when any of these did not register - an
        /// overlay without its boundary must not go live.
        /// </summary>
        private bool subscribeToNavigation()
        {
            bool armed = true;

            // Top-level and iframe navigation run through the same filter -
            // an unfiltered iframe would still load a foreign page.
            navigationStartingCallback = new ComCallback(WebView2Api.IID_NavigationStarting,
                (IntPtr sender, IntPtr args) => onNavigationStarting(args, true));
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddNavigationStarting)(
                webView, navigationStartingCallback.Pointer, out _) == WebView2Api.S_OK;

            frameNavigationCallback = new ComCallback(WebView2Api.IID_NavigationStarting,
                (IntPtr sender, IntPtr args) => onNavigationStarting(args, false));
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddFrameNavigationStarting)(
                webView, frameNavigationCallback.Pointer, out _) == WebView2Api.S_OK;

            navigationCompletedCallback = new ComCallback(WebView2Api.IID_NavigationCompleted, (IntPtr sender, IntPtr args) =>
            {
                if (args == IntPtr.Zero)
                    return WebView2Api.S_OK;
                // A completion that arrives while we are still waiting for our
                // navigation to start belongs to the one it replaced. Taking
                // it would mark the page the mod is waiting for as loaded -
                // and flush the outbox into a document that is on its way out.
                // Its failures are not ours to report either: a superseded
                // navigation completes as cancelled.
                if (awaitingNavigationStart)
                    return WebView2Api.S_OK;
                WebView2Api.Method<WebView2Api.GetIntDelegate>(args, WebView2Api.NavCompletedArgs_GetIsSuccess)(
                    args, out int success);
                if (success != 0)
                {
                    pageReady = true;
                    // Flush only when the document that actually loaded is the
                    // target the mod asked for. A redirect to a different -
                    // even allowed - origin must not receive data buffered for
                    // the original target.
                    if (currentDocumentIsTarget())
                    {
                        replayRetained();
                        flushOutbox();
                        // "The mod's own page is live", which is what a
                        // consumer means by loaded - not merely "a document
                        // finished", which a redirect elsewhere also is.
                        pageLoaded = true;
                        PageLoaded?.Invoke();
                    }
                    else if (outbox.Count > 0)
                    {
                        OverlayHost.LogWarning("dropped " + outbox.Count
                            + " buffered send(s); the page ended up somewhere else than the mod's target.");
                        clearOutbox();
                    }
                }
                else
                {
                    // Say so. A navigation that fails outright - a file missing
                    // from a mapped folder, a host that will not answer - used
                    // to produce no output at all, so a typo in a page name
                    // looked exactly like a slow load: the overlay sat there
                    // with IsPageLoaded false and a quietly filling outbox, and
                    // nothing anywhere said why.
                    //
                    // Only a message. The target stays as it is, because it
                    // still is the target: the mod may fix the URL and try
                    // again, and everything buffered is still meant for
                    // whatever page it names. Retargeting or closing settles
                    // those promises, as it always did.
                    WebView2Api.Method<WebView2Api.GetIntDelegate>(
                        args, WebView2Api.NavCompletedArgs_GetWebErrorStatus)(args, out int status);
                    OverlayHost.LogWarning("the page did not load (" + (pendingUrl ?? "inline markup")
                        + "), web error status " + status
                        + (outbox.Count > 0 ? "; " + outbox.Count + " send(s) stay buffered for it" : "") + ".");
                }
                return WebView2Api.S_OK;
            });
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddNavigationCompleted)(
                webView, navigationCompletedCallback.Pointer, out _) == WebView2Api.S_OK;

            newWindowCallback = new ComCallback(WebView2Api.IID_NewWindowRequested, (IntPtr sender, IntPtr args) =>
            {
                if (args != IntPtr.Zero)
                    WebView2Api.Method<WebView2Api.PutBoolDelegate>(args, WebView2Api.NewWindowArgs_PutHandled)(args, 1);
                return WebView2Api.S_OK;
            });
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddNewWindowRequested)(
                webView, newWindowCallback.Pointer, out _) == WebView2Api.S_OK;

            permissionCallback = new ComCallback(WebView2Api.IID_PermissionRequested, (IntPtr sender, IntPtr args) =>
            {
                if (args != IntPtr.Zero)
                    WebView2Api.Method<WebView2Api.PutBoolDelegate>(args, WebView2Api.PermissionArgs_PutState)(
                        args, WebView2Api.PermissionStateDeny);
                return WebView2Api.S_OK;
            });
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddPermissionRequested)(
                webView, permissionCallback.Pointer, out _) == WebView2Api.S_OK;

            processFailedCallback = new ComCallback(WebView2Api.IID_ProcessFailed, (IntPtr sender, IntPtr args) =>
            {
                int kind = -1;
                if (args != IntPtr.Zero)
                    WebView2Api.Method<WebView2Api.GetIntDelegate>(args, WebView2Api.ProcessFailedArgs_GetKind)(
                        args, out kind);
                if (kind == WebView2Api.ProcessFailedKindBrowserExited)
                {
                    fail(OverlayFailure.RendererCrashed, "the browser process exited; the overlay is dead.");
                }
                else if (kind == WebView2Api.ProcessFailedKindRenderExited
                    || kind == WebView2Api.ProcessFailedKindRenderUnresponsive)
                {
                    if (renderRecoveries < 2)
                    {
                        // A crashed or frozen renderer leaves a dead page;
                        // reloading the mod's content usually recovers.
                        // Bounded, so a page that kills its renderer on load
                        // cannot loop forever.
                        renderRecoveries++;
                        OverlayHost.LogWarning("the page's renderer failed (kind " + kind
                            + "); reloading (attempt " + renderRecoveries + ").");
                        // The scripts that were running in it are gone with it,
                        // and the reload starts a document that never saw them.
                        // Their callers are owed the "could not run" answer now
                        // rather than at Dispose. Settle only - the completions
                        // are still registered natively, and Settle is
                        // once-only, so a late one changes nothing.
                        foreach (ScriptCall pending in pendingScripts.ToArray())
                        {
                            if (pending.Settle())
                                answer(pending.Result, null);
                        }
                        if (!startPendingNavigation())
                        {
                            fail(OverlayFailure.RendererCrashed,
                                "the page's renderer failed and the reload was refused; the overlay is dead.");
                            return WebView2Api.S_OK;
                        }
                    }
                    else
                    {
                        // A handle that stays "Ready" over a permanently dead
                        // page would never hand the consumer to its fallback.
                        fail(OverlayFailure.RendererCrashed, "the page's renderer keeps failing; the overlay is dead.");
                    }
                }
                else
                {
                    OverlayHost.LogWarning("a browser subprocess failed (kind " + kind + ").");
                }
                return WebView2Api.S_OK;
            });
            armed &= WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddProcessFailed)(
                webView, processFailedCallback.Pointer, out _) == WebView2Api.S_OK;

            return armed;
        }

        private int onNavigationStarting(IntPtr args, bool topLevel)
        {
            if (args == IntPtr.Zero)
                return WebView2Api.S_OK;
            string uri = readString(args, WebView2Api.NavArgs_GetUri);
            if (!isNavigationAllowed(uri, topLevel))
            {
                OverlayHost.LogWarning("blocked navigation to " + (uri ?? "<unknown>") + ".");
                WebView2Api.Method<WebView2Api.PutBoolDelegate>(args, WebView2Api.NavArgs_PutCancel)(args, 1);
                if (topLevel)
                {
                    // This navigation started and is over. The browser accepted
                    // the request - a URI with no origin to allow, or a host
                    // whose folder mapping failed - and only this filter turned
                    // it down, so nothing else will ever end the wait. Leaving
                    // it set would make the overlay ignore every completion
                    // that follows, silently, until some later navigation
                    // happened to clear it. The generation is NOT bumped: no
                    // document changed, so nothing owed to the page showing is
                    // stale.
                    awaitingNavigationStart = false;
                    // Nothing was left behind, so nothing of the page still on
                    // screen is thrown away.
                    forgetOnNavigationStart = false;
                    forgetTarget();
                }
                return WebView2Api.S_OK;
            }

            // The document is about to change: sends must buffer until the new
            // page reports completion, or they would vanish into the old one.
            if (topLevel)
            {
                pageReady = false;
                pageLoaded = false;
                // The navigation we asked for has begun, so the next
                // completion belongs to it. Cleared here rather than at the
                // call, because only this event says the browser really took
                // it - and only for a navigation the filter above allowed.
                awaitingNavigationStart = false;
                // A new top-level document supersedes the one that asked any
                // question still in flight; see the generation check below.
                documentGeneration++;
                // And here, not at the call: the page being left is only left
                // once the browser really starts going somewhere else.
                if (forgetOnNavigationStart)
                {
                    forgetOnNavigationStart = false;
                    forgetPageState();
                }
            }
            return WebView2Api.S_OK;
        }

        /// <summary>
        /// Whether the top-level document currently shown is the one the mod
        /// last targeted: for LoadHtml the inline page (about:blank or its
        /// data: form), for Navigate a document on the same origin as the URL.
        /// The check runs against the live source, so buffered and direct
        /// sends both stay bound to the mod's own target.
        /// </summary>
        private bool currentDocumentIsTarget()
        {
            string source = readString(webView, WebView2Api.WebView_GetSource);
            if (source == null)
                return false;
            if (htmlLoaded)
                return source == "about:blank" || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            if (pendingUrl == null)
                return false;
            string expected = originOf(pendingUrl);
            string actual = originOf(source);
            return expected != null && expected == actual;
        }

        private static string readString(IntPtr comObject, int slot)
        {
            WebView2Api.Method<WebView2Api.GetPointerDelegate>(comObject, slot)(comObject, out IntPtr text);
            if (text == IntPtr.Zero)
                return null;
            string value = Marshal.PtrToStringUni(text);
            Marshal.FreeCoTaskMem(text);
            return value;
        }

        private void allowOrigin(string uriOrOrigin)
        {
            string origin = originOf(uriOrOrigin);
            if (origin != null)
                allowedOrigins.Add(origin);
        }

        /// <summary>
        /// NavigateToString is implemented by the browser as a navigation to a
        /// data: URI (measured in game: the filter blocked it and LoadHtml
        /// pages stayed white). That exact navigation is allowed once per
        /// LoadHtml via a one-shot bound to the top level - a frame must
        /// neither consume nor use it. Runtimes that report the inline page as
        /// about:blank disarm the one-shot there instead.
        /// </summary>
        private bool isNavigationAllowed(string uri, bool topLevel)
        {
            if (uri == null)
                return false;
            if (uri == "about:blank")
            {
                if (topLevel && htmlLoaded)
                    expectInlineNavigation = false;
                return htmlLoaded;
            }
            if (topLevel && expectInlineNavigation && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                expectInlineNavigation = false;
                return true;
            }
            string origin = originOf(uri);
            if (origin == null)
                return false;
            // A host the mod wanted served from disk, whose mapping is not in
            // place: this would fetch whatever the name resolves to on the
            // network, under an origin the mod believes is its own folder.
            if (unmappedOrigins.Contains(origin))
                return false;
            return allowedOrigins.Contains(origin);
        }

        /// <summary>
        /// Messages from the mod's own inline page report about:blank or its
        /// data: URI as their source, depending on the runtime. Both are only
        /// reachable through LoadHtml (navigation there is blocked otherwise),
        /// so while inline HTML is loaded they are the mod's own content.
        /// </summary>
        private bool isMessageAllowed(string source)
        {
            if (source == null)
                return false;
            if (htmlLoaded
                && (source == "about:blank" || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                return true;
            string origin = originOf(source);
            return origin != null && allowedOrigins.Contains(origin);
        }

        /// <summary>
        /// Only http/https with a real host produce an origin. Everything else
        /// (data:, javascript:, file:, about:) returns null and is therefore
        /// never allowed - authority-less schemes would otherwise all collapse
        /// onto the same empty string and trust each other.
        /// </summary>
        private static string originOf(string uri)
        {
            try
            {
                var parsed = new Uri(uri, UriKind.Absolute);
                if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                    return null;
                if (string.IsNullOrEmpty(parsed.Host))
                    return null;
                return parsed.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        private bool onKey(int virtualKey)
        {
            if (options.CloseKeys != null && Array.IndexOf(options.CloseKeys, virtualKey) >= 0)
            {
                Hide();
                return true;
            }

            KeyPressed?.Invoke(virtualKey);
            return false;
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            TargetState previous = captureTarget();
            // Retargeting means the target CHANGED, not merely that there was
            // one. Asking for the page already showing is a reload, and a
            // reload keeps the state the mod set up for that page - which is
            // what happens when the page reloads itself. Two routes to the
            // same visible outcome must not end up in different states.
            bool retargeting = previous.HandedOver
                && !(previous.Html == null && string.Equals(previous.Url, url, StringComparison.Ordinal));
            pendingUrl = url;
            pendingHtml = null;
            htmlLoaded = false;
            expectInlineNavigation = false;
            // The mod asked for this page, so its origin becomes trusted.
            allowOrigin(url);
            if (webView == IntPtr.Zero)
            {
                // Only recorded for now, so nothing can be rejected: the page
                // being left is already out of reach.
                if (retargeting)
                    forgetPageState();
                return;
            }
            pageReady = false;
            pageLoaded = false;
            awaitingNavigationStart = true;
            if (!checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                webView, WebView2Api.WebView_Navigate)(webView, url), "Navigate"))
            {
                restoreTarget(previous);
                return;
            }
            targetHandedToBrowser = true;
            // Not yet: the browser has taken the request, but the filter below
            // can still refuse it - and then this page stays on screen with
            // the state that belongs to it. What is buffered and remembered is
            // dropped when the new navigation actually STARTS, which is the
            // moment the old page is really being left. Choosing the FIRST
            // page is not that: a mod that sets its state up before naming a
            // page should keep it, which is the order the state is worth
            // having in.
            forgetOnNavigationStart = retargeting;
        }

        /// <summary>
        /// A synchronously rejected navigation (invalid URL, inline HTML over
        /// the 2 MB limit) never produces NavigationCompleted. Nothing
        /// happened, so nothing here changes: the buffered sends and the
        /// retained state stay with whatever page is - or becomes - the
        /// target. Dropping them used to look tidy, but the page they were
        /// meant for is either still on screen or still to be named, and a mod
        /// left believing its configuration is up while the page runs on its
        /// defaults is exactly the silent loss Retain exists to prevent.
        /// </summary>
        private bool checkNavigationResult(int hr, string what)
        {
            if (hr == WebView2Api.S_OK)
                return true;
            OverlayHost.LogWarning("" + what + " was rejected, hr=0x" + hr.ToString("X8")
                + "; the page will not change"
                + (outbox.Count > 0 ? " (" + outbox.Count + " buffered send(s) still waiting)" : "") + ".");
            return false;
        }

        /// <summary>
        /// What the mod currently points the overlay at. A rejected navigation
        /// leaves the old document on screen, so the bookkeeping has to go back
        /// with it - otherwise the overlay would consider a page that never
        /// loaded its target, and report "not loaded" for the rest of its life
        /// while buffering every send into nothing.
        /// </summary>
        private struct TargetState
        {
            public string Url;
            public string Html;
            public bool HtmlLoaded;
            public bool ExpectInline;
            public bool PageReady;
            public bool PageLoaded;
            public bool HandedOver;
        }

        private TargetState captureTarget() => new TargetState
        {
            Url = pendingUrl,
            Html = pendingHtml,
            HtmlLoaded = htmlLoaded,
            ExpectInline = expectInlineNavigation,
            PageReady = pageReady,
            PageLoaded = pageLoaded,
            HandedOver = targetHandedToBrowser,
        };

        private void restoreTarget(TargetState previous)
        {
            // The refused navigation will never start, so the overlay must not
            // go on ignoring completions while it waits for one - nor throw
            // away what belongs to the page that is staying.
            awaitingNavigationStart = false;
            forgetOnNavigationStart = false;
            pendingUrl = previous.Url;
            pendingHtml = previous.Html;
            htmlLoaded = previous.HtmlLoaded;
            expectInlineNavigation = previous.ExpectInline;
            pageReady = previous.PageReady;
            pageLoaded = previous.PageLoaded;
            targetHandedToBrowser = previous.HandedOver;
        }

        /// <summary>Shows markup directly, so a mod needs no web server at all.</summary>
        public void LoadHtml(string html)
        {
            if (html == null)
                return;
            TargetState previous = captureTarget();
            // Same rule as Navigate: identical markup is a reload of the page
            // already showing, not a move away from it.
            bool retargeting = previous.HandedOver
                && !(previous.Url == null && string.Equals(previous.Html, html, StringComparison.Ordinal));
            pendingHtml = html;
            pendingUrl = null;
            htmlLoaded = true;
            if (webView == IntPtr.Zero)
            {
                if (retargeting)
                    forgetPageState();
                return;
            }
            pageReady = false;
            pageLoaded = false;
            expectInlineNavigation = true;
            awaitingNavigationStart = true;
            if (!checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                webView, WebView2Api.WebView_NavigateToString)(webView, html), "LoadHtml"))
            {
                restoreTarget(previous);
                return;
            }
            targetHandedToBrowser = true;
            forgetOnNavigationStart = retargeting;
        }

        /// <summary>
        /// Takes anything that is a channel envelope; everything else is left
        /// alone, so a page that never heard of channels keeps talking to
        /// <see cref="MessageReceived"/> exactly as before.
        /// </summary>
        private bool routeChannelMessage(string message)
        {
            if (!ChannelProtocol.TryParse(message, out string kind, out string channel, out string payload, out int id))
                return false;

            // The library's own channels are handled here and never surface as
            // consumer traffic - the whole prefix, not just the names that
            // exist today, because the README promises the prefix and a future
            // internal channel must not start leaking into mods.
            bool reserved = channel.StartsWith(ChannelProtocol.ReservedPrefix, StringComparison.Ordinal);

            if (kind == ChannelProtocol.KindMessage)
            {
                if (!reserved)
                    ChannelMessage?.Invoke(channel, payload);
                else if (channel == ChannelProtocol.ShapeChannel)
                    applyShapeFromPage(payload);
                return true;
            }

            if (kind == ChannelProtocol.KindAnswer)
            {
                if (pendingRequests.TryGetValue(id, out PendingRequest waiting))
                {
                    pendingRequests.Remove(id);
                    stopRequestTimerIfIdle();
                    answer(waiting.Answer, payload);
                }
                return true;
            }

            // A question from the page. Answering exactly once matters on this
            // side too: the page is holding a promise open.
            int answered = 0;
            if (reserved)
            {
                // No internal channel answers questions; say so at once rather
                // than let the page wait out its own timeout.
                OverlayHost.Post(() => PostMessageToPage(ChannelProtocol.Answer(channel, null, id)));
                return true;
            }
            // Which document is asking. The page numbers its questions from 1
            // again in every new document, and matches an answer on that
            // number alone - so an answer the mod takes its time over would
            // otherwise resolve whatever question the NEXT document happens to
            // have given the same number. A deferred reply is the ordinary
            // case, not an exotic one: with main-thread dispatch it already
            // crosses a frame.
            int asked = documentGeneration;
            Action<string> reply = value =>
            {
                if (System.Threading.Interlocked.Exchange(ref answered, 1) != 0)
                    return;
                OverlayHost.Post(() =>
                {
                    // The page that asked is gone. Its promise went with it,
                    // and the document now showing is waiting for an answer of
                    // its own.
                    if (documentGeneration != asked)
                        return;
                    // The generation travels with it: an answer to a question
                    // asked from a parse-time script can still be waiting in
                    // the outbox when the document changes.
                    PostMessageToPage(ChannelProtocol.Answer(channel, value, id), null, 0, asked);
                });
            };
            if (RequestReceived == null)
                reply(null);
            else
                RequestReceived(channel, payload, reply);
            return true;
        }

        /// <summary>
        /// Cuts the overlay down to a set of rectangles: it draws there and
        /// takes the mouse there, and everything outside belongs to the game.
        /// Null restores the whole window.
        ///
        /// Both halves come from one mechanism on purpose, because Windows
        /// offers no other. Answering the hit test with "not me" keeps the
        /// picture but only passes clicks to windows of the same thread, which
        /// the game never is - measured, the click reached nothing at all. A
        /// window region does route clicks to whatever is behind, across
        /// processes, but clips the picture to the same shape. So a caller
        /// gets exactly one contract, and it is this one.
        /// </summary>
        public void SetShape(WebView2Api.RECT[] regions) => setShape(regions);

        private void setShape(WebView2Api.RECT[] regions)
        {
            if (window == IntPtr.Zero)
                return;
            WebView2Api.RECT[] wanted = regions != null && regions.Length > 0 ? regions : null;
            if (wanted == null)
            {
                // NULL hands the whole window back; the old region is freed by
                // the system, which owns every region passed in here.
                SetWindowRgn(window, IntPtr.Zero, true);
                shape = null;
                return;
            }

            // Callers - the page included - describe rectangles inside the
            // page, while a window region is measured from the window's outer
            // corner. On a framed overlay those differ by the caption and
            // border, and using the wrong one would cut the title bar off.
            var origin = new POINT { x = 0, y = 0 };
            int offsetX = 0, offsetY = 0;
            if (ClientToScreen(window, ref origin) && GetWindowRect(window, out WebView2Api.RECT bounds))
            {
                offsetX = origin.x - bounds.left;
                offsetY = origin.y - bounds.top;
            }

            IntPtr combined = CreateRectRgn(0, 0, 0, 0);
            if (combined == IntPtr.Zero)
            {
                OverlayHost.LogWarning("the overlay shape could not be built; the shape is unchanged.");
                return;
            }
            foreach (WebView2Api.RECT part in wanted)
            {
                IntPtr piece = CreateRectRgn(part.left + offsetX, part.top + offsetY,
                    part.right + offsetX, part.bottom + offsetY);
                if (piece == IntPtr.Zero)
                    continue;
                CombineRgn(combined, combined, piece, RGN_OR);
                DeleteObject(piece);
            }
            // Ownership passes to the system with this call - never delete it
            // afterwards, and only believe the shape once it was accepted.
            if (SetWindowRgn(window, combined, true) == 0)
            {
                DeleteObject(combined);
                OverlayHost.LogWarning("the overlay shape could not be applied; the shape is unchanged.");
                return;
            }
            shape = wanted;
        }

        /// <summary>
        /// A shape the page sent. A list that cannot be read is ignored
        /// outright: dropping it is not the same as clearing the shape, and
        /// clearing it would hand a full-screen interactive HUD back the whole
        /// mouse - the very thing the caller used a shape to avoid.
        /// </summary>
        private void applyShapeFromPage(string payload)
        {
            if (tryParseRegions(payload, out WebView2Api.RECT[] regions))
                setShape(regions);
            else
                OverlayHost.LogWarning("ignored a malformed shape from the page ("
                    + describe(payload) + "); the overlay keeps the shape it had.");
        }

        /// <summary>
        /// "x,y,w,h;x,y,w,h" in device pixels, which is what the page shim
        /// sends after scaling its CSS rectangles. Deliberately not JSON: this
        /// is the library talking to its own shim. An empty payload is an
        /// explicit "no shape"; anything unreadable is a failure, and the two
        /// must not be confused.
        /// </summary>
        private static bool tryParseRegions(string payload, out WebView2Api.RECT[] regions)
        {
            regions = null;
            if (string.IsNullOrEmpty(payload))
                return true;
            string[] parts = payload.Split(';');
            var rects = new List<WebView2Api.RECT>(parts.Length);
            foreach (string part in parts)
            {
                if (part.Length == 0)
                    continue;
                string[] numbers = part.Split(',');
                if (numbers.Length != 4)
                    return false;
                var rect = new WebView2Api.RECT();
                if (!int.TryParse(numbers[0], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out rect.left)
                    || !int.TryParse(numbers[1], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out rect.top)
                    || !int.TryParse(numbers[2], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int width)
                    || !int.TryParse(numbers[3], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int height))
                    return false;
                rect.right = rect.left + Math.Max(0, width);
                rect.bottom = rect.top + Math.Max(0, height);
                rects.Add(rect);
            }
            regions = rects.Count == 0 ? null : rects.ToArray();
            return true;
        }

        /// <summary>
        /// Moves or resizes the overlay at runtime, in screen coordinates. Not
        /// remembered: the bounds store is for the spot the player chose, and a
        /// mod resizing its own window must not overwrite that. It does win
        /// over a remembered spot for the rest of the session, though - the mod
        /// asked last.
        /// </summary>
        public void SetBounds(int? x, int? y, int? width, int? height)
        {
            if (window == IntPtr.Zero)
                return;
            if (!GetWindowRect(window, out WebView2Api.RECT current))
                return;
            int left = x ?? current.left;
            int top = y ?? current.top;
            int newWidth = Math.Max(MinimumWidth, width ?? (current.right - current.left));
            int newHeight = Math.Max(MinimumHeight, height ?? (current.bottom - current.top));
            everPositioned = true;
            SetWindowPos(window, IntPtr.Zero, left, top, newWidth, newHeight,
                SWP_NOZORDER | SWP_NOACTIVATE);
            fitToClientArea();
        }

        /// <summary>Sends a message to the page on a named channel.</summary>
        public void PostToChannel(string channel, string payload, PostOptions options)
        {
            if (channel == null)
                return;
            if ((options & PostOptions.Retain) != 0)
            {
                remember(channel, payload);
                // No point buffering it as well: the page cannot have it yet,
                // and the replay on load is what will hand it over - buffering
                // too would deliver the same value twice.
                if (webView == IntPtr.Zero || !pageReady)
                    return;
            }
            PostMessageToPage(ChannelProtocol.Message(channel, payload),
                (options & PostOptions.LatestOnly) != 0 ? channel : null);
        }

        private void remember(string channel, string payload)
        {
            for (int i = 0; i < retained.Count; i++)
            {
                if (retained[i].Key != channel)
                    continue;
                // Newest wins, but keeps its place: the order a mod set these
                // up in is the order a fresh page should see them.
                retained[i] = new KeyValuePair<string, string>(channel, payload);
                return;
            }
            retained.Add(new KeyValuePair<string, string>(channel, payload));
        }

        /// <summary>
        /// Everything that was meant for the page being left behind.
        /// </summary>
        private void forgetPageState()
        {
            clearOutbox();
            retained.Clear();
        }

        /// <summary>
        /// Hands a fresh document the state the mod has been assuming. Before
        /// the outbox, so a message the mod sent while the page was loading
        /// wins over the older retained value on the same channel.
        /// </summary>
        private void replayRetained()
        {
            if (retained.Count == 0)
                return;
            var values = retained.ToArray();
            foreach (KeyValuePair<string, string> value in values)
                PostMessageToPage(ChannelProtocol.Message(value.Key, value.Value), null);
        }

        /// <summary>
        /// Asks the page a question. The callback is answered exactly once:
        /// with the page's reply, or with null once the deadline passes - a
        /// page that cannot answer must not be able to hang the mod.
        /// </summary>
        public void RequestFromPage(string channel, string payload, Action<string> reply, int timeoutMilliseconds)
        {
            if (channel == null)
            {
                answer(reply, null);
                return;
            }
            if (timeoutMilliseconds <= 0)
                timeoutMilliseconds = 5000;

            // Never zero: the envelope uses id 0 to mean "no id at all".
            int id = ++nextRequestId;
            if (id == 0)
                id = ++nextRequestId;
            pendingRequests[id] = new PendingRequest
            {
                Answer = reply,
                DeadlineMilliseconds = clock.ElapsedMilliseconds + timeoutMilliseconds,
            };
            startRequestTimer();
            // The id travels with the envelope so that, if the page is not
            // ready and this waits in the outbox, the deadline can take it
            // back out again.
            PostMessageToPage(ChannelProtocol.Request(channel, payload, id), null, id, documentGeneration);
        }

        private void startRequestTimer()
        {
            if (requestTimer != null)
                return;
            // Sweeps on the overlay thread, where the request map lives.
            requestTimer = new System.Threading.Timer(
                _ => OverlayHost.Post(expireRequests), null, 250, 250);
        }

        private void stopRequestTimerIfIdle()
        {
            if (pendingRequests.Count > 0 || requestTimer == null)
                return;
            requestTimer.Dispose();
            requestTimer = null;
        }

        private void expireRequests()
        {
            if (pendingRequests.Count == 0)
            {
                stopRequestTimerIfIdle();
                return;
            }
            long now = clock.ElapsedMilliseconds;
            List<int> due = null;
            foreach (KeyValuePair<int, PendingRequest> entry in pendingRequests)
            {
                if (entry.Value.DeadlineMilliseconds > now)
                    continue;
                due = due ?? new List<int>();
                due.Add(entry.Key);
            }
            if (due == null)
                return;
            foreach (int id in due)
            {
                PendingRequest expired = pendingRequests[id];
                pendingRequests.Remove(id);
                // If the question never left the outbox, take it back: the
                // caller has just been told there is no answer, and putting
                // the question to a page that loads later would run its
                // handler - side effects and all - for nobody.
                for (int i = outbox.Count - 1; i >= 0; i--)
                {
                    if (outbox[i].RequestId == id)
                        outbox.RemoveAt(i);
                }
                answer(expired.Answer, null);
            }
            stopRequestTimerIfIdle();
        }

        public void PostMessageToPage(string message) => PostMessageToPage(message, null);

        public void PostMessageToPage(string message, string latestChannel) =>
            PostMessageToPage(message, latestChannel, 0, 0);

        public void PostMessageToPage(string message, string latestChannel, int requestId, int generation)
        {
            if (message == null)
                return;
            if (refuseAfterFailure(null))
                return;
            // WebView2 does not deliver messages sent before the page finished
            // loading, so they wait in a bounded outbox until it has - and a
            // live send only goes out while the mod's own target is showing.
            if (webView == IntPtr.Zero || !pageReady)
            {
                buffer(false, message, null, latestChannel, requestId, generation);
                return;
            }
            if (!currentDocumentIsTarget())
            {
                OverlayHost.LogWarning("dropped a message; the page is not the mod's target document.");
                return;
            }
            WebView2Api.Method<WebView2Api.StringDelegate>(webView, WebView2Api.WebView_PostWebMessageAsString)(
                webView, message);
        }

        public void ExecuteScript(string script, Action<string> result = null)
        {
            if (script == null)
            {
                answer(result, null);
                return;
            }
            if (refuseAfterFailure(result))
                return;
            if (webView == IntPtr.Zero || !pageReady)
            {
                buffer(true, script, result);
                return;
            }
            if (!currentDocumentIsTarget())
            {
                OverlayHost.LogWarning("dropped a script; the page is not the mod's target document.");
                answer(result, null);
                return;
            }

            // A real completion handler: passing null there is undocumented
            // behavior, and its error code is the only way a script failure
            // ever becomes visible. Without a result to deliver, one shared
            // callback serves every call; with one, each call needs its own,
            // or two overlapping scripts would resolve to the same consumer.
            ComCallback callback;
            if (result == null)
            {
                if (scriptCompletedCallback == null)
                    scriptCompletedCallback = new ComCallback(WebView2Api.IID_ExecuteScriptCompleted, (int hrScript, IntPtr resultJson) =>
                    {
                        if (hrScript != WebView2Api.S_OK)
                            OverlayHost.LogWarning("a script failed, hr=0x" + hrScript.ToString("X8") + ".");
                        return WebView2Api.S_OK;
                    });
                callback = scriptCompletedCallback;
            }
            else
            {
                var call = new ScriptCall { Result = result };
                call.Callback = new ComCallback(WebView2Api.IID_ExecuteScriptCompleted, (int hrScript, IntPtr resultJson) =>
                {
                    if (hrScript != WebView2Api.S_OK)
                        OverlayHost.LogWarning("a script failed, hr=0x" + hrScript.ToString("X8") + ".");
                    // The string belongs to the browser for the length of this
                    // call only, so it is copied before it goes anywhere.
                    if (call.Settle())
                        answer(result, hrScript == WebView2Api.S_OK && resultJson != IntPtr.Zero
                            ? Marshal.PtrToStringUni(resultJson) : null);
                    pendingScripts.Remove(call);
                    call.Callback.Dispose();
                    return WebView2Api.S_OK;
                });
                pendingScripts.Add(call);
                callback = call.Callback;
            }

            int hr = WebView2Api.Method<WebView2Api.ExecuteScriptDelegate>(webView, WebView2Api.WebView_ExecuteScript)(
                webView, script, callback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                OverlayHost.LogWarning("ExecuteScript was rejected, hr=0x" + hr.ToString("X8") + ".");
                // Rejected calls never complete, so the handler has to be
                // retired here or it would wait for a callback forever.
                ScriptCall rejected = result == null ? null : pendingScripts.Find(c => c.Callback == callback);
                if (rejected != null)
                {
                    pendingScripts.Remove(rejected);
                    rejected.Callback.Dispose();
                    if (rejected.Settle())
                        answer(result, null);
                }
            }
        }

        private void buffer(bool isScript, string payload, Action<string> result = null,
            string latestChannel = null, int requestId = 0, int generation = 0)
        {
            if (latestChannel != null)
            {
                // Only the newest is worth keeping: the page has not seen any
                // of these yet, so an older one would only be a stale frame
                // rendered before the current one.
                for (int i = 0; i < outbox.Count; i++)
                {
                    if (outbox[i].LatestChannel != latestChannel)
                        continue;
                    Pending existing = outbox[i];
                    existing.Payload = payload;
                    outbox[i] = existing;
                    return;
                }
            }
            if (outbox.Count < OutboxLimit)
            {
                outbox.Add(new Pending
                {
                    IsScript = isScript,
                    Payload = payload,
                    Result = result,
                    RequestId = requestId,
                    Generation = generation,
                    LatestChannel = latestChannel,
                });
                return;
            }
            overflowDropped++;
            if (overflowDropped == 1)
                OverlayHost.LogWarning("the outbox is full (" + OutboxLimit
                    + " entries); further sends are dropped until the page loads.");
            answer(result, null);
        }

        /// <summary>
        /// Hands a script result - or the absence of one - to the consumer.
        /// Every path that gives up on a script has to come through here.
        /// </summary>
        private static void answer(Action<string> result, string value)
        {
            if (result == null)
                return;
            try
            {
                result(value);
            }
            catch (Exception ex)
            {
                OverlayHost.LogWarning("a script result handler threw ("
                    + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>
        /// Drops everything buffered, telling any waiting script caller that
        /// no result is coming.
        /// </summary>
        private void clearOutbox()
        {
            if (outbox.Count == 0)
                return;
            var dropped = outbox.ToArray();
            outbox.Clear();
            foreach (Pending item in dropped)
                answer(item.Result, null);
        }

        private void flushOutbox()
        {
            if (outbox.Count == 0)
                return;
            var items = outbox.ToArray();
            outbox.Clear();
            foreach (Pending item in items)
            {
                // Bound to a document that has since been replaced: a question
                // whose asker is gone, or an answer to one. An ordinary send
                // carries no generation and follows the mod's target as always.
                if (item.Generation != 0 && item.Generation != documentGeneration)
                {
                    answer(item.Result, null);
                    continue;
                }
                if (item.IsScript)
                    ExecuteScript(item.Payload, item.Result);
                else
                    // The id and the generation go back with it: flushing can
                    // re-buffer when the page is not ready after all, and an
                    // entry that lost them could no longer be withdrawn on its
                    // deadline or recognised as belonging to a gone document.
                    PostMessageToPage(item.Payload, item.LatestChannel, item.RequestId, item.Generation);
            }
        }

        public void OpenDevTools()
        {
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.NoArgsDelegate>(webView, WebView2Api.WebView_OpenDevToolsWindow)(webView);
        }

        public void Show()
        {
            desiredVisible = true;
            OverlayHost.LogDiagnostic("overlay '" + title + "': Show() entered, window="
                + window.ToString("X") + " state=" + state
                + " displayMode=" + OverlayHost.DisplayModeSupported);
            if (window == IntPtr.Zero || state == CreationState.Failed)
            {
                // Nothing to show yet - creation will call this again once the
                // window exists, because desiredVisible is now set.
                OverlayHost.LogDiagnostic("overlay '" + title + "': Show() deferred");
                return;
            }
            if (!OverlayHost.DisplayModeSupported)
            {
                // A window over an exclusive-fullscreen game minimises it, and
                // every consumer has more than one show path to remember this
                // on. Refusing here costs a log line; forgetting it once costs
                // the player their raid start. Not a failure - the player can
                // switch back - so the overlay stays alive and simply hidden.
                if (!warnedAboutFullscreen)
                {
                    warnedAboutFullscreen = true;
                    OverlayHost.LogWarning("not showing the overlay: the game is in exclusive fullscreen,"
                        + " where a window over it would minimise it. Use borderless windowed.");
                }
                desiredVisible = false;
                return;
            }
            // Repositioning on every Show is what reset the window each toggle.
            // A panel keeps the spot the player gave it; only the first show,
            // a spot that ended up off every screen, and HUDs (which follow
            // the game window) position anew. RememberBounds off restores the
            // old recenter-on-every-show behaviour, as its documentation says.
            if (options.Transparent || !options.RememberBounds || !everPositioned || isOffScreen())
                positionOverGame();
            everPositioned = true;
            fitToClientArea();
            setControllerVisible(true);
            // A HUD must never take the keyboard from the game.
            ShowWindow(window, options.Transparent ? SW_SHOWNOACTIVATE : SW_SHOW);
            if (!options.Transparent)
                SetForegroundWindow(window);
            else
                SetTimer(window, TrackTimerId, TrackIntervalMilliseconds, IntPtr.Zero);
            setVisible(true);
        }

        public void Hide()
        {
            desiredVisible = false;
            if (window == IntPtr.Zero)
                return;
            if (options.Transparent)
                KillTimer(window, TrackTimerId);
            setControllerVisible(false);
            ShowWindow(window, SW_HIDE);
            setVisible(false);
            // Hand the keyboard back to the game, not to whatever sits behind.
            // A HUD never had it, so there is nothing to hand back.
            if (!options.Transparent
                && OverlayHost.GameWindow != IntPtr.Zero && IsWindow(OverlayHost.GameWindow))
                SetForegroundWindow(OverlayHost.GameWindow);
            Closed?.Invoke();
        }

        /// <summary>
        /// A HUD is glued to the game picture, but the game window can move,
        /// change monitor or resolution. A cheap half-second check keeps the
        /// canvas aligned without hooking the game's message loop.
        /// </summary>
        private void followGameWindow()
        {
            if (!isVisible || window == IntPtr.Zero)
                return;
            getBounds(out int x, out int y, out int width, out int height);
            if (!GetWindowRect(window, out WebView2Api.RECT current))
                return;
            if (current.left == x && current.top == y
                && current.right - current.left == width && current.bottom - current.top == height)
                return;

            SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            fitToClientArea();
            notifyPositionChanged();
        }

        /// <summary>
        /// Translates a real mouse message into the browser's input call. The
        /// event kinds mirror the WM codes and the virtual-key flags mirror
        /// the MK_* flags in wParam, so the translation is nearly verbatim;
        /// only the wheel needs work (screen coordinates, delta in the high
        /// word) plus capture and leave bookkeeping.
        /// </summary>
        private void forwardMouse(uint message, IntPtr wParam, IntPtr lParam)
        {
            int virtualKeys = (int)((long)wParam & 0xFFFF);
            uint mouseData = 0;
            int x = (short)((long)lParam & 0xFFFF);
            int y = (short)(((long)lParam >> 16) & 0xFFFF);

            if (message == WM_MOUSEWHEEL || message == WM_MOUSEHWHEEL)
            {
                mouseData = (uint)(short)(((long)wParam >> 16) & 0xFFFF);
                // The wheel messages alone carry screen coordinates.
                var point = new POINT { x = x, y = y };
                ScreenToClient(window, ref point);
                x = point.x;
                y = point.y;
            }
            else if (message == WM_XBUTTONDOWN || message == WM_XBUTTONUP)
            {
                // Which X button lives in the high word.
                mouseData = (uint)(((long)wParam >> 16) & 0xFFFF);
            }

            switch (message)
            {
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_XBUTTONDOWN:
                    // Capture, so a drag that leaves the window keeps
                    // delivering until the button is released.
                    if (mouseButtonsDown++ == 0)
                        SetCapture(window);
                    break;
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                case WM_MBUTTONUP:
                case WM_XBUTTONUP:
                    if (mouseButtonsDown > 0 && --mouseButtonsDown == 0)
                        ReleaseCapture();
                    break;
                case WM_MOUSEMOVE:
                    if (!trackingMouseLeave)
                    {
                        // Without this the browser never learns the cursor
                        // left, and hover states stick.
                        var track = new TRACKMOUSEEVENT
                        {
                            cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)),
                            dwFlags = TME_LEAVE,
                            hwndTrack = window,
                        };
                        trackingMouseLeave = TrackMouseEvent(ref track);
                    }
                    break;
            }

            sendMouse((int)message, virtualKeys, mouseData, x, y);
        }

        private void sendMouse(int eventKind, int virtualKeys, uint mouseData, int x, int y)
        {
            if (compositionController == IntPtr.Zero)
                return;
            long point = (uint)x | ((long)y << 32);
            WebView2Api.Method<WebView2Api.SendMouseInputDelegate>(
                compositionController, WebView2Api.Composition_SendMouseInput)(
                compositionController, eventKind, virtualKeys, mouseData, point);
        }

        private void saveBounds()
        {
            if (!remembersBounds || window == IntPtr.Zero)
                return;
            if (!GetWindowRect(window, out WebView2Api.RECT rect))
                return;
            BoundsStore.Save(persistenceKey, new BoundsStore.StoredBounds
            {
                X = rect.left,
                Y = rect.top,
                Width = rect.right - rect.left,
                Height = rect.bottom - rect.top,
            });
        }

        private bool isOffScreen()
        {
            if (window == IntPtr.Zero || !GetWindowRect(window, out WebView2Api.RECT rect))
                return false;
            return !isOnAnyScreen(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        private static bool isOnAnyScreen(int x, int y, int width, int height)
        {
            var rect = new WebView2Api.RECT { left = x, top = y, right = x + width, bottom = y + height };
            return MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
        }

        /// <summary>
        /// WebView2 places dialogs, tooltips and accessibility popups relative
        /// to remembered screen coordinates; it asks to be told when they move.
        /// </summary>
        private void notifyPositionChanged()
        {
            if (controller == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.NoArgsDelegate>(
                controller, WebView2Api.Controller_NotifyParentWindowPositionChanged)(controller);
        }

        public void CloseFromHost()
        {
            closed = true;
            bool wasVisible = isVisible;
            isVisible = false;
            // Each native resource on its own: one failing close must not skip
            // the rest, and every pointer is nulled regardless so a retry can
            // never double-release.
            try
            {
                if (controller != IntPtr.Zero)
                {
                    IntPtr toClose = controller;
                    controller = IntPtr.Zero;
                    // Counted down before the calls that could throw: this view
                    // is gone either way, and leaving the count high would give
                    // every later windowed overlay a second browser it does not
                    // need.
                    if (!usesComposition)
                        OverlayHost.WindowedControllerClosed(windowedInMainEnvironment);
                    WebView2Api.Method<WebView2Api.NoArgsDelegate>(toClose, WebView2Api.Controller_Close)(toClose);
                    Marshal.Release(toClose);
                }
            }
            catch
            {
            }
            try
            {
                // get_CoreWebView2 handed out its own reference; Close() above
                // does not return it.
                if (webView != IntPtr.Zero)
                {
                    IntPtr toRelease = webView;
                    webView = IntPtr.Zero;
                    Marshal.Release(toRelease);
                }
            }
            catch
            {
            }
            try
            {
                if (window != IntPtr.Zero)
                {
                    IntPtr toDestroy = window;
                    window = IntPtr.Zero;
                    byHandle.Remove(toDestroy);
                    OverlayHost.DestroyWindow(toDestroy);
                }
            }
            catch
            {
            }

            try
            {
                if (compositionController != IntPtr.Zero)
                {
                    IntPtr toRelease = compositionController;
                    compositionController = IntPtr.Zero;
                    // Before the release, for the same reason as above.
                    OverlayHost.ComposedControllerClosed();
                    Marshal.Release(toRelease);
                }
            }
            catch
            {
            }
            releaseDComp();

            controllerCallback?.Dispose();
            keyCallback?.Dispose();
            messageCallback?.Dispose();
            navigationStartingCallback?.Dispose();
            frameNavigationCallback?.Dispose();
            navigationCompletedCallback?.Dispose();
            newWindowCallback?.Dispose();
            permissionCallback?.Dispose();
            processFailedCallback?.Dispose();
            scriptCompletedCallback?.Dispose();
            channelShimCallback?.Dispose();
            compositionCompleted?.Dispose();
            cursorChangedCallback?.Dispose();
            // The controller is closed above, so a completion still owed to a
            // caller will never arrive: hand the memory back and answer every
            // waiting script with "no result" instead of leaving it hanging.
            foreach (ScriptCall pending in pendingScripts.ToArray())
            {
                pending.Callback.Dispose();
                if (pending.Settle())
                    answer(pending.Result, null);
            }
            pendingScripts.Clear();
            // Questions the page can no longer answer, for the same reason.
            if (requestTimer != null)
            {
                requestTimer.Dispose();
                requestTimer = null;
            }
            var openRequests = new List<PendingRequest>(pendingRequests.Values);
            pendingRequests.Clear();
            foreach (PendingRequest open in openRequests)
                answer(open.Answer, null);
            clearOutbox();
            OverlayHost.Unregister(this);
            if (wasVisible)
            {
                Closed?.Invoke();
                // Same reasoning as in fail(): during shutdown this would only
                // wake a fallback while the game is going away. Closed keeps
                // its long-standing behaviour, unchanged here.
                if (!OverlayHost.Stopping)
                    VisibilityChanged?.Invoke(false);
            }
        }

        private bool createWindow()
        {
            // Chroma-key HUD windows get their own class because the class
            // carries the background brush, and theirs must be the
            // transparency key: those are exactly the pixels the chroma key
            // later removes. Composition windows have no redirection surface
            // at all, so the brush - and the special class - are meaningless
            // there. The brush is created once and owned by the class for the
            // rest of the process; one per window would leak a GDI handle.
            bool chromaKeyHud = options.Transparent && !usesComposition;
            string className = chromaKeyHud ? HudWindowClassName : WindowClassName;
            if (chromaKeyHud && hudBrush == IntPtr.Zero)
            {
                hudBrush = CreateSolidBrush(TransparencyKey);
                if (hudBrush == IntPtr.Zero)
                {
                    // Without the key brush the chroma key has nothing to key
                    // out - the HUD would be an opaque sheet. Fail instead.
                    OverlayHost.LogWarning("the HUD key brush could not be created.");
                    return false;
                }
            }
            var windowClass = new OverlayHost.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(OverlayHost.WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(classProc),
                hInstance = OverlayHost.GetModuleHandle(null),
                lpszClassName = className,
                hbrBackground = chromaKeyHud
                    ? hudBrush
                    : (IntPtr)(COLOR_WINDOW + 1)
            };

            if (OverlayHost.RegisterClassEx(ref windowClass) == 0
                && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
            {
                OverlayHost.LogWarning("could not register the overlay window class.");
                return false;
            }

            // An owned popup rather than a child window: Unity presents through
            // a flip-model swapchain, which does not composite child windows.
            uint style = WS_POPUP;
            if (options.Frame && !options.Transparent)
                style |= WS_CAPTION | WS_SYSMENU | WS_SIZEBOX;

            uint exStyle = WS_EX_TOOLWINDOW;
            byte alpha = opacityAsAlpha();
            if (usesComposition)
            {
                // No redirection surface: the DComp visual is the only pixel
                // source, which is what gives true per-pixel alpha. Interactive
                // HUDs keep receiving mouse messages; display-only ones must be
                // click-through - and WS_EX_TRANSPARENT only makes hit-testing
                // skip a window when WS_EX_LAYERED is set too (measured: the
                // composed HUD swallowed every click without it). Neither
                // variant takes focus.
                exStyle |= WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE;
                if (!options.Interactive)
                    exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            }
            else if (options.Transparent)
            {
                // Layered for the chroma key; transparent and no-activate so the
                // mouse and the keyboard never leave the game.
                exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            }
            else if (alpha < byte.MaxValue)
            {
                exStyle |= WS_EX_LAYERED;
            }

            getBounds(out int x, out int y, out int width, out int height);
            window = OverlayHost.CreateWindowEx(
                exStyle,
                className,
                title,
                style,
                x, y, width, height,
                OverlayHost.GameWindow,
                IntPtr.Zero,
                OverlayHost.GetModuleHandle(null),
                IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                OverlayHost.LogWarning("could not create the overlay window, win32 error "
                    + Marshal.GetLastWin32Error() + ".");
                return false;
            }

            byHandle[window] = this;

            if (options.Frame && !options.Transparent)
                applyDarkFrame();

            if (usesComposition && !options.Interactive)
            {
                // A layered window shows nothing until its attributes are set;
                // fully opaque here - the per-pixel alpha comes from DComp.
                SetLayeredWindowAttributes(window, 0, byte.MaxValue, LWA_ALPHA);
            }

            if (chromaKeyHud)
            {
                uint flags = LWA_COLORKEY;
                if (alpha < byte.MaxValue)
                    flags |= LWA_ALPHA;
                if (!SetLayeredWindowAttributes(window, TransparencyKey, alpha, flags))
                {
                    // Same reasoning as the brush: no chroma key, no HUD.
                    OverlayHost.LogWarning("the HUD chroma key could not be applied, win32 error "
                        + Marshal.GetLastWin32Error() + ".");
                    return false;
                }
            }
            else if (alpha < byte.MaxValue)
            {
                SetLayeredWindowAttributes(window, 0, alpha, LWA_ALPHA);
            }

            return true;
        }

        /// <summary>
        /// A stock Windows title bar over a game breaks the picture, so the
        /// frame is recolored to a dark game-appropriate grey. Windows 11 takes
        /// the exact colors; Windows 10 only knows its dark mode; anything
        /// older keeps the stock frame. All of it is cosmetic, so every call
        /// may fail freely.
        /// </summary>
        private void applyDarkFrame()
        {
            uint caption = CaptionColor;
            if (DwmSetWindowAttribute(window, DWMWA_CAPTION_COLOR, ref caption, sizeof(uint)) == 0)
            {
                uint text = CaptionTextColor;
                DwmSetWindowAttribute(window, DWMWA_TEXT_COLOR, ref text, sizeof(uint));
                uint border = CaptionColor;
                DwmSetWindowAttribute(window, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
            }
            else
            {
                uint darkMode = 1;
                DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(uint));
            }
        }

        private byte opacityAsAlpha()
        {
            double opacity = options.Opacity;
            if (double.IsNaN(opacity) || opacity >= 1.0)
                return byte.MaxValue;
            if (opacity < 0.15)
                opacity = 0.15;
            return (byte)Math.Round(opacity * byte.MaxValue);
        }

        /// <summary>
        /// A composition HUD needs a DComp device (no D3D device required) and
        /// an environment that can create composition controllers. Both checks
        /// are synchronous and cheap, so the mode is known before any window
        /// exists. The device is kept for the wiring later.
        /// </summary>
        private bool tryPrepareComposition()
        {
            try
            {
                if (DCompApi.DCompositionCreateDevice2(IntPtr.Zero, DCompApi.IID_DesktopDevice, out dcompDevice) != WebView2Api.S_OK
                    || dcompDevice == IntPtr.Zero)
                {
                    OverlayHost.LogWarning("no DirectComposition device"
                        + (options.Interactive ? "." : "; the HUD uses the chroma key instead."));
                    return false;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // Missing dcomp.dll or a Windows too old to export the entry
                // point - either way composition is simply not available here.
                OverlayHost.LogWarning("DirectComposition is unavailable on this Windows"
                    + (options.Interactive ? "." : "; the HUD uses the chroma key instead."));
                return false;
            }

            Guid iid = WebView2Api.IID_Environment3;
            if (Marshal.QueryInterface(OverlayHost.EnvironmentFor(true), ref iid, out IntPtr environment3) != WebView2Api.S_OK
                || environment3 == IntPtr.Zero)
            {
                OverlayHost.LogWarning("this WebView2 runtime cannot host composition"
                    + (options.Interactive ? "." : "; the HUD uses the chroma key instead."));
                releaseDComp();
                return false;
            }
            Marshal.Release(environment3);
            return true;
        }

        /// <summary>
        /// The composed path: the browser draws into a DirectComposition
        /// visual instead of child windows, which is what makes true
        /// per-pixel alpha and input forwarding possible. The controller that
        /// comes back also answers for ICoreWebView2Controller, so the whole
        /// existing configuration path applies unchanged.
        /// </summary>
        private bool createComposedView()
        {
            Guid iid3 = WebView2Api.IID_Environment3;
            if (Marshal.QueryInterface(OverlayHost.EnvironmentFor(true), ref iid3, out IntPtr environment3) != WebView2Api.S_OK
                || environment3 == IntPtr.Zero)
            {
                fail(OverlayFailure.CompositionUnavailable, "the composition environment disappeared.");
                return false;
            }

            compositionCompleted = new ComCallback(WebView2Api.IID_CompositionControllerCompleted, (int result, IntPtr pointer) =>
            {
                if (closed)
                {
                    // The delivered pointer is the COMPOSITION controller,
                    // whose own vtable ends long before the plain controller's
                    // Close slot - calling slot 24 on it would jump into
                    // arbitrary memory. Close through the QI'd controller
                    // interface, like the success path does.
                    if (result == WebView2Api.S_OK && pointer != IntPtr.Zero)
                    {
                        Guid iidClose = WebView2Api.IID_Controller;
                        if (Marshal.QueryInterface(pointer, ref iidClose, out IntPtr asController) == WebView2Api.S_OK
                            && asController != IntPtr.Zero)
                        {
                            WebView2Api.Method<WebView2Api.NoArgsDelegate>(asController, WebView2Api.Controller_Close)(asController);
                            Marshal.Release(asController);
                        }
                    }
                    return WebView2Api.S_OK;
                }
                if (result != WebView2Api.S_OK || pointer == IntPtr.Zero)
                {
                    fail(OverlayFailure.ViewFailed, "the composed browser view failed, hr=0x" + result.ToString("X8") + ".");
                    return WebView2Api.S_OK;
                }

                compositionController = pointer;
                Marshal.AddRef(compositionController);
                // From here the main browser will not create a windowed view;
                // the host needs to know so the next one gets a browser that
                // will.
                OverlayHost.ComposedControllerOpened();

                // The same object speaks the plain controller interface; the
                // QI hands over its own reference, so no extra AddRef here.
                Guid iidController = WebView2Api.IID_Controller;
                if (Marshal.QueryInterface(compositionController, ref iidController, out controller) != WebView2Api.S_OK
                    || controller == IntPtr.Zero)
                {
                    fail(OverlayFailure.ViewFailed, "the composed view does not answer as a controller.");
                    return WebView2Api.S_OK;
                }

                try
                {
                    if (!wireVisualTree())
                    {
                        fail(OverlayFailure.CompositionUnavailable, "the composition visual tree could not be wired.");
                        return WebView2Api.S_OK;
                    }
                    subscribeToCursor();
                    configure();
                }
                catch (Exception ex)
                {
                    fail(OverlayFailure.ViewFailed, "composed configuration threw (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.Method<WebView2Api.CreateControllerDelegate>(
                environment3, WebView2Api.Environment3_CreateCompositionController)(
                environment3, window, compositionCompleted.Pointer);
            Marshal.Release(environment3);
            if (hr != WebView2Api.S_OK)
            {
                fail(OverlayFailure.ViewFailed, "could not request a composed browser view, hr=0x" + hr.ToString("X8") + ".");
                return false;
            }
            return true;
        }

        private bool wireVisualTree()
        {
            int hrTarget = WebView2Api.Method<DCompApi.CreateTargetForHwndDelegate>(
                dcompDevice, DCompApi.Device_CreateTargetForHwnd)(dcompDevice, window, 1, out dcompTarget);
            int hrVisual = WebView2Api.Method<WebView2Api.GetPointerDelegate>(
                dcompDevice, DCompApi.Device_CreateVisual)(dcompDevice, out dcompVisual);
            if (hrTarget != WebView2Api.S_OK || hrVisual != WebView2Api.S_OK
                || dcompTarget == IntPtr.Zero || dcompVisual == IntPtr.Zero)
                return false;

            int hrRoot = WebView2Api.Method<WebView2Api.PutPointerDelegate>(
                compositionController, WebView2Api.Composition_PutRootVisualTarget)(compositionController, dcompVisual);
            int hrSetRoot = WebView2Api.Method<WebView2Api.PutPointerDelegate>(
                dcompTarget, DCompApi.Target_SetRoot)(dcompTarget, dcompVisual);
            int hrCommit = WebView2Api.Method<WebView2Api.NoArgsDelegate>(dcompDevice, DCompApi.Device_Commit)(dcompDevice);
            return hrRoot == WebView2Api.S_OK && hrSetRoot == WebView2Api.S_OK && hrCommit == WebView2Api.S_OK;
        }

        /// <summary>
        /// In visual hosting the browser cannot set the mouse cursor itself;
        /// it announces what it wants and the window applies it on WM_SETCURSOR.
        /// </summary>
        private void subscribeToCursor()
        {
            if (!options.Interactive)
                return;
            cursorChangedCallback = new ComCallback(WebView2Api.IID_CursorChanged, (IntPtr sender, IntPtr args) =>
            {
                WebView2Api.Method<WebView2Api.GetUIntDelegate>(
                    compositionController, WebView2Api.Composition_GetSystemCursorId)(compositionController, out uint cursorId);
                currentCursor = LoadCursor(IntPtr.Zero, (IntPtr)cursorId);
                return WebView2Api.S_OK;
            });
            WebView2Api.Method<WebView2Api.AddEventDelegate>(
                compositionController, WebView2Api.Composition_AddCursorChanged)(
                compositionController, cursorChangedCallback.Pointer, out _);
        }

        private void releaseDComp()
        {
            try
            {
                if (dcompVisual != IntPtr.Zero) { Marshal.Release(dcompVisual); dcompVisual = IntPtr.Zero; }
            }
            catch
            {
            }
            try
            {
                if (dcompTarget != IntPtr.Zero) { Marshal.Release(dcompTarget); dcompTarget = IntPtr.Zero; }
            }
            catch
            {
            }
            try
            {
                if (dcompDevice != IntPtr.Zero) { Marshal.Release(dcompDevice); dcompDevice = IntPtr.Zero; }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Asks the browser to render nothing where the page paints nothing.
        /// Those pixels then show this window's key-color background, which the
        /// chroma key in turn replaces with the game. Needs the Controller2
        /// interface, present in every WebView2 runtime from 2021 on.
        /// </summary>
        private bool applyTransparentBackground()
        {
            Guid iid = WebView2Api.IID_Controller2;
            if (Marshal.QueryInterface(controller, ref iid, out IntPtr controller2) != WebView2Api.S_OK
                || controller2 == IntPtr.Zero)
            {
                OverlayHost.LogWarning("this WebView2 runtime cannot make a HUD transparent; update it.");
                return false;
            }

            try
            {
                // COREWEBVIEW2_COLOR {A,R,G,B} by value; zero is fully transparent.
                int hr = WebView2Api.Method<WebView2Api.PutColorDelegate>(
                    controller2, WebView2Api.Controller2_PutDefaultBackgroundColor)(controller2, 0u);
                if (hr != WebView2Api.S_OK)
                {
                    OverlayHost.LogWarning("transparent background failed, hr=0x" + hr.ToString("X8") + ".");
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.Release(controller2);
            }
        }

        private IntPtr wndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            // A managed exception must never unwind into native frames.
            try
            {
                switch (message)
                {
                    case WM_CLOSE:
                        Hide();
                        return IntPtr.Zero;
                    case WM_SIZE:
                        fitToClientArea();
                        return IntPtr.Zero;
                    case WM_MOVE:
                        notifyPositionChanged();
                        break;
                    case WM_EXITSIZEMOVE:
                        // The player finished dragging or resizing: that spot
                        // is the one to reopen at.
                        saveBounds();
                        return IntPtr.Zero;
                    case WM_GETMINMAXINFO:
                        // The same floor the restore path clamps to, so a live
                        // window can never be resized below what a later
                        // session would accept.
                        if (options.Frame && !options.Transparent && lParam != IntPtr.Zero)
                        {
                            Marshal.WriteInt32(lParam, 24, MinimumWidth);   // ptMinTrackSize.x
                            Marshal.WriteInt32(lParam, 28, MinimumHeight);  // ptMinTrackSize.y
                            return IntPtr.Zero;
                        }
                        break;
                    case WM_TIMER:
                        if (wParam.ToInt64() == TrackTimerId)
                        {
                            followGameWindow();
                            return IntPtr.Zero;
                        }
                        break;
                    case WM_MOUSEMOVE:
                    case WM_LBUTTONDOWN:
                    case WM_LBUTTONUP:
                    case WM_RBUTTONDOWN:
                    case WM_RBUTTONUP:
                    case WM_MBUTTONDOWN:
                    case WM_MBUTTONUP:
                    case WM_MOUSEWHEEL:
                    case WM_MOUSEHWHEEL:
                        if (usesComposition && options.Interactive)
                        {
                            forwardMouse(message, wParam, lParam);
                            return IntPtr.Zero;
                        }
                        break;
                    case WM_XBUTTONDOWN:
                    case WM_XBUTTONUP:
                        if (usesComposition && options.Interactive)
                        {
                            forwardMouse(message, wParam, lParam);
                            // MSDN: X-button messages report handled as TRUE.
                            return (IntPtr)1;
                        }
                        break;
                    case WM_MOUSELEAVE:
                        if (usesComposition && options.Interactive)
                        {
                            trackingMouseLeave = false;
                            sendMouse(WebView2Api.MouseEventLeave, 0, 0, 0, 0);
                            return IntPtr.Zero;
                        }
                        break;
                    case WM_CAPTURECHANGED:
                        // Windows can revoke capture without a button-up
                        // (Alt-Tab, another SetCapture, WM_CANCELMODE). The
                        // counter must resynchronize or it stays offset for
                        // the window's remaining lifetime, and the page needs
                        // a leave so its pressed/hover state resets too.
                        if (usesComposition && options.Interactive
                            && lParam != window && mouseButtonsDown > 0)
                        {
                            mouseButtonsDown = 0;
                            sendMouse(WebView2Api.MouseEventLeave, 0, 0, 0, 0);
                        }
                        break;
                    case WM_SETCURSOR:
                        if (usesComposition && options.Interactive)
                        {
                            // Visual hosting cannot set the cursor itself; the
                            // browser announced its wish via CursorChanged.
                            SetCursor(currentCursor != IntPtr.Zero ? currentCursor : LoadCursor(IntPtr.Zero, (IntPtr)IDC_ARROW));
                            return (IntPtr)1;
                        }
                        break;
                    case WM_KEYDOWN:
                    case WM_SYSKEYDOWN:
                        // Reached when the frame itself holds the keyboard, for
                        // instance right after dragging the window: neither the
                        // page nor the game sees the key then.
                        onKey(wParam.ToInt32());
                        return IntPtr.Zero;
                }
            }
            catch
            {
            }

            return OverlayHost.DefWindowProc(hwnd, message, wParam, lParam);
        }

        private void setControllerVisible(bool visible)
        {
            if (controller == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.PutBoolDelegate>(controller, WebView2Api.Controller_PutIsVisible)(
                controller, visible ? 1 : 0);
        }

        private void fitToClientArea()
        {
            if (controller == IntPtr.Zero || window == IntPtr.Zero)
                return;
            if (!GetClientRect(window, out WebView2Api.RECT client))
                return;
            WebView2Api.Method<WebView2Api.PutBoundsDelegate>(controller, WebView2Api.Controller_PutBounds)(
                controller, client);
        }

        private void positionOverGame()
        {
            getBounds(out int x, out int y, out int width, out int height);
            // NOACTIVATE matters for HUDs: SetWindowPos would otherwise
            // activate the window and undo everything WS_EX_NOACTIVATE built.
            // The interactive path activates explicitly in Show().
            SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void getBounds(out int x, out int y, out int width, out int height)
        {
            // The remembered spot wins - unless it is no longer on any screen
            // (monitor unplugged, resolution changed), then the default takes
            // over so the window cannot get lost. A sub-minimum stored size is
            // clamped rather than rejected: throwing away the position because
            // the size was small would reset exactly the window the player
            // customized most.
            if (remembersBounds
                && BoundsStore.TryGet(persistenceKey, out BoundsStore.StoredBounds stored)
                && stored.Width > 0 && stored.Height > 0
                && isOnAnyScreen(stored.X, stored.Y, stored.Width, stored.Height))
            {
                x = stored.X;
                y = stored.Y;
                width = Math.Max(stored.Width, MinimumWidth);
                height = Math.Max(stored.Height, MinimumHeight);
                return;
            }

            width = options.Width;
            height = options.Height;

            // Measure the game picture; when that fails (window not found, or
            // gone mid-shutdown) the primary screen stands in, so a default
            // size never collapses to a zero-sized window.
            IntPtr owner = OverlayHost.GameWindow;
            var topLeft = new POINT();
            int clientWidth;
            int clientHeight;
            if (owner != IntPtr.Zero
                && GetClientRect(owner, out WebView2Api.RECT client)
                && ClientToScreen(owner, ref topLeft))
            {
                clientWidth = client.right - client.left;
                clientHeight = client.bottom - client.top;
            }
            else
            {
                topLeft = new POINT();
                clientWidth = GetSystemMetrics(SM_CXSCREEN);
                clientHeight = GetSystemMetrics(SM_CYSCREEN);
            }
            if (options.Transparent)
            {
                // A HUD's natural canvas is the whole game picture; the page
                // decides where on it something appears.
                if (options.Width <= 0)
                    width = clientWidth;
                if (options.Height <= 0)
                    height = clientHeight;
                x = topLeft.x;
                y = topLeft.y;
                return;
            }

            if (options.Width <= 0)
                width = Math.Max(640, (int)(clientWidth * 0.8));
            if (options.Height <= 0)
                height = Math.Max(480, (int)(clientHeight * 0.85));
            x = topLeft.x + Math.Max(0, (clientWidth - width) / 2);
            y = topLeft.y + Math.Max(0, (clientHeight - height) / 2);
        }

        private const string WindowClassName = "WebOverlayWindow";
        private const string HudWindowClassName = "WebOverlayHudWindow";

        /// <summary>
        /// COLORREF (0x00BBGGRR) of rgb(3,1,3): dark enough that antialiased
        /// edges blending towards it stay invisible, and unlikely enough that no
        /// page paints it on purpose.
        /// </summary>
        private const uint TransparencyKey = 0x00030103;

        /// <summary>One brush per process; the HUD window class owns it forever.</summary>
        private static IntPtr hudBrush;

        // COLORREF (0x00BBGGRR): a dark desaturated grey close to the game's
        // own panels (#1B1C17), with the light grey the game uses for text.
        private const uint CaptionColor = 0x00171C1B;
        private const uint CaptionTextColor = 0x00BDCDD0;

        private const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const uint DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_CAPTION_COLOR = 35;
        private const uint DWMWA_TEXT_COLOR = 36;

        private const int COLOR_WINDOW = 5;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_SIZEBOX = 0x00040000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        // SetWindowLongPtrW only exists on 64-bit; the game is 64-bit, so the
        // 32-bit fallback other code carries is not needed here.
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint LWA_COLORKEY = 0x1;
        private const uint LWA_ALPHA = 0x2;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_SIZE = 0x0005;
        private const uint WM_MOVE = 0x0003;
        private const uint WM_TIMER = 0x0113;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_NCDESTROY = 0x0082;
        private const uint WM_EXITSIZEMOVE = 0x0232;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const int RGN_OR = 2;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_MOUSEHWHEEL = 0x020E;
        private const uint WM_XBUTTONDOWN = 0x020B;
        private const uint WM_XBUTTONUP = 0x020C;
        private const uint WM_CAPTURECHANGED = 0x0215;
        private const uint WM_MOUSELEAVE = 0x02A3;
        private const uint WM_SETCURSOR = 0x0020;
        private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        private const uint TME_LEAVE = 0x00000002;
        private const int IDC_ARROW = 32512;
        private const uint MONITOR_DEFAULTTONULL = 0;
        private const int MinimumWidth = 200;
        private const int MinimumHeight = 150;
        private const int TrackTimerId = 1;
        private const uint TrackIntervalMilliseconds = 500;
        private const int OutboxLimit = 100;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out WebView2Api.RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern IntPtr SetTimer(IntPtr hwnd, int id, uint interval, IntPtr callback);

        [DllImport("user32.dll")]
        private static extern bool KillTimer(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out WebView2Api.RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref WebView2Api.RECT rect, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
    }
}
