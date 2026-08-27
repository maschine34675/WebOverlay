# Follow-up-Review: WebOverlay v1.0.0

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Its findings were addressed in the releases that
> followed - see `CHANGELOG.md`. It is kept as evidence, not as a
> description of the current library.

> **Historischer Bericht.** Bezieht sich auf einen aelteren Commit; Befunde, Hashes und Laufzeitgrenzen sind ueberholt. Alle als valide bestaetigten Befunde saemtlicher Review-Runden wurden bis Commit `261f0af` umgesetzt; die zugehoerige Fault-Injection-Matrix ist in `docs/FAULT-TESTS.md` festgehalten.


**Datum:** 1. August 2026  
**WebOverlay-Basis:** `defc55fa8b0e92bbd1492ac0a0d6205725509e2d` (`master`, Tag `v1.0.0`)  
**CraftQueue-Basis:** `f39bc93820a451265f374f3d5f577ce450758522` (`main`)  
**Zielruntime:** SPT 4.0.13, EFT 0.16.9.40087, BepInEx 5.4.23.2, .NET Framework 4.7.2, Windows x64  
**Review-Art:** fokussiertes Follow-up nach Umsetzung der letzten Reviewpunkte  
**Identitätsentscheidung:** `Anvil` beziehungsweise `com.anvil.weboverlay` ist eine ausdrücklich genehmigte Ausnahme von der sonstigen `maschine`-Konvention und kein Befund.

## 1. Kurzurteil

Der aktuelle Stand ist gegenüber den vorherigen Reviews deutlich stabiler. Der normale In-Game-Pfad ist praktisch nachgewiesen: WebView2 startet, CraftQueue kann das Overlay umschalten, Inline-HTML wird dargestellt und Page-to-Mod-Messaging funktioniert. Es wurde kein neuer offensichtlicher COM-Use-after-free gefunden.

Vor einer Veröffentlichung verbleibt jedoch ein **P1-Blocker**: Die Outbox ist weiterhin nicht an eine konkrete Navigation und deren finalen Origin gebunden. Bei einem Redirect zu einem ebenfalls erlaubten Origin können gepufferte Nachrichten oder Skripte an das falsche Dokument gelangen. Daneben bestehen mehrere P2-Fehlerpfade rund um Startup/Shutdown, Eventregistrierung, Renderer-Recovery und die Demo.

**Gesamturteil:** fast releasebereit, aber noch nicht veröffentlichen, bevor mindestens FUR-01 bis FUR-04 korrigiert und gezielt getestet wurden.

Die älteren Dateien `CODE-REVIEW-2026-08-01.md` und `REGRESSION-REVIEW-2026-08-01.md` bleiben historische Momentaufnahmen ihrer jeweiligen Commits. Für den aktuellen Stand ist dieser Bericht maßgeblich.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das vor Veröffentlichung gehärtet werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. Verbleibender P1-Befund

### FUR-01 – Outbox ist nicht an Navigation und finalen Origin gebunden

**Evidenz**

- `WebOverlay/OverlayWindow.cs:381-395` setzt bei jedem erfolgreichen `NavigationCompleted` pauschal `pageReady = true` und flusht die gesamte Outbox.
- `WebOverlay/OverlayWindow.cs:447-463` setzt bei einer erlaubten Top-Level-Navigation nur `pageReady = false`; die gepufferten Einträge erhalten keine Navigation-ID und keinen erwarteten Origin.
- `WebOverlay/OverlayWindow.cs:489-501` erlaubt alle Origins, die zuvor über `Navigate()` oder `AllowedOrigins` freigegeben wurden.
- `WebOverlay/OverlayWindow.cs:586-625` speichert Nachrichten und Skripte lediglich als FIFO-Einträge ohne Navigationsgeneration.
- `WebOverlay/Interop/WebView2Api.cs:93-99` bindet die im lokalen WebView2-SDK vorhandene `NavigationId` nicht an.

**Konkretes Fehlerszenario**

1. Ein Consumer ruft `Navigate(A)` auf.
2. Direkt danach wird `Post(secret)` oder `ExecuteScript(...)` gepuffert.
3. A leitet zu einem ebenfalls erlaubten Origin B um.
4. Der erfolgreiche Abschluss von B flusht die für A bestimmte Outbox an B.

