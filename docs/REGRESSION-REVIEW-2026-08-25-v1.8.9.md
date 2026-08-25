# Regression-Review: WebOverlay v1.8.9

**Datum:** 25. August 2026  
**WebOverlay-Basis:** `b5182fcc006364a9090c6ffe6c84cf5702e178d7` (`main`, Commit-Message `Put the cursor diagnostic behind Advanced (v1.8.9)`)  
**Verglichen mit:** `6349a8aab8a4bfb31c6289a770c14e5de6d0b3e9` (`Ask the game for the cursor instead of overruling it (v1.8.3)`)  
**Art:** defect-first Regression-Review der Spanne 1.8.4–1.8.9 (`6349a8a`..`b5182fc`) — ohne Code-Änderungen

Verbraucher zur Fokus-/Cursor-Kette: `D:\SPT41\Development\ModProfiler` (gerahmtes Panel, ~80 % der Fläche, `FreeCursorWhileShown`, ab 1.8.8 `ClickThroughWhenUnfocused`, `EventDispatch.Manual`).

Die älteren Berichte bis v1.8.3 bleiben historische Momentaufnahmen. **Maßgeblich für den 1.8.9-Stand ist dieser Bericht.**

Die v1.8.3-Befunde WOV-1831 bis WOV-1834 sind in dieser Spanne geschlossen. `Branding.PluginVersion` ist `"1.8.9"`.

Commits in der Spanne: `68ff4c0` (State erst beim NavigationStart), `c129e48` (v1.8.4: Cursor-Rückgabe prüfen), Diagnostik-Serie (`e815cf1`–`4707587`), `27160a9` (v1.8.5: Vordergrund allein), `d82a186` (v1.8.6: Click-Through), `f117ce9` (v1.8.7: nur bei gehaltenem Cursor), `c007e8b` (v1.8.8: 8-Frame-Einigung), `b5182fc` (v1.8.9: Diagnostik hinter Advanced).

## 1. Kurzurteil

1.8.4–1.8.9 sind die Fokus-/Maus-Serie. Was in einem Raid wie „die Maus ist tot“ aussieht, war nacheinander: Cursor nach dem Schließen unsichtbar aber nicht captured (1.8.4), `FreeCursorWhileShown` verlor das Race zwischen `SetForegroundWindow` und `isVisible` (1.8.5), ein Panel über der Bildschirmmitte schluckte die Mausbewegung obwohl das Spiel den Vordergrund hatte (1.8.6), Click-Through machte das Panel in Menüs unbenutzbar (1.8.7), und ein von Menü und Spiel umkämpfter Cursor schrieb den Fensterstil zweimal pro Frame um (1.8.8). 1.8.9 versteckt die Diagnostik, die diese Unterscheidung überhaupt erst möglich gemacht hat.

ModProfiler ist genau dieser Consumer: großes gerahmtes Fenster, Hotkey, Spiel-Input stumm solange *sein* Fenster vorn ist (`ForegroundProbe` fragt das OS, nicht `Application.isFocused`). Ohne 1.8.5 bleibt der Cursor im Raid captured, während der Profiler die Befehle mutet — tot in beide Richtungen. Ohne 1.8.6–1.8.8 liegt die Mausbewegung auf dem Panel, sobald das Spiel wieder vorn ist. Das Gate verlangt deshalb `1.8.8`.

Kein P0, kein nativer Use-after-free im Happy Path. Click-Through setzt die Fenster-Alpha fest auf 255 und wischt damit `Opacity`. Ein vom Origin-Filter abgelehntes `Navigate` nach einer schon sichtbaren Seite räumt das Ziel komplett statt es wiederherzustellen.

**Gesamturteil:** den Raid-Pfad eines ModProfiler-artigen Panels ab 1.8.8 nicht als blockiert betrachten; WOV-1891 schließen, bevor jemand Click-Through mit `Opacity` kombiniert.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P2-Befunde

### WOV-1891 – Click-Through darf `Opacity` nicht auf voll deckend setzen

**Evidenz**

- `WebOverlay/OverlayWindow.cs:325-337` — beim Einschalten von `WS_EX_TRANSPARENT` wird `SetLayeredWindowAttributes(..., byte.MaxValue, LWA_ALPHA)` gerufen. Der Kommentar sagt ausdrücklich, die Alpha gehe „back to fully opaque“.
- `WebOverlay/OverlayWindow.cs:339-344` — beim Ausschalten bleibt `WS_EX_LAYERED` stehen, wenn `opacityAsAlpha() != 255`, die Attribute werden aber nicht auf den Mod-Wert zurückgesetzt.
- `WebOverlay/OverlayWindow.cs:2515-2522` / `2483-2486` — `Opacity < 1` ist der dokumentierte Weg, ein gerahmtes Fenster halbtransparent zu machen; derselbe Code setzt dort `LWA_ALPHA` auf `opacityAsAlpha()`.

**Regression**

