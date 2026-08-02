using BepInEx;
using System;
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

            if (OverlayHost.GameWindow == IntPtr.Zero)
                this.Logger.LogWarning("the game window was not found; overlays will be unparented.");

            this.Logger.LogInfo(Branding.PluginName + " " + Branding.PluginVersion + " ready.");
        }

        private void OnDestroy()
        {
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
