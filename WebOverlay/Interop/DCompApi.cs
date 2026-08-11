using System;
using System.Runtime.InteropServices;

namespace WebOverlay.Interop
{
    /// <summary>
    /// The sliver of DirectComposition the composed overlay needs: a device
    /// (no D3D required - WebView2 brings the content), a target bound to the
    /// window, and one visual the browser draws into.
    ///
    /// Slots verified against the Windows SDK dcomp.h by two independent
    /// readers, and proven by the composition probe (device creation, visual
    /// tree, per-pixel alpha, synthetic input all observed working). dcomp.h
    /// is C++-only and MSVC reverses consecutive overload PAIRS in the binary
    /// vtable (SetOffsetX/Y, SetTransform, SetClip) - none of the members
    /// used here are overloaded, so the counted slots hold.
    /// </summary>
    internal static class DCompApi
    {
        public static readonly Guid IID_DesktopDevice = new Guid("5F4633FE-1E08-4CB8-8C75-CE24333F5602");

        // IDCompositionDesktopDevice : IDCompositionDevice2 : IUnknown.
        // (The v1 IDCompositionDevice is a SEPARATE lineage with different
        // slots - never mix them.)
        public const int Device_Commit = 3;
        public const int Device_CreateVisual = 6;
        public const int Device_CreateTargetForHwnd = 24;

        // IDCompositionTarget - SetRoot is its only method.
        public const int Target_SetRoot = 3;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateTargetForHwndDelegate(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);

        /// <summary>
        /// renderingDevice may be null: such a device can commit a visual tree
        /// (all this library needs) but not create GPU surfaces of its own.
        /// </summary>
        [DllImport("dcomp.dll")]
        public static extern int DCompositionCreateDevice2(
            IntPtr renderingDevice,
            [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
            out IntPtr device);
    }
}
