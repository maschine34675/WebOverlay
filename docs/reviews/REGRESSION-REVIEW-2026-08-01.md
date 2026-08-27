# Regression-Review: WebOverlay-Fixes

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Many of its findings were addressed in the releases
> that followed - see `CHANGELOG.md`; the ones still open recur in newer
> reports until a release closes them. It is kept as evidence, not as a
> description of the current library.

> **Historischer Bericht.** Bezieht sich auf einen aelteren Commit; Befunde, Hashes und Laufzeitgrenzen sind ueberholt. Alle als valide bestaetigten Befunde saemtlicher Review-Runden wurden bis Commit `261f0af` umgesetzt; die zugehoerige Fault-Injection-Matrix ist in `docs/FAULT-TESTS.md` festgehalten.



**Datum:** 1. August 2026
**WebOverlay-Basis:** `d2a0b2abdbf9d87b91c07353f2a6037e5508514f`
**CraftQueue-Basis:** `f39bc93820a451265f374f3d5f577ce450758522`
**Verglichen mit:** WebOverlay `e6213db`, CraftQueue `18be8b3`
**Zielruntime:** SPT 4.0.13, EFT 0.16.9.40087, BepInEx 5.4.23.2, .NET Framework 4.7.2, Windows x64

## Kurzurteil

Die wichtigsten ursprünglichen Crash- und Hauptthreadprobleme wurden plausibel behoben. Insbesondere wurde kein neuer nativer Use-after-free- oder COM-Crashpfad gefunden. Der neue asynchrone Lifecycle und die Outbox haben jedoch zwei neue P1-Risiken eingeführt. Zusätzlich existieren mehrere P2-Regressions- beziehungsweise unvollständige Fehlerpfade, und die vorhandenen Release-ZIPs enthalten noch die Vor-Fix-Binaries.

**Aktuelles Urteil:** Noch nicht veröffentlichen, bevor die beiden P1-Codebefunde behoben und beide Releasepakete neu versioniert beziehungsweise erzeugt wurden.

## Neue P1-Befunde

### REG-01 – Früher Hostfehler kann ein Handle ohne `Ready`, `Failed` oder Fallback hinterlassen

**Evidenz**

- `WebOverlay/OverlayHost.cs:52-67` startet den Overlay-Thread und meldet sofort Erfolg.
- `WebOverlay/OverlayHost.cs:113-124` kann bei fehlendem Loader oder fehlgeschlagener Runtime seine zu diesem Zeitpunkt noch leere Queue abarbeiten und den Thread beenden.
- `WebOverlay/WebOverlays.cs:249-253` registriert und postet das neue Fenster erst nach `EnsureStarted()`.

**Regression**

Scheitert der Host schneller, als `OverlayHandle.Start()` das Fenster registriert und dessen `Create()` einreiht, ist der Overlay-Thread bereits beendet. `Post()` legt die Arbeit anschließend in eine Queue, die niemand mehr verarbeitet. Das Handle erhält weder `Ready` noch `Failed`, und CraftQueue aktiviert deshalb auch keinen externen Browser-Fallback.

Dieser Race ist besonders im eigentlich vorgesehenen Fehlerfall relevant: fehlender `WebView2Loader.dll`, fehlende WebView2-Runtime oder sehr früher Startupfehler.

**Korrekturrichtung**

- Registrierung und Startannahme unter einem gemeinsamen Lifecycle-Lock atomar machen.
- `Post()` beziehungsweise `Register()` müssen Erfolg oder Ablehnung zurückmelden.
- Ein bereits fehlgeschlagener oder beendeter Host muss jedes neue Fenster unmittelbar auf `Failed` setzen.
- Der Host darf nicht nach einem einmaligen Queue-Drain enden, während noch neue Arbeit angenommen werden kann.

### REG-02 – Die neue Outbox überschreitet Reihenfolge, Navigation und Origin

**Evidenz**

