# Regression-Review: WebOverlay v1.8.0

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Many of its findings were addressed in the releases
> that followed - see `CHANGELOG.md`; the ones still open recur in newer
> reports until a release closes them. It is kept as evidence, not as a
> description of the current library.

**Datum:** 22. August 2026  
**WebOverlay-Basis:** `4b394c07c5e8d5a113f5e8ac7959cb25bb4edea0` (`main`, Commit-Message `Write down the soft-dependency rules and what a HUD has to decide itself`)  
**Verglichen mit:** `1bf88ff8baccf35b18dba23bb8d8b7d437f2cc52` (`Stop one mod's HUD from breaking another mod's window`)  
**Art:** defect-first Regression-Review der Spanne 1.8.0 (`1bf88ff`..`4b394c0`) — ohne Code-Änderungen

Die älteren Berichte bis v1.7.0 bleiben historische Momentaufnahmen. **Maßgeblich für den 1.8.0-Stand ist dieser Bericht.**

Die v1.7.0-Befunde WOV-1701 bis WOV-1705 sind in `9f81905` geschlossen (eigene Create-Queue, kein Spare-Alias, `abandoned`, Kommentar, Zähler vor Close/Release). `FreeCursorWhileShown` hängt am Vordergrundfenster laut System, nicht an `Application.isFocused`. `Branding.PluginVersion` ist `"1.8.0"`.

Commits in der Spanne: `9f81905` (1.7.0-Review schließen + Cursor), `5abc0f9` (Browser-Datenordner vor dem Modal ablehnen), `b5ced7c` (Retain / LatestOnly / `EventDispatch.Manual`), `b356b71` (Probe nach `tools/Probe`), `4b394c0` (SOFT-DEPENDENCY, HUD-vs-Spielzustand, Version 1.8.0).

## 1. Kurzurteil

1.8.0 ist die Retain-/Latest-Wins- und Manual-Dispatch-Version, plus die 1.7.0-Nacharbeit. `Post(..., PostOptions.Retain)` merkt den letzten Wert pro Kanal und spielt ihn nach einem Renderer-Reload wieder ein; ein echtes Retarget (`LoadHtml` / `Navigate` weg von einer schon benannten Seite) vergisst ihn. `LatestOnly` kollabiert nur, solange die Library die Nachricht noch hält; die Seite kann `{ latest: true }` verlangen. `EventDispatch.Manual` plus `PumpEvents()` legt die Zustellung in den Frame des Mods. Ein unbrauchbarer Browser-Datenordner wird nicht mehr an WebView2 durchgereicht. Probe-Zeilen 35–39 und 36 (Spare-Ordner) sind PASS.

Kein P0, kein nativer Use-after-free im Happy Path. Die neuen Retain- und Spare-Retry-Pfade verlieren merkbare Konfiguration nach einer abgelehnten Navigation und können den vorigen Environment-Callback unrooted lassen, sobald ein zweiter Spare-Start den ersten überschreibt.

**Gesamturteil:** den Retain-Happy-Path (Reload nach Crash, erfolgreiches Retarget) nicht als blockiert betrachten; WOV-1801 schließen, bevor Mods Retain als „die Seite bleibt im angenommenen Zustand“ behandeln, und WOV-1802 bevor ein Spare-Timeout plus Retry in der Session vorkommt.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P2-Befunde

