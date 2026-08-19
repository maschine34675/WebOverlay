# Regression-Review: WebOverlay v1.4.0

> **Bearbeitungsstand 2026-08-19:** WOV-1401 bis WOV-1405 sind vor der
> Veroeffentlichung von v1.4.0 behoben; der Bericht bleibt als Begruendung
> stehen. WOV-1401: fehlgeschlagenes Mapping ist jetzt terminal
> (`OverlayFailure.VirtualHostFailed`) und angeforderte, aber ungemappte
> Origins werden vom Navigations-Filter abgewiesen - belegt durch Probe-Modus
> `vhost-fail` gegen `example.com`. WOV-1402: die erste Startursache gewinnt,
> und die 30-Sekunden-Warte endet, sobald eine Ursache feststeht. WOV-1403:
> abgewiesenes `Navigate`/`LoadHtml` stellt den vorherigen Zielzustand wieder
> her (Probe `nav-reject`). WOV-1404: Dispatcher- und Thread-Startfehler sind
> klassifiziert. WOV-1405: die Latch-Dokumentation nennt die Dispatch-Semantik.
> Die Testluecken aus Abschnitt 7 sind als Zeilen 10-16 in
> `docs/FAULT-TESTS.md` nachgetragen.

**Datum:** 19. August 2026  
**WebOverlay-Basis:** `d9f7a06ec999d6c12c26341cf94e94c770cbdac1` (`main`, Commit-Message `v1.4.0: virtual hosts, classified failures, main-thread dispatch`)  
**Verglichen mit:** `47f2e697cf2ffe623195c6da16d37291d196f33b`  
**Art:** defect-first Regression-Review des 1.4.0-Commits (VirtualHosts, klassifizierte `Failed`-Ursachen, `DispatchOnMainThread`, `PageLoaded` / `IsPageLoaded`) — ohne Code-Änderungen

Die älteren Berichte `CODE-REVIEW-2026-08-01.md`, `CODE-REVIEW-2026-08-01-aktuell.md`, `REGRESSION-REVIEW-2026-08-01.md` und `FOLLOW-UP-REVIEW-2026-08-01.md` beschreiben Zwischenstände bis v1.0.0. **Maßgeblich für den 1.4.0-Stand ist dieser Bericht.**

## 1. Kurzurteil

Für bestehende Mods ohne die neuen Optionen ist das Regressionsrisiko klein: Events bleiben standardmäßig auf dem Overlay-Thread, `Failed` ist unverändert, `Failure` / `PageLoaded` sind additiv. `MessageReceived` läuft jetzt auch ohne Dispatch durch `invokeIsolated` — ein werfender Handler reißt den COM-Callback nicht mehr um.

Der Release-Blocker sitzt im neuen Virtual-Host-Pfad: Mapping ist best-effort, das dokumentierte `Navigate("https://<host>/...")` aber nicht. Schlägt das Mapping fehl, fällt die Navigation auf echtes HTTPS im Netz zurück — mit Message-Bridge.

`DispatchOnMainThread` plus Plugin-`Update` ist für den Opt-in plausibel (Queue-Cap, Shutdown-Drop, `BepInDependency` im Demo).

**Gesamturteil:** den Default-Pfad (LoadHtml, Overlay-Thread-Events) nicht als blockiert betrachten; vor der 1.4.0-Verbreitung mindestens WOV-1401 beheben. WOV-1402 und WOV-1403 zeitnah nachziehen.

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-, Daten- oder Sicherheitsnotfall |
| P1 | Releaseblocker: Sicherheitsgrenze, zentraler API-Vertrag oder realistische schwere Fehlfunktion |
| P2 | relevanter Fehlerpfad oder unzuverlässiges Verhalten, das zeitnah behoben werden sollte |
| P3 | kleine Robustheits-, Dokumentations- oder Wartbarkeitslücke |

## 3. P1-Befund

### WOV-1401 – Fehlgeschlagenes Virtual-Host-Mapping erlaubt Netz-Navigation mit Message-Bridge

**Evidenz**

- `WebOverlay/OverlayWindow.cs:343-348` — `QueryInterface(ICoreWebView2_3)` fehlgeschlagen: nur Log, Return, Overlay geht weiter auf `Ready`.
- `WebOverlay/OverlayWindow.cs:357-376` — unbrauchbarer Host-Name, fehlender Ordner oder Mapping-HRESULT: nur Log, `continue`, kein `fail()`.
- `WebOverlay/OverlayWindow.cs:378-380` — `allowOrigin("https://" + host.Host)` nur nach erfolgreichem Mapping.
- `WebOverlay/OverlayWindow.cs:817-834` — `Navigate(url)` allowlistet die URL-Origin danach bedingungslos und navigiert.
- README-Rezept: `VirtualHosts = new[] { new VirtualHost("yourmod.assets", assetFolder) }` plus `Navigate("https://yourmod.assets/index.html")`.
- `isUsableHostName` erlaubt auflösbare Namen (`localhost`, echte TLDs, Dev-Domains).