- `WebOverlay/OverlayWindow.cs:39-40` verwendet getrennte Listen für Nachrichten und Skripte.
- `WebOverlay/OverlayWindow.cs:352-366` flusht beim nächsten erfolgreichen `NavigationCompleted`, ohne Navigation-ID oder Ziel-Origin zu prüfen.
- `WebOverlay/OverlayWindow.cs:468-490` wechselt das Ziel, ohne bereits gepufferte Operationen einer Navigationsgeneration zuzuordnen.
- `WebOverlay/OverlayWindow.cs:493-535` sendet beim Flush erst alle Nachrichten und danach alle Skripte.
- `pageReady` wird nur durch öffentliche `Navigate()`-/`LoadHtml()`-Aufrufe zurückgesetzt, nicht bei einer von der Seite ausgelösten erlaubten Navigation.

**Regression**

Beispiele:

1. `Post("A") -> ExecuteScript("B") -> Post("C")` wird als `A -> C -> B` ausgeführt.
2. `Navigate(A) -> Post(secret) -> Navigate(B)` kann `secret` nach erfolgreichem Laden an B senden.
3. Eine fehlgeschlagene Navigation lässt die Outbox stehen; eine spätere erfolgreiche Seite erhält die alten Einträge.
4. Bei einer erlaubten Link-, Redirect- oder JavaScript-Navigation bleibt `pageReady` zunächst `true`; neue Aufrufe gehen damit an das alte oder gerade wechselnde Dokument statt in die Outbox.

Das ist für eine Shared Library nicht nur ein Zuverlässigkeitsproblem, sondern bei mehreren erlaubten Origins auch ein möglicher Datenabfluss.

**Korrekturrichtung**

- Eine gemeinsame FIFO-Queue mit typisierten Operationen verwenden.
- Jede Operation an Navigationsgeneration beziehungsweise Navigation-ID und erwarteten Origin binden.
- Bei Zielwechsel oder fehlgeschlagener Navigation alte Operationen definiert verwerfen oder an den Consumer zurückmelden.
- `pageReady` im erlaubten Top-Level-`NavigationStarting` zurücksetzen und erst im passenden `NavigationCompleted` derselben Navigation wieder setzen.
- Vor `PostWebMessage` und `ExecuteScript` zusätzlich den aktuellen Top-Level-Origin prüfen.

## Neue beziehungsweise verbleibende P2-Befunde

### REG-03 – Normales Dispose während Controller-Erstellung wird als Fehler gemeldet

`WebOverlay/OverlayWindow.cs:126-141` behandelt zuerst das Fehler-HRESULT und prüft erst danach `closed`. Wird das Parent-Fenster während der asynchronen Controller-Erstellung zerstört, ruft WebView2 den Completion-Handler erwartungsgemäß mit `E_ABORT` auf.

Dadurch wird ein absichtliches `Dispose()` als `Failed` publiziert. CraftQueue interpretiert dies in `CraftQueue.Client/UI/WebOverlayGate.cs:80-97` als Startfehler und kann beim Spiel- oder Plugin-Shutdown unerwartet den externen Browser öffnen.

**Korrektur:** `closed` beziehungsweise einen expliziten Cancellation-Zustand vor der HRESULT-Fehlerbehandlung prüfen. Erwartetes `E_ABORT` nach absichtlichem Close darf kein `Failed` auslösen.

### REG-04 – Ein werfender `Ready`-Handler kann die interne Fertigstellung abbrechen

`WebOverlay/OverlayWindow.cs:185-196` setzt den Zustand auf `Ready` und ruft danach den Consumer-Handler auf. Pending Navigation und `Show()` folgen erst anschließend. `WebOverlay/WebOverlays.cs:236-245` isoliert die Handler nicht; die Ausnahme wird später durch den nativen Callback-Thunk geschluckt.

Das Ergebnis ist ein dauerhaft als `Ready` markiertes Handle, das unter Umständen nie navigiert, nie sichtbar wird und kein `Failed` auslöst. Außerdem verhindert der erste werfende Handler die Ausführung weiterer Subscriber.

**Korrektur:** Interne Navigation und Sichtbarkeit vor der externen Benachrichtigung abschließen und Consumer-Handler einzeln mit Fehlerprotokollierung isolieren.

### REG-05 – `Ready` und `Failed` besitzen keine feste Thread-Affinität

Die öffentliche API dokumentiert den Overlay-Thread. `WebOverlay/WebOverlays.cs:224-233` führt einen verspätet registrierten Handler jedoch synchron auf dem abonnierenden Thread aus. Derselbe Handler läuft je nach Timing somit entweder auf dem Overlay- oder Unity-Thread; eine Ausnahme kann außerdem direkt aus dem Event-`add` zurückkehren.