### WOV-1801 – Abgelehntes Retarget darf Retain nicht mitnehmen, ohne ihn wiederherzustellen

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1001-1027` — `Navigate` setzt `pendingUrl` zuerst, erkennt Retarget an der vorigen Zielseite, ruft `forgetPageState()` auf und stellt bei einem HRESULT-Fehler nur `restoreTarget(previous)` wieder her.
- `WebOverlay/OverlayWindow.cs:1084-1105` — `LoadHtml` ebenso.
- `WebOverlay/OverlayWindow.cs:1343-1355` — `forgetPageState` leert Outbox **und** `retained`.
- `WebOverlay/OverlayWindow.cs:1054-1082` — `TargetState` / `restoreTarget` kennen Url, Html, Flags — nicht `retained`.
- `WebOverlay/OverlayWindow.cs:1037-1044` — `checkNavigationResult` leert bei Ablehnung nochmals die Outbox, erwähnt Retain nicht.

**Regression**

Genau der Fall, für den `restoreTarget` existiert: eine synchron abgelehnte Navigation (ungültige URL, Inline-HTML über 2 MB) lässt das **alte** Dokument stehen. `IsPageLoaded` und spätere Sends gelten wieder für diese Seite (Probe Zeile 14). Retain ist der neue Zustand, der zu dieser Seite gehört — und er ist schon weg, bevor das HRESULT zurückkommt. Ein Renderer-Crash danach spielt nichts mehr ein; die Seite fällt auf ihre Defaults zurück, während der Mod weiter glaubt, die Konfiguration stehe. Das ist derselbe stille Verlust, den Retain in 1.8.0 schließen sollte, nur ausgelöst durch einen Fehlversuch statt durch einen Reload.

Die erste `Navigate`/`LoadHtml` ist nicht betroffen (`retargeting` ist false, Retain bleibt). Ein **erfolgreiches** Retarget soll Retain vergessen — das ist dokumentiert und von Probe `retained` (R6) abgedeckt.

**Korrekturrichtung**

`retained` (und nur das) in `TargetState` mitführen und in `restoreTarget` zurücklegen; oder `forgetPageState` erst nach erfolgreichem `Navigate`/`NavigateToString` aufrufen, nicht vor dem HRESULT. Die Outbox für die fehlgeschlagene neue Seite darf weiter weg — die gehörte nie der alten.

### WOV-1802 – Spare-Retry darf den vorigen Environment-Callback nicht unrooted lassen

**Evidenz**

- `WebOverlay/OverlayHost.cs:43` — ein Feld `spareEnvironmentCallback`.
- `WebOverlay/OverlayHost.cs:236-237` — jeder Spare-Versuch schreibt `out spareEnvironmentCallback`.
- `WebOverlay/OverlayHost.cs:244-252` — bei Misserfolg bleibt `spareEnvironment` `IntPtr.Zero`; der nächste windowed Create versucht es erneut (WOV-1702).
- `WebOverlay/OverlayHost.cs:542-601` — nach Timeout setzt `abandoned` die späte Completion auf Verwerfen, ohne `AddRef`. Die Completion selbst läuft weiter über denselben `ComCallback`.
- `WebOverlay/Interop/ComCallback.cs:30-41, 158-177` — die Delegates leben nur, solange die Managed-Instanz verwurzelt ist. `Dispose` hängt eine noch nativ gehaltene Instanz in `leaked`; ein bloßes Überschreiben des Feldes tut das nicht.

**Regression**

WOV-1703 ist für **ein** aufgegebenes Environment geschlossen: die späte Completion wird nicht mehr übernommen. Der Retry aus WOV-1702 macht daraus zwei Completions hintereinander. Der zweite `createEnvironment` ersetzt das Feld, ohne `Dispose` auf dem ersten Callback. Der erste bleibt nativ registriert (genau der Fall, für den `abandoned` gebaut wurde), ist managed aber nicht mehr verwurzelt. Sobald der GC die Instanz nimmt, sind die Function Pointer tot — und genau dann darf die verspätete Completion noch eintreffen.

Vor `9f81905` war dieser Pfad geschlossen: ein fehlgeschlagener Spare wurde auf den Main-Browser aliasiert, `createSpareEnvironment` kehrte beim nächsten Mal sofort zurück, das Feld blieb stehen.

**Korrekturrichtung**

Vor dem nächsten `out spareEnvironmentCallback` den bisherigen Callback `Dispose()`n (dann hält `leaked` ihn, bis native loslässt), oder die Callbacks in einer Liste behalten, bis ihre Completion oder der Shutdown sie beendet. Nicht das Feld allein als Root benutzen, sobald es überschrieben werden kann.

## 4. P3-Befunde

### WOV-1803 – XML-Kommentar an `forgetPageState` gehört zu `replayRetained`

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1343-1355` — zwei `<summary>` hintereinander. Der erste beschreibt das Replay vor der Outbox; der zweite das Vergessen. Die Methode darunter ist `forgetPageState`.

