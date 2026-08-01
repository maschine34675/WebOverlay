using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace WebOverlay.Interop
{
    /// <summary>
    /// A COM object built by hand: a block of memory whose first field points at
    /// a vtable of four function pointers (IUnknown plus one Invoke).
    ///
    /// This exists because the official WebView2 wrapper cannot be used under
    /// Unity's Mono. The SDK's interfaces express inherited vtable slots with
    /// `_VtblGap` markers, which Mono ignores, so calls land on the wrong
    /// function - measured: it kills the process with no managed exception.
    /// Function pointers taken from delegates, on the other hand, Mono handles
    /// reliably.
    /// </summary>
    internal sealed class ComCallback : IDisposable
    {
        private const int S_OK = 0;
        private const int E_NOINTERFACE = unchecked((int)0x80004002);

        private static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        private readonly Guid interfaceId;
        private readonly Func<int, IntPtr, int> onCompleted;
        private readonly Func<IntPtr, IntPtr, int> onEvent;

        // Held as fields on purpose: the runtime does not keep a delegate alive
        // because native code holds its function pointer, and a collected one
        // is an access violation at the next callback.
        private readonly QueryInterfaceDelegate queryInterface;
        private readonly AddRefDelegate addRef;
        private readonly AddRefDelegate release;
        private readonly Delegate invoke;

        // Instances that outlive their Dispose because native code still held
        // them. Rooting them here keeps the delegates alive too - a leaked
        // vtable whose thunks got collected would crash just the same.
        private static readonly List<ComCallback> leaked = new List<ComCallback>();

        private IntPtr vtable;
        private int references = 1;
        private int disposed;

        /// <summary>Completion handler shape: Invoke(HRESULT, result).</summary>
        public ComCallback(Guid interfaceId, Func<int, IntPtr, int> handler)
            : this(interfaceId)
        {
            onCompleted = handler;
            invoke = (CompletedDelegate)onCompletedThunk;
            writeInvokeSlot();
        }

        /// <summary>Event handler shape: Invoke(sender, args).</summary>
        public ComCallback(Guid interfaceId, Func<IntPtr, IntPtr, int> handler)
            : this(interfaceId)
        {
            onEvent = handler;
            invoke = (EventDelegate)onEventThunk;
            writeInvokeSlot();
        }

        private ComCallback(Guid interfaceId)
        {
            this.interfaceId = interfaceId;
            queryInterface = onQueryInterface;
            addRef = onAddRef;
            release = onRelease;

            vtable = Marshal.AllocHGlobal(IntPtr.Size * 4);
            Marshal.WriteIntPtr(vtable, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(queryInterface));
            Marshal.WriteIntPtr(vtable, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(addRef));
            Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(release));

            Pointer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(Pointer, vtable);
        }

        public IntPtr Pointer { get; private set; }

        private void writeInvokeSlot()
        {
            Marshal.WriteIntPtr(vtable, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(invoke));
        }

        // Every thunk swallows exceptions: letting one unwind into Chromium's
        // native frames takes the process down.
        private int onQueryInterface(IntPtr self, ref Guid requested, out IntPtr result)
        {
            try
            {
                if (requested == interfaceId || requested == IID_IUnknown)
                {
                    result = self;
                    onAddRef(self);
                    return S_OK;
                }
            }
            catch
            {
            }

            result = IntPtr.Zero;
            return E_NOINTERFACE;
        }

        private uint onAddRef(IntPtr self)
        {
            try { return (uint)Interlocked.Increment(ref references); }
            catch { return 1; }
        }

        private uint onRelease(IntPtr self)
        {
            try { return (uint)Math.Max(0, Interlocked.Decrement(ref references)); }
            catch { return 0; }
        }

        private int onCompletedThunk(IntPtr self, int errorCode, IntPtr result)
        {
            try { return onCompleted(errorCode, result); }
            catch { return S_OK; }
        }

        private int onEventThunk(IntPtr self, IntPtr sender, IntPtr args)
        {
            try { return onEvent(sender, args); }
            catch { return S_OK; }
        }

        /// <summary>
        /// Releases the managed owner's reference. The memory is freed only
        /// when the native side holds no reference either - a completion that
        /// never arrived, or an event source that was not closed, still owns
        /// the pointer, and freeing memory native code can call is a process
        /// crash. Such instances are deliberately leaked (and rooted, so their
        /// delegates survive). Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            if (Interlocked.Decrement(ref references) > 0)
            {
                lock (leaked)
                    leaked.Add(this);
                return;
            }

            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
            if (vtable != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(vtable);
                vtable = IntPtr.Zero;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid requested, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CompletedDelegate(IntPtr self, int errorCode, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EventDelegate(IntPtr self, IntPtr sender, IntPtr args);
    }
}
