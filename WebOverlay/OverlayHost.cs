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
        /// <summary>
        /// One queued command. The owner is the window a droppable command
        /// was posted for, so its share of the queue can be given back when
        /// the command runs; an obligation - a Dispose, a page answer - has
        /// none, because it is never counted against a share.
        /// </summary>
        private struct WorkItem
        {
            public OverlayWindow Owner;
            public Action Action;
        }

        private static readonly ConcurrentQueue<WorkItem> work = new ConcurrentQueue<WorkItem>();

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
        private static int spareAttempts;

        /// <summary>
        /// How often a second browser is asked for before the library stops
        /// trying. Enough to ride out a transient failure, few enough that a
        /// machine where it cannot work does not pay the wait every time.
        /// </summary>
        private const int SpareAttemptLimit = 3;
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

        /// <summary>
        /// Turns on the window-lifecycle reporting used to answer "the panel is
        /// up but the library thinks it is hidden". Set from the plugin's
        /// Diagnostics switch; costs nothing while off.
        /// </summary>
        internal static volatile bool Diagnose;

        /// <summary>
        /// Test seam: makes the next channel-shim completion report failure.
        /// A real browser never rejects the library's own shim on demand, and
        /// the failure path that tells a consumer its channels are dead must
        /// not be the one path in this library nothing can prove. Set only by
        /// the probe, via reflection; nothing in production writes it.
        /// </summary>
        internal static volatile bool SimulateShimRejection;

        internal static void LogDiagnostic(string line)
        {
            if (Diagnose)
                LogInfo(line);
        }

        /// <summary>
        /// Whether pages should report their own problems. Read once per
        /// OVERLAY, when its shim is registered - the browser replays that one
        /// script for every document afterwards, so an overlay that already
        /// exists keeps whatever it was created with, reload or not.
        /// </summary>
        internal static volatile bool DiagnosePage;

        private static readonly System.Diagnostics.Stopwatch pageDiagnosticClock =
            System.Diagnostics.Stopwatch.StartNew();
        private static long pageDiagnosticWindowStart;
        private static int pageDiagnosticsInWindow;

        /// <summary>
        /// One line for something the page said went wrong. Rate-limited on
        /// purpose: a page can throw once per frame, and an instrument that
        /// floods the log is not one anybody reads.
        /// </summary>
        /// <remarks>
        /// The notice that reports are being held back is written the moment
        /// the limit is passed, not when the next one happens to arrive after
        /// the window expires - a burst that stops would otherwise leave its
        /// own count unreported forever, and the count would be attributed to
        /// whichever window spoke next. The budget is shared by every overlay,
        /// which is deliberate: what is being protected is one log.
        /// </remarks>
        internal static void LogPageDiagnostic(string title, string text)
        {
            if (!DiagnosePage || text == null)
                return;
            const int PerWindow = 5;
            const int Longest = 500;
            long now = pageDiagnosticClock.ElapsedMilliseconds;
            if (now - pageDiagnosticWindowStart >= 5000)
            {
                pageDiagnosticWindowStart = now;
                pageDiagnosticsInWindow = 0;
            }
            pageDiagnosticsInWindow++;
            if (pageDiagnosticsInWindow < PerWindow)
            {
                // The page caps its own text, but the channel is reachable by
                // any script on the page, so the cap that matters is this one.
                LogInfo("page (" + title + "): "
                    + (text.Length > Longest ? text.Substring(0, Longest) + "..." : text));
            }
            else if (pageDiagnosticsInWindow == PerWindow)
            {
                LogInfo("page (" + title + "): reporting a lot; further reports are held back"
                    + " for a few seconds.");
            }
        }

        /// <summary>Forgets the rate-limit budget, so a fresh session starts quiet.</summary>
        internal static void ResetPageDiagnostics()
        {
            pageDiagnosticWindowStart = pageDiagnosticClock.ElapsedMilliseconds;
            pageDiagnosticsInWindow = 0;
        }

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

        /// <summary>
        /// The overlay thread's command queue is bounded in two tiers. The
        /// ceiling is the process's memory bound, like MainThreadQueueLimit -
        /// at the measured ~9,600 messages per second it is close to half a
        /// second of maximum-rate traffic already waiting. Below it, each
        /// window has a share (<see cref="WindowShare"/>), and that is what
        /// makes a mod posting in a hot loop cost itself rather than every
        /// mod in the process: the queue is one queue, shared by every overlay
        /// of every mod, so before 1.11.0 the first mod to fill it had every
        /// other mod's next Show, Hide or Post refused. Now the flooder is
        /// refused at its share while everyone else still enters; what they
        /// cannot be spared is the wait behind the flooder's backlog, since it
        /// is one queue drained by one thread - about a tenth of a second at
        /// the measured rate. The share is generous by the same measure that
        /// bounds the outbox: no healthy consumer has a hundred commands
        /// waiting, let alone a thousand.
        /// </summary>
        private const int WorkQueueLimit = 4096;
        internal const int WindowShare = 1024;
        private static int workQueued;
        private static int workOverflowWarnedAt;

        /// <summary>
        /// How often an overflow warning may repeat while the condition
        /// lasts: once, then at most once a minute. A burst poster that fills
        /// and drains its share five times a second must not write five lines
        /// a second, and a mod that floods again an hour later must be named
        /// again - one stamp serves both.
        /// </summary>
        internal const int OverflowWarnIntervalMilliseconds = 60000;

        /// <summary>
        /// Rate-limits one warning through a tick stamp: zero means never
        /// warned, and the stored value is kept odd so a genuine tick count
        /// can never read as zero. Safe from any thread - the caller that
        /// wins the exchange is the one that logs.
        /// </summary>
        internal static bool ShouldWarnAgain(ref int warnedAt)
        {
            int now = System.Environment.TickCount | 1;
            int last = Volatile.Read(ref warnedAt);
            if (last != 0 && unchecked(now - last) < OverflowWarnIntervalMilliseconds)
                return false;
            return Interlocked.CompareExchange(ref warnedAt, now, last) == last;
        }

        /// <summary>
        /// Creations are not bounded, deliberately: refusing one would need a
        /// failure path into a window that does not exist yet, and each entry
        /// is one consumer-held handle, so the consumer's own memory grows at
        /// least as fast as this queue. Past the threshold it is named in the
        /// log instead - a mod creating overlays in a loop is a bug report
        /// waiting to be written, and the line writes most of it.
        /// </summary>
        private const int CreationBacklogThreshold = 64;
        private static int creationsQueued;
        private static int creationBacklogWarned;

        internal static void PostCreation(Action action)
        {
            if (stopping)
                return;
            if (Interlocked.Increment(ref creationsQueued) > CreationBacklogThreshold
                && Interlocked.Exchange(ref creationBacklogWarned, 1) == 0)
            {
                LogWarning("more than " + CreationBacklogThreshold + " overlay creations are waiting"
                    + " - is a mod creating overlays in a loop?");
            }
            creations.Enqueue(action);
            wake();
        }

        /// <summary>
        /// Fire-and-forget work for one window: dropped, with a warning, when
        /// that window's share or the queue's ceiling is full. Returns true
        /// while the game is shutting down although nothing is queued then -
        /// every path here accepts and swallows on the way out, and that
        /// uniform answer is what keeps a refused Request or ExecuteScript
        /// from waking a consumer's fallback during teardown. For work that
        /// carries an obligation - an answer somebody is waiting for, a
        /// Dispose that releases native state - use <see cref="Post"/>, which
        /// never drops and is never counted against a share.
        /// </summary>
        internal static bool TryPost(OverlayWindow owner, Action action)
        {
            if (stopping)
                return true;
            if (owner != null && !owner.TakeCommandSlot())
                return false;
            if (Interlocked.Increment(ref workQueued) > WorkQueueLimit)
            {
                Interlocked.Decrement(ref workQueued);
                owner?.ReleaseCommandSlot();
                if (ShouldWarnAgain(ref workOverflowWarnedAt))
                    LogWarning("the overlay command queue is full (" + WorkQueueLimit
                        + "); commands are being dropped - are several mods posting in a loop?");
                return false;
            }
            work.Enqueue(new WorkItem { Owner = owner, Action = action });
            wake();
            return true;
        }

        internal static void Post(Action action)
        {
            if (stopping)
                return;
            // Counted against the ceiling but never refused, and never against
            // a window's share: the count of obligations is bounded by the
            // calls that created them, so this cannot run away - and dropping
            // a Dispose would leak a native window, dropping an answer would
            // break "answered exactly once".
            Interlocked.Increment(ref workQueued);
            work.Enqueue(new WorkItem { Owner = null, Action = action });
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

            // A failed attempt is deliberately not remembered as "the main
            // browser will do" - one bad moment must not become the old defect
            // for the rest of the session. But trying forever is its own
            // defect: every windowed overlay would hold the creation queue for
            // the full timeout on a machine where the second browser can never
            // start. So it is retried a few times and then left alone.
            if (spareAttempts >= SpareAttemptLimit)
            {
                LogWarning("not asking for a second browser again after " + spareAttempts
                    + " failed attempts; this overlay will fail to open while a transparent one is up.");
                return;
            }

            LogInfo("a transparent overlay is open, so this windowed overlay needs a second browser.");

            // Checked here rather than left to the browser: when WebView2
            // cannot create its data folder it puts a modal error box on the
            // player's screen, which is not something a mod should be able to
            // cause. If the folder is not usable, no browser is asked for.
            string folder = userDataFolder + SpareSuffix;
            if (!prepareFolder(folder))
            {
                // Counted as an attempt like any other, so a folder that will
                // never be usable stops producing this line every time.
                spareAttempts++;
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
                // Ten seconds, not the thirty the first browser gets. This one
                // is optional and the queue of waiting overlays is held while
                // it starts, so a machine where it will never come up must not
                // cost half a minute per windowed overlay. The browser it
                // clones is already running, so ten is generous.
                spareEnvironment = createEnvironment(folder,
                    "the second browser", ref spareEnvironmentCallback, required: false,
                    timeoutSeconds: 10);
            }
            finally
            {
                creatingEnvironment = false;
                spareAttempts++;
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
        internal static bool DispatchToMainThread(Action action) =>
            DispatchToMainThread(action, droppable: true);

        /// <summary>
        /// Hands work to the game's main thread. Events may be dropped when the
        /// queue is full - that is what the limit is for - but an answer
        /// somebody is waiting for may not: the count of outstanding answers is
        /// bounded by the calls that asked for them, so letting those past the
        /// limit cannot run away. Dropping one instead would break the
        /// "answered exactly once" contract, and delivering it inline would run
        /// a handler that expects the main thread on the overlay thread, which
        /// for a handler touching Unity objects is worse than either.
        /// </summary>
        internal static bool DispatchToMainThread(Action action, bool droppable)
        {
            // During shutdown the game is past caring, and a queued handler
            // could start a fallback while everything is being torn down.
            // Asked before the pump check on purpose: the plugin takes the
            // pump away just before it asks for the shutdown, and answering
            // "no pump" then would hand the event to the caller to run inline
            // on the overlay thread - the exact wake-up this swallow exists to
            // prevent.
            if (stopping)
                return true;

            if (action == null || !mainThreadPumpAvailable)
            {
                if (action != null && Interlocked.Exchange(ref mainThreadWarned, 1) == 0)
                    LogWarning("no main-thread pump is running, so events stay on the overlay thread"
                        + " (DispatchOnMainThread works inside the game only).");
                return false;
            }

            if (Interlocked.Increment(ref mainThreadQueued) > MainThreadQueueLimit && droppable)
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
                catch (Exception ex)
                {
                    // Assuming "supported" is the safe answer for a window,
                    // but a probe that throws is a probe that never refuses -
                    // which is how the fullscreen refusal went dead for every
                    // release before 1.11.0, when the plugin's probe read a
                    // Unity property off Unity's thread. Said under Diagnose
                    // so it can never be silent again.
                    LogDiagnostic("the display-mode probe threw " + ex.GetType().Name
                        + "; assuming the display mode is supported.");
                    return true;
                }
            }
        }

        /// <summary>
        /// Which window the OS says is in front, for a caller that wants to
        /// report it. Zero when nothing is.
        /// </summary>
        internal static IntPtr ForegroundWindow() => GetForegroundWindow();

        /// <summary>
        /// Every live overlay window with the handle it actually has, so a
        /// report can say which one the foreground failed to match.
        /// </summary>
        internal static string DescribeOverlayWindows()
        {
            var text = new System.Text.StringBuilder();
            lock (windows)
            {
                foreach (OverlayWindow window in windows)
                {
                    if (text.Length > 0)
                        text.Append(',');
                    text.Append(window.Describe());
                }
            }
            return text.Length == 0 ? "none" : text.ToString();
        }

        /// <summary>
        /// Whether the foreground window belongs to an overlay of ours. A
        /// handle comparison and nothing else: this answers "which window is
        /// in front", so what that window asked for must not enter into it.
        /// </summary>
        internal static bool ForegroundIsOverlay(IntPtr foreground)
        {
            lock (windows)
            {
                foreach (OverlayWindow window in windows)
                {
                    if (window.Is(foreground))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the game currently holds the mouse - locked to a point and
        /// hidden, the way it is while the player is looking around. Only Unity
        /// knows, and the rest of this library deliberately does not know
        /// Unity, so the plugin registers it; the same bridge the fullscreen
        /// guard uses. Null means "assume it does", which is the safe end: the
        /// overlay then behaves as it did before this was asked.
        /// </summary>
        internal static Func<bool> CursorCapturedProbe;

        /// <summary>
        /// How many frames in a row the answer has to disagree with what is
        /// believed before the belief changes. Two mods writing the cursor in
        /// the same frame - one showing it, the game hiding it again - make
        /// the raw answer alternate at frame rate, and acting on that would
        /// rewrite the window style twice a frame forever. Neither reading is
        /// wrong; the state is simply contested, and the honest response to a
        /// contested state is to keep the last settled one.
        /// </summary>
        private const int CapturedAgreement = 8;

        private static bool capturedBelief = true;
        private static int capturedDisagreements;

        private static bool cursorCaptured()
        {
            bool now = askCursorCaptured();
            if (now == capturedBelief)
            {
                capturedDisagreements = 0;
                return capturedBelief;
            }
            // An alternating answer never gets a run of its own, so this never
            // reaches the threshold and the belief stands - which is the point.
            if (++capturedDisagreements < CapturedAgreement)
                return capturedBelief;
            capturedDisagreements = 0;
            capturedBelief = now;
            LogDiagnostic("cursor capture settled on " + (now ? "held by the game" : "free"));
            return capturedBelief;
        }

        private static bool askCursorCaptured()
        {
            Func<bool> probe = CursorCapturedProbe;
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

        /// <summary>
        /// Lets the mouse through overlays that asked for it while the game is
        /// in front. Called once per frame; each window only acts on a change.
        /// </summary>
        internal static void UpdateClickThrough()
        {
            IntPtr foreground = GetForegroundWindow();
            // Only while the game actually holds the mouse. In a menu the
            // cursor is free and the game has no use for the middle of the
            // screen, so letting the mouse through there would cost a
            // clickable window and buy nothing. Call this once per frame: it
            // advances the agreement count that keeps a contested answer from
            // flipping the window style back and forth.
            bool captured = cursorCaptured();
            lock (windows)
            {
                foreach (OverlayWindow window in windows)
                    window.UpdateClickThrough(foreground, captured);
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
                ref environmentCallback, required: true);
            return environment != IntPtr.Zero;
        }

        private static IntPtr createEnvironment(string userData, string what,
            ref ComCallback callback, bool required, int timeoutSeconds = 30)
        {
            // The slot may already hold the handler of an attempt that timed
            // out. Native code can still call that one - the whole point of
            // the `abandoned` flag below - and overwriting the field would
            // leave it unrooted while it is still reachable from native code,
            // so its thunks would be collected out from under the call.
            // Dispose hands it to the leak list, which roots it until native
            // lets go. `ref` rather than `out` so this cannot be forgotten at
            // a call site.
            if (callback != null)
            {
                callback.Dispose();
                callback = null;
            }

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
                && timer.Elapsed.TotalSeconds < timeoutSeconds)
            {
                pump();
                Thread.Sleep(5);
            }

            if (created == IntPtr.Zero && !stopping && !refused)
                report(required, OverlayFailure.EnvironmentFailed,
                    what + " did not start within " + timeoutSeconds + " seconds.");
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
            drainCommands();
            // Creations wait while an environment is being created: that wait
            // pumps messages, and starting the next overlay from inside the
            // current one is exactly what the hold prevents. Commands are not
            // held - an overlay that is already up stays answerable.
            while (!creatingEnvironment && creations.TryDequeue(out Action creation))
            {
                Interlocked.Decrement(ref creationsQueued);
                run(creation);
                drainCommands();
            }
        }

        private static void drainCommands()
        {
            while (work.TryDequeue(out WorkItem item))
            {
                // Given back before the command runs, as the count was taken
                // before it was queued: the slot measures what is waiting.
                Interlocked.Decrement(ref workQueued);
                item.Owner?.ReleaseCommandSlot();
                run(item.Action);
            }
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
