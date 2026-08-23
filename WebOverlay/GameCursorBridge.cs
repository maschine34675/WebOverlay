using System;
using System.Reflection;

namespace WebOverlay
{
    /// <summary>
    /// Asks the game to show the mouse cursor, through the game's own
    /// mechanism, without this library knowing anything about the game at
    /// compile time.
    ///
    /// Why not simply set <c>Cursor.visible</c>: the game's input manager
    /// decides once per frame what the cursor state should be, and writes it
    /// only when the live state disagrees. Setting the property is exactly
    /// what creates that disagreement, so it re-hides the cursor, the mod
    /// shows it again, and the pair alternate at frame rate - the flicker
    /// every overlay mod runs into. The game's own write also swaps the cursor
    /// bitmap for a fully transparent one, which a mod forcing only the
    /// property never restores.
    ///
    /// The way out is to make the game *want* the cursor visible. It has a
    /// flag for exactly that, OR-ed into its per-frame decision and set by a
    /// global event, and it uses that flag itself where its own world needs a
    /// cursor mid-raid. Then there is no disagreement, nothing alternates,
    /// and the one write the game does perform restores visibility, lock mode
    /// and bitmap together.
    ///
    /// All of it by reflection: this assembly references BepInEx and Unity and
    /// nothing else, and a game that renames these types must leave the
    /// library working - it falls back to setting the properties, which is
    /// what it did before and is still better than nothing.
    /// </summary>
    internal static class GameCursorBridge
    {
        private const string ControllerType = "EFT.GlobalEvents.GlobalEventsController";
        private const string EventType = "EFT.GlobalEvents.ToggleShowInGameCursorEvent";

        private static bool resolved;
        private static PropertyInfo controllerInstance;
        private static MethodInfo createEvent;
        private static MethodInfo raise;

        /// <summary>
        /// Whether the game exposes what this needs. False means a caller
        /// should fall back to setting the cursor properties itself.
        /// </summary>
        internal static bool Available
        {
            get
            {
                resolve();
                return raise != null;
            }
        }

        /// <summary>
        /// Tells the game to keep the cursor shown, or to stop. Only worth
        /// calling when the answer changes - it is a state, not a nudge.
        /// Returns false if the request could not be made, so the caller can
        /// fall back for this frame.
        /// </summary>
        internal static bool Show(bool show)
        {
            resolve();
            if (raise == null)
                return false;
            try
            {
                object controller = controllerInstance.GetValue(null, null);
                if (controller == null)
                    return false;
                object gameEvent = createEvent.Invoke(controller, null);
                if (gameEvent == null)
                    return false;
                raise.Invoke(gameEvent, new object[] { show });
                return true;
            }
            catch (Exception error)
            {
                // Once. A game that changed shape underneath this should cost
                // one log line, not one per frame.
                OverlayHost.LogWarning("could not ask the game to show the cursor ("
                    + error.GetType().Name + "); falling back to setting it directly.");
                raise = null;
                return false;
            }
        }

        private static void resolve()
        {
            if (resolved)
                return;
            resolved = true;
            try
            {
                Type controller = findType(ControllerType);
                Type gameEvent = findType(EventType);
                if (controller == null || gameEvent == null)
                    return;

                controllerInstance = controller.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                MethodInfo create = controller.GetMethod("CreateCommonEvent",
                    BindingFlags.Public | BindingFlags.Instance);
                // The one-argument form: the base class has a parameterless
                // Invoke that would raise the event without setting the flag.
                MethodInfo invoke = gameEvent.GetMethod("Invoke", new[] { typeof(bool) });
                if (controllerInstance == null || create == null || invoke == null
                    || !create.IsGenericMethodDefinition)
                    return;

                createEvent = create.MakeGenericMethod(gameEvent);
                raise = invoke;
            }
            catch (Exception error)
            {
                OverlayHost.LogInfo("the game's cursor event is not available ("
                    + error.GetType().Name + "); the cursor will be set directly.");
            }
        }

        private static Type findType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found;
                try
                {
                    found = assembly.GetType(fullName, false);
                }
                catch
                {
                    // A dynamic or half-loaded assembly is not worth failing over.
                    continue;
                }
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
