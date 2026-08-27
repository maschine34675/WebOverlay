# Regression-Review: WebOverlay v1.5.0

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Its findings were addressed in the releases that
> followed - see `CHANGELOG.md`. It is kept as evidence, not as a
> description of the current library.

> **Bearbeitungsstand 2026-08-19:** WOV-1501 bis WOV-1504 sind vor der
> Veroeffentlichung von v1.5.0 behoben; der Bericht bleibt als Begruendung
> stehen. WOV-1501: der Close beantwortet jetzt jeden noch offenen
> Result-Callback mit `null` - die `Action<string>` haengt an einem
> `ScriptCall` neben dem `ComCallback`, und `Settle()` macht die Antwort
> einmalig, auch wenn spaeter doch noch eine Completion eintrifft (Probe
> `close-race`, Skript blockiert den Renderer 4 s). WOV-1502: Ergebnisse
> laufen ueber `raiseResult` statt `raise` und werden auch nach `Dispose`
> zugestellt - nur ein laufender Shutdown verschluckt sie weiterhin.
> WOV-1503: `fail()` und der Close melden `VisibilityChanged` nicht mehr,
> waehrend `OverlayHost.Stopping` gilt (Probe `shutdown-quiet`). WOV-1504:
> die Demo liest die Live-Zeile jetzt an `PageLoaded` statt am ersten
> Sichtbarwerden. Die Testluecken aus Abschnitt 7 stehen als Zeilen 19-21 in
> `docs/FAULT-TESTS.md`.

**Datum:** 19. August 2026 (Abend)  
**WebOverlay-Basis:** `aee3a9bd250b86eae8ff503f4d473277cef02434` (`main`, Commit-Message `v1.5.0: script results and a visibility event`)  
**Verglichen mit:** `ebd72d8c` (`Close the v1.4.0 review findings before release`)  
**Art:** defect-first Regression-Review des 1.5.0-Commits (`ExecuteScript`-Ergebnis, `VisibilityChanged`, `ComCallback`-Freigabe beim letzten Release) — ohne Code-Änderungen

Die älteren Berichte bis v1.0.0 und `docs/REGRESSION-REVIEW-2026-08-19.md` (v1.4.0, Befunde WOV-1401 bis WOV-1405 vor Release behoben) bleiben historische Momentaufnahmen. **Maßgeblich für den 1.5.0-Stand ist dieser Bericht.**

## 1. Kurzurteil

Wishlist-Einträge 5 und 6: `ExecuteScript` kann das JSON-Ergebnis zurückgeben, `VisibilityChanged` meldet echte Sichtbarkeitswechsel, `Closed` bleibt unverändert. Der Default-Pfad `ExecuteScript(script)` ohne Result-Callback nutzt weiter den geteilten Completion-Handler; bestehende Mods ohne die neuen Overloads sind wenig betroffen. `VisibilityChanged` filtert redundante Show/Hide korrekt.

Die `ComCallback`-Änderung (Dispose dekrementiert die Managed-Referenz, der letzte native Release gibt den Puffer frei) ist für One-Shot-Handler schlüssig und laut Probe (200 Scripts) nicht abgestürzt. Restrisiko bleibt ein spätes natives Event nach `Controller_Close` — genau das, weshalb Event-Handler zuvor bewusst geleakt wurden.

Der Release-Haken ist der neue Vertrag „der Result-Callback kommt genau einmal“: Teardown und Main-Thread-Dispatch können ihn verschlucken.

**Gesamturteil:** vor der 1.5.0-Verbreitung mindestens WOV-1501 und WOV-1502 beheben. WOV-1503 zeitnah nachziehen.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P1-Befunde

