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

        /// <summary>
        /// The COM object handed to native code. Zero once freed - which only
        /// happens after both sides let go.
        /// </summary>
        public IntPtr Pointer;

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
            try
            {
                int count = Math.Max(0, Interlocked.Decrement(ref references));
                // Plain COM: the last release frees the object - but only once
                // the managed owner has let go too, because until then the
                // handler is still registered somewhere. By contract nothing
                // touches the pointer after a Release that returned zero, so
                // this is the one moment freeing it is safe. Without it every
                // one-shot completion handler would leak.
                if (count == 0 && disposed != 0)
                    freeMemory();
                return (uint)count;
            }
            catch
            {
                return 0;
            }
            finally
            {
                // The delegates that native code is calling live on this
                // instance, and the free above may have unrooted it.
                GC.KeepAlive(this);
            }
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
        /// crash. Such an instance stays rooted here until native releases it,
        /// which frees it then; one that is never released stays for the
        /// process lifetime, which is the safe end of the trade. Idempotent.
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

            freeMemory();
        }

        private void freeMemory()
        {
            // Rooted only while native code could still call in; once the
            // memory is gone there is nothing left to keep alive.
            lock (leaked)
                leaked.Remove(this);

            IntPtr pointer = Interlocked.Exchange(ref Pointer, IntPtr.Zero);
            if (pointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pointer);
            IntPtr table = Interlocked.Exchange(ref vtable, IntPtr.Zero);
            if (table != IntPtr.Zero)
                Marshal.FreeHGlobal(table);
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
