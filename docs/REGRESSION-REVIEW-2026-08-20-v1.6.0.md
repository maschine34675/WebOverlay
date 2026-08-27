# Regression-Review: WebOverlay v1.6.0

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Its findings were addressed in the releases that
> followed - see `CHANGELOG.md`. It is kept as evidence, not as a
> description of the current library.

> **Bearbeitungsstand 2026-08-20:** WOV-1601 bis WOV-1606 sind vor der
> Veroeffentlichung von v1.6.0 behoben; der Bericht bleibt als Begruendung
> stehen. WOV-1601: `tryParseRegions` trennt "leer = Form loeschen" von
> "unlesbar = ignorieren"; eine kaputte Liste laesst die alte Form stehen und
> wird geloggt. WOV-1602: der ganze Praefix `__wo.` wird gefiltert, fuer
> Nachrichten wie fuer Anfragen - eine Anfrage darauf wird sofort mit `null`
> beantwortet statt offen gelassen. WOV-1603: die Rechtecke werden vor
> `CreateRectRgn` vom Client- in den Fensterursprung verschoben, also bleibt
> die Titelleiste eines gerahmten Fensters erhalten (Probe misst die
> Region-Box bei 8,31). WOV-1604/1605: `cref` auf `SetShape` korrigiert,
> Bildschirmkoordinaten bei `SetBounds` dokumentiert. WOV-1606: GDI-Fehler
> werden geprueft, und `shape` wird erst nach erfolgreichem `SetWindowRgn`
> uebernommen. Die Testluecken aus Abschnitt 6 stehen als Zeilen 26-28 in
> `docs/FAULT-TESTS.md` (Probe-Modus `shape-guards`).

**Datum:** 20. August 2026  
**WebOverlay-Basis:** `132da1412c276a5b2509a6715644fc245e33db25` (`main`, Commit-Message `Shape an overlay, move it, and one measured dead end`)  
**Verglichen mit:** `bc623c5588c5a84828478533638263588d985687` (`Named channels and request/reply for pages`)  
**Art:** defect-first Regression-Review des 1.6.0-Commits (`SetShape` / `overlay.setShape`, `SetBounds`, `SetWindowRgn` statt Hit-Test-Regionen) — ohne Code-Änderungen

Die älteren Berichte bis v1.5.0 bleiben historische Momentaufnahmen. **Maßgeblich für den 1.6.0-Stand ist dieser Bericht.**

## 1. Kurzurteil

Wishlist-Eintrag 7: `SetBounds` verschiebt/skaliert zur Laufzeit ohne den Bounds-Store zu schreiben; `SetShape` schneidet Bild und Maus gemeinsam auf Rechtecke zu, weil `HTTRANSPARENT` Clicks nicht an das Spiel (anderen Thread) durchreicht — das ist gemessen und in README plus Answers-Dokument ehrlich dokumentiert. Der Probe-Host hat den geformten interaktiven HUD-Pfad (Zeile 24) und `SetBounds` (Zeile 25) als PASS.

Für bestehende Mods ohne die neuen Aufrufe ändert sich am Overlay-Verhalten nichts; der Channel-Pfad filtert zusätzlich `__wo.shape`.

Kein P0. Kein klarer nativer Crash-Pfad. Vor der Verbreitung sollten die beiden P2-Stellen an `setShape`/`parseRegions` und der reservierte Channel-Präfix geschlossen werden: ein malformed `overlay.setShape` macht ein interaktives Vollbild-HUD wieder zum Mausfänger über dem ganzen Spiel.

**Gesamturteil:** den HUD-Happy-Path nicht als blockiert betrachten; WOV-1601 und WOV-1602 vor der 1.6.0-Verbreitung beheben.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P2-Befunde

