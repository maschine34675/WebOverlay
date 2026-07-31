using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using WebOverlay.Interop;

namespace WebOverlay
{
    /// <summary>
    /// Owns the one thread everything WebView2 runs on, and the one browser
    /// environment shared by every overlay in the process.
    ///
    /// Both are deliberately singular. WebView2 is COM and needs a thread that
    /// is STA and pumps messages, which the game's main thread is not and does
    /// not; and every additional environment starts its own browser process
    /// tree and wants exclusive use of its user-data folder, so one per mod
    /// would cost memory and collide.
    /// </summary>
    internal static class OverlayHost
    {
        private static readonly ConcurrentQueue<Action> work = new ConcurrentQueue<Action>();
        private static readonly List<OverlayWindow> windows = new List<OverlayWindow>();
        private static readonly object startSync = new object();

        private static Thread thread;
        private static IntPtr dispatcherWindow;
        private static IntPtr environment;
        private static ComCallback environmentCallback;
        private static WndProcDelegate dispatcherProc;
        private static volatile bool running;
        private static volatile bool startFailed;
        private static ManualResetEventSlim environmentReady;

        internal static Action<string> LogInfo = _ => { };
        internal static Action<string> LogWarning = _ => { };

        internal static string RuntimeVersion { get; private set; }

        internal static IntPtr GameWindow { get; set; }

        /// <summary>
        /// Starts the thread and the browser environment on first use. Blocks
        /// the caller until the environment is up, because a mod calling
        /// Create() wants a usable overlay back.
        /// </summary>
        internal static bool EnsureStarted()
        {
            lock (startSync)
            {
                if (startFailed)
                    return false;
                if (running && environment != IntPtr.Zero)
                    return true;

                if (thread == null)
                {
                    environmentReady = new ManualResetEventSlim(false);
                    thread = new Thread(run) { IsBackground = true, Name = "WebOverlay" };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }

            // Chromium needs a moment on a cold start.
            if (!environmentReady.Wait(TimeSpan.FromSeconds(30)))
            {
                LogWarning("WebOverlay: the browser environment did not start within 30 seconds.");
                startFailed = true;
                return false;
            }

            return environment != IntPtr.Zero;
        }

        internal static void Post(Action action)
        {
            work.Enqueue(action);
            if (dispatcherWindow != IntPtr.Zero)
                PostMessage(dispatcherWindow, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        internal static IntPtr Environment => environment;

        internal static void Register(OverlayWindow window)
        {
            lock (windows)
                windows.Add(window);
        }

        internal static void Unregister(OverlayWindow window)
        {
            lock (windows)
                windows.Remove(window);
        }

        internal static void Shutdown()
        {
            running = false;
            if (dispatcherWindow != IntPtr.Zero)
                PostMessage(dispatcherWindow, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        private static void run()
        {
            running = true;
            try
            {
                if (!createDispatcherWindow() || !loadRuntime() || !createEnvironment())
                {
                    startFailed = true;
                    environmentReady.Set();
                    return;
                }

                while (running)
                {
                    drainWork();
                    pump();
                    Thread.Sleep(5);
                }
            }
            catch (ThreadAbortException)
            {
                // Normal on game shutdown.
            }
            catch (Exception ex)
            {
                startFailed = true;
                LogWarning("WebOverlay: the overlay thread stopped (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
            finally
            {
                environmentReady?.Set();
                closeEverything();
            }
        }

        private static bool createDispatcherWindow()
        {
            dispatcherProc = dispatcherWndProc;
            var windowClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(dispatcherProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = "WebOverlayDispatcher"
            };

            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
            {
                LogWarning("WebOverlay: could not register the dispatcher window class.");
                return false;
            }

            dispatcherWindow = CreateWindowEx(0, "WebOverlayDispatcher", "WebOverlay", 0,
                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            return dispatcherWindow != IntPtr.Zero;
        }

        private static bool loadRuntime()
        {
            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            if (LoadLibrary(Path.Combine(folder, "WebView2Loader.dll")) == IntPtr.Zero)
            {
                LogWarning("WebOverlay: WebView2Loader.dll was not found next to the plugin (" + folder + ").");
                return false;
            }

            if (WebView2Api.GetAvailableCoreWebView2BrowserVersionString(null, out IntPtr version) != WebView2Api.S_OK
                || version == IntPtr.Zero)
            {
                LogWarning("WebOverlay: no WebView2 runtime is installed; overlays are unavailable.");
                return false;
            }

            RuntimeVersion = Marshal.PtrToStringUni(version);
            Marshal.FreeCoTaskMem(version);
            LogInfo("WebOverlay: using WebView2 runtime " + RuntimeVersion + ".");
            return true;
        }

        private static bool createEnvironment()
        {
            // Never under the game folder, which may be read-only.
            string userData = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "WebOverlay", "BrowserData");
            Directory.CreateDirectory(userData);

            environmentCallback = new ComCallback(WebView2Api.IID_EnvironmentCompleted, (int result, IntPtr pointer) =>
            {
                if (result == WebView2Api.S_OK && pointer != IntPtr.Zero)
                {
                    environment = pointer;
                    Marshal.AddRef(environment);
                }
                else
                {
                    LogWarning("WebOverlay: the browser environment failed, hr=0x" + result.ToString("X8") + ".");
                }

                environmentReady.Set();
                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.CreateCoreWebView2EnvironmentWithOptions(
                null, userData, IntPtr.Zero, environmentCallback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                LogWarning("WebOverlay: could not request a browser environment, hr=0x" + hr.ToString("X8") + ".");
                return false;
            }

            // The completion may arrive re-entrantly or through the pump.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (environment == IntPtr.Zero && !environmentReady.IsSet && timer.Elapsed.TotalSeconds < 30)
            {
                pump();
                Thread.Sleep(5);
            }

            return environment != IntPtr.Zero;
        }

        private static void drainWork()
        {
            while (work.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogWarning("WebOverlay: an overlay action failed (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }
        }

        private static void pump()
        {
            while (PeekMessage(out MSG message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        private static IntPtr dispatcherWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (message == WM_APP_WORK)
                {
                    drainWork();
                    return IntPtr.Zero;
                }
            }
            catch
            {
            }

            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private static void closeEverything()
        {
            try
            {
                OverlayWindow[] open;
                lock (windows)
                    open = windows.ToArray();
                foreach (OverlayWindow window in open)
                    window.CloseFromHost();

                if (environment != IntPtr.Zero)
                {
                    Marshal.Release(environment);
                    environment = IntPtr.Zero;
                }
                environmentCallback?.Dispose();
                if (dispatcherWindow != IntPtr.Zero)
                {
                    DestroyWindow(dispatcherWindow);
                    dispatcherWindow = IntPtr.Zero;
                }
            }
            catch
            {
            }
        }

        internal const uint WM_APP_WORK = 0x8000 + 1;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const uint PM_REMOVE = 0x0001;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        internal delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint removeMessage);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