Auch eine fehlgeschlagene Navigation verwirft die Queue nicht. Eine spätere erfolgreiche, seitenseitig ausgelöste Navigation zu einem erlaubten Ziel kann daher alte Einträge erhalten.

**Risiko**

Ein Overlay darf mehrere erlaubte Origins besitzen. Die Tatsache, dass B grundsätzlich erlaubt ist, bedeutet nicht, dass für A gepufferte Daten an B weitergegeben werden dürfen. Für eine von mehreren Mods gemeinsam verwendete Browserbibliothek ist dies ein Bruch der Origin- und Dokumentgrenze.

**Korrektur**

- Jede Navigation mit einer eigenen Generation beziehungsweise der WebView2-`NavigationId` verfolgen.
- Die Outbox zusätzlich an den erwarteten finalen Origin binden.
- Nur der erfolgreiche Abschluss genau dieser Navigation darf deren Einträge flushen.
- Bei Abbruch, Fehler oder unerwartetem End-Origin die zugehörigen Einträge verwerfen oder den Consumer über einen definierten Fehlerpfad informieren.
- Redirect, Navigationsfehler und zwei erlaubte Origins automatisiert testen.

## 4. Verbleibende P2-Befunde

### FUR-02 – Shutdown während des Environment-Starts verarbeitet Create-Aufträge als reguläre Fehler

**Evidenz**

- `WebOverlay/OverlayHost.cs:96-102` setzt `stopping = true` und `running = false`, weckt den Dispatcher jedoch mit `WM_APP_WORK`.
- `WebOverlay/OverlayHost.cs:237-248` pumpt während der bis zu 30 Sekunden langen Environment-Erstellung weiter, ohne `stopping` oder `running` zu prüfen.
- `WebOverlay/OverlayHost.cs:275-283` ruft bei `WM_APP_WORK` immer `drainWork()` auf, auch solange `acceptingWork` noch nicht erreicht ist.

Während der Browser noch startet, kann der Shutdown-Weckruf dadurch bereits eingereihte `Create()`-Aktionen mit `Environment == 0` ausführen. Die Handles melden anschließend `Failed`. CraftQueue kann diesen Fehler als Anlass für seinen externen Browser-Fallback interpretieren und beim normalen Spielende einen Browser öffnen. Der Hintergrundthread kann außerdem bis zum Timeout weiterlaufen.

**Korrektur**

- Während der Environment-Erstellung keine Consumer-Arbeit aus dem Dispatcher drainen.
- Die Warteschleife bei `stopping` beziehungsweise `!running` abbrechen.
- Beim Shutdown Handles schließen, ohne einen normalen Startup-`Failed`-Fallback auszulösen.
- Einen Test „Create während Environment-Start, unmittelbar danach Shutdown“ ergänzen.

### FUR-03 – `NavigationCompleted` kann fehlen, obwohl das Overlay `Ready` meldet

`WebOverlay/OverlayWindow.cs:381-395` ignoriert das HRESULT von `AddNavigationCompleted`. Im Gegensatz zu NavigationStarting, FrameNavigationStarting, Popup-, Permission- und ProcessFailed-Registrierung fließt dieses Ergebnis nicht in `armed` ein.

Scheitert nur diese Registrierung, läuft `configure()` weiter, das Handle meldet `Ready`, `pageReady` wird aber nie gesetzt. Sämtliche Nachrichten und Skripte bleiben dauerhaft gepuffert.

**Korrektur:** Das Registrierungsergebnis in `armed` aufnehmen und bei Fehlschlag `Failed` statt `Ready` auslösen.

### FUR-04 – Renderer-Recovery endet in einem weiterhin `Ready` markierten toten Overlay

`WebOverlay/OverlayWindow.cs:416-438` versucht bei `RenderExited` höchstens zwei Reloads. Beim dritten Absturz wird nur noch gewarnt. `RenderProcessUnresponsive` wird ebenfalls lediglich protokolliert.

Der Consumer kann deshalb ein `Ready`-Handle behalten, obwohl dessen Inhalt dauerhaft abgestürzt oder eingefroren ist.