**Korrektur:** Gelatchte Events immer über den Overlay-Dispatcher zustellen oder den Vertrag ausdrücklich auf einen neutralen Thread ändern und sämtliche Zustellungen konsistent daran ausrichten.

### REG-06 – Sicherheitskritische Registrierungen bleiben fail-open

`WebOverlay/OverlayWindow.cs:199-400` ignoriert weiterhin die HRESULTs von Settings und Eventregistrierungen. Danach wird bedingungslos `Ready` gesetzt. Ein Overlay kann deshalb trotz fehlgeschlagenem Navigation-, Message-, Popup-, Permission-, Close-Key- oder ProcessFailed-Handler als einsatzbereit gelten.

Besonders Navigation-, Message-, Popup- und Permission-Schutz müssen Teil des Creation-Erfolgs sein. Schlägt einer dieser Schritte fehl, muss das Overlay `Failed` statt `Ready` melden.

### REG-07 – Prozess-Recovery ist nur teilweise umgesetzt

`BrowserProcessExited` löst jetzt korrekt `Failed` aus. `RenderProcessExited` und `RenderProcessUnresponsive` werden in `WebOverlay/OverlayWindow.cs:387-400` jedoch nur protokolliert. Ein sichtbares Overlay kann dadurch als `Ready` bestehen bleiben, obwohl sein Hauptinhalt abgestürzt oder eingefroren ist.

### REG-08 – Demo behält fehlgeschlagene Handles

Die neuen `Failed`-Handler im Demo protokollieren nur. Sie disposen und nullen `overlay` beziehungsweise `hud` nicht. Nach einem Fehler toggeln F10/F11 deshalb weiterhin ein totes Handle, bis das Plugin zerstört wird.

## CraftQueue-Integration

Verifiziert behoben:

- Der separate Default-Shortcut `LeftShift+F9` ist jetzt erreichbar; BepInEx' exakter Modifier-Abgleich blockiert ihn nicht mehr hinter dem normalen F9-Pfad.
- Der aktuelle Client referenziert `Anvil-WebOverlay, Version=1.0.0.0`.
- Ein gegen die frühere AssemblyVersion `0.0.0.0` gebauter normaler Consumer konnte mit der neuen schwach signierten DLL geladen werden.
- Der konfigurierte Haupt-Key wird für die CloseKeys grundsätzlich in einen Win32 Virtual Key übersetzt.
- Asynchrone Creation-Fehler können den Browser-Fallback auslösen, sofern REG-01 den `Failed`-Pfad nicht vollständig verschluckt.

Verbleibende Integrationsgrenzen:

- Modifier-Kombinationen können nicht vollständig als `CloseKeys` dargestellt werden; bei beispielsweise `Ctrl+F9` schließt bereits F9 allein.
- Nicht alle von Unity/BepInEx unterstützten Tasten besitzen eine Abbildung in `toVirtualKey()`.
- Ein schneller gelatchter Startupfehler kann bereits während `Toggle()` den Browser öffnen, während CraftQueue danach trotzdem noch „overlay is starting“ beziehungsweise „toggled“ meldet.
- Navigationsfehler nach erfolgreichem Controller-`Ready` lösen keinen CraftQueue-Fallback aus.

## Release-Artefakte

### WebOverlay

| Artefakt | SHA-256 |
|---|---|
| aktueller Build und Live-DLL | `C5C3B887AA84406D6691A00A22BE65AFE2C66A735AC9126DF0EF1F32434FEB50` |
| DLL im vorhandenen `Anvil-WebOverlay-v1.0.0.zip` | `EB25F1395CEADF4D5BD985CEFE33B577AD6264FEC9DC33B799FFF8A8D2BCCD56` |

Das vorhandene ZIP enthält damit noch den Vor-Fix-Code und eine ältere README. Der neue Paketgenerator erzeugt formal den richtigen Installationsbaum inklusive eigener MIT-Lizenz, Microsoft-Lizenz und Notice; das finale Paket muss dennoch nach den Codekorrekturen neu erzeugt und hashgeprüft werden.

### CraftQueue

