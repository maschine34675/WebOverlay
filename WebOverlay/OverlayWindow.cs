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

        // One FIFO for messages and scripts: their relative order is part of
        // what the consumer expressed. Key=true means script. Cleared whenever
        // the mod retargets the overlay - buffered items belonged to the old
        // page and must not leak into the new one.
        private readonly List<KeyValuePair<bool, string>> outbox = new List<KeyValuePair<bool, string>>();

        private IntPtr window;
        private IntPtr controller;
        private IntPtr webView;
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
        public Action<int> KeyPressed;
        public Action Closed;
        public Action Ready;
        public Action Failed;

        /// <summary>
        /// Marks the overlay broken and tells the consumer, exactly once. The
        /// window is hidden rather than destroyed - the consumer still owns the
        /// handle and disposes it.
        /// </summary>
        private void fail(string reason)
        {
            if (state == CreationState.Failed)
                return;
            state = CreationState.Failed;
            OverlayHost.LogWarning(reason);
            if (window != IntPtr.Zero && isVisible)
            {
                ShowWindow(window, SW_HIDE);
                isVisible = false;
            }
            // During game shutdown a failure is expected, and notifying the
            // consumer would trigger its fallback - picture a fallback browser
            // window popping up while the game quits.
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
                fail("creation threw (" + ex.GetType().Name + ": " + ex.Message + ").");
                return false;
            }
        }

        private bool createCore()
        {
            if (OverlayHost.Environment == IntPtr.Zero)
            {
                fail("no browser environment; the overlay cannot be created.");
                return false;
            }

            if (!createWindow())
            {
                fail("the overlay window could not be created.");
                return false;
            }

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
                    fail("the browser view failed, hr=0x" + result.ToString("X8") + ".");
                    return WebView2Api.S_OK;
                }

                controller = pointer;
                Marshal.AddRef(controller);
                try
                {
                    configure();
                }
                catch (Exception ex)
                {
                    // The ComCallback thunk would swallow this silently and
                    // leave the handle in Creating forever.
                    fail("configuration threw (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.Method<WebView2Api.CreateControllerDelegate>(
                OverlayHost.Environment, WebView2Api.Environment_CreateController)(
                OverlayHost.Environment, window, controllerCallback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                fail("could not request a browser view, hr=0x" + hr.ToString("X8") + ".");
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
                fail("the browser view could not be obtained.");
                return;
            }

            // The security boundary is part of creation success: an overlay
            // whose navigation filter, message filter, popup suppression or
            // permission denial did not register must not present itself as
            // working - that would be fail-open.
            if (!applySettings() || !subscribeToMessages() || !subscribeToNavigation())
            {
                fail("a security-critical setting could not be applied.");
                return;
            }
            if (options.Transparent && !applyTransparentBackground())
            {
                // Without a transparent background the "HUD" would be a white
                // click-through sheet over the whole game. Staying invisible is
                // the only safe failure.
                fail("the HUD stays hidden because transparency is unavailable.");
                return;
            }
            if (!subscribeToKeys())
            {
                // A frameless interactive overlay whose close keys did not
                // register would trap the player: no frame, no way out. A
                // framed one still has its close button; a HUD never has focus.
                if (!options.Frame && !options.Transparent)
                {
                    fail("the close keys could not register on a frameless overlay.");
                    return;
                }
                OverlayHost.LogWarning("the close keys could not register; use the close button.");
            }
            fitToClientArea();

            // Replays what the mod asked for while the browser was starting -
            // through an internal path, because the public methods clear the
            // outbox and would wipe messages posted right after Create. A
            // Hide() or Toggle() in the gap must win over the default Show.
            startPendingNavigation();
            if (desiredVisible)
                Show();

            // Ready last: everything internal is done, and a consumer handler
            // that throws must not be able to leave the overlay half-built.
            state = CreationState.Ready;
            Ready?.Invoke();
        }

        private void startPendingNavigation()
        {
            if (pendingHtml != null)
            {
                pageReady = false;
                expectInlineNavigation = true;
                checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                    webView, WebView2Api.WebView_NavigateToString)(webView, pendingHtml), "LoadHtml");
            }
            else if (pendingUrl != null)
            {
                pageReady = false;
                checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                    webView, WebView2Api.WebView_Navigate)(webView, pendingUrl), "Navigate");
            }
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
                if (args == IntPtr.Zero || MessageReceived == null)
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
                MessageReceived(message);
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
                        flushOutbox();
                    }
                    else if (outbox.Count > 0)
                    {
                        OverlayHost.LogWarning("dropped " + outbox.Count
                            + " buffered send(s); the page ended up somewhere else than the mod's target.");
                        outbox.Clear();
                    }
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
                    fail("the browser process exited; the overlay is dead.");
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
                        startPendingNavigation();
                    }
                    else
                    {
                        // A handle that stays "Ready" over a permanently dead
                        // page would never hand the consumer to its fallback.
                        fail("the page's renderer keeps failing; the overlay is dead.");
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
                return WebView2Api.S_OK;
            }

            // The document is about to change: sends must buffer until the new
            // page reports completion, or they would vanish into the old one.
            if (topLevel)
                pageReady = false;
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
            return origin != null && allowedOrigins.Contains(origin);
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
            pendingUrl = url;
            pendingHtml = null;
            htmlLoaded = false;
            expectInlineNavigation = false;
            // Anything still buffered was meant for the previous page.
            outbox.Clear();
            // The mod asked for this page, so its origin becomes trusted.
            allowOrigin(url);
            if (webView == IntPtr.Zero)
                return;
            pageReady = false;
            checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                webView, WebView2Api.WebView_Navigate)(webView, url), "Navigate");
        }

        /// <summary>
        /// A synchronously rejected navigation (invalid URL, inline HTML over
        /// the 2 MB limit) never produces NavigationCompleted. The buffered
        /// sends were meant for the page that will now never load, so they are
        /// dropped - silently growing a queue nobody will ever flush would
        /// hide the defect from the consumer.
        /// </summary>
        private void checkNavigationResult(int hr, string what)
        {
            if (hr == WebView2Api.S_OK)
                return;
            OverlayHost.LogWarning("" + what + " was rejected, hr=0x" + hr.ToString("X8")
                + "; the page will not change" + (outbox.Count > 0 ? " and " + outbox.Count + " buffered send(s) were dropped" : "") + ".");
            outbox.Clear();
        }

        /// <summary>Shows markup directly, so a mod needs no web server at all.</summary>
        public void LoadHtml(string html)
        {
            if (html == null)
                return;
            pendingHtml = html;
            pendingUrl = null;
            htmlLoaded = true;
            outbox.Clear();
            if (webView == IntPtr.Zero)
                return;
            pageReady = false;
            expectInlineNavigation = true;
            checkNavigationResult(WebView2Api.Method<WebView2Api.StringDelegate>(
                webView, WebView2Api.WebView_NavigateToString)(webView, html), "LoadHtml");
        }

        public void PostMessageToPage(string message)
        {
            if (message == null)
                return;
            // WebView2 does not deliver messages sent before the page finished
            // loading, so they wait in a bounded outbox until it has - and a
            // live send only goes out while the mod's own target is showing.
            if (webView == IntPtr.Zero || !pageReady)
            {
                buffer(false, message);
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

        public void ExecuteScript(string script)
        {
            if (script == null)
                return;
            if (webView == IntPtr.Zero || !pageReady)
            {
                buffer(true, script);
                return;
            }
            if (!currentDocumentIsTarget())
            {
                OverlayHost.LogWarning("dropped a script; the page is not the mod's target document.");
                return;
            }

            // A real completion handler: passing null there is undocumented
            // behavior, and its error code is the only way a script failure
            // ever becomes visible. One callback serves every call.
            if (scriptCompletedCallback == null)
                scriptCompletedCallback = new ComCallback(WebView2Api.IID_ExecuteScriptCompleted, (int hrScript, IntPtr resultJson) =>
                {
                    if (hrScript != WebView2Api.S_OK)
                        OverlayHost.LogWarning("a script failed, hr=0x" + hrScript.ToString("X8") + ".");
                    return WebView2Api.S_OK;
                });

            int hr = WebView2Api.Method<WebView2Api.ExecuteScriptDelegate>(webView, WebView2Api.WebView_ExecuteScript)(
                webView, script, scriptCompletedCallback.Pointer);
            if (hr != WebView2Api.S_OK)
                OverlayHost.LogWarning("ExecuteScript was rejected, hr=0x" + hr.ToString("X8") + ".");
        }

        private void buffer(bool isScript, string payload)
        {
            if (outbox.Count < OutboxLimit)
            {
                outbox.Add(new KeyValuePair<bool, string>(isScript, payload));
                return;
            }
            overflowDropped++;
            if (overflowDropped == 1)
                OverlayHost.LogWarning("the outbox is full (" + OutboxLimit
                    + " entries); further sends are dropped until the page loads.");
        }

        private void flushOutbox()
        {
            if (outbox.Count == 0)
                return;
            var items = outbox.ToArray();
            outbox.Clear();
            foreach (KeyValuePair<bool, string> item in items)
            {
                if (item.Key)
                    ExecuteScript(item.Value);
                else
                    PostMessageToPage(item.Value);
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
            if (window == IntPtr.Zero || state == CreationState.Failed)
                return;
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
            isVisible = true;
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
            isVisible = false;
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
            OverlayHost.Unregister(this);
            if (wasVisible)
                Closed?.Invoke();
        }

        private bool createWindow()
        {
            // HUD windows get their own class because the class carries the
            // background brush, and theirs must be the transparency key: those
            // are exactly the pixels the chroma key later removes. The brush is
            // created once and owned by the class for the rest of the process;
            // creating one per window would leak a GDI handle each time.
            string className = options.Transparent ? HudWindowClassName : WindowClassName;
            if (options.Transparent && hudBrush == IntPtr.Zero)
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
                hbrBackground = options.Transparent
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
            if (options.Transparent)
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

            if (options.Transparent)
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

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
    }
}