**Korrektur:** Nach ausgeschöpfter Recovery und bei dauerhaftem Unresponsive-Zustand `Failed` auslösen, das Fenster ausblenden und den Consumer über den bestehenden Fallbackvertrag übernehmen lassen.

### FUR-05 – Unerwartete Ausnahmen können ein Handle dauerhaft in `Creating` lassen

- `WebOverlay/OverlayWindow.cs:132-155` ruft `configure()` im nativen Controller-Callback ohne eigenen Terminalfehler-Schutz auf.
- `WebOverlay/Interop/ComCallback.cs:121-130` verschluckt Ausnahmen an der nativen Grenze absichtlich, um kein Unwinding in Chromium zuzulassen.
- `WebOverlay/OverlayHost.cs:251-263` protokolliert eine aus `window.Create()` kommende Ausnahme nur.

Das Verhindern nativen Unwindings ist korrekt. Ohne anschließendes `fail(...)` erhält der Consumer jedoch weder `Ready` noch `Failed`.

**Korrektur:** Öffentliche Create-/Configure-Grenzen mit `try/catch` umgeben, protokollieren und das betroffene Fenster garantiert in `Failed` überführen.

### FUR-06 – Die Demo kann nach einem gelatchten `Failed` eine `NullReferenceException` auslösen

**Interaktives Overlay:** `WebOverlay.Demo/DemoPlugin.cs:120-129`  
**HUD:** `WebOverlay.Demo/DemoPlugin.cs:72-80`

`Ready` und `Failed` sind gelatcht. Ein `Failed`-Handler darf daher schon während der Eventregistrierung synchron laufen. Der Handler disposet das lokale Handle und setzt das Feld `overlay` beziehungsweise `hud` auf null. Der unmittelbar folgende Aufruf `overlay.LoadHtml(...)` oder `hud.LoadHtml(...)` kann anschließend auf null zugreifen.

**Korrektur:** Für den folgenden `LoadHtml`-Aufruf ebenfalls ausschließlich `created` beziehungsweise `createdHud` verwenden. Das Feld bleibt nur der austauschbare Besitzzeiger.

### FUR-07 – Unmittelbare Navigationsfehler werden verworfen

`WebOverlay/OverlayWindow.cs:214-228` sowie `556-583` ignorieren die HRESULTs von `Navigate` und `NavigateToString`. Ein konkreter Fall ist Inline-HTML oberhalb des WebView2-Limits von 2 MiB: Der Aufruf kann unmittelbar scheitern, während `pageReady = false` bleibt und weitere Sendungen still gepuffert werden.

**Korrektur:** HRESULT prüfen und den Navigationsfehler in einen definierten Zustand überführen. Mindestens ungültige URL, übergroßes Inline-HTML und synchron abgelehnte Navigation testen.

### FUR-08 – Ein früher Cleanupfehler kann den restlichen nativen Cleanup überspringen

`WebOverlay/OverlayWindow.cs:702-741` legt `Controller_Close`, Controller-Release, WebView-Release und `DestroyWindow` in einen einzigen breiten `try`-Block. Wirft bereits der erste Schritt, werden die übrigen Ressourcen übersprungen; das Fenster wird danach trotzdem aus der Hostliste entfernt.

Die Callback-Lebensdauerstrategie verhindert voraussichtlich einen Use-after-free, aber ein nativer Zombie kann bis zum Prozessende bestehen bleiben.

**Korrektur:** Controller, WebView und HWND jeweils in getrennten, idempotenten Cleanup-Blöcken behandeln und jeden Pointer nach erfolgreichem oder endgültig verworfenem Cleanup nullen.

## 5. P3- und Dokumentationsbefunde

### FUR-09 – Inline-HTML-One-Shot ist nicht auf die erwartete Top-Level-Navigation beschränkt

`WebOverlay/OverlayWindow.cs:369-379` führt Top-Level- und Frame-Navigation durch denselben Filter. `WebOverlay/OverlayWindow.cs:489-499` verbraucht `expectInlineNavigation` bei der ersten `data:`-Navigation unabhängig davon, ob sie top-level ist.

Eine Frame-Navigation kann dadurch den One-Shot verbrauchen. Meldet ein Runtimepfad für `NavigateToString` stattdessen `about:blank`, wird der One-Shot wegen des früheren Returns nicht verbraucht und bleibt für eine spätere `data:`-Navigation aktiv.

