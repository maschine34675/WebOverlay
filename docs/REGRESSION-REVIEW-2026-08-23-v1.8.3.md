# Regression-Review: WebOverlay v1.8.3

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Its findings were addressed in the releases that
> followed - see `CHANGELOG.md`. It is kept as evidence, not as a
> description of the current library.

**Datum:** 23. August 2026  
**WebOverlay-Basis:** `6349a8aab8a4bfb31c6289a770c14e5de6d0b3e9` (`main`, Commit-Message `Ask the game for the cursor instead of overruling it (v1.8.3)`)  
**Verglichen mit:** `4b394c07c5e8d5a113f5e8ac7959cb25bb4edea0` (`Write down the soft-dependency rules and what a HUD has to decide itself`)  
**Art:** defect-first Regression-Review der Spanne 1.8.1–1.8.3 (`4b394c0`..`6349a8a`) — ohne Code-Änderungen

Die älteren Berichte bis v1.8.0 bleiben historische Momentaufnahmen. **Maßgeblich für den 1.8.3-Stand ist dieser Bericht.**

Die v1.8.0-Befunde WOV-1801 bis WOV-1803 sind in dieser Spanne geschlossen. `Branding.PluginVersion` ist `"1.8.3"`.

Commits in der Spanne: `ef5467f` (Probe-Pixelbedingung), `e7c2465` / `1625253` (v1.8.1: abgelehntes Retarget, Spare-Callback, Fehlerpfade), `c200995` (Runtime-Hinweis), `49306b1` (v1.8.2: Dokumentgeneration, Main-Thread-Antworten, erste Navigation wartet aufs Shim), `6349a8a` (v1.8.3: Cursor über das Spiel).

## 1. Kurzurteil

1.8.1–1.8.3 sind Nacharbeit an den Verträgen, die 1.8.0 neu eingeführt hat, plus den Cursor-Flackern. Ein synchron abgelehntes `Navigate`/`LoadHtml` lässt Retain und Outbox bei der Seite, die wirklich stehen bleibt. Ein zweiter Spare-Versuch `Dispose`t den vorigen Environment-Callback, bevor das Feld überschrieben wird. Seitenfragen und Host-Requests tragen die Dokumentgeneration; eine verspätete Antwort trifft nicht mehr die nächste Seite mit derselben Page-Id. `EventDispatch.MainThread` lässt Antworten über die Queue-Grenze, `Manual` hat eine eigene Ergebnis-Queue und flushs sie bei `Dispose`. Die erste Navigation wartet auf die Shim-Completion. Der Cursor wird über `ToggleShowInGameCursorEvent` gesetzt, nicht mehr jedes Frame gegen das Spiel.

Kein P0, kein nativer Use-after-free im Happy Path. Die neuen Warte-Flags um Navigation und Shim können Completions schlucken oder dieselbe Seite zweimal anstoßen, und der Cursor-Fallback greift nicht, sobald eine einmal erfolgreiche Spiel-Anfrage später fehlschlägt.

**Gesamturteil:** Retain, Spare-Retry und Request-Generation nicht als blockiert betrachten; WOV-1831 und WOV-1833 schließen, bevor die erste Navigation bzw. ein vom Filter verworfener Start als „die Seite kommt schon“ gelesen wird, und WOV-1832 bevor `FreeCursorWhileShown` nach einem Spiel-API-Bruch stecken bleibt.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P2-Befunde

