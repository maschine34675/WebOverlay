# Depending on this library without requiring it

Most mods that use Anvil-WebOverlay should still work when it is not
installed - with an IMGUI window, an external browser, or simply without the
extra view. That is a **soft dependency**, and on Mono it takes more than a
`try`/`catch`: a mod that gets it slightly wrong either dies at startup on a
machine without the library, or, worse, quietly breaks *other people's* mods.

Every rule below comes from something that actually went wrong. Do not build
the gate from prose: copy
[`examples/WebOverlayGate.cs`](../examples/WebOverlayGate.cs), which follows
every rule and is compiled verbatim on every release. Two shipping mods carry
the same gate in the wild - [CraftQueue](https://github.com/maschine34675/CraftQueue)
falls back to the external browser,
[ModProfiler](https://github.com/maschine34675/ModProfiler) to its own IMGUI
overlay.

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
| 1.8.5 | `FreeCursorWhileShown` on a panel shown and focused in one call - it existed from 1.7.0 but did not fire for that case, which is how a panel normally opens |
| 1.8.8 | `OverlayOptions.ClickThroughWhenUnfocused` - the member exists from 1.8.6, but 1.8.6 engaged it whenever the panel was not in front, leaving it unclickable in menus too |
| 1.9.0 | `VirtualHost.Access` / `HostAccess` |
| 1.10.0 | `ChannelsFailed`, `ChannelsAvailable`, `OverlayOptions.AllowDownloads` |
| 1.11.0 | `TryPost`, `Show(Action<VisibilityOutcome>)` / `Hide(Action<VisibilityOutcome>)`, `VisibilityOutcome` |

1.8.4 through 1.8.7 were never released; the first build a player can install
that has any of the 1.8.4-1.8.7 work is **1.8.8**. Gate on that rather than on
the version a member first appeared in - which is the second half of the rule,
and the more easily missed one:

> **A version gate answers whether a member behaves, not whether it exists.**

`ClickThroughWhenUnfocused` is the worked example. Asking only "is the property
there" would have said yes on 1.8.6 and handed the player a panel that could
not be clicked. That is also why an "apply this option if the library knows the
name" helper would not replace the comparison: the name existed two releases
before the behaviour did.

### The straddle body

When an option is newer than your floor, do not raise the floor - put that one
assignment in a body of its own that nothing calls below the version it needs:

```csharp
private static readonly Version ClickThroughSince = new Version(1, 8, 8);

// In the gate's own creation path, which is already behind IsUsable:
var options = new WebOverlay.OverlayOptions { /* things your floor has */ };
if (FoundVersion >= ClickThroughSince)
    letTheMouseThrough(options);

/// <summary>Set from a body nothing calls below 1.8.8.</summary>
[MethodImpl(MethodImplOptions.NoInlining)]
private static void letTheMouseThrough(object options)
{
    // object, NOT OverlayOptions: a parameter type is part of the SIGNATURE
    // and is resolved when something reflects over this type's methods, not
    // when the method is called. A library type there defeats the whole point
    // of the separate body. The cast lives in the body, resolved lazily.
    ((WebOverlay.OverlayOptions)options).ClickThroughWhenUnfocused = true;
}
```

Three consumers arrived at exactly these ten lines independently, and one of
them arrived at the typed-parameter version first and had it rejected by the
build check below. Two things about the shape are worth stating plainly:

- **It costs one body per version tier, not one per option.** Five options
  arriving in the same release share one body.
- **What it buys is that an older library loses one feature rather than the
  window.** Raise the floor instead when your fallback is cheap - a built-in
  overlay, say. Use the straddle body when your fallback is expensive, such as
  sending the player to an external browser.

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
reviewer. `tools/Audit-SoftDependency.ps1` in this repository is that check: a
Mono.Cecil pass over your compiled plugin that fails the build on **any field,
base type, interface, generic argument, return type or parameter that names a
type from `Anvil-WebOverlay`** - method bodies may, and only inside the gate
class - and on any gate body that touches the library without being
`NoInlining`, since an inlined body carries its references into a caller that
is not the gate.

```xml
<Target Name="AuditSoftDependency" AfterTargets="Build">
  <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(WebOverlayRepo)/tools/Audit-SoftDependency.ps1&quot; -AssemblyPath &quot;$(TargetPath)&quot; -GateType YourMod.UI.WebOverlayGate" />
</Target>
```

Every violation it finds is a real one; there are no acceptable exceptions,
which is what makes it worth automating. It caught the typed parameter in the
straddle body above, in this library's own consumer, after review had passed
it.

## While you are here: two related traps

- `Ready` and `Failed` are **latched**. Subscribing after the fact still fires
  them - possibly during the `+=` itself, on the overlay thread. Set your state
  before subscribing, and never let a handler read a field you assign further
  down the method. The opposite trap comes with the other dispatch modes: with
  `EventDispatch.MainThread` a late latched fire waits for the library's next
  frame, and with `EventDispatch.Manual` for your own next `PumpEvents()`. So a
  gate must not decide "the overlay failed, use the fallback" on the frame it
  subscribed - it has not been told yet.
- A failed overlay must not swallow the hotkey for the rest of the session.
  Latch `Failed` into a flag your gate checks first, and fall back from then on.
