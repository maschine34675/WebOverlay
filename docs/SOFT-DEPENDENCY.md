# Depending on this library without requiring it

Most mods that use Anvil-WebOverlay should still work when it is not
installed - with an IMGUI window, an external browser, or simply without the
extra view. That is a **soft dependency**, and on Mono it takes more than a
`try`/`catch`: a mod that gets it slightly wrong either dies at startup on a
machine without the library, or, worse, quietly breaks *other people's* mods.

Every rule below comes from something that actually went wrong. Two shipping
gates follow all of them and are worth reading as references:

- `CraftQueue.Client/UI/WebOverlayGate.cs` - falls back to the external browser
- `ModProfiler/UI/WebOverlayGate.cs` - falls back to its own IMGUI overlay

## Put every reference in one class

One `internal static class WebOverlayGate` is the only file in your plugin
allowed to name a `WebOverlay.*` type. Everything else calls the gate and gets
back plain values. This is not tidiness: the rules below are checkable only if
there is one place to check.

## 1. Every gate member is `NoInlining`, and is called only behind the check

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
public static bool Toggle(string html, Action<string> logWarning)
{
    var handle = overlay as WebOverlay.IWebOverlay;
    ...
}
```

Mono resolves a method body the first time that method runs, not when its type
loads. So a body full of `WebOverlay` types is harmless on a machine without
the library - as long as it never runs. That is the whole trick, and inlining
breaks it: an inlined body is compiled as part of *its caller*, which is not
behind the check. `MethodImplOptions.NoInlining` keeps each body where the
guard can protect it.

The guard itself must not touch a library type. `BepInEx.Bootstrap.Chainloader.PluginInfos`
is BepInEx, so it is always safe:

```csharp
public const string LibraryGuid = "com.anvil.weboverlay";

private static bool? loaded;

public static bool IsLoaded
{
    get
    {
        if (loaded == null)
            loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LibraryGuid);
        return loaded.Value;
    }
}
```

## 2. Handles captured by a lambda must be typed `object`

This is the rule that costs other mods, and it is invisible in review unless
you know it.

```csharp
handle.Failed += () =>
{
    ((WebOverlay.IWebOverlay)created).Dispose();   // cast in the BODY
};
```

with `created` declared as:

```csharp
object created = handle;                           // NOT IWebOverlay
```

A lambda that captures a local turns that local into a **field** of a
compiler-generated closure class in your assembly. Fields are resolved when the
type loads, not lazily like method bodies - so a field of type
`WebOverlay.IWebOverlay` makes that closure class unloadable when the library
is absent. Your plugin still starts, because nothing constructs the closure.
The damage lands on everyone else: any mod that calls `Assembly.GetTypes()`
over the loaded assemblies now gets a `ReflectionTypeLoadException` from
*your* plugin.

That is not hypothetical. WTT-ClientCommonLib enumerates types across loaded
assemblies; one closure field of a library type aborted its custom item-parent
registration, and every affected item's stack size silently fell back to 1.
The mod that caused it worked fine.

The same applies to fields, base types, interfaces, generic arguments and
method **signatures** - parameters and return types. Only method bodies are
lazy. So a gate method may use library types inside it, but must not take or
return one.

## 3. Gate on a minimum version, not on presence

Releases of this library are additive: 1.4 through 1.8 added members and
changed nothing a consumer depended on. That is convenient, and it means
presence is the wrong question. A gate body that calls a 1.6 member fails at
JIT time on a 1.3 install for exactly the same reason a missing assembly does -
the member cannot be resolved - and it fails at the moment the player presses
the key, not at startup.

```csharp
/// Named channels with request/reply arrived in 1.6.0, main-thread dispatch
/// and classified failures in 1.4.0; this gate uses all of them, so an older
/// library gets the IMGUI overlay instead.
public static readonly Version MinimumVersion = new Version(1, 6, 0);

private static Version foundVersion;

public static bool IsLoaded
{
    get
    {
        if (loaded == null)
        {
            loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LibraryGuid, out BepInEx.PluginInfo info);
            if (loaded.Value)
                foundVersion = info.Metadata.Version;
        }
        return loaded.Value;
    }
}

/// Present and new enough - the only state in which the gate bodies may run.
public static bool IsUsable => IsLoaded && foundVersion != null && foundVersion >= MinimumVersion;
```

Log the mismatch once, at info level, and say what the player can do:
"Anvil-WebOverlay 1.3.0 is installed; this needs 1.6.0 or newer - using the
built-in window instead." Silence here reads as a bug in your mod.

Set `MinimumVersion` from the newest member you actually use:

| At least | For |
|---|---|
| 1.4.0 | `OverlayOptions.VirtualHosts`, `Failure`/`FailureMessage`, `PageLoaded`/`IsPageLoaded`, main-thread dispatch |
| 1.5.0 | `ExecuteScript(script, result)`, `VisibilityChanged` |
| 1.6.0 | `Post(channel, payload)`, `Request`/`OnRequest`, `SetShape`, `SetBounds` |
| 1.7.0 | `OnRequest` with a deferred `reply`, `IWebOverlay.Transparency`, `InjectTheme`, `FreeCursorWhileShown`, `WebOverlayPlugin.VirtualKey` / `CloseKeysFor` |
| 1.8.0 | `PostOptions.Retain` / `LatestOnly`, `OverlayOptions.Dispatch`, `PumpEvents()` |

The page-side shim comes from the installed library, not from your files, so
`overlay.on(..., { latest: true })` and `overlay.setShape` follow the same
table.

## 4. Declare the soft dependency to BepInEx

```csharp
[BepInDependency(WebOverlayGate.LibraryGuid, BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
```

Without it your plugin may load first, and the library's own `Update` - which
is what pumps main-thread event dispatch - would run after yours in the frame.
With it, load order is settled and the flag keeps the dependency optional.

## 5. Check it at build time

The rules above are mechanical, so let the build enforce them rather than a
reviewer. A short Mono.Cecil pass over your compiled plugin: **no field, base
type, interface, generic argument or method signature anywhere in the assembly
may reference `Anvil-WebOverlay`** - method bodies may, and only inside the
gate class. Every violation this finds is a real one; there are no acceptable
exceptions, which is what makes it worth automating.

## While you are here: two related traps

- `Ready` and `Failed` are **latched**. Subscribing after the fact still fires
  them - possibly during the `+=` itself, on the overlay thread. Set your state
  before subscribing, and never let a handler read a field you assign further
  down the method.
- A failed overlay must not swallow the hotkey for the rest of the session.
  Latch `Failed` into a flag your gate checks first, and fall back from then on.