`ClickThroughWhenUnfocused` ist für genau die gerahmten Panels gedacht, die auch `Opacity` nutzen können. Sobald das Spiel den Vordergrund hat (ModProfiler: Raid, Panel liegt über der Mitte), wird das Fenster voll deckend. Nach dem nächsten Fokus auf das Panel bleibt es voll deckend, weil niemand die Alpha zurückschreibt. ModProfiler setzt `Opacity` nicht (Default 1.0) — der Happy Path dort bleibt unberührt. Jeder Consumer, der beides setzt, verliert die Fade dauerhaft nach dem ersten Click-Through-Zyklus.

Die Attribute müssen gesetzt werden, sonst malt ein frisch gelayertes Fenster nichts. Sie müssen nicht 255 sein.

**Korrekturrichtung**

Beim Ein- und Ausschalten `SetLayeredWindowAttributes` mit `opacityAsAlpha()` (und Color-Key, falls das Fenster einen hat). 255 nur, wenn der Mod wirklich 1.0 verlangt.

### WOV-1892 – Filter-Abbruch nach einer schon sichtbaren Seite soll das vorige Ziel wiederherstellen, nicht `forgetTarget`

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1358-1376` — `Navigate` setzt `pageReady`/`pageLoaded` auf false, dann `forgetOnNavigationStart = retargeting`.
- `WebOverlay/OverlayWindow.cs:1163-1178` — bricht der Origin-Filter ab, werden `awaitingNavigationStart` und `forgetOnNavigationStart` gelöscht und **`forgetTarget()`** gerufen: `pendingUrl`/`htmlLoaded`/`targetHandedToBrowser` weg, ohne `restoreTarget`.
- `WebOverlay/OverlayWindow.cs:1361-1365` — ein synchrones HRESULT macht dasselbe Szenario mit `restoreTarget(previous)`: die alte Seite bleibt Ziel, `IsPageLoaded` bleibt wahr (Probe-Zeile 14).

**Regression**

WOV-1831 ist für das Hängen an `awaitingNavigationStart` geschlossen, und Probe N10–N12 (Zeilen 50–51) verlangen zu Recht, dass die *abgelehnte* URL nicht Ziel bleibt. Nach einer **bereits sichtbaren** Seite ist `forgetTarget` zu grob: das alte Dokument steht noch, Retain wird nicht vergessen — aber `IsPageLoaded` bleibt false, `currentDocumentIsTarget` ist false, Sends landen in der Outbox. Das nächste `LoadHtml`/`Navigate` gilt als erste Seite (`HandedOver` ist false), also als kein Retarget: die Outbox der Zwischenzeit läuft in die neue Seite.

Der Kommentar „Nothing was left behind“ trifft nur die allererste Navigation (kein Dokument). Für ein Retarget ist etwas zurückgeblieben, genau wie beim HRESULT. `restoreTarget` würde N11/N12 weiter erfüllen (die `file:`-URL ist auch dann nicht das Ziel) und `IsPageLoaded` nicht belügen.

**Korrekturrichtung**

Bei Filter-Cancel dasselbe wie bei HRESULT: voriges `TargetState` wiederherstellen, wenn eines existierte; `forgetTarget` nur, wenn es nichts zum Zurückkehren gibt (erster Versuch, wie `startPendingNavigation`).

## 4. P3-Befunde

### WOV-1893 – `ForegroundIsOverlay` testet `WantsFreeCursor`, nicht die Fensterzugehörigkeit

**Evidenz**

- `WebOverlay/OverlayHost.cs:428-438` — die Diagnostik fragt `window.WantsFreeCursor(foreground)`, also `FreeCursorWhileShown && window == foreground`.
- `WebOverlay/WebOverlayPlugin.cs:178-180` — ein Panel ohne `FreeCursorWhileShown` wird als `other` geloggt, obwohl es das Overlay ist.

Die Zeile existiert, um „welches Fenster ist vorn“ zu beantworten. Genau das Panel, das nur Click-Through gesetzt hat, fällt durch. Handle-Vergleich ohne die Cursor-Option.

### WOV-1894 – Loses zweites `<summary>` an `Describe` / `ForegroundWindow`

**Evidenz**

- `WebOverlay/OverlayWindow.cs:280-289` — der Cursor-Kommentar hängt über `Describe()`.
- `WebOverlay/OverlayHost.cs:398-407` — der `WantsFreeCursor`-Kommentar hängt über `ForegroundWindow()`.

Dieselbe Form wie WOV-1803 / WOV-1834.

## 5. Was in dieser Spanne nicht als Regression gilt

- **WOV-1831:** abgebrochenes `NavigationStarting` löscht `awaitingNavigationStart`. Probe-Zeile 50.
- **WOV-1832:** `Show(...) == false` setzt `askedGameForCursor` zurück; `OnDestroy` fällt auf Unity-Cursor zurück.
- **WOV-1833:** Shim-Completion navigiert nicht, wenn `targetHandedToBrowser`. Probe-Zeile 52. Das ist der `Ready` → `LoadHtml`-Pfad, den ModProfiler in `WebOverlayGate.Toggle` geht.
- **WOV-1834:** ein `<summary>` an `injectChannelShim`.
- **1.8.5:** `WantsFreeCursor` ist `window == foreground`. `Show()` macht sichtbar und nimmt den Vordergrund *bevor* `isVisible` geschrieben wird; Unity liest parallel. ModProfiler öffnet genau so (Toggle → `Show`/`SetForegroundWindow` in einem Rutsch) und mutet parallel die EFT-Befehle — ohne diesen Fix tot in beide Richtungen.
- **1.8.4:** eine Frame später `hidden && !Locked` → einmal `CursorLockMode.Locked`. Der globale `ToggleShowInGameCursorEvent` bleibt ein ungezähltes Flag (ModProfilers IMGUI-Pfad nimmt stattdessen den Input-Tree; der Web-Pfad nimmt die Library). Dokumentiert.
- **1.8.6–1.8.8:** `ClickThroughWhenUnfocused` default aus; an nur für gerahmte, nicht-transparente Fenster; nur solange `CursorCapturedProbe` (Unity: unsichtbar oder locked) acht Frames lang dasselbe sagt. `LateUpdate` schreibt den Stil nicht mehr. Probe-Zeilen 53–58. ModProfiler opt-in, Gate `1.8.8`, Hotkey bleibt der Rückweg — Click-Through kann das Panel nicht anklicken.
- ModProfilers `ForegroundProbe.OverlayInFront` ist jedes Fenster desselben Prozesses außer dem Spiel, nicht zwingend *dieses* Overlay. Das ist Consumer-Politik (Input sperren), kein Library-Vertrag. Solange das Spiel vorn ist, ist Click-Through an, der Blocker aus — die Maus erreicht das Spiel.
- Diagnostik hinter Advanced; `cfg`-Key bleibt. Cross-Thread-`SetWindowLongPtr` vom Unity-`Update` auf das Overlay-HWND ist bewusst (ein Wechsel pro Einigung, nicht 100×/s).
- `forgetOnNavigationStart` erst im erlaubten `NavigationStarting`: Retain überlebt ein abgelehntes Retarget, bis wirklich ein neues Dokument beginnt.

## 6. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. Gerahmtes Overlay mit `Opacity = 0.5` und `ClickThroughWhenUnfocused`: nach Spiel-vorn → Panel-vorn muss die Alpha wieder 128 sein, nicht 255.
2. Sichtbare Seite, dann `Navigate("file:///...")` (Filter-Cancel): `IsPageLoaded` muss wahr bleiben und Sends die alte Seite erreichen — analog Zeile 14, nicht analog N12 allein.
3. Diagnostik mit einem Panel, das nur `ClickThroughWhenUnfocused` setzt: Log muss `overlay` sagen, nicht `other`.
4. In-Game (ModProfiler): Raid, Panel auf, Spiel wieder vorn — Spieler kann drehen; Stash auf — Panel ist mit dem nächsten Klick bedienbar; F12-Menü — kein Stil-Flackern. Die Probe deckt die Mechanik, nicht EFT-Capture plus Harmony-Blocker.

## 7. Betroffene Dateien in der Spanne

| Datei | Rolle |
|---|---|
| `WebOverlay/OverlayWindow.cs` | `forgetOnNavigationStart`, Filter-Cancel, `WantsFreeCursor` ohne `isVisible`, `UpdateClickThrough` |
| `WebOverlay/OverlayHost.cs` | `CursorCapturedProbe`, 8-Frame-Einigung, `UpdateClickThrough`, Diagnostik-Beschreibungen |
| `WebOverlay/WebOverlayPlugin.cs` | Cursor-Rückgabe prüfen, Diagnostik, Click-Through nur aus `Update`, Config hinter Advanced |
| `WebOverlay/WebOverlays.cs` | `ClickThroughWhenUnfocused` |
| `WebOverlay/ConfigurationManagerAttributes.cs` | neu, duck-typed Advanced |
| `WebOverlay/Branding.cs` | `1.8.9` |
| `README.md`, `CHANGELOG.md`, `docs/FAULT-TESTS.md` | 1.8.4–1.8.9, Probe 50–58 |
| `tools/Probe/*` | `click-through`, Filter-Cancel, Ready-LoadHtml |

### Consumer (nicht im Diff, zur Fokus-Kette)

| Datei | Rolle |
|---|---|
| `ModProfiler/UI/WebOverlayGate.cs` | Minimum 1.8.8, `FreeCursorWhileShown`, `ClickThroughWhenUnfocused`, Manual-Pump |
| `ModProfiler/UI/WebPanelController.cs` | Sichtbarkeit vs. OS-Vordergrund, EFT-Input nur muten solange das Panel vorn ist |
| `ModProfiler/UI/ForegroundProbe.cs` | dieselbe OS-Frage wie die Library, bewusst nicht `Application.isFocused` |
| `ModProfiler/UI/CursorRequestNode.cs` | IMGUI-Fallback: Input-Tree statt globalem Flag |
