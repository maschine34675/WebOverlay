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
        private void Awake()
        {
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
            if (wanted != askedGameForCursor && GameCursorBridge.Show(wanted))
            {
                askedGameForCursor = wanted;
                return;
            }
            if (askedGameForCursor)
                return;

            // Fallback, for a game that does not have that lever: set it
            // directly and keep setting it. This is the flickering path, but a
            // flickering cursor beats an unreachable window.
            if (!wanted)
                return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // Whether the game is currently holding the cursor visible on this
        // plugin's behalf. Released again in OnDestroy, or the player would
        // keep a cursor nobody asked for.
        private bool askedGameForCursor;

        private void OnDestroy()
        {
            // Give the cursor back before going away, or the game keeps
            // showing one because of an overlay that no longer exists.
            if (askedGameForCursor)
            {
                GameCursorBridge.Show(false);
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