### WOV-1601 – Malformed Shape-Payload nicht als „ganzes Fenster“ anwenden

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1077-1078` — jede Message auf `__wo.shape` geht nach `setShape(parseRegions(payload))`.
- `WebOverlay/OverlayWindow.cs:1155-1158` — Kommentar: eine malformed Liste werde *verworfen*, nicht halb angewandt.
- `WebOverlay/OverlayWindow.cs:1162-1163` und `1172-1182` — leerer *oder* unparsbarer Payload liefert `null`.
- `WebOverlay/OverlayWindow.cs:1130-1135` — `setShape(null)` ruft `SetWindowRgn(..., NULL)` und stellt das **ganze** Fenster wieder her.
- Shim (`WebOverlay/ChannelProtocol.cs:67-69`): Nicht-Elemente werden als `{x,y,w,h}` gelesen. Ein Objekt mit `left`/`top`/`width`/`height` (DOMRect-Felder ohne `x`/`w`) erzeugt `NaN,…`; `TryParse` scheitert.

**Regression**

„Drop“ und „restore full window“ sind nicht dasselbe. Ein Tippfehler oder ein einmalig kaputtes `overlay.setShape` auf einem interaktiven Vollbild-HUD hebt die Form auf: das Overlay nimmt wieder die Maus über dem ganzen Spiel entgegen. Genau das sollte `SetShape` verhindern. Leeres `setShape([])` sendet `""` und darf weiterhin zurücksetzen.

**Korrekturrichtung**

Parse-Fehler von „keine Rechtecke / Form löschen“ trennen: bei malformed Payload `setShape` nicht aufrufen (vorherige Region lassen) und loggen. Nur `null`/leer explizit als Restore behandeln.

### WOV-1602 – Reservierten Channel-Präfix `__wo.` nicht nur für Shape filtern

**Evidenz**

- README: Channel-Namen, die mit `__wo.` beginnen, gehören der Library.
- `WebOverlay/ChannelProtocol.cs:29-32` — `ReservedPrefix` und `ShapeChannel`.
- `WebOverlay/OverlayWindow.cs:1075-1080` — nur `channel == ShapeChannel` wird intern behandelt; alles andere erreicht `ChannelMessage`.
- Kind `q`/`a` auf `__wo.shape` oder `__wo.anything` läuft ungefiltert in `RequestReceived` / Pending-Requests.

**Regression**

Der Kommentar spricht von „the library's own channels“ (Plural), der Code kennt nur eines. `overlay.send('__wo.future', …)` oder `overlay.request('__wo.shape', …)` landet beim Consumer. Künftige interne Kanäle würden so leaken; die README-Garantie gilt nicht.

**Korrekturrichtung**

Nachrichten, deren Channel mit `ReservedPrefix` beginnt: bekannte Kanäle intern behandeln, den Rest still droppen (kein `ChannelMessage` / `RequestReceived`).

### WOV-1603 – Shape-Koordinaten sind HWND-relativ, Seite und `OverlayRegion` sind Client/WebView-relativ

**Evidenz**

- `SetWindowRgn` misst vom **Fensterursprung** inklusive Non-Client (Caption, Rahmen).
- `WebOverlay/OverlayWindow.cs:1786-1788` — gerahmte Overlay: `WS_CAPTION | WS_SYSMENU | WS_SIZEBOX`.
- `fitToClientArea()` setzt den WebView auf `GetClientRect` — Seite `(0,0)` ist Client oben links, unter der Titelleiste.
- Shim skaliert `getBoundingClientRect()` (Viewport/Client) mit `devicePixelRatio`.
- `OverlayRegion` XML: Pixel vom „top-left“ des Overlays; cref zeigt fälschlich auf `SetInteractiveRegions`.

**Regression**

Der gemessene HUD-Pfad ist rahmenlos (`Transparent` ignoriert `Frame`) — dort stimmen die Koordinaten. `SetShape` / `overlay.setShape` stehen aber auf jedem Overlay. Auf einem gerahmten Fenster schneidet eine seiten- oder client-relative Form die Titelleiste weg und verschiebt den Inhalt um die Caption-Höhe. Ziehen/Schließen geht verloren, Klicks treffen daneben.

**Korrekturrichtung**

Vor `CreateRectRgn` den Client-Ursprung in Fensterkoordinaten mappen (`ClientToScreen`/`ScreenToClient` oder Caption+Border), oder in der Doku klar sagen: nur rahmenlose/HUD-Fenster, und JS/C# in derselben Basis beschreiben.

## 4. P3-Befunde

### WOV-1604 – `OverlayRegion` XML verweist auf eine nicht existierende API

**Evidenz**

- `WebOverlay/WebOverlays.cs:158-161` — `see cref="IWebOverlay.SetInteractiveRegions"`.
- Öffentliche Methode heißt `SetShape`.

Broken cref (CS1574), Rest aus dem Wishlist-Namen. Auf `SetShape` umbiegen.

### WOV-1605 – `SetBounds`-Koordinatensystem ist in der öffentlichen Doku nicht genannt

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1200-1208` — `GetWindowRect` / `SetWindowPos` in **Screen**-Pixeln.
- README und XML: „moves or resizes“, ohne Screen vs. Spiel-Client vs. Overlay-Client.