**Regression**

Ohne Mapping behandelt WebView2 `https://<host>/...` als normale HTTPS-Navigation. Ein Tippfehler im Ordner, ein zu altes Runtime (kein `ICoreWebView2_3`) oder ein Mapping-HRESULT macht aus „fehlende lokale Dateien“ eine fremde Seite im Overlay: `PageLoaded` feuert, `currentDocumentIsTarget()` ist true, die Message-Bridge ist offen. Das verletzt die Sicherheitsgrenze „eine fremde Seite erreicht die Bridge nie“.

Realistisch vor allem, wenn der Host-Name im DNS existiert (Dev-Domain, `localhost`, versehentlich `example.com`) und der Ordner auf der Nutzer-Maschine fehlt oder falsch gepackt ist.

**Korrekturrichtung**

- Overlay failen, wenn `VirtualHosts` gesetzt sind und mindestens ein Eintrag nicht gemappt werden konnte; oder
- Navigation zu einem angeforderten Virtual-Host verweigern, solange das Mapping nicht sitzt;
- nicht still auf das offene Netz fallen, nur weil das Mapping best-effort ist.

## 4. P2-Befunde

### WOV-1402 – Environment-HRESULT wird nach 30 s durch die Timeout-Meldung überschrieben

**Evidenz**

- `WebOverlay/OverlayHost.cs:338-340` — der Environment-Callback schreibt bei Fehler `startFailure(EnvironmentFailed, "… hr=0x…")`.
- `WebOverlay/OverlayHost.cs:359-363` — die Warteschleife prüft nur `environment == IntPtr.Zero && !stopping`, nicht ob bereits eine Startursache steht.
- `WebOverlay/OverlayHost.cs:365-369` — nach Ablauf überschreibt `startFailure` die Message mit „did not start within 30 seconds“.
- `WebOverlay/OverlayWindow.cs:186-190` — spätere Handles lesen genau `StartFailure` / `StartFailureMessage`.

**Regression**

Die 30-Sekunden-Warte nach einem bereits bekannten Callback-Fehler war vorher schon da (beide Meldungen standen im Log). Neu ist die consumer-sichtbare Klassifikation: `FailureMessage` trägt nur noch den Timeout, das konkrete HRESULT ist weg. `Failure` bleibt `EnvironmentFailed` — die für den User lesbare Ursache ist falsch.

**Korrekturrichtung**

- Die Schleife beenden, sobald der erste klassifizierte Startfehler steht.
- `startFailure` darf eine bereits gesetzte Ursache nicht überschreiben.

### WOV-1403 – `IsPageLoaded` bleibt nach abgewiesenem Navigate/LoadHtml dauerhaft false

**Evidenz**

- `WebOverlay/OverlayWindow.cs:831-834` (`Navigate`) und `864-868` (`LoadHtml`) setzen `pageReady` / `pageLoaded` auf false, *bevor* `checkNavigationResult` das HRESULT sieht.
- `WebOverlay/OverlayWindow.cs:844-851` — bei Fehler nur Log und `outbox.Clear()`, kein `NavigationCompleted`, kein erneutes `PageLoaded`.
- Öffentliche Doku: Sends vor `IsPageLoaded == true` puffern; Streaming-Consumer sollen das Posten anhalten, solange das Flag false ist.

**Regression**

Lehnt WebView2 den Aufruf ab (ungültige URL, HTML über 2 MB), ändert sich das Dokument nicht. Die alte Zielseite bleibt sichtbar, `IsPageLoaded` bleibt false, `PageLoaded` feuert nicht noch einmal. Ein Consumer, der sich an die neue API-Doku hält, hängt. Intern galt dasselbe schon für `pageReady`; neu ist, dass genau dieses Flag jetzt öffentlich und dokumentiert ist.

**Korrekturrichtung**

- Den vorherigen Loaded-Zustand wiederherstellen, wenn `checkNavigationResult` fehlschlägt; oder
- `pageLoaded` / `pageReady` erst nach akzeptierter Navigation löschen.

## 5. P3-Befunde

### WOV-1404 – Host-Start ohne Dispatcher-Fenster bleibt unklassifiziert

**Evidenz**

- `WebOverlay/OverlayHost.cs:275-283` — `createDispatcherWindow()` loggt nur, ruft `startFailure` nicht.
- `WebOverlay/OverlayHost.cs:215-217` — `startFailed = true` ohne gesetzte `StartFailure`.
- `WebOverlay/OverlayHost.cs:254-256` — unerwarteter Thread-Abort ebenso.
- `WebOverlay/OverlayWindow.cs:186-190` — `StartFailure == Unknown` wird zu `EnvironmentFailed` plus „no browser environment“.