| Artefakt | SHA-256 |
|---|---|
| aktueller Client-Build und Live-DLL | `DA1F3A642ED15A112EB8558A63146A529DD0EE5E08F2953271FDB9C54819BBAF` |
| Client im vorhandenen `maschine-CraftQueue-v1.1.0.zip` | `7D543BCDA58D43BA542E32CEF3701CE52044CBCF65122B7E0A57C2760E2619AA` |

Der Shortcut-/Fallback-Fix ist damit in keinem vorhandenen CraftQueue-ZIP enthalten. Da `1.1.0` bereits veröffentlicht wurde, sollte das alte Paket nicht still ersetzt werden; vor der nächsten Paketierung ist ein synchroner Versionsbump für Client, Server und Shared-Konstanten erforderlich.

## Build- und Teststatus

- WebOverlay Library und Demo: **0 Warnungen, 0 Fehler**.
- CraftQueue Solution: **0 Warnungen, 0 Fehler** in der vorhandenen Entwicklerumgebung.
- CraftQueue Tests: **42 bestanden, 0 fehlgeschlagen, 0 übersprungen**.
- WebOverlay und Demo tragen jetzt `AssemblyVersion 1.0.0.0`, `FileVersion 1.0.0` und `ProductVersion 1.0.0`.
- Aktuelle WebOverlay- und CraftQueue-Client-Live-DLL sind jeweils hashgleich mit dem geprüften Build.
- Beide Repositories waren nach der Prüfung sauber.
- WebOverlay besitzt weiterhin keine automatisierten Tests oder CI; die CraftQueue-Tests decken den neuen Shortcut-, Gate- und Fallbackpfad nicht ab.
- Ein frischer CraftQueue-Source-Export benötigt weiterhin die lokalen, nicht versionierten `CraftQueue.Client/Libraries`-Verknüpfungen für EFT-/BepInEx-Referenzen; der Client-Build ist außerhalb dieser vorbereiteten Umgebung nicht selbstständig reproduzierbar.

## Laufzeitstatus

Der vorhandene BepInEx-Log endet vor den Fix-Commits. Er beweist weiterhin nur das Laden des früheren WebOverlay-Builds, die erkannte WebView2-Runtime und einen angenommenen CraftQueue-Toggle. Nicht bewiesen sind für `d2a0b2a`/`f39bc93`:

- erfolgreicher Controller- und Navigationsabschluss;
- korrekte `Ready`-/`Failed`-Reihenfolge;
- Fallback bei fehlendem Loader, fehlender Runtime und Controllerfehler;
- Dispose während ausstehender Controller-Erstellung;
- Origin-Filter, Outbox und Redirectverhalten;
- Renderer-/Browserprozessfehler;
- Shutdown ohne unerwarteten Browserstart.

Beim Abschluss der Nachprüfung lief kein EFT-Prozess. Ein aktueller In-Game- und Fault-Injection-Test wurde daher nicht behauptet.

## Empfohlene Reihenfolge

1. REG-01 atomar lösen und einen reproduzierbaren „Loader fehlt“-Test ergänzen.
2. REG-02 durch eine navigationgebundene FIFO-Outbox korrigieren.
3. Erwartetes `E_ABORT` bei Dispose von echtem `Failed` trennen.
4. `Ready`-/`Failed`-Callbacks isolieren und ihre Thread-Affinität vereinheitlichen.
5. Sicherheitskritische HRESULTs fail-closed behandeln.
6. Minimaltests für Hoststart, Latching, Dispose und Outbox hinzufügen.
7. In-Game-/Fault-Injection-Matrix ausführen.
8. WebOverlay-Paket neu erzeugen; CraftQueue-Version erhöhen und anschließend dessen Paket neu bauen.

## Schlussurteil

Die Fixrunde verbessert WebOverlay deutlich und beseitigt den gefährlichsten ursprünglichen COM-Lebensdauerfehler. Der neue asynchrone Pfad ist aber noch nicht vollständig atomar, und die neue Outbox kann Operationen an das falsche Dokument weitergeben. Zusammen mit den veralteten Releasepaketen bleibt der Stand deshalb **releaseblockiert**, bis REG-01 und REG-02 korrigiert und die relevanten Laufzeittests bestanden sind.
