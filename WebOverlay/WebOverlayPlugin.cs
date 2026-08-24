using BepInEx;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WebOverlay
{
    /// <summary>
    /// Library plugin. It hosts no interface of its own; it exists so mods can
    /// depend on one shared browser instead of each carrying its own, and so
    /// the game window and the overlay thread are found and shut down once.
    /// </summary>
    [BepInPlugin(Branding.PluginGuid, Branding.PluginName, Branding.PluginVersion)]
    public class WebOverlayPlugin : BaseUnityPlugin
    {
        /// <summary>
        /// Off by default: it is a tool for answering one specific kind of
        /// report - "the mouse stopped working while a panel was open" - which
        /// cannot be told from correct behaviour without knowing which window
        /// the OS has in front.
        /// </summary>
        internal static ConfigEntry<bool> DiagnoseCursor;

        private void Awake()
        {
            DiagnoseCursor = Config.Bind("Diagnostics", "Log cursor state", false,
                "Writes one line whenever the foreground window or the cursor state changes."
                + " Only useful when reporting a problem with the mouse.");
            OverlayHost.LogInfo = this.Logger.LogInfo;
            OverlayHost.LogWarning = this.Logger.LogWarning;
            OverlayHost.GameWindow = findGameWindow();
            // This component's Update is the main thread, so from here on
            // overlays created with DispatchOnMainThread can be served.
            OverlayHost.MainThreadPumpAvailable = true;
            // Only Unity knows the display mode, and the rest of the library
            // deliberately does not know Unity; this is how Show() can refuse
            // exclusive fullscreen without every consumer remembering to.
            OverlayHost.DisplayModeProbe = () => IsDisplayModeSupported;

            if (OverlayHost.GameWindow == IntPtr.Zero)
                this.Logger.LogWarning("the game window was not found; overlays will be unparented.");

            this.Logger.LogInfo(Branding.PluginName + " " + Branding.PluginVersion + " ready.");
        }

        /// <summary>
        /// Delivers the events of overlays that asked for main-thread
        /// dispatch. Nothing is queued unless a mod opted in, so this is an
        /// empty dequeue attempt per frame otherwise.
        /// </summary>
        private void Update()
        {
            OverlayHost.PumpMainThread();
            freeCursorIfWanted();
        }

        /// <summary>
        /// Only for the fallback path, where the cursor is set directly and
        /// the game keeps setting it back; asking the game for the cursor is a
        /// state change and needs no second call per frame.
        /// </summary>
        private void LateUpdate()
        {
            freeCursorIfWanted();
        }

        /// <summary>
        /// A game that captures the mouse keeps capturing it when another
        /// window of the same process takes the foreground, which would leave
        /// a framed overlay unreachable and the player stuck. While such an
        /// overlay is up and the game is not focused, the cursor goes back to
        /// the player; as soon as the game has the focus again the library
        /// stops touching it and the game takes it back on its own.
        /// </summary>
        private void freeCursorIfWanted()
        {
            // Whether the overlay is the window in front, asked of the OS -
            // Unity's own notion of focus does not have to agree, and the game
            // keeps its cursor regardless of what it thinks.
            bool wanted = OverlayHost.WantsFreeCursor();

            // Preferred: ask the game to want the cursor too, which is a state
            // and so only worth saying when it changes. Then the game stops
            // fighting - it is not being overruled, it agrees - and the one
            // write it does perform brings back the lock mode and the cursor
            // bitmap along with the visibility.
            if (wanted != askedGameForCursor)
            {
                if (GameCursorBridge.Show(wanted))
                {
                    askedGameForCursor = wanted;
                    // Giving it back is the half that can go wrong quietly, so
                    // it is watched for a few calls; see verifyCursorReturned.
                    cursorReturnChecks = wanted ? 0 : ReturnChecks;
                    return;
                }
                // The game would not take the request - it changed shape under
                // us, or went away. Stop believing it is holding the cursor,
                // or the fallback below would never be reached again and the
                // player would be left without one.
                askedGameForCursor = false;
            }
            else if (askedGameForCursor)
            {
                return;
            }

            if (cursorReturnChecks > 0)
                verifyCursorReturned();

            reportCursorState();

            // Fallback, for a game that does not have that lever: set it
            // directly and keep setting it. This is the flickering path, but a
            // flickering cursor beats an unreachable window.
            if (!wanted)
                return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>
        /// Says, once per change, who holds the foreground and what the cursor
        /// looks like. Off unless the consumer turns it on, and it prints only
        /// on a transition, so a session that behaves produces a handful of
        /// lines.
        ///
        /// This exists because a report of "the mouse stops working while a
        /// panel is open" cannot be told apart from its neighbours by reading
        /// the code: the pointer being pinned to the middle of the screen is
        /// what a correctly captured cursor looks like, and whether that is
        /// wrong depends entirely on which window the OS thinks is in front.
        /// Guessing at that from here has cost two wrong answers already.
        /// </summary>
        private void reportCursorState()
        {
            if (!DiagnoseCursor.Value)
                return;
            IntPtr foreground = OverlayHost.ForegroundWindow();
            bool overlayInFront = foreground != IntPtr.Zero
                && foreground != OverlayHost.GameWindow
                && OverlayHost.ForegroundIsOverlay(foreground);
            string state = (foreground == OverlayHost.GameWindow ? "game"
                    : overlayInFront ? "overlay" : "other")
                + " | visible=" + Cursor.visible
                + " | lock=" + Cursor.lockState
                + " | asked=" + askedGameForCursor;
            if (state == lastCursorState)
                return;
            lastCursorState = state;
            Logger.LogInfo("cursor: " + state);
        }

        private string lastCursorState;

        /// <summary>
        /// Checks, one frame after the cursor was given back, that the game
        /// really took it - and repairs the one state it cannot have meant.
        ///
        /// The game writes the cursor only when the live state disagrees with
        /// what it wants, which is what makes asking it so much better than
        /// overruling it. The cost is that a state it agrees with by accident
        /// is a state it never corrects. Hidden but not captured is exactly
        /// that: the pointer is invisible and the mouse moves it instead of
        /// turning the player, so the game looks frozen to the mouse while the
        /// keyboard still works. It can be reached because the lock mode and
        /// the visibility are set by different parties at different moments -
        /// the game itself reapplies the lock from the CURRENT visibility when
        /// the window regains focus, which is the same moment this release
        /// happens.
        ///
        /// One write, only when that state is actually observed. Hiding always
        /// means capturing, so there is exactly one right answer here.
        /// </summary>
        private void verifyCursorReturned()
        {
            CursorLockMode found = Cursor.lockState;
            if (Cursor.visible || found == CursorLockMode.Locked)
            {
                // Either the game has taken it back or it still intends to;
                // both are its business and neither needs help.
                cursorReturnChecks = 0;
                return;
            }
            // Watched over several calls rather than judged on the first: this
            // runs from Update and LateUpdate, and the game's own Update may
            // not have had its turn yet in this frame. Only a state that is
            // still wrong after all of them is one nobody is going to fix.
            if (--cursorReturnChecks > 0)
                return;
            Cursor.lockState = CursorLockMode.Locked;
            if (!warnedAboutCursorReturn)
            {
                warnedAboutCursorReturn = true;
                Logger.LogWarning("the cursor was left hidden but not captured after handing it back"
                    + " (lock mode was " + found + "); the game keeps the mouse now.");
            }
        }

        // Whether the game is currently holding the cursor visible on this
        // plugin's behalf. Released again in OnDestroy, or the player would
        // keep a cursor nobody asked for.
        private bool askedGameForCursor;
        private int cursorReturnChecks;
        private bool warnedAboutCursorReturn;

        // Update and LateUpdate both call in, so this is a handful of frames.
        private const int ReturnChecks = 8;

        private void OnDestroy()
        {
            // Give the cursor back before going away, or the game keeps
            // showing one because of an overlay that no longer exists.
            if (askedGameForCursor)
            {
                // If the game will not take it back, put the cursor where it
                // would have been anyway rather than leave one on screen for
                // an overlay that no longer exists.
                if (!GameCursorBridge.Show(false))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                askedGameForCursor = false;
            }
            // Nothing may be handed to the main thread after this component
            // stops running, or the events would queue up forever.
            OverlayHost.MainThreadPumpAvailable = false;
            OverlayHost.DisplayModeProbe = null;
            OverlayHost.Shutdown();
        }

        /// <summary>
        /// Enumerating this thread's windows beats searching globally: the
        /// thread already implies the process, the class filter drops Unity's
        /// hidden helper window, and unlike the foreground window it does not
        /// depend on the game being focused.
        /// </summary>
        private static IntPtr findGameWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(GetCurrentThreadId(), (hwnd, param) =>
            {
                var className = new StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() != "UnityWndClass")
                    return true;
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                    return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// A window over the game cannot work in exclusive fullscreen: showing
        /// it would minimise the game. Mods should check this and fall back.
        /// </summary>
        public static bool IsDisplayModeSupported =>
            Screen.fullScreenMode != FullScreenMode.ExclusiveFullScreen;

        /// <summary>
        /// The Win32 virtual-key code for a Unity key, or 0 when there is
        /// none. <see cref="OverlayOptions.CloseKeys"/> speaks virtual keys
        /// while a configurable hotkey speaks <see cref="KeyCode"/>, and every
        /// consumer was writing this table again - the second copy only
        /// because a review caught a close key hard-coded while the toggle key
        /// was rebindable.
        /// </summary>
        public static int VirtualKey(KeyCode key)
        {
            if (key >= KeyCode.A && key <= KeyCode.Z)
                return 0x41 + (key - KeyCode.A);
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return 0x30 + (key - KeyCode.Alpha0);
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return 0x60 + (key - KeyCode.Keypad0);
            if (key >= KeyCode.F1 && key <= KeyCode.F15)
                return 0x70 + (key - KeyCode.F1);

            switch (key)
            {
                case KeyCode.Backspace: return 0x08;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Return: return 0x0D;
                case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Pause: return 0x13;
                case KeyCode.CapsLock: return 0x14;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Space: return 0x20;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.End: return 0x23;
                case KeyCode.Home: return 0x24;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.Print: return 0x2C;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Numlock: return 0x90;
                case KeyCode.ScrollLock: return 0x91;
                default: return 0;
            }
        }

        /// <summary>
        /// Close keys for a configurable hotkey: Escape, which players expect,
        /// plus the key that opened the overlay, so the same press closes it
        /// again. Keys without a virtual-key code are left out.
        /// </summary>
        public static int[] CloseKeysFor(KeyboardShortcut shortcut)
        {
            var keys = new List<int> { 0x1B };
            int main = VirtualKey(shortcut.MainKey);
            if (main != 0 && main != 0x1B)
                keys.Add(main);
            return keys.ToArray();
        }

        private const uint GW_OWNER = 4;

        private delegate bool EnumThreadWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    }
}