### WOV-1831 – Abgebrochenes `NavigationStarting` darf `awaitingNavigationStart` nicht gesetzt lassen

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1262-1268` — `Navigate` setzt `awaitingNavigationStart` und nimmt ein `S_OK` von `Navigate` als „der Browser hat sie angenommen“.
- `WebOverlay/OverlayWindow.cs:1085-1106` — `onNavigationStarting` bricht eine nicht erlaubte URI ab und kehrt **zurück, bevor** das Flag gelöscht und die Generation erhöht wird. Nur eine durchgelassene Top-Level-Navigation räumt das Flag.
- `WebOverlay/OverlayWindow.cs:949-950` — `NavigationCompleted` in diesem Fenster ist ein No-op: keine Warnung, kein `pageReady`.
- `WebOverlay/OverlayWindow.cs:1174-1178` — ein Host aus `unmappedOrigins` (fehlgeschlagenes Virtual-Host-Mapping) ist genau so eine abgebrochene Top-Level-Navigation, obwohl `allowOrigin` in `Navigate` denselben Host schon auf die Allowlist gelegt hat.

**Regression**

1.8.2 ignoriert Completions zwischen `Navigate` und dem *erlaubten* `NavigationStarting`, damit eine ersetzte Navigation nicht `PageLoaded` und die Outbox der neuen Seite liefert. Ein Start, den der eigene Filter verwirft, erzeugt dieses Starting nie als erlaubt — oft aber ein fehlgeschlagenes Completed. Das Completed wird geschluckt. `IsPageLoaded` bleibt false, die neue Warnung aus 1.8.1 erscheint nicht, Completions bleiben stumm, bis irgendeine *spätere* erlaubte Navigation das Flag löscht.

Das ist nicht der Sync-HRESULT-Fall aus WOV-1801 (`restoreTarget` räumt das Flag). Es ist der asynchrone Abbruch: Mapping fehlgeschlagen und trotzdem `Navigate` auf diesen Host, oder eine URI ohne http(s)-Origin (`file:`, kaputte URL), die `Navigate` mit `S_OK` annimmt und Starting dann kassiert.

**Korrekturrichtung**

Bei `PutCancel` auf Top-Level `awaitingNavigationStart` zurücksetzen (Generation nicht erhöhen — das Dokument hat nicht gewechselt). Dieselbe Warnung wie beim fehlgeschlagenen Completed ist hier die ehrlichere Meldung als Stille.

### WOV-1832 – Schlägt `GameCursorBridge.Show(false)` fehl, gibt es keinen Fallback

**Evidenz**

- `WebOverlay/GameCursorBridge.cs:61-85` — `Show` verspricht `false`, damit der Aufrufer in diesem Frame selbst setzen kann; bei Exception wird `raise = null`.
- `WebOverlay/WebOverlayPlugin.cs:79-85` — Erfolg schreibt `askedGameForCursor` und kehrt zurück. Misserfolg ändert das Flag nicht. Steht es noch `true`, kehrt die Methode sofort zurück und erreicht den Unity-Fallback nicht.

**Regression**

1.8.3 fragt das Spiel einmal pro Zustandswechsel, damit Sichtbarkeit, Lock und Bitmap zusammenkommen. Nach einem erfolgreichen `Show(true)` ist `askedGameForCursor` wahr. Wird `Show(false)` danach false (Instance weg, Invoke wirft, Typ umbenannt), bleibt das Flag wahr. Jeder folgende Frame — inklusive „Overlay zu, Cursor wieder dem Spiel“ — kehrt bei `if (askedGameForCursor) return` zurück. Weder Unity-`Cursor.visible` noch ein zweiter Versuch über den toten `raise`-Zeiger räumt den Zustand. `OnDestroy` ruft `Show(false)` ohne Fallback auf denselben toten Pfad.

Genau das Gegenteil der in `Show` dokumentierten Fallbacksemantik.

**Korrekturrichtung**

Bei `Show(...) == false` `askedGameForCursor` auf false setzen und in den bestehenden Unity-Fallback fallen. `OnDestroy` denselben Fallback, wenn `Show(false)` scheitert.

### WOV-1833 – Shim-Completion darf eine schon angestoßene erste Navigation nicht ein zweites Mal starten

**Evidenz**

- `WebOverlay/OverlayWindow.cs:537-540` — ist das Shim noch nicht bestätigt, wird `navigationOwedToShim` gesetzt; `Ready` folgt trotzdem (Absicht: `Ready` heißt nicht „Seite geladen“).
- `WebOverlay/WebOverlays.cs:939-941` — `Navigate`/`LoadHtml` vom Consumer sind `Post`s, also Work-Items, keine synchronen Aufrufe aus `Ready`.
- `WebOverlay/OverlayHost.cs:696-704` — nach jedem Create wird `work` geleert: ein `Ready`-Handler, der sofort `LoadHtml` postet, läuft auf dem Overlay-Thread **nach** Create und typischerweise **vor** der COM-Completion des Shim.
- `WebOverlay/OverlayWindow.cs:1262-1269` / `1364-1371` — dieser `LoadHtml`/`Navigate` spricht den Browser selbst an und lässt `navigationOwedToShim` unberührt.
- `WebOverlay/OverlayWindow.cs:576-586` — die spätere Shim-Completion ruft `startPendingNavigation()` trotzdem auf, also noch einmal `Navigate`/`NavigateToString` auf dieselbe pending Zielseite.

**Regression**

1.8.2 wartet mit der *internen* ersten Navigation auf das Shim, damit `window.overlay` in der ersten Seite steht. Der übliche Consumer-Pfad ist: `Ready` → `LoadHtml`. Der läuft nicht durch `startPendingNavigation` beim Create, sondern durch die öffentliche API, und räumt die Schuld gegenüber dem Shim nicht. Die Completion navigiert erneut. Folge: zwei Top-Level-Starts, `documentGeneration` zweimal, `PageLoaded` zweimal, Retain zweimal, erste Skripte der Seite zweimal. Das Shim ist beim zweiten Start zwar da — der doppelte Start ist trotzdem ein beobachtbarer Vertragsbruch (`PageLoaded` einmal).

**Korrekturrichtung**

`navigationOwedToShim = false` in `Navigate`/`LoadHtml`, sobald der Browser die URI wirklich angenommen hat; oder `startPendingNavigation` no-op, wenn `targetHandedToBrowser` bzw. `awaitingNavigationStart` schon gesetzt ist.

## 4. P3-Befunde

### WOV-1834 – Zweites `<summary>` an `injectChannelShim`

**Evidenz**

- `WebOverlay/OverlayWindow.cs:550-567` — zwei `<summary>` hintereinander. Der erste (altes „vor den Seitenskripten“) liegt in der Luft; der zweite beschreibt das Warten auf die Completion.

Dieselbe Form, die 1.8.1 an `forgetPageState` / `replayRetained` beseitigt hat. Den ersten Block entfernen oder in den zweiten mergen.

## 5. Was in dieser Spanne nicht als Regression gilt

- **WOV-1801:** `forgetPageState` erst nach erfolgreichem `Navigate`/`NavigateToString`; Sync-Ablehnung ruft `restoreTarget` auf, ohne Retain/Outbox anzufassen. Probe-Zeilen 14 und 40.
- **WOV-1802:** `createEnvironment(..., ref callback)` `Dispose`t den vorigen Callback, bevor das Feld überschrieben wird; `ComCallback.Dispose` hängt nativ gehaltene Instanzen in die Leak-Liste.
- **WOV-1803:** Replay-Kommentar sitzt an `replayRetained`.
- Abgelehnte *erste* Navigation vor dem Browser: `forgetTarget()` statt einer Zielseite, die nie existiert hat. Nächstes `LoadHtml` ist kein Retarget.
- `Navigate`/`LoadHtml` auf die schon gezeigte Zielseite ist ein Reload, kein Retarget — Retain bleibt. Probe-Zeile 47.
- Dokumentgeneration an Page→Mod-Antworten und Outbox-Einträgen mit `RequestId`; Host→Page-Ids laufen weiter. Probe-Zeile 48 / Mode `generation`.
- `DispatchToMainThread(..., droppable: false)` für Antworten; Manual-Ergebnisse eigene Queue, Drain bei `Dispose` und zu Beginn von `PumpEvents`. Probe-Zeilen 44–45.
- `fail()` räumt `pageReady`/`pageLoaded` und begleicht offene Skript-Aufrufe; Renderer-Crash ebenso, verweigerter Reload ist terminal.
- Spare höchstens dreimal, Timeout 10 s — bewusst gegen Endloswarte auf einer Maschine, auf der der zweite Browser nie startet. Der Alias auf den Main-Browser bleibt abwesend.
- `DENY_CORS` statt `DENY` für Virtual Hosts: Inline-`LoadHtml` hat eine opake Origin; unter `DENY` käme die eigene Seite nicht an ihre Dateien. Die Doku sagt jetzt, was WebView2 wirklich sperrt (`fetch`/XHR, nicht `script`/`img`/`iframe`).
- `FreeCursorWhileShown` nur, wenn das Overlay Vordergrund ist. HUD mit `WS_EX_NOACTIVATE` unverändert sinnlos für diese Option.
- `tools/Probe` und die neuen FAULT-TESTS-Zeilen 40–49 sind kein Library-Vertrag.

## 6. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. `Navigate` auf einen Virtual-Host, dessen Mapping fehlgeschlagen ist (oder auf `file:`): `awaitingNavigationStart` muss wieder false sein, Completions und eine folgende gültige Navigation dürfen nicht schweigend liegen bleiben.
2. `Ready`-Handler, der sofort `LoadHtml` postet, während die Shim-Completion noch aussteht: genau ein `PageLoaded`, die Seite nicht zweimal.
3. `GameCursorBridge.Show(true)` einmal erfolgreich, danach `raise = null` (bzw. `Show(false)` false): der Unity-Fallback muss den Cursor wieder dem Spiel geben.
4. Spare-Timeout plus zweiter windowed Create — strukturell über `ref`/`Dispose` argumentiert, nicht von der Probe erzwungen (akzeptiert in FAULT-TESTS); bleibt Restrisiko.

## 7. Betroffene Dateien in der Spanne

| Datei | Rolle |
|---|---|
| `WebOverlay/OverlayWindow.cs` | Retain nach Ablehnung, `awaitingNavigationStart`, Dokumentgeneration, Shim-Warte, `fail()` räumt Skripte |
| `WebOverlay/OverlayHost.cs` | Spare-Limit/Timeout, `ref` Environment-Callback, `DispatchToMainThread(droppable:)` |
| `WebOverlay/WebOverlays.cs` | Manual-Ergebnisqueue, Main-Thread-Antworten nicht droppable |
| `WebOverlay/GameCursorBridge.cs` | neu: Spiel-Flag per Reflection |
| `WebOverlay/WebOverlayPlugin.cs` | Cursor über Bridge, Fallback, Release in `OnDestroy` |
| `WebOverlay/Interop/WebView2Api.cs` | `NavCompletedArgs_GetWebErrorStatus`, Kommentar zu `DENY_CORS` |
| `WebOverlay/Branding.cs` | `1.8.3` |
| `README.md`, `CHANGELOG.md`, `docs/FAULT-TESTS.md`, `docs/SOFT-DEPENDENCY.md` | 1.8.1–1.8.3, Probe 40–49 |
| `tools/Probe/*` | `failed-nav`, `generation`, Cursor-Fallback außerhalb des Spiels |
