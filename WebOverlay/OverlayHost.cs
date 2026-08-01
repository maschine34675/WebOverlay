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
        private static volatile bool stopping;
        private static volatile bool startFailed;
        private static volatile bool acceptingWork;

        internal static Action<string> LogInfo = _ => { };
        internal static Action<string> LogWarning = _ => { };

        internal static string RuntimeVersion { get; private set; }

        internal static IntPtr GameWindow { get; set; }

        /// <summary>
        /// Starts the thread and the browser environment on first use, without
        /// waiting for either: Unity's thread must never block on a browser
        /// start that can take many seconds. Work posted meanwhile is queued
        /// and runs once the environment attempt has finished; whether it
        /// succeeded surfaces through each overlay's Failed event.
        /// </summary>
        internal static bool EnsureStarted()
        {
            lock (startSync)
            {
                if (startFailed || stopping)
                    return false;

                if (thread == null)
                {
                    thread = new Thread(run) { IsBackground = true, Name = "WebOverlay" };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }

            return true;
        }

        internal static void Post(Action action)
        {
            if (stopping)
                return;
            work.Enqueue(action);
            // Before the environment attempt finishes the pump already runs (it
            // must, for the completion callback), so the dispatcher must not
            // drain overlay work yet - the queue holds it until then.
            if (dispatcherWindow != IntPtr.Zero && acceptingWork)
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
            stopping = true;
            running = false;
            if (dispatcherWindow != IntPtr.Zero)
                PostMessage(dispatcherWindow, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        private static void run()
        {
            running = true;
            // Shutdown sets stopping before running; whichever way the writes
            // interleave, either this check exits or the loop condition does.
            if (stopping)
                return;
            try
            {
                bool started = createDispatcherWindow() && loadRuntime() && createEnvironment();
                if (!started)
                    startFailed = true;

                acceptingWork = true;
                drainWork();

                // The loop runs even after a failed start: a window creation
                // racing the failure may be posted at any moment, and it must
                // run so it can fail through its own path and raise Failed -
                // work rotting in a dead queue would leave the consumer with a
                // handle that never answers. One idle thread is the price.
                if (dispatcherWindow != IntPtr.Zero)
                {
                    // GetMessage blocks until something arrives; Post() and
                    // Shutdown() both wake it with WM_APP_WORK. No idle polling.
                    while (running && GetMessage(out MSG message, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                        drainWork();
                    }
                }
                else
                {
                    // No dispatcher window (extreme failure): a slow poll still
                    // serves late registrations their failure.
                    while (running)
                    {
                        drainWork();
                        Thread.Sleep(50);
                    }
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

                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.CreateCoreWebView2EnvironmentWithOptions(
                null, userData, IntPtr.Zero, environmentCallback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                LogWarning("WebOverlay: could not request a browser environment, hr=0x" + hr.ToString("X8") + ".");
                return false;
            }

            // The completion may arrive re-entrantly or through the pump. This
            // wait runs on the overlay thread, never on Unity's.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (environment == IntPtr.Zero && timer.Elapsed.TotalSeconds < 30)
            {
                pump();
                Thread.Sleep(5);
            }

            if (environment == IntPtr.Zero)
                LogWarning("WebOverlay: the browser environment did not start within 30 seconds.");
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
            // Each resource on its own: one failing close must not leak the
            // rest, and each failure should be visible.
            OverlayWindow[] open;
            lock (windows)
                open = windows.ToArray();
            foreach (OverlayWindow window in open)
            {
                try
                {
                    window.CloseFromHost();
                }
                catch (Exception ex)
                {
                    LogWarning("WebOverlay: closing an overlay failed (" + ex.GetType().Name + ").");
                }
            }

            try
            {
                if (environment != IntPtr.Zero)
                {
                    Marshal.Release(environment);
                    environment = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                LogWarning("WebOverlay: releasing the environment failed (" + ex.GetType().Name + ").");
            }

            // environmentCallback is deliberately never disposed: if the
            // environment creation timed out, the native side may still hold
            // the handler, and freeing memory that native code can call is a
            // process crash. One small allocation for the process lifetime is
            // the safe trade.

            try
            {
                if (dispatcherWindow != IntPtr.Zero)
                {
                    DestroyWindow(dispatcherWindow);
                    dispatcherWindow = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                LogWarning("WebOverlay: closing the dispatcher failed (" + ex.GetType().Name + ").");
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}