Der Replay-Text hängt in der Luft und beschreibt die falsche Methode. Den ersten Block an `replayRetained` ziehen.

## 5. Was in dieser Spanne nicht als Regression gilt

- WOV-1701: `drainWork` leert `work` immer; `creations` warten nur während `creatingEnvironment`. Probe Zeile 35.
- WOV-1702: kein Alias `spareEnvironment = environment`; `EnvironmentFor` fällt für diesen Versuch auf Main zurück, der nächste versucht den Spare neu.
- WOV-1703: `abandoned` in `createEnvironment` — für **einen** Versuch. Der Retry ohne Root ist WOV-1802, nicht dasselbe.
- WOV-1704 / WOV-1705: Kommentar an `createEnvironments` und Zähler vor Close/Release.
- Erste `LoadHtml`/`Navigate` verwirft Outbox und Retain nicht mehr — das ist die dokumentierte Korrektur, nicht ein Leck in die neue Seite.
- `LatestOnly` nur in der Outbox, `{ latest: true }` in der Seite: so beschrieben, Probe Zeile 38.
- `FreeCursorWhileShown` nur, wenn das Overlay das Vordergrundfenster ist. Ein composed HUD mit `WS_EX_NOACTIVATE` nimmt den Vordergrund nicht; die Option sagt das (`pointless for a HUD`). Der In-Game-Fix trifft gerahmte Fenster.
- `EventDispatch.Manual`: volle Queue verwirft Events mit einer Warnung — dokumentiert. `raiseResult` geht dieselbe Queue (ein Consumer, der nicht pumpt, bekommt weder Events noch Antworten). Dasselbe Muster wie die Main-Thread-Queue, die bei Überlauf ebenfalls `true` zurückgibt und nichts zustellt.
- `prepareFolder` prüft `CreateDirectory`, nicht Schreibrechte danach. Zeile 36 deckt „Ordner nicht anlegbar“ ab; ein existierender, aber nicht beschreibbarer Ordner ist ein Restpfad, kein neuer Vertrag.
- `tools/Probe` im Baum und `docs/SOFT-DEPENDENCY.md` ändern die Library nicht. Probe-Bugs sind keine Library-Regression.
- Getrenntes Spare-Profil bleibt dokumentiert.

## 6. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. `Post(..., Retain)`, dann `LoadHtml` über 2 MB bzw. `Navigate` auf eine abgelehnte URL, während eine Seite schon live ist: Retain muss die stehende Seite überleben und nach einem anschließenden Reload (oder `location.reload` wie in `retained`) wieder ankommen.
2. Spare-Environment-Timeout, danach ein zweites windowed `Create`: der erste Completion-Callback muss noch leben (kein Prozessabsturz), der zweite Versuch darf ein Environment bekommen.
3. `Dispatch = Manual`, Queue voll, dann `ExecuteScript` mit Result — heute still verworfen; wenn WOV-1801/1802 geschlossen sind und dieser Pfad bewusst bleibt, reicht die bestehende Overflow-Warnung als Doku.

## 7. Betroffene Dateien in der Spanne

| Datei | Rolle |
|---|---|
| `WebOverlay/OverlayHost.cs` | Create-Queue, Spare ohne Alias, `abandoned`, `prepareFolder`, Cursor am Vordergrundfenster |
| `WebOverlay/OverlayWindow.cs` | Retain / LatestOnly, `forgetPageState` nur beim Retarget, Zähler vor Close |
| `WebOverlay/WebOverlays.cs` | `PostOptions`, `EventDispatch.Manual`, `PumpEvents`, `PostCreation` |
| `WebOverlay/ChannelProtocol.cs` | `overlay.on(..., { latest: true })`, `off` über `handler.inner` |
| `WebOverlay/WebOverlayPlugin.cs` | Cursor ohne `Application.isFocused` |
| `WebOverlay/Branding.cs` | `1.8.0` |
| `README.md`, `CHANGELOG.md`, `docs/FAULT-TESTS.md`, `docs/SOFT-DEPENDENCY.md` | Doku 1.8.0, Probe 35–39 / 36 |
| `tools/Probe/*` | Probe-Host im Repository (nicht Teil des ausgelieferten DLL-Vertrags) |