Ein Unity-Caller, der Spiel-relative Pixel übergibt, schiebt das Fenster an die Screen-Position. Eine Zeile „screen coordinates of the overlay HWND“ in XML/README reicht.

### WOV-1606 – GDI-Fehler beim Zusammensetzen der Region

**Evidenz**

- `WebOverlay/OverlayWindow.cs:1139-1143` — `CreateRectRgn` / `CombineRgn` ohne Null-Check; `piece == IntPtr.Zero` wird trotzdem ge-OR-t und gelöscht.
- Bei `combined == IntPtr.Zero` ist `SetWindowRgn(window, 0, …)` ein Restore auf das volle Fenster, während `shape` schon die Rechtecke hält.

Selten (GDI-Handle-Erschöpfung). Stück überspringen oder abbrechen, Feld `shape` nur nach erfolgreichem `SetWindowRgn` setzen.

## 5. Was in diesem Commit nicht als Regression gilt

- `HTTRANSPARENT` nicht zu shippen ist die gemessene Entscheidung; `SetWindowRgn` als einziges Cross-Process-Modell ist dokumentiert (Bild und Maus untrennbar).
- `SetBounds` schreibt nicht in `BoundsStore` (`WM_EXITSIZEMOVE` bleibt Spieler-Geste); `everPositioned = true` lässt gerahmte Fenster beim nächsten Show nicht auf den Store zurückfallen.
- HUDs folgen weiter dem Spielbild (`Show` / `followGameWindow` rufen `positionOverGame`); README sagt ausdrücklich, `SetBounds` sei für Panels. Das ist kein Vertragsbruch.
- `SetShape(null)` und leeres `overlay.setShape([])` stellen das ganze Fenster wieder her — gewollt.
- Ownership: erfolgreiches `SetWindowRgn` übergibt die HRGN ans System; Fehlschlag löscht `combined`.
- Der Array-Copy in `OverlayHandle.SetShape` verhindert, dass der Caller nachträglich live mutiert.
- Fault-Zeilen 24–25 belegen den rahmenlosen interaktiven Shape-Pfad und `SetBounds`, nicht WOV-1601–1603.

## 6. Testlücken

In `docs/FAULT-TESTS.md` ungetestet, aber durch die Befunde nahegelegt:

1. `overlay.setShape` mit unparsbarer Liste (z. B. `{left,top,width,height}` ohne `x,w`) bei bereits gesetzter Form — Region muss stehen bleiben, nicht auf Vollfläche zurückfallen.
2. `overlay.send('__wo.other', …)` und `overlay.request('__wo.shape', …)` — darf `ChannelMessage` / `RequestReceived` nicht erreichen.
3. `SetShape` / `overlay.setShape` auf einem **gerahmten** Overlay — Caption bleibt bedienbar, Rechtecke liegen auf dem Client.
4. `CreateRectRgn`-Fehlschlag ( wenigstens Code-Review / defensives Skip).

## 7. Betroffene Dateien im Commit

| Datei | Rolle |
|---|---|
| `WebOverlay/WebOverlays.cs` | `OverlayRegion`, `SetShape`, `SetBounds`, kaputter cref |
| `WebOverlay/OverlayWindow.cs` | `SetWindowRgn`, `parseRegions`, Channel-Filter, `SetBounds` |
| `WebOverlay/ChannelProtocol.cs` | `ReservedPrefix`, Shim `overlay.setShape` |
| `WebOverlay/Branding.cs` | `1.6.0` |
| `README.md`, `FORGE.md`, `docs/CONSUMER-API-WISHLIST-ANSWERS.md`, `docs/FAULT-TESTS.md` | Doku 1.6.0 |
