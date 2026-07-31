using System;
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
        private readonly string title;
        private readonly OverlayOptions options;
        private readonly OverlayHost.WndProcDelegate windowProc;

        private IntPtr window;
        private IntPtr controller;
        private IntPtr webView;
        private ComCallback controllerCallback;
        private ComCallback keyCallback;
        private ComCallback messageCallback;
        private string pendingUrl;
        private string pendingHtml;

        public OverlayWindow(string title, OverlayOptions options)
        {
            this.title = title;
            this.options = options;
            windowProc = wndProc;
        }

        public bool IsVisible { get; private set; }

        public Action<string> MessageReceived;
        public Action<int> KeyPressed;
        public Action Closed;

        public bool Create()
        {
            if (!createWindow())
                return false;

            controllerCallback = new ComCallback(WebView2Api.IID_ControllerCompleted, (int result, IntPtr pointer) =>
            {
                if (result != WebView2Api.S_OK || pointer == IntPtr.Zero)
                {
                    OverlayHost.LogWarning("WebOverlay: the browser view failed, hr=0x" + result.ToString("X8") + ".");
                    return WebView2Api.S_OK;
                }

                controller = pointer;
                Marshal.AddRef(controller);
                configure();
                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.Method<WebView2Api.CreateControllerDelegate>(
                OverlayHost.Environment, WebView2Api.Environment_CreateController)(
                OverlayHost.Environment, window, controllerCallback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                OverlayHost.LogWarning("WebOverlay: could not request a browser view, hr=0x" + hr.ToString("X8") + ".");
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
                OverlayHost.LogWarning("WebOverlay: the browser view could not be obtained.");
                return;
            }

            applySettings();
            subscribeToKeys();
            subscribeToMessages();
            fitToClientArea();

            if (pendingHtml != null)
                LoadHtml(pendingHtml);
            else if (pendingUrl != null)
                Navigate(pendingUrl);

            Show();
        }

        private void applySettings()
        {
            WebView2Api.Method<WebView2Api.GetPointerDelegate>(webView, WebView2Api.WebView_GetSettings)(
                webView, out IntPtr settings);
            if (settings == IntPtr.Zero)
                return;

            try
            {
                // Messaging is what turns a page into a control surface, so it is
                // on by default; the browser chrome is off because this should
                // not look like a browser.
                setSetting(settings, WebView2Api.Settings_PutIsWebMessageEnabled, true);
                setSetting(settings, WebView2Api.Settings_PutIsStatusBarEnabled, false);
                setSetting(settings, WebView2Api.Settings_PutAreDefaultContextMenusEnabled, options.ContextMenu);
                setSetting(settings, WebView2Api.Settings_PutAreDevToolsEnabled, options.DevTools);
            }
            finally
            {
                Marshal.Release(settings);
            }
        }

        private static void setSetting(IntPtr settings, int slot, bool value)
        {
            WebView2Api.Method<WebView2Api.PutBoolDelegate>(settings, slot)(settings, value ? 1 : 0);
        }

        private void subscribeToKeys()
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
                onKey((int)key);
                return WebView2Api.S_OK;
            });

            WebView2Api.Method<WebView2Api.AddEventDelegate>(controller, WebView2Api.Controller_AddAcceleratorKeyPressed)(
                controller, keyCallback.Pointer, out _);
        }

        private void subscribeToMessages()
        {
            messageCallback = new ComCallback(WebView2Api.IID_WebMessageReceived, (IntPtr sender, IntPtr args) =>
            {
                if (args == IntPtr.Zero || MessageReceived == null)
                    return WebView2Api.S_OK;

                WebView2Api.Method<WebView2Api.GetPointerDelegate>(
                    args, WebView2Api.MessageArgs_TryGetWebMessageAsString)(args, out IntPtr text);
                if (text == IntPtr.Zero)
                    return WebView2Api.S_OK;

                string message = Marshal.PtrToStringUni(text);
                Marshal.FreeCoTaskMem(text);
                MessageReceived(message);
                return WebView2Api.S_OK;
            });

            WebView2Api.Method<WebView2Api.AddEventDelegate>(webView, WebView2Api.WebView_AddWebMessageReceived)(
                webView, messageCallback.Pointer, out _);
        }

        private void onKey(int virtualKey)
        {
            if (options.CloseKeys != null && Array.IndexOf(options.CloseKeys, virtualKey) >= 0)
            {
                Hide();
                return;
            }

            KeyPressed?.Invoke(virtualKey);
        }

        public void Navigate(string url)
        {
            pendingUrl = url;
            pendingHtml = null;
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.StringDelegate>(webView, WebView2Api.WebView_Navigate)(webView, url);
        }

        /// <summary>Shows markup directly, so a mod needs no web server at all.</summary>
        public void LoadHtml(string html)
        {
            pendingHtml = html;
            pendingUrl = null;
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.StringDelegate>(webView, WebView2Api.WebView_NavigateToString)(webView, html);
        }

        public void PostMessageToPage(string message)
        {
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.StringDelegate>(webView, WebView2Api.WebView_PostWebMessageAsString)(
                webView, message);
        }

        public void ExecuteScript(string script)
        {
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.ExecuteScriptDelegate>(webView, WebView2Api.WebView_ExecuteScript)(
                webView, script, IntPtr.Zero);
        }

        public void OpenDevTools()
        {
            if (webView == IntPtr.Zero)
                return;
            WebView2Api.Method<WebView2Api.NoArgsDelegate>(webView, WebView2Api.WebView_OpenDevToolsWindow)(webView);
        }

        public void Show()
        {
            if (window == IntPtr.Zero)
                return;
            positionOverGame();
            fitToClientArea();
            setControllerVisible(true);
            ShowWindow(window, SW_SHOW);
            SetForegroundWindow(window);
            IsVisible = true;
        }

        public void Hide()
        {
            if (window == IntPtr.Zero)
                return;
            setControllerVisible(false);
            ShowWindow(window, SW_HIDE);
            IsVisible = false;
            // Hand the keyboard back to the game, not to whatever sits behind.
            if (OverlayHost.GameWindow != IntPtr.Zero && IsWindow(OverlayHost.GameWindow))
                SetForegroundWindow(OverlayHost.GameWindow);
            Closed?.Invoke();
        }

        public void CloseFromHost()
        {
            try
            {
                if (controller != IntPtr.Zero)
                {
                    WebView2Api.Method<WebView2Api.NoArgsDelegate>(controller, WebView2Api.Controller_Close)(controller);
                    Marshal.Release(controller);
                    controller = IntPtr.Zero;
                }
                webView = IntPtr.Zero;
                if (window != IntPtr.Zero)
                {
                    OverlayHost.DestroyWindow(window);
                    window = IntPtr.Zero;
                }
            }
            catch
            {
            }

            controllerCallback?.Dispose();
            keyCallback?.Dispose();
            messageCallback?.Dispose();
            OverlayHost.Unregister(this);
        }

        private bool createWindow()
        {
            var windowClass = new OverlayHost.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(OverlayHost.WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProc),
                hInstance = OverlayHost.GetModuleHandle(null),
                lpszClassName = WindowClassName,
                hbrBackground = (IntPtr)(COLOR_WINDOW + 1)
            };

            if (OverlayHost.RegisterClassEx(ref windowClass) == 0
                && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
            {
                OverlayHost.LogWarning("WebOverlay: could not register the overlay window class.");
                return false;
            }

            // An owned popup rather than a child window: Unity presents through
            // a flip-model swapchain, which does not composite child windows.
            uint style = WS_POPUP;
            if (options.Frame)
                style |= WS_CAPTION | WS_SYSMENU | WS_SIZEBOX;

            getBounds(out int x, out int y, out int width, out int height);
            window = OverlayHost.CreateWindowEx(
                WS_EX_TOOLWINDOW,
                WindowClassName,
                title,
                style,
                x, y, width, height,
                OverlayHost.GameWindow,
                IntPtr.Zero,
                OverlayHost.GetModuleHandle(null),
                IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                OverlayHost.LogWarning("WebOverlay: could not create the overlay window, win32 error "
                    + Marshal.GetLastWin32Error() + ".");
                return false;
            }

            return true;
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
            SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER);
        }

        private void getBounds(out int x, out int y, out int width, out int height)
        {
            x = 120;
            y = 120;
            width = options.Width;
            height = options.Height;

            IntPtr owner = OverlayHost.GameWindow;
            if (owner == IntPtr.Zero || !GetClientRect(owner, out WebView2Api.RECT client))
                return;

            var topLeft = new POINT { x = client.left, y = client.top };
            if (!ClientToScreen(owner, ref topLeft))
                return;

            int clientWidth = client.right - client.left;
            int clientHeight = client.bottom - client.top;
            if (options.Width <= 0)
                width = Math.Max(640, (int)(clientWidth * 0.8));
            if (options.Height <= 0)
                height = Math.Max(480, (int)(clientHeight * 0.85));
            x = topLeft.x + Math.Max(0, (clientWidth - width) / 2);
            y = topLeft.y + Math.Max(0, (clientHeight - height) / 2);
        }

        private const string WindowClassName = "WebOverlayWindow";
        private const int COLOR_WINDOW = 5;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_SIZEBOX = 0x00040000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_SIZE = 0x0005;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint SWP_NOZORDER = 0x0004;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

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
    }
}