**Korrektur:** Die Freigabe an die konkrete, erwartete Top-Level-Navigation beziehungsweise Navigation-ID binden.

### FUR-10 – Öffentliche Beispiele bilden den asynchronen Vertrag nicht sicher ab

- `FORGE.md:19-24` verwendet das Ergebnis von `WebOverlays.Create()` ohne Nullprüfung und ohne `Failed`-Handler.
- `README.md:47-48` behauptet pauschal, Events kämen vom Overlay-Thread. Spät abonnierte `Ready`-/`Failed`-Handler laufen laut öffentlichem API-Vertrag auf dem abonnierenden Thread.
- Das Demo-ZIP benötigt die separat installierte Hauptbibliothek, enthält aber keinen eigenen kurzen Abhängigkeitshinweis.

Da die Demo und FORGE-Seite als Referenz für andere Modautoren dienen, sollten sie den sicheren Besitz-, Fehler- und Threadingvertrag vollständig vormachen.

### FUR-11 – Veröffentlichungsdokumentation verweist auf einen derzeit nicht erreichbaren GitHub-Ort

`FORGE.md:44`, die Assembly-Metadaten und die CraftQueue-README verweisen auf `https://github.com/maschine34675/WebOverlay`. Das lokale Repository besitzt keinen Remote; `gh repo view maschine34675/WebOverlay` konnte das Repository zum Reviewzeitpunkt nicht auflösen.

Vor Veröffentlichung entweder das Repository anlegen und pushen oder die Zusage und Links entfernen beziehungsweise auf den tatsächlichen Veröffentlichungsort ändern.

### FUR-12 – Die im Tag enthaltenen älteren Reviewberichte sind ohne Superseded-Hinweis irreführend

Der Tag `v1.0.0` enthält sowohl `CODE-REVIEW-2026-08-01.md` als auch `REGRESSION-REVIEW-2026-08-01.md`. Beide beziehen sich auf ältere Commits und nennen bereits behobene Findings, alte Hashes sowie frühere Laufzeitgrenzen.

Sie dürfen als Audit-Historie bestehen bleiben, sollten aber am Anfang deutlich auf diesen aktuellen Follow-up-Bericht verweisen und als historisch gekennzeichnet werden.

## 6. Bestätigte Verbesserungen

Gegenüber den vorherigen Ständen wurden unter anderem folgende Punkte korrekt verbessert:

- Der frühe Hostfehler lässt den Overlay-Thread weiterlaufen, sodass spät eingereihte Handles grundsätzlich noch ihren `Failed`-Pfad erreichen.
- Die Outbox bewahrt jetzt die Reihenfolge von Nachrichten und Skripten.
- Explizites `Navigate()` beziehungsweise `LoadHtml()` verwirft für das alte Ziel gepufferte Einträge.
- Dispose während Controller-Erstellung behandelt das erwartete `E_ABORT` nicht mehr als regulären Startfehler.
- Consumer-Handler werden einzeln isoliert; eine Ausnahme verhindert nicht mehr weitere Subscriber oder interne Fertigstellung.
- `Ready` und `Failed` dokumentieren ihre mögliche Thread-Affinität im öffentlichen API-Vertrag.
- Wesentliche Navigations-, Message-, Popup-, Permission- und ProcessFailed-Registrierungen arbeiten fail-closed.
- Browserprozessverlust führt zu `Failed`; Rendererabstürze besitzen eine begrenzte Recovery.
- Die Demo verwirft fehlgeschlagene Handles grundsätzlich und hält sie nicht mehr dauerhaft fest.
- `LoadHtml()` funktioniert mit dem aktuell gemessenen `data:`-Navigationsverhalten wieder.
- CraftQueue kompiliert weiterhin gegen die aktuelle öffentliche API.
- Die bewusste `Anvil`-Identität ist über Plugin-GUID, Assembly und Paketnamen konsistent.

## 7. Build-, Test- und Artefaktstatus

### WebOverlay und Demo

- Rebuild: **0 Warnungen, 0 Fehler**.
- Aktuelle WebOverlay-Build- und Live-DLL sind SHA-256-identisch:
  - `43F2A0C05523040E1245067F620CDD2F7BC25EAFAF012C3201CDB463AE6385FC`
