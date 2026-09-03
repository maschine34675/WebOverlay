// The complete soft-dependency gate: your mod keeps working when the library
// is NOT installed, without breaking anyone else's mod when it is not. Copy
// this file instead of inventing the pattern - three shipping mods converged
// on exactly this shape, and every deviation from it has failed in a specific,
// measured way (docs/SOFT-DEPENDENCY.md has the rules and the reasons).
//
// DO NOT simplify this to a try/catch around Create. On Mono, a type whose
// SIGNATURES mention the missing library breaks Assembly.GetTypes() for every
// mod that scans loaded assemblies - other people's mods fail, not yours.
//
// This file compiles against the installed library on every release (the
// packaging check builds it). Wire tools/Audit-SoftDependency.ps1 into your
// build to keep your copy honest.

using System;
using System.Runtime.CompilerServices;

namespace YourMod.UI
{
    /// <summary>
    /// The only class in your mod allowed to touch Anvil-WebOverlay types.
    /// Everything here that uses a library type is NoInlining and called
    /// strictly behind <see cref="IsUsable"/> - present AND new enough - so
    /// no library type is ever resolved unless both hold.
    /// </summary>
    internal static class WebOverlayGate
    {
        public const string LibraryGuid = "com.anvil.weboverlay";

        /// <summary>
        /// The newest member the always-compiled bodies below use decides
        /// this floor. An older library gets your fallback instead - and the
        /// log says which of the two happened, because silence here reads as
        /// a bug in your mod.
        /// </summary>
        public static readonly Version MinimumVersion = new Version(1, 11, 0);

        private static bool? loaded;
        private static Version foundVersion;
        private static object overlay;

        public static bool IsLoaded
        {
            get
            {
                if (loaded == null)
                {
                    BepInEx.PluginInfo info;
                    loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LibraryGuid, out info);
                    if (loaded.Value && info != null && info.Metadata != null)
                        foundVersion = info.Metadata.Version;
                }
                return loaded.Value;
            }
        }

        /// <summary>The library version actually loaded, or null when there is none.</summary>
        public static Version FoundVersion
        {
            get
            {
                bool unused = IsLoaded;
                return foundVersion;
            }
        }

        /// <summary>Present and new enough. Gate every call below on THIS, not on IsLoaded.</summary>
        public static bool IsUsable => IsLoaded && foundVersion != null && foundVersion >= MinimumVersion;

        /// <summary>
        /// Opens or toggles the overlay. Returns false when the caller should
        /// use its fallback (library missing, too old, or the browser
        /// unavailable).
        /// </summary>
        public static bool Toggle(Action<string> logWarning)
        {
            if (!IsUsable)
            {
                if (IsLoaded)
                    logWarning("Anvil-WebOverlay " + FoundVersion + " is older than "
                        + MinimumVersion + "; using the fallback.");
                return false;
            }
            return toggleCore(logWarning);
        }

        // From here down, library types may appear - in BODIES only. A method
        // body is JIT-resolved on first call; a signature is resolved when
        // anything reflects over this type's methods, which other mods do.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool toggleCore(Action<string> logWarning)
        {
            var handle = overlay as WebOverlay.IWebOverlay;
            if (handle != null)
            {
                handle.Toggle();
                return true;
            }

            handle = WebOverlay.WebOverlays.Create("Your panel", new WebOverlay.OverlayOptions
            {
                Width = 720,
                Height = 460,
                FreeCursorWhileShown = true,
                ClickThroughWhenUnfocused = true,
                DispatchOnMainThread = true,
            });
            if (handle == null)
            {
                logWarning("overlays are unavailable (is the WebView2 runtime installed?); using the fallback.");
                return false;
            }

            overlay = handle;

            // object, NOT the interface type: this local is captured by the
            // lambda below and becomes a FIELD of a compiler-generated closure
            // class - and a field's type is resolved whenever anything scans
            // this assembly's types, library installed or not.
            object created = handle;
            handle.Failed += () =>
            {
                logWarning("overlay failed; using the fallback from now on.");
                ((WebOverlay.IWebOverlay)created).Dispose();
                if (ReferenceEquals(overlay, created))
                    overlay = null;
            };

            handle.LoadHtml("<!doctype html><h1>Hello from the gate</h1>");
            return true;
        }

        /// <summary>
        /// The straddle body, for an option newer than your floor: set it from
        /// a body of its own that nothing calls below the version it needs, so
        /// an older library loses one feature rather than the window. The
        /// parameter is object for the same signature reason as above - the
        /// cast lives in the body, resolved lazily.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void setNewerOption(object options)
        {
            // Example shape. Guard the CALL with:
            //   if (FoundVersion >= new Version(1, 10, 0)) setNewerOption(options);
            ((WebOverlay.OverlayOptions)options).AllowDownloads = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Shutdown()
        {
            (overlay as WebOverlay.IWebOverlay)?.Dispose();
            overlay = null;
        }
    }
}
