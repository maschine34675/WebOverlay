# Regression-Review: WebOverlay v1.7.0

> **Bearbeitungsstand 2026-08-22:** WOV-1701 bis WOV-1705 sind vor der
> Veroeffentlichung von v1.7.0 behoben. WOV-1701: Overlay-Erzeugung liegt in
> einer eigenen Queue, nur sie wartet auf einen startenden Browser - Kommandos
> an bereits offene Overlays laufen weiter (Probe `spare-browser`, Zeile 35).
> WOV-1702: ein fehlgeschlagener zweiter Browser wird nicht mehr als
> "Hauptbrowser genuegt" gemerkt; der naechste Versuch startet neu.
> WOV-1703: `createEnvironment` hat ein eigenes `abandoned`-Flag, eine
> verspaetete Completion wird verworfen statt ohne Besitzer festgehalten.
> WOV-1704: Kommentar korrigiert. WOV-1705: beide Zaehler werden vor den
> COM-Aufrufen dekrementiert, die werfen koennen.
>
> Zusaetzlich aus dem In-Game-Test: `FreeCursorWhileShown` haengt nicht mehr an
> `Application.isFocused`, sondern am Vordergrundfenster laut System - Unitys
> Fokusbegriff muss damit nicht uebereinstimmen.

**Datum:** 22. August 2026  
**WebOverlay-Basis:** `1bf88ff8baccf35b18dba23bb8d8b7d437f2cc52` (`main`, Commit-Message `Stop one mod's HUD from breaking another mod's window`)  
**Verglichen mit:** `d889b27ead2321bdefa4146bfb41adf1a2e3068c` (`Deferred answers, page environment, cursor and display guards`)  
**Art:** defect-first Regression-Review des 1.7.0-Commits (zweites WebView2-Environment, wenn ein fensterndes Overlay in einen Browser mit composed Views soll) — ohne Code-Änderungen

Die älteren Berichte bis v1.6.0 bleiben historische Momentaufnahmen. **Maßgeblich für den 1.7.0-Stand ist dieser Bericht.**

Hinweis: `Branding.PluginVersion` ist in diesem Commit nicht geändert; die Zeile steht bereits auf `1.7.0` (Eltern-Commit). Inhaltlich ist `1bf88ff` der 1.7.0-Fix.

## 1. Kurzurteil

WebView2 lehnt ein windowed `CreateController` ab, solange derselbe Browser nur composed/transparente Views hat (`ERROR_INVALID_STATE`). Zwei Environments mit demselben User-Data-Ordner teilen den Browser — deshalb ein zweites Environment mit Ordner `BrowserData-windowed`, nur für den Kollisionsfall, und nur für das fensternde Overlay. HUDs-only, Windows-only, oder Fenster vor HUD bleiben bei einem Browser. Probe-Zeilen 32–34 (beide Reihenfolgen plus Footprint) sind PASS.

Die Zählung `composedControllers` / `windowedControllers` und `EnvironmentFor` bilden die gemessene Regel ab: Spare nur wenn composed > 0 und windowed-in-main == 0. Aliasing bei fehlgeschlagenem Spare verhindert Double-Free im Shutdown.

Kein P0, kein nativer Use-after-free im Happy Path. Die neuen Warte- und Fehlerpfade des Spare-Browsers können alle Overlay-Kommandos einfrieren, einen fehlgeschlagenen Spare dauerhaft auf den kaputten Main-Browser legen und ein verspätetes Environment leaken.

**Gesamturteil:** den gemessenen Success-Pfad (HUD, dann Panel) nicht als blockiert betrachten; WOV-1701 und WOV-1702 vor der 1.7.0-Verbreitung schließen.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P2-Befunde

### WOV-1701 – Overlay-Work-Queue nicht komplett halten, während der Spare-Browser startet

**Evidenz**

- `WebOverlay/OverlayHost.cs:197-206` — `createSpareEnvironment` setzt `creatingEnvironment` und wartet in `createEnvironment` bis zu 30 s (`pump` + `Sleep`).
- `WebOverlay/OverlayHost.cs:565-571` — `drainWork` kehrt bei gesetztem Flag sofort zurück, ohne die Queue zu leeren.
- `pump()` dispatcht `WM_APP_WORK`; `drainWork` ist dann ein No-op, die Work-Items bleiben liegen.
- `Post`, `Navigate`, `Hide`, `Dispose`, `ExecuteScript` laufen alle über dieselbe Queue.

**Regression**

Genau der Kollisionsfall (QuestMarkers-HUD schon da, ModProfiler-Fenster öffnet) startet den zweiten Browser. Solange der startet, sind **alle** Overlays kommandotaub — inklusive des schon sichtbaren HUDs (keine `Post`s, kein Hide). Die Fensterprozedur läuft weiter (Maus), die Managed-API nicht. Typisch Sekunden, im Timeout 30 s. Der Hold soll nur verschachtelte *Creates* während des Pumps verhindern.

**Korrekturrichtung**

Nur neue `Create`-Work-Items zurückhalten, oder die Queue nach dem Spare-Wait zuverlässig drainen (der extra `PostMessage` reicht nicht, solange `Create` selbst noch in `drainWork` steckt — das ist unkritisch — aber jedes *andere* Item muss durch). Alternativ `creatingEnvironment` nur um COM-Completion, nicht um `drainWork`.

### WOV-1702 – Fehlgeschlagenen Spare-Start nicht auf den Main-Browser festnageln

**Evidenz**