- Aktuelle Demo-Build- und Live-DLL sind SHA-256-identisch:
  - `DF63E902A027B378926FC67F4479E6E958A9721008ECC58182F3F939B2B5EA86`
- Die neu erzeugten Haupt- und Demo-ZIPs enthalten die geprüften aktuellen DLLs.
- `v1.0.0` ist ein lokaler Tag auf `defc55f`.

### CraftQueue

- Lösung gegen die aktuelle WebOverlay-DLL gebaut: **0 Warnungen, 0 Fehler**.
- Tests: **42 bestanden, 0 fehlgeschlagen, 0 übersprungen**.
- WebOverlay und CraftQueue waren vor Erstellung dieses Berichts in ihren jeweiligen Repositories sauber.

### Automatisierungsgrenze

WebOverlay besitzt weiterhin keine automatisierten Tests und keine CI-Konfiguration. Die CraftQueue-Tests beweisen die Consumer-Kompilierung und ihre eigene Logik, decken aber die nativen WebView2-Fehler-, Redirect-, Callback- und Shutdownpfade nicht ab.

## 8. Aktuelle Laufzeitevidenz

Der aktuelle `C:\SPT\BepInEx\LogOutput.log` wurde am 1. August 2026 um 20:16 Uhr geschrieben, also nach dem funktionalen Fix-Commit `94da985` und vor dem ausschließlich dokumentations-/paketbezogenen Commit `defc55f`.

Beobachtet wurden:

- WebOverlay lud die WebView2-Runtime `150.0.4078.105`.
- CraftQueue schaltete das In-Game-Overlay zweimal um.
- Die Demo empfing die Page-Nachrichten `button one` und `button two`.
- Im aktuellen Lauf wurden keine WebOverlay-Warnungen für diese Normalpfade protokolliert.

Damit sind Start, Consumerintegration, Inline-Rendering und Page-to-Mod-Messaging praktisch belegt. Nicht belegt sind weiterhin:

- Redirect mit zwei erlaubten Origins und gepufferter Outbox;
- fehlgeschlagene Navigation;
- fehlgeschlagene Eventregistrierung oder Settings-HRESULTs;
- fehlender Loader beziehungsweise fehlende WebView2-Runtime;
- Shutdown während Environment- oder Controller-Erstellung;
- Renderer-Crash und Render-Unresponsive;
- Cleanupfehler und wiederholte Create/Dispose-Stresstests.

## 9. Empfohlene Korrekturreihenfolge

1. FUR-01: Outbox an Navigation-ID und finalen Origin binden.
2. FUR-02: Startup/Shutdown-Drain atomar machen und die Environment-Warteschleife abbrechbar machen.
3. FUR-03 und FUR-07: Registrierungs- und Navigations-HRESULTs terminal behandeln.
4. FUR-04: Recovery-Cap und Unresponsive in `Failed` überführen.
5. FUR-05 und FUR-08: alle Ausnahme- und Cleanup-Pfade in einen definierten Terminalzustand bringen.
6. FUR-06: Demo ausschließlich über die bereits erfassten lokalen Handles fortsetzen.
7. FUR-09: Inline-Freigabe an die erwartete Top-Level-Navigation binden.
8. FUR-10 bis FUR-12: Beispiele, Links und historische Reports für die Veröffentlichung bereinigen.
9. Danach die Fault-Injection-/Redirect-Matrix aus Abschnitt 8 ausführen, Pakete neu erzeugen und Hashgleichheit erneut bestätigen.

## 10. Schlussurteil

Die umgesetzten Fixes haben die zentralen COM-, Event- und normalen Consumerpfade wesentlich verbessert. Die aktuelle In-Game-Evidenz ist belastbarer als in den vorherigen Reviews. Die verbleibende navigationsübergreifende Outbox ist für eine gemeinsam von mehreren Mods verwendete Browserbibliothek jedoch weiterhin ein echter Releaseblocker. Zusammen mit dem Startup/Shutdown-Race und den noch nicht terminal behandelten Fehlerpfaden sollte `v1.0.0` erst nach einer letzten kleinen Hardening-Runde veröffentlicht werden.

