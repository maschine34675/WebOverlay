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
        public static readonly Guid IID_NavigationStarting = new Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3");
        public static readonly Guid IID_NavigationCompleted = new Guid("d33a35bf-1c49-4f98-93ab-006e0533fe1c");
        public static readonly Guid IID_NewWindowRequested = new Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651");
        public static readonly Guid IID_PermissionRequested = new Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd");
        public static readonly Guid IID_ProcessFailed = new Guid("79e0aea4-990b-42d9-aa1d-0fcc2e5bc7f1");
        public static readonly Guid IID_Settings3 = new Guid("fdb5ab74-af33-4854-84f0-0a631deb5eba");
        public static readonly Guid IID_Settings4 = new Guid("cb56846c-4168-4d53-b04f-03b6d6796ff2");

        // ICoreWebView2Environment
        public const int Environment_CreateController = 3;

        // ICoreWebView2Controller
        public const int Controller_PutIsVisible = 4;
        public const int Controller_PutBounds = 6;
        public const int Controller_AddAcceleratorKeyPressed = 19;
        public const int Controller_NotifyParentWindowPositionChanged = 23;
        public const int Controller_Close = 24;
        public const int Controller_GetCoreWebView2 = 25;

        // ICoreWebView2Controller2 - only after QueryInterface(IID_Controller2).
        // Slot verified empirically: an opaque color visibly painted the view's
        // background and an alpha of 0 let the host window show through.
        public const int Controller2_PutDefaultBackgroundColor = 27;

        // ICoreWebView2
        public const int WebView_GetSettings = 3;
        public const int WebView_GetSource = 4;
        public const int WebView_Navigate = 5;
        public const int WebView_NavigateToString = 6;
        public const int WebView_AddNavigationStarting = 7;
        public const int WebView_AddNavigationCompleted = 15;
        // Anchored between the verified NavigationCompleted pair (15/16) and
        // ScriptDialogOpening (21); same handler and args as NavigationStarting.
        public const int WebView_AddFrameNavigationStarting = 17;
        public const int WebView_AddPermissionRequested = 23;
        public const int WebView_AddProcessFailed = 25;
        public const int WebView_ExecuteScript = 29;
        public const int WebView_PostWebMessageAsJson = 32;
        public const int WebView_PostWebMessageAsString = 33;
        public const int WebView_AddWebMessageReceived = 34;
        public const int WebView_AddNewWindowRequested = 44;
        public const int WebView_OpenDevToolsWindow = 51;

        // ICoreWebView2Settings
        public const int Settings_PutAreDefaultScriptDialogsEnabled = 8;
        public const int Settings_PutIsWebMessageEnabled = 6;
        public const int Settings_PutIsStatusBarEnabled = 10;
        public const int Settings_PutAreDevToolsEnabled = 12;
        public const int Settings_PutAreDefaultContextMenusEnabled = 14;

        // ICoreWebView2Settings3/4 - only after QueryInterface, see the header
        // rule above. Slots counted through Settings v1 (3-20) and Settings2's
        // UserAgent pair (21-22), confirmed against the C-style vtbl listing.
        public const int Settings3_PutAreBrowserAcceleratorKeysEnabled = 24;
        public const int Settings4_PutIsPasswordAutosaveEnabled = 26;
        public const int Settings4_PutIsGeneralAutofillEnabled = 28;

        // ICoreWebView2AcceleratorKeyPressedEventArgs
        public const int KeyArgs_GetKeyEventKind = 3;
        public const int KeyArgs_GetVirtualKey = 4;
        public const int KeyArgs_PutHandled = 8;

        // ICoreWebView2WebMessageReceivedEventArgs
        public const int MessageArgs_GetSource = 3;
        public const int MessageArgs_TryGetWebMessageAsString = 5;

        // ICoreWebView2NavigationStartingEventArgs
        public const int NavArgs_GetUri = 3;
        public const int NavArgs_PutCancel = 8;

        // ICoreWebView2NavigationCompletedEventArgs
        public const int NavCompletedArgs_GetIsSuccess = 3;

        // ICoreWebView2NewWindowRequestedEventArgs
        public const int NewWindowArgs_PutHandled = 6;

        // ICoreWebView2PermissionRequestedEventArgs
        public const int PermissionArgs_PutState = 7;
        public const int PermissionStateDeny = 2;

        // ICoreWebView2ProcessFailedEventArgs
        public const int ProcessFailedArgs_GetKind = 3;
        public const int ProcessFailedKindBrowserExited = 0;
        public const int ProcessFailedKindRenderExited = 1;
        public const int ProcessFailedKindRenderUnresponsive = 2;

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