- `WebOverlay/OverlayHost.cs:208-214` — `spareEnvironment == IntPtr.Zero` nach dem Versuch: Log, dann `spareEnvironment = environment` (Alias).
- `WebOverlay/OverlayHost.cs:189` — jeder spätere Aufruf sieht `spareEnvironment != 0` und kehrt sofort zurück, ohne neu zu versuchen.
- `EnvironmentFor` liefert dann den Main-Browser, in dem composed Views leben: `CreateController` scheitert weiter mit `ERROR_INVALID_STATE`.

**Regression**

Ein einmaliger Spare-Timeout oder -HRESULT macht den Fix für den Rest der Session unwirksam: jedes weitere fensternde Overlay fällt in denselben Defekt wie vor 1.7.0. Der Kommentar „better than refusing outright“ trifft nur den ersten Versuch; danach gibt es keinen Retry, obwohl der nächste Öffnen-Versuch Sekunden später gelingen könnte.

**Korrekturrichtung**

Alias nicht in `spareEnvironment` speichern. Bei Misserfolg `IntPtr.Zero` lassen, für *diesen* Create den Main-Pointer zurückgeben (oder das Overlay gezielt `fail`en), und den nächsten windowed Create den Spare erneut versuchen lassen.

### WOV-1703 – Verspätete Spare-Completion nicht verwerfen

**Evidenz**

- `WebOverlay/OverlayHost.cs:498-508` — Completion übernimmt den Pointer, sofern nicht `startFailed || stopping`.
- Spare-Start: die Library läuft bereits, `startFailed` bleibt false.
- Nach Timeout gibt `createEnvironment` `IntPtr.Zero` zurück; `createSpareEnvironment` aliased auf Main (`WOV-1702`).
- Eine späte Completion schreibt `created` nur noch in die Heap-Closure, `AddRef` ohne Owner — `spareEnvironment` zeigt weiter auf Main.

**Regression**

Genau das, was der Callback für das *erste* Environment mit `startFailed` vermeiden will, gilt für den Spare nicht. Ergebnis: ein dritter Browser-Prozess (AddRef ohne Release) bis zum Spielende, plus der Alias-Defekt. Der Commit argumentiert mit 258 MB — das Leck ist dieselbe Größenordnung.

**Korrekturrichtung**

Ein `abandoned`/`finished`-Flag in `createEnvironment`, das die Completion nach Return verwirft und den Pointer `Release`t. Nicht `startFailed` wiederverwenden.

## 4. P3-Befunde

### WOV-1704 – Kommentar an `createEnvironments` vertauscht die Rollen

**Evidenz**

- `WebOverlay/OverlayHost.cs:472-476` — „The browser every windowed overlay is built from. The second one, for transparent overlays…“
- Code und README: HUDs/composed bleiben im **ersten** Browser; der Spare ist für **windowed** Overlays im Kollisionsfall.

Den Kommentar an `EnvironmentFor` angleichen.

### WOV-1705 – `ComposedControllerClosed` bei Exception im Close überspringen

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1778-1786` — `ComposedControllerClosed` steht in demselben `try` hinter `Close`/`Release`.
- Wirft `Controller_Close`, bleibt `composedControllers` erhöht. Jedes spätere fensternde Overlay bekommt einen Spare, obwohl kein composed View mehr lebt.

`Closed` in einem `finally` oder nach dem Release unabhängig vom Close-HRESULT zählen.

## 5. Was in diesem Commit nicht als Regression gilt

- Die Regel „Spare nur bei composed > 0 und keinen windowed Views im Main-Browser“ entspricht der Messung (Fenster zuerst + HUD = ein Browser; HUD zuerst + Fenster = Spare).
- `EnvironmentFor(true)` bleibt immer der Main-Browser; Chroma-Key-HUDs (`usesComposition == false`) zählen als windowed — das ist die WebView2-Unterscheidung (Composition-Controller vs. `CreateController`), nicht „jedes Transparent“.
- Alias-Check in `closeEverything` verhindert Double-Free, wenn Spare wirklich auf Main zeigt.
- `creatingEnvironment` plus nachgeschobenes `WM_APP_WORK` ist die richtige Idee gegen reentrantes Create während `pump()`; zu grob ist nur der Umfang (WOV-1701).
- Getrenntes Profil im Spare-Ordner ist dokumentiert (localStorage teilt sich nicht mit dem HUD-Browser).
- Probe `mixed` / `mixed-reverse` / `footprint` deckt den Success-Pfad ab, nicht Timeout, Alias-Latch oder Queue-Hold.

## 6. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. Spare-Environment-Timeout oder Create-HRESULT — nächster windowed Create muss erneut einen Spare versuchen, nicht still auf Main fallen.
2. Während `createSpareEnvironment` wartet: `Post`/`Hide` auf dem schon sichtbaren HUD muss ankommen (oder die Doku muss die Pause nennen).
3. Completion nach Spare-Timeout — kein zusätzlicher Browser-Prozess, kein AddRef ohne Owner.
4. `Controller_Close` wirft auf einem composed Overlay — `EnvironmentFor(false)` darf danach wieder Main liefern.

## 7. Betroffene Dateien im Commit

| Datei | Rolle |
|---|---|
| `WebOverlay/OverlayHost.cs` | `EnvironmentFor`, Spare-Environment, Queue-Hold, Shutdown-Release |
| `WebOverlay/OverlayWindow.cs` | Zähler Open/Close, `CreateController` auf `host` |
| `README.md`, `CHANGELOG.md`, `docs/FAULT-TESTS.md` | Doku 1.7.0, Probe 32–34 |
