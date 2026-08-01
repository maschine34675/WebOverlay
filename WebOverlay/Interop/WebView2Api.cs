using System;
using System.Runtime.InteropServices;

namespace WebOverlay.Interop
{
    /// <summary>
    /// Vtable slot numbers and signatures for the parts of WebView2 this library
    /// uses, taken from the official WebView2.h.
    ///
    /// The `_VtblGap` rule: the SDK's managed wrapper expresses inherited vtable
    /// slots with `_VtblGap` markers that Mono ignores, so that wrapper must
    /// never be used - calls would dispatch to the wrong function. Members of
    /// versioned interfaces (Controller2 and later) are reachable, but only via
    /// an explicit QueryInterface for that interface's IID plus an absolute slot
    /// counted through every inherited member in WebView2.h - and each such slot
    /// must be proven by an observable effect (see the transparency probe notes
    /// in the README) before it is trusted.
    /// </summary>
    internal static class WebView2Api
    {
        public const int S_OK = 0;

        public static readonly Guid IID_EnvironmentCompleted = new Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d");
        public static readonly Guid IID_ControllerCompleted = new Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c");
        public static readonly Guid IID_AcceleratorKeyPressed = new Guid("b29c7e28-fa79-41a8-8e44-65811c76dcb2");
        public static readonly Guid IID_WebMessageReceived = new Guid("57213f19-00e6-49fa-8e07-898ea01ecbd2");
        public static readonly Guid IID_Controller2 = new Guid("c979903e-d4ca-4228-92eb-47ee3fa96eab");

        // ICoreWebView2Environment
        public const int Environment_CreateController = 3;

        // ICoreWebView2Controller
        public const int Controller_PutIsVisible = 4;
        public const int Controller_PutBounds = 6;
        public const int Controller_AddAcceleratorKeyPressed = 19;
        public const int Controller_Close = 24;
        public const int Controller_GetCoreWebView2 = 25;

        // ICoreWebView2Controller2 - only after QueryInterface(IID_Controller2).
        // Slot verified empirically: an opaque color visibly painted the view's
        // background and an alpha of 0 let the host window show through.
        public const int Controller2_PutDefaultBackgroundColor = 27;

        // ICoreWebView2
        public const int WebView_GetSettings = 3;
        public const int WebView_Navigate = 5;
        public const int WebView_NavigateToString = 6;
        public const int WebView_ExecuteScript = 29;
        public const int WebView_PostWebMessageAsJson = 32;
        public const int WebView_PostWebMessageAsString = 33;
        public const int WebView_AddWebMessageReceived = 34;
        public const int WebView_OpenDevToolsWindow = 51;

        // ICoreWebView2Settings
        public const int Settings_PutIsWebMessageEnabled = 6;
        public const int Settings_PutIsStatusBarEnabled = 10;
        public const int Settings_PutAreDevToolsEnabled = 12;
        public const int Settings_PutAreDefaultContextMenusEnabled = 14;

        // ICoreWebView2AcceleratorKeyPressedEventArgs
        public const int KeyArgs_GetKeyEventKind = 3;
        public const int KeyArgs_GetVirtualKey = 4;

        // ICoreWebView2WebMessageReceivedEventArgs
        public const int MessageArgs_TryGetWebMessageAsString = 5;

        public const int KeyEventKindKeyDown = 0;
        public const int KeyEventKindSystemKeyDown = 2;

        /// <summary>Binds a vtable slot of a COM object to a callable delegate.</summary>
        public static T Method<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(function, typeof(T)) as T;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateControllerDelegate(IntPtr self, IntPtr parentWindow, IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int PutBoolDelegate(IntPtr self, int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int PutBoundsDelegate(IntPtr self, RECT bounds);

        /// <summary>
        /// COREWEBVIEW2_COLOR is four bytes {A,R,G,B} passed by value, which on
        /// x64 travels as the little-endian integer A | R&lt;&lt;8 | G&lt;&lt;16 | B&lt;&lt;24.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int PutColorDelegate(IntPtr self, uint color);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetPointerDelegate(IntPtr self, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int StringDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int ExecuteScriptDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string script, IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int AddEventDelegate(IntPtr self, IntPtr handler, out long token);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetIntDelegate(IntPtr self, out int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetUIntDelegate(IntPtr self, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int NoArgsDelegate(IntPtr self);

        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
        public static extern int CreateCoreWebView2EnvironmentWithOptions(
            string browserExecutableFolder,
            string userDataFolder,
            IntPtr environmentOptions,
            IntPtr handler);

        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
        public static extern int GetAvailableCoreWebView2BrowserVersionString(
            string browserExecutableFolder,
            out IntPtr versionInfo);
    }
}
