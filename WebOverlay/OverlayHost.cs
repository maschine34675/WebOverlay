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

        // Creating an overlay is the only work that may have to wait for a
        // browser to start, and waiting means pumping messages. Keeping it in
        // its own queue is what lets everything else - a Post, a Hide, a
        // Dispose on an overlay that is already up - keep flowing meanwhile.
        private static readonly ConcurrentQueue<Action> creations = new ConcurrentQueue<Action>();
        private static readonly List<OverlayWindow> windows = new List<OverlayWindow>();
        private static readonly object startSync = new object();

        private static Thread thread;
        private static IntPtr dispatcherWindow;
        private static IntPtr environment;
        private static IntPtr spareEnvironment;
        private static int composedControllers;
        private static int windowedControllers;
        private static string userDataFolder;
        private static bool creatingEnvironment;
        private static ComCallback environmentCallback;
        private static ComCallback spareEnvironmentCallback;
        private static WndProcDelegate dispatcherProc;
        private static volatile bool running;
        private static volatile bool stopping;
        private static volatile bool startFailed;
        private static volatile bool acceptingWork;

        // Events a consumer asked to receive on the game's main thread. The
        // core stays free of Unity: it only holds the queue, and whoever owns
        // the main thread - the library plugin, from its Update - drains it.
        // Without such a pump (the probe host, any non-Unity use) nothing
        // registers, and events keep their normal threading.
        private static readonly ConcurrentQueue<Action> mainThreadWork = new ConcurrentQueue<Action>();
        private static volatile bool mainThreadPumpAvailable;
        private static int mainThreadQueued;
        private static int mainThreadWarned;
        private static int mainThreadOverflowWarned;

        // A frozen or missing pump must not grow the queue without bound. The
        // cap is far above one frame's worth of events at any sane rate.
        private const int MainThreadQueueLimit = 4096;

        internal static Action<string> LogInfo = _ => { };
        internal static Action<string> LogWarning = _ => { };

        /// <summary>
        /// Why the shared start failed, so an overlay created afterwards can
        /// tell its consumer something more useful than "no environment".
        /// </summary>
        internal static OverlayFailure StartFailure { get; private set; } = OverlayFailure.Unknown;

        /// <summary>
        /// The sentence behind <see cref="StartFailure"/>. An overlay failing
        /// for want of the shared environment reports this rather than its own
        /// "no environment", which names the symptom instead of the cause.
        /// </summary>
        internal static string StartFailureMessage { get; private set; }

        /// <summary>
        /// Records the first cause and keeps it: what follows a failed start
        /// are its consequences - a timeout after a browser that already said
        /// no - and the consumer needs the cause, not the last symptom.
        /// </summary>
        private static void startFailure(OverlayFailure kind, string reason)
        {
            LogWarning(reason);
            if (StartFailure != OverlayFailure.Unknown)
                return;
            StartFailure = kind;
            StartFailureMessage = reason;
        }

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

        internal static void PostCreation(Action action)
        {
            if (stopping)
                return;
            creations.Enqueue(action);
            wake();
        }

        internal static void Post(Action action)
        {
            if (stopping)
                return;
            work.Enqueue(action);
            wake();
        }

        /// <summary>
        /// Before the first environment attempt finishes the pump already runs
        /// (it must, for the completion callback), so the dispatcher must not
        /// drain overlay work yet - the queues hold it until then.
        /// </summary>
        private static void wake()
        {
            if (dispatcherWindow != IntPtr.Zero && acceptingWork)
                PostMessage(dispatcherWindow, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Which browser an overlay of this kind has to be built from.
        ///
        /// A browser hosting a transparent overlay refuses to create a
        /// windowed one: the second creation fails with ERROR_INVALID_STATE,
        /// which in practice is one mod's HUD breaking another mod's panel.
        /// Two environments sharing a user data folder share the browser
        /// process, so the way out is a second browser with a folder of its
        /// own - and that costs real memory (measured: about six processes and
        /// a quarter of a gigabyte). So it is created only when the collision
        /// actually happens, and it takes the windowed overlay: a game whose
        /// mods only use HUDs, only use windows, or open the window first
        /// never pays for it.
        /// </summary>
        internal static IntPtr EnvironmentFor(bool composed)
        {
            // A windowed view is refused only when the browser is hosting
            // transparent overlays and nothing windowed - measured: with a
            // windowed view already alive there, another one is fine. So the
            // second browser is for the one case that actually needs it.
            if (composed || composedControllers == 0 || windowedControllers > 0)
                return environment;
            if (spareEnvironment == IntPtr.Zero)
                createSpareEnvironment();
            // Still nothing means the second browser could not be started; the
            // main one is what is left, and it will refuse with a message that
            // says so.
            return spareEnvironment != IntPtr.Zero ? spareEnvironment : environment;
        }

        /// <summary>
        /// Counted so the decision above can be made: a live transparent
        /// overlay is what makes the main browser refuse windowed ones.
        /// Both are called on the overlay thread.
        /// </summary>
        internal static void ComposedControllerOpened() => composedControllers++;

        /// <summary>Windowed views in the main browser, for the same decision.</summary>
        internal static void WindowedControllerOpened(bool inMainEnvironment)
        {
            if (inMainEnvironment)
                windowedControllers++;
        }

        internal static void WindowedControllerClosed(bool inMainEnvironment)
        {
            if (inMainEnvironment && windowedControllers > 0)
                windowedControllers--;
        }

        internal static void ComposedControllerClosed()
        {
            if (composedControllers > 0)
                composedControllers--;
        }

        /// <summary>
        /// Runs on the overlay thread, inside the work item that is creating
        /// the overlay that needs it.
        /// </summary>
        private static void createSpareEnvironment()
        {
            if (spareEnvironment != IntPtr.Zero || environment == IntPtr.Zero || stopping)
                return;

            LogInfo("a transparent overlay is open, so this windowed overlay needs a second browser.");

            // Checked here rather than left to the browser: when WebView2
            // cannot create its data folder it puts a modal error box on the
            // player's screen, which is not something a mod should be able to
            // cause. If the folder is not usable, no browser is asked for.
            string folder = userDataFolder + SpareSuffix;
            if (!prepareFolder(folder))
            {
                LogWarning("no second browser: its data folder could not be created (" + folder
                    + "). This overlay will fail to open while a transparent one is up.");
                return;
            }

            // Waiting for an environment means pumping messages, and pumping
            // would otherwise let the next queued overlay start on top of the
            // one being created. The queue keeps until this returns.
            creatingEnvironment = true;
            try
            {
                spareEnvironment = createEnvironment(folder,
                    "the second browser", out spareEnvironmentCallback, required: false);
            }
            finally
            {
                creatingEnvironment = false;
            }

            if (spareEnvironment == IntPtr.Zero)
            {
                // Deliberately not remembered as "the main browser will do":
                // that would turn one bad moment - a timeout, a transient
                // HRESULT - into the old defect for the rest of the session.
                // This overlay falls back and probably fails; the next one
                // tries for a second browser again.
                LogWarning("no second browser this time; this overlay will fail to open"
                    + " while a transparent one is up, and the next one will try again.");
            }

            // Creations that arrived while they were held need a nudge, since
            // the message that would have drained them is long gone.
            wake();
        }

        /// <summary>
        /// A folder of its own is the whole point: environments that share one
        /// share the browser process, and the browser is what refuses to host
        /// both kinds.
        /// </summary>
        private const string SpareSuffix = "-windowed";

        /// <summary>The windowed environment; whether the library started.</summary>
        internal static IntPtr Environment => environment;

        /// <summary>
        /// Offers one event for delivery on the game's main thread. Returns
        /// false when nobody is pumping, which is the caller's cue to invoke it
        /// itself - queuing into a queue nobody drains would simply lose the
        /// event.
        /// </summary>
        internal static bool DispatchToMainThread(Action action)
        {
            if (action == null || !mainThreadPumpAvailable)
            {
                if (action != null && Interlocked.Exchange(ref mainThreadWarned, 1) == 0)
                    LogWarning("no main-thread pump is running, so events stay on the overlay thread"
                        + " (DispatchOnMainThread works inside the game only).");
                return false;
            }

            // During shutdown the game is past caring, and a queued handler
            // could start a fallback while everything is being torn down.
            if (stopping)
                return true;

            if (Interlocked.Increment(ref mainThreadQueued) > MainThreadQueueLimit)
            {
                Interlocked.Decrement(ref mainThreadQueued);
                if (Interlocked.Exchange(ref mainThreadOverflowWarned, 1) == 0)
                    LogWarning("the main-thread event queue is full (" + MainThreadQueueLimit
                        + "); events are being dropped - is the game's frame loop stalled?");
                return true;
            }

            mainThreadWork.Enqueue(action);
            return true;
        }

        /// <summary>
        /// Whether main-thread delivery is possible; set by whoever pumps.
        /// Turning it off leaves queued events undelivered on purpose - that
        /// only happens as the game shuts down.
        /// </summary>
        internal static bool MainThreadPumpAvailable
        {
            get => mainThreadPumpAvailable;
            set => mainThreadPumpAvailable = value;
        }

        /// <summary>
        /// Whether a window over the game can be shown at all. The plugin
        /// registers this, because only Unity knows the display mode and this
        /// half of the library stays free of it; without a probe every mode is
        /// assumed fine, which is what a non-Unity host wants.
        /// </summary>
        internal static Func<bool> DisplayModeProbe;

        internal static bool DisplayModeSupported
        {
            get
            {
                Func<bool> probe = DisplayModeProbe;
                if (probe == null)
                    return true;
                try
                {
                    return probe();
                }
                catch
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Whether any visible overlay asked for the cursor to be freed while
        /// the game is unfocused. Asked once per frame by the plugin, which
        /// owns the Unity side of that.
        /// </summary>
        internal static bool WantsFreeCursor()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == GameWindow)
                return false;
            lock (windows)
            {
                foreach (OverlayWindow window in windows)
                {
                    if (window.WantsFreeCursor(foreground))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Delivers queued events. Call this from the main thread only.</summary>
        internal static void PumpMainThread()
        {
            while (mainThreadWork.TryDequeue(out Action action))
            {
                Interlocked.Decrement(ref mainThreadQueued);
                if (stopping)
                    continue;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogWarning("a main-thread event failed (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }
        }

        /// <summary>The game is quitting; failures are expected, not news.</summary>
        internal static bool Stopping => stopping;

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
                bool started = createDispatcherWindow() && loadRuntime() && createEnvironments();
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
                startFailure(OverlayFailure.EnvironmentFailed,
                    "the overlay thread stopped (" + ex.GetType().Name + ": " + ex.Message + ").");
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
                startFailure(OverlayFailure.EnvironmentFailed,
                    "could not register the dispatcher window class.");
                return false;
            }

            dispatcherWindow = CreateWindowEx(0, "WebOverlayDispatcher", "WebOverlay", 0,
                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (dispatcherWindow == IntPtr.Zero)
                startFailure(OverlayFailure.EnvironmentFailed, "could not create the dispatcher window.");
            return dispatcherWindow != IntPtr.Zero;
        }

        private static bool loadRuntime()
        {
            string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            if (LoadLibrary(Path.Combine(folder, "WebView2Loader.dll")) == IntPtr.Zero)
            {
                startFailure(OverlayFailure.LibraryIncomplete,
                    "WebView2Loader.dll was not found next to the plugin (" + folder + ").");
                return false;
            }

            if (WebView2Api.GetAvailableCoreWebView2BrowserVersionString(null, out IntPtr version) != WebView2Api.S_OK
                || version == IntPtr.Zero)
            {
                startFailure(OverlayFailure.RuntimeMissing,
                    "no WebView2 runtime is installed; overlays are unavailable.");
                return false;
            }

            RuntimeVersion = Marshal.PtrToStringUni(version);
            Marshal.FreeCoTaskMem(version);
            LogInfo("using WebView2 runtime " + RuntimeVersion + ".");
            return true;
        }

        /// <summary>
        /// The browser every overlay is built from to begin with - transparent
        /// ones always. Only a windowed overlay that runs into a browser busy
        /// hosting transparent ones gets a second browser, and only then; see
        /// <see cref="EnvironmentFor"/>.
        /// </summary>
        private static bool createEnvironments()
        {
            // Never under the game folder, which may be read-only.
            string userData = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "WebOverlay", "BrowserData");
            if (!prepareFolder(userData))
            {
                startFailure(OverlayFailure.EnvironmentFailed,
                    "the browser's data folder could not be created: " + userData + ".");
                return false;
            }
            userDataFolder = userData;

            environment = createEnvironment(userData, "the browser environment",
                out environmentCallback, required: true);
            return environment != IntPtr.Zero;
        }

        private static IntPtr createEnvironment(string userData, string what,
            out ComCallback callback, bool required)
        {
            IntPtr created = IntPtr.Zero;
            bool refused = false;
            bool abandoned = false;
            callback = new ComCallback(WebView2Api.IID_EnvironmentCompleted, (int result, IntPtr pointer) =>
            {
                // A completion that arrives after this wait gave up must not
                // adopt the environment: nobody would ever use it, and the
                // browser process would idle until game exit. Not storing it -
                // and not adding a reference - lets the browser shut itself
                // down. `startFailed` covers only the very first environment,
                // which is why this has a flag of its own.
                if (abandoned || startFailed || stopping)
                {
                    LogWarning("a late environment arrived after the wait; discarding it.");
                    return WebView2Api.S_OK;
                }

                if (result == WebView2Api.S_OK && pointer != IntPtr.Zero)
                {
                    created = pointer;
                    Marshal.AddRef(created);
                }
                else
                {
                    refused = true;
                    report(required, OverlayFailure.EnvironmentFailed,
                        what + " failed, hr=0x" + result.ToString("X8") + ".");
                }

                return WebView2Api.S_OK;
            });

            int hr = WebView2Api.CreateCoreWebView2EnvironmentWithOptions(
                null, userData, IntPtr.Zero, callback.Pointer);
            if (hr != WebView2Api.S_OK)
            {
                report(required, OverlayFailure.EnvironmentFailed,
                    "could not request " + what + ", hr=0x" + hr.ToString("X8") + ".");
                return IntPtr.Zero;
            }

            // The completion may arrive re-entrantly or through the pump. This
            // wait runs on the overlay thread, never on Unity's - and a
            // shutdown mid-wait ends it instead of holding the thread for the
            // full timeout. It also ends the moment the completion reports a
            // failure: waiting out the full timeout after a definitive "no"
            // would delay every consumer's fallback by half a minute and bury
            // the real cause under a timeout message.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (created == IntPtr.Zero && !stopping && !refused
                && timer.Elapsed.TotalSeconds < 30)
            {
                pump();
                Thread.Sleep(5);
            }

            if (created == IntPtr.Zero && !stopping && !refused)
                report(required, OverlayFailure.EnvironmentFailed,
                    what + " did not start within 30 seconds.");
            // From here nothing owns what the completion might still bring.
            abandoned = created == IntPtr.Zero;
            return created;
        }

        /// <summary>
        /// A browser told to use a folder it cannot create shows the player a
        /// modal error box of its own, so the folder is made here first and a
        /// failure stays a log line.
        /// </summary>
        private static bool prepareFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                LogWarning("cannot use " + path + " as a browser data folder ("
                    + ex.GetType().Name + ": " + ex.Message + ").");
                return false;
            }
        }

        /// <summary>
        /// A required environment failing is why the library cannot work; an
        /// optional one failing is worth knowing but not a start failure.
        /// </summary>
        private static void report(bool required, OverlayFailure kind, string reason)
        {
            if (required)
                startFailure(kind, reason);
            else
                LogWarning(reason);
        }

        private static void drainWork()
        {
            drainQueue(work);
            // Creations wait while an environment is being created: that wait
            // pumps messages, and starting the next overlay from inside the
            // current one is exactly what the hold prevents. Commands are not
            // held - an overlay that is already up stays answerable.
            while (!creatingEnvironment && creations.TryDequeue(out Action creation))
            {
                run(creation);
                drainQueue(work);
            }
        }

        private static void drainQueue(ConcurrentQueue<Action> queue)
        {
            while (queue.TryDequeue(out Action action))
                run(action);
        }

        private static void run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogWarning("an overlay action failed (" + ex.GetType().Name + ": " + ex.Message + ").");
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
                    // Not before the environment attempt finished: the pump
                    // runs during it (it must, for the completion), and a
                    // Shutdown wake arriving then would execute queued window
                    // creations against a still-absent environment.
                    if (acceptingWork)
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
                    LogWarning("closing an overlay failed (" + ex.GetType().Name + ").");
                }
            }

            try
            {
                // Aliased when the second one could not be created; releasing
                // it twice would be a double free.
                bool shared = spareEnvironment == environment;
                if (spareEnvironment != IntPtr.Zero && !shared)
                    Marshal.Release(spareEnvironment);
                spareEnvironment = IntPtr.Zero;
                if (environment != IntPtr.Zero)
                {
                    Marshal.Release(environment);
                    environment = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                LogWarning("releasing the environments failed (" + ex.GetType().Name + ").");
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
                LogWarning("closing the dispatcher failed (" + ex.GetType().Name + ").");
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