### WOV-1501 – In-flight-`ExecuteScript`-Callbacks werden beim Close nicht beantwortet

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1325-1340` — `CloseFromHost` schließt zuerst den Controller.
- `WebOverlay/OverlayWindow.cs:1397-1405` — danach `pendingScripts` nur `Dispose()`, Liste leeren, `clearOutbox()`.
- `clearOutbox()` beantwortet nur gepufferte, noch nicht abgeschickte Scripts.
- Die Completions in `pendingScripts` tragen den `Action<string>` nur in der Closure; `ComCallback.Dispose()` ruft ihn nicht auf.
- Kommentar an genau dieser Stelle: Completions kämen nicht mehr; Caller sollten erfahren, dass kein Ergebnis kommt.
- README / XML: Callback genau einmal, inklusive „overlay that closed“.

**Regression**

`ExecuteScript(script, result)` direkt vor `Dispose()` kann den Caller dauerhaft warten lassen, sofern WebView2 die Completion nicht reentrant in `Controller_Close` liefert. Genau das sollte die neue API laut Commit-Message verhindern.

**Korrekturrichtung**

Beim Close jeden noch nicht beantworteten Result-Callback mit `null` durch `answer()` schicken (einmalig, auch wenn `Controller_Close` doch noch eine Completion auslöst). Die `Action<string>` neben dem `ComCallback` halten, nicht nur in der Closure.

### WOV-1502 – Script-Ergebnisse nach `Dispose` nicht über den Event-Drop-Pfad schicken

**Evidenz**

- `WebOverlay/WebOverlays.cs:582-597` — Result-Callback wird als `value => raise(() => result(value))` auf den Overlay-Thread gelegt.
- `WebOverlay/WebOverlays.cs:418-427` — `raise` queued bei `DispatchOnMainThread` mit `if (disposed == 0)`.
- `WebOverlay/WebOverlays.cs:601-608` — `Dispose` setzt `disposed` sofort, `CloseFromHost` folgt erst per `Post`.

**Regression**

Für Events ist das Verwerfen nach `Dispose` dokumentiert. Für `ExecuteScript(script, result)` widerspricht es dem neuen Vertrag: eine Completion oder ein bereits gequeutes Ergebnis nach `Dispose` kommt nie an. Mit `DispatchOnMainThread` reicht „Dispose vor dem nächsten `Update`“.

**Korrekturrichtung**

Ergebnis-Callbacks auch nach `Dispose` genau einmal liefern (`null`, oder das schon kopierte JSON). Nicht denselben Drop wie bei `MessageReceived` / `Closed` verwenden.

## 4. P2-Befund

### WOV-1503 – `VisibilityChanged` während Shutdown nicht aus `fail()` feuern

**Evidenz**

- `WebOverlay/OverlayWindow.cs:190-199` — bei sichtbarem Fenster `setVisible(false)`, danach `Failed` nur wenn nicht `Stopping`.
- README: `VisibilityChanged` ist das Event für „ist mein Overlay sichtbar“, inklusive `false` wenn ein Failure das Overlay versteckt.

**Regression**

`Failed` unterdrückt bewusst Consumer-Fallbacks während des Game-Quit. `VisibilityChanged(false)` läuft in demselben `fail()` davor trotzdem. Wer daraus einen Fallback startet, bekommt denselben Shutdown-Effekt, den `Failed` vermeiden soll.

**Korrekturrichtung**

`setVisible` / `VisibilityChanged` in `fail()` ebenfalls an `Stopping` binden, analog zu `Failed`.

## 5. P3-Befund

### WOV-1504 – Demo-Script nicht am ersten `VisibilityChanged(true)` festmachen

**Evidenz**

- `WebOverlay.Demo/DemoPlugin.cs:315-323` — F10-Panel ruft `ExecuteScript` auf, sobald das Overlay sichtbar wird.
- `WebOverlay/OverlayWindow.cs:316-323` (Create): `Show()` / `setVisible(true)` läuft in `Create`, oft bevor das per `Post` folgende `LoadHtml` den Overlay-Thread erreicht.
- `LoadHtml` / `Navigate` leeren die Outbox per `clearOutbox()` und beantworten wartende Scripts mit `null`.

**Regression**

Ist die Seite beim ersten Show noch nicht das Ziel, kommt sofort `null`. Landet der Aufruf in der Outbox, verwirft das anschließende `LoadHtml` ihn. Die Live-Zeile wird nicht gelesen. `PageLoaded` (oder ein Script nach dem Load) wäre der passende Zeitpunkt.

**Korrekturrichtung**

Im Demo `ExecuteScript` an `PageLoaded` hängen, nicht an das erste Sichtbarwerden.

## 6. Was in diesem Commit nicht als Regression gilt

- `ExecuteScript(string)` ohne Result bleibt der geteilte Completion-Handler; bestehende Caller ändern sich nicht.
- `VisibilityChanged` no-opt bei redundantem Show/Hide; `Closed` feuert weiter bei jedem `Hide`.
- Destroy eines sichtbaren Overlays: ein `false`, kein Doppel-Event, wenn `fail()` schon versteckt hat (`CloseFromHost` liest `wasVisible` nach dem direkten `isVisible = false`).
- Vtable/Slot für `ExecuteScript` unverändert (29, Completion-IID wie bisher).
- `ComCallback.Dispose` dekrementiert die Managed-Ref; `onRelease` gibt frei, wenn Zähler 0 und `disposed != 0`. Das schließt das One-Shot-Leck, ohne Event-Handler vor `Close` freizugeben.
- Fault-Zeilen 17 und 18 in `docs/FAULT-TESTS.md` belegen Burst-Ergebnisse und Visibility-Übergänge im Probe-Host, nicht die Teardown-Lücken oben.

## 7. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. `ExecuteScript(script, result)` und sofort `Dispose()`, ohne auf die Completion zu warten — Callback muss genau einmal `null` (oder JSON) sein.
2. Dasselbe mit `DispatchOnMainThread = true`, `Dispose` vor dem Pump.
3. `fail()` eines sichtbaren Overlays während `OverlayHost.Stopping` — kein `VisibilityChanged`, das einen Fallback startet.
4. Demo-Pfad: erstes Show vor `LoadHtml` darf kein hängendes oder still verworfenes Result hinterlassen, wenn man ihn als Vorbild nimmt.

## 8. Betroffene Dateien im Commit

| Datei | Rolle |
|---|---|
| `WebOverlay/WebOverlays.cs` | `ExecuteScript(script, result)`, `VisibilityChanged`, `raise` für beides |
| `WebOverlay/OverlayWindow.cs` | Per-Call-Completion, Outbox `Pending.Result`, `setVisible`, Close-Pfad |
| `WebOverlay/Interop/ComCallback.cs` | Letzter Release gibt den Puffer frei |
| `WebOverlay.Demo/DemoPlugin.cs` | `VisibilityChanged` + Result-`ExecuteScript` am F10-Panel |
| `README.md`, `FORGE.md`, `docs/CONSUMER-API-WISHLIST-ANSWERS.md`, `docs/FAULT-TESTS.md` | Doku 1.5.0 |