Loader und Runtime können in diesem Pfad vorhanden sein; der Handle berichtet trotzdem eine Environment-Ursache.

**Korrekturrichtung**

Auch Dispatcher- und Thread-Startfehler über `startFailure` klassifizieren (eigene Art oder ehrliches `Unknown` mit der tatsächlichen Log-Zeile).

### WOV-1405 – Späte gelatchte Handler sind bei `DispatchOnMainThread` nicht mehr synchron

**Evidenz**

- `WebOverlay/WebOverlays.cs:373-377` und `380-388` — XML: nachträglich abonnierte `Ready`/`Failed`-Handler laufen sofort auf dem abonnierenden Thread.
- `WebOverlay/WebOverlays.cs:435-438` — Kommentar im Handle dasselbe.
- `WebOverlay/WebOverlays.cs:457-466` — `addLatched` ruft bei `already` `raise(value)` auf.
- `WebOverlay/WebOverlays.cs:418-427` — `raise` queued bei `DispatchOnMainThread` bis zum Plugin-`Update`.

Der normale Subscribe-vor-Ready-Pfad bleibt korrekt. Code, der nachträglich abonniert und danach synchron weiterarbeitet, sieht den Latch zu früh.

**Korrekturrichtung**

Entweder sofort ausführen, wenn das Event schon steht, oder die Latch-Doku an die Dispatch-Semantik anpassen („bis zu einem Frame später, auch nachträglich“).

## 6. Was in diesem Commit nicht als Regression gilt

- Default `DispatchOnMainThread = false`: bestehende Consumer behalten Overlay-Thread-Events.
- `Failure` / `FailureMessage` / `PageLoaded` / `IsPageLoaded` sind additiv und source-kompatibel.
- Vtable-Slot 71 für `SetVirtualHostNameToFolderMapping` nach `QueryInterface(IID_WebView2_3)` ist gegen `WebView2.h` korrekt und laut Commit durch den Probe-Host belegt.
- `DENY_CORS` ist für eine Seite, die selbst auf dem gemappten Host lebt, die passende Access-Kind; Fonts brauchen CORS auf derselben Origin.
- Queue-Cap 4096 und Drop bei Shutdown sind bewusst und dokumentiert.
- Demo-Glaspanel mit `DispatchOnMainThread` plus direktes Loggen (ohne `fromPage`-Queue) ist der gewollte Opt-in; F10 nutzt weiter Queue-and-Drain.

## 7. Testlücken

`docs/FAULT-TESTS.md` endet bei der v1.0.0-Matrix. Die Commit-Message verweist auf Probe-Modi (`vhost`, `dispatch`, `failure-kind`) außerhalb des Repos.

Im Baum ungetestet, aber durch die Befunde nahegelegt:

1. Mapping-Misserfolg (fehlender Ordner oder kein `ICoreWebView2_3`) plus dokumentiertes `Navigate("https://<host>/...")` — darf nicht ins offene Netz mit Bridge.
2. Environment-Callback mit Fehler-HRESULT, dann `FailureMessage` am Handle (darf nicht der Timeout-Text sein).
3. Abgewiesenes `Navigate` / `LoadHtml` gegen `IsPageLoaded` und sichtbares Vorgängerdokument.
4. `DispatchOnMainThread` ohne laufendes Library-Plugin (Probe-Host): Fallback auf Overlay-Thread plus einmalige Warnung.
5. Nachträglich abonniertes `Ready` bei `DispatchOnMainThread = true`.

## 8. Betroffene Dateien im Commit

| Datei | Rolle |
|---|---|
| `WebOverlay/WebOverlays.cs` | `OverlayFailure`, `VirtualHost`, `DispatchOnMainThread`, `PageLoaded` / `IsPageLoaded`, `raise` |
| `WebOverlay/OverlayWindow.cs` | klassifiziertes `fail()`, Mapping, `pageLoaded` |
| `WebOverlay/OverlayHost.cs` | `StartFailure`, Main-Thread-Queue, `PumpMainThread` |
| `WebOverlay/WebOverlayPlugin.cs` | `MainThreadPumpAvailable`, `Update` |
| `WebOverlay/Interop/WebView2Api.cs` | `IID_WebView2_3`, Slot 71, `DENY_CORS` |
| `WebOverlay.Demo/DemoPlugin.cs` | `Failure` in Logs, Glaspanel mit Dispatch |
| `README.md`, `FORGE.md`, `docs/CONSUMER-API-WISHLIST-ANSWERS.md` | Doku 1.4.0 |
