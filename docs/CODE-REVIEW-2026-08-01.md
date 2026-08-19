# Vollständiges Code-Review: WebOverlay

> **Historischer Bericht.** Bezieht sich auf einen aelteren Commit; Befunde, Hashes und Laufzeitgrenzen sind ueberholt. Alle als valide bestaetigten Befunde saemtlicher Review-Runden wurden bis Commit `261f0af` umgesetzt; die zugehoerige Fault-Injection-Matrix ist in `docs/FAULT-TESTS.md` festgehalten.



**Projekt:** `C:\SPT\Development\WebOverlay`
**Review-Datum:** 1. August 2026
**Review-Basis:** `e6213db11fdb5da80d520f2cc42abac684eb6cfd` (`master`)
**Consumer-Basis:** CraftQueue `18be8b3` (`main`)
**Zielruntime:** SPT 4.0.13, EFT 0.16.9.40087, BepInEx 5.4.23.2, .NET Framework 4.7.2, Windows x64
**Ergebnis:** **Nicht releasebereit.** Kein P0, aber sechs P1-Blocker in COM-Lebensdauer, Sicherheitsmodell, API-Zustand, Hauptthread-Verhalten und Veröffentlichung.

## 1. Kurzfazit

Die technische Grundidee ist gut und mehrere besonders gefährliche Interop-Details sind korrekt gelöst:

- ein gemeinsamer WebView2-Environment statt eines Browserbaums pro Mod;
- ein dedizierter STA-Thread mit Message-Pump;
- alle WebView2-Aufrufe werden auf diesen Thread serialisiert;
- die verwendeten IIDs, VTable-Slots und nativen Signaturen stimmen mit dem gebündelten SDK 1.0.3485.44 überein;
- der statische, prozesslebenslange Window-Proc beseitigt den früheren Delegate-Use-after-free;
- CraftQueues optionale Abhängigkeit ist durch `Private=false`, `NoInlining` und einen Plugin-Präsenzcheck grundsätzlich sauber isoliert.

Der aktuelle Stand darf trotzdem noch nicht veröffentlicht werden. Die wichtigsten Gründe sind:

1. Nach einem 30-Sekunden-Timeout kann ein weiterhin nativ referenzierter Environment-Callback freigegeben werden. Ein späteres `Invoke` oder `Release` kann EFT nativ abstürzen lassen.
2. Die Web↔Host-Brücke hat keine Origin-Grenze. Nach Redirect, Link-Navigation, XSS oder HTTP-Manipulation bleibt eine fremde Seite voll nachrichtenberechtigt.
3. `Create()` liefert Erfolg, bevor Fenster, Controller, Einstellungen und Transparenz erfolgreich erstellt wurden. CraftQueue unterdrückt daraufhin den Browser-Fallback und behält ein totes Handle.
4. Der erste Aufruf kann den Unity-Hauptthread bis zu 30 Sekunden blockieren.
5. Die gebauten DLLs tragen trotz Projektwerten nur `0.0.0.0` und keine Company-/Produktmetadaten.
6. Es gibt noch keinen installierbaren, lizenzvollständigen Releasekanal; der bereits von CraftQueue verlinkte GitHub-Pfad ist nicht auflösbar.

## 2. Prioritäten

| Priorität | Bedeutung in diesem Bericht |
|---|---|
| P0 | unmittelbare, breit auslösbare Datenzerstörung oder Codeausführung; keine gefunden |
| P1 | Releaseblocker: nativer Prozessabsturz, Sicherheitsgrenze, zentraler Vertragsbruch oder nicht nutzbarer Release |
| P2 | erheblicher Zuverlässigkeits-, Integrations-, Datenschutz- oder Wartungsfehler |
| P3 | Hardening, Diagnose, Dokumentation oder begrenzter Randfall |

## 3. P1-Befunde

### WOV-01 – Environment-Timeout kann einen weiterhin nativ gehaltenen COM-Callback freigeben

**Evidenz**

- `WebOverlay/OverlayHost.cs:185-225`: asynchrone Environment-Erzeugung und interner 30-Sekunden-Timeout.
- `WebOverlay/OverlayHost.cs:269-285`: `closeEverything()` entsorgt anschließend `environmentCallback`.
- `WebOverlay/Interop/ComCallback.cs:37-38,102-134`: ein eigener Referenzzähler existiert, `Dispose()` ignoriert ihn aber und gibt Objekt sowie VTable sofort frei.

**Auslöser**

`CreateCoreWebView2EnvironmentWithOptions` hält den Completion-Handler noch nativ, benötigt aber länger als 30 Sekunden, etwa durch Runtime-/Profilprobleme, Sicherheitssoftware oder einen hängenden Browserstart. `createEnvironment()` läuft in den Timeout, der Host beendet seinen Thread und `Dispose()` ruft `FreeHGlobal` auf beide Callback-Blöcke auf.

**Auswirkung**

Ein späteres natives `Invoke`, `Release` oder Cleanup greift auf freigegebenen Speicher zu. Das ist kein managed Fehlerpfad, sondern kann Access Violation, Heap-Korruption und einen vollständigen EFT-Absturz auslösen.

**Korrektur**

- Callback-Speicher erst freigeben, wenn die Owner-Referenz abgegeben wurde **und** der echte COM-Referenzzähler null erreicht.
- `Dispose` per `Interlocked.Exchange` idempotent machen.
- Der Timeout darf nur den wartenden Consumer beenden; er darf keinen möglicherweise noch registrierten nativen Handler vernichten.
- Wenn die native Operation nicht abbrechbar ist, Callback und Thread bis zum garantierten Completion-/Release-Punkt am Leben halten.

**Wichtige Abgrenzung**

Der zunächst ähnlich wirkende Pfad „Controller wird während Create sofort disposed“ ist laut dem zum Loader gehörenden `WebView2.idl:3852-3856` abgesichert: Wird das Parent-HWND per `DestroyWindow` zerstört, ruft WebView2 den Controller-Completion-Handler synchron vor Rückkehr aus `DestroyWindow` mit `E_ABORT` auf. Dieser Pfad wird daher **nicht** als Finding gewertet.

### WOV-02 – Die native Nachrichtenbrücke besitzt keine Origin-Grenze

**Evidenz**

- `WebOverlay/OverlayWindow.cs:177-196`: `WebMessageReceived` liest nur den String; `WebMessageReceivedEventArgs.Source` wird nicht gelesen.
- `WebOverlay/OverlayWindow.cs:210-249`: Navigation, Host-Nachricht und Script-Ausführung sind ohne aktuelle Source-/Origin-Prüfung möglich.
- `WebOverlay/Interop/WebView2Api.cs:44-65`: weder Source-/Navigation- noch Cancellation-Slots sind angebunden.
- `WebOverlay/WebOverlays.cs:124-147`: die öffentliche API liefert dem Consumer keine Herkunft mit.

**Auslöser**

Eine vertrauenswürdige lokale Mod-Seite folgt einem Link oder Redirect, enthält eine XSS-Lücke oder wird auf einer HTTP-Verbindung manipuliert. Die neue Top-Level-Seite kann danach weiterhin `window.chrome.webview.postMessage(...)` aufrufen. Umgekehrt sendet der Mod spätere `Post()`- und `ExecuteScript()`-Aufrufe an das inzwischen fremde Dokument.

**Auswirkung**

Ein Consumer kann Herkunft nicht validieren und könnte fremde Strings als privilegierte Mod-, Inventar- oder Profilaktion ausführen. Hostdaten können an eine unerwartete Seite gesendet werden. Microsoft fordert für WebView2 ausdrücklich, vor Webnachrichten, `PostWebMessage` und `ExecuteScript` den aktuellen Origin zu prüfen und unerwünschte Navigation zu blockieren: [Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security).

**Korrektur**

- Pro Overlay eine explizite Origin-/Navigation-Allowlist; sicherer Default ist nur der ursprünglich geladene Origin.
- `NavigationStarting` und Frame-Navigation prüfen und standardmäßig blockieren.
- Source aus den EventArgs lesen und öffentlich als strukturiertes Event statt nur `Action<string>` liefern.
- Vor ausgehenden Nachrichten/Scripts die aktuelle Top-Level-Source prüfen.
- Nachrichtenlänge, Version und Schema validieren.
- Direktes HTML vorzugsweise unter einem eindeutigen virtuellen HTTPS-Host mit CSP betreiben.

**CraftQueue heute:** CraftQueue abonniert `MessageReceived` nicht. Der aktuelle Consumer hat deshalb noch keinen Web→Native-Command-Pfad; der Bibliotheksvertrag bleibt dennoch unsicher.

### WOV-03 – `Create()` meldet Erfolg, bevor ein funktionsfähiges Overlay existiert

**Evidenz**

- `WebOverlay/WebOverlays.cs:31-40`: null wird nur zurückgegeben, wenn die gemeinsame Environment nicht startet.
- `WebOverlay/WebOverlays.cs:175-180`: `Start()` registriert `window.Create()` lediglich in der Queue und liefert bedingungslos `true`.
- `WebOverlay/OverlayWindow.cs:61-125`: Fenster-, Controller-, WebView-, Settings-, Event- und HUD-Erzeugung können danach noch scheitern.
- `CraftQueue.Client/UI/WebOverlayGate.cs:38-62`: CraftQueue speichert jedes non-null Handle und liefert Erfolg.
- `CraftQueue.Client/Plugin.cs:1166-1173`: dadurch wird der externe Browser nicht geöffnet.

**Auslöser**

Die WebView2-Runtime ist vorhanden, aber `CreateWindowEx`, Controller-Erzeugung, `get_CoreWebView2`, eine kritische Eventregistrierung oder HUD-Transparenz schlägt fehl. Dokumentierte Beispiele sind ungültiges HWND, UDF-Berechtigung, Runtime-Update oder ein inkompatibler Profilzustand.

**Auswirkung**

CraftQueue loggt „toggled“, behält ein unsichtbares/totes Handle und unterdrückt dauerhaft seinen Browser-Fallback. Weitere F9-Aufrufe toggeln nur dieses Handle. Die Aussage in `README.md:40-42`, ein null-Check sei immer der Fallback, ist damit falsch.

**Korrektur**

- Explizite Zustandsmaschine `Creating -> Ready | Failed -> Closed`.
- Bevorzugt eine asynchrone `CreateAsync`-/Ready-/Failed-API statt synchronem Schein-Erfolg.
- Jeder kritische HRESULT-/Win32-Fehler muss in `Failed` einfließen.
- CraftQueue muss bei `Failed` Handle und URL verwerfen, disposen und den externen Browser öffnen.
- Erfolgslogging erst nach Controller, Settings, Events, Navigation und sichtbarem Fenster.

### WOV-04 – Der erste Aufruf kann EFT bis zu 30 Sekunden im Unity-Hauptthread einfrieren

**Evidenz**

- `WebOverlay/OverlayHost.cs:44-75`: `EnsureStarted()` wartet synchron bis zu 30 Sekunden.
- `CraftQueue.Client/Plugin.cs:76-79,1141-1205`: F9 ruft diesen Pfad direkt aus `Update()` auf.

**Auswirkung**

Bei langsamer oder hängender Runtime steht der gesamte Unity-Hauptthread. Menüs, Rendering und im Raid auch zeitkritische Gameplay-/Netzwerkverarbeitung pausieren. „Everything is safe to call from Unity's thread“ ist für diese Latenz nicht ausreichend.

**Korrektur**

- Keine blockierende Wait-Operation im Unity-Thread.
- Environment asynchron vorwärmen oder ein Pending-Handle/`CreateAsync` liefern.
- Consumer über `Ready`/`Failed` informieren und bei Fehler asynchron fallbacken.
- Startzeit messen und diagnostisch loggen.

### WOV-05 – Versions- und Produktmetadaten landen nicht in den DLLs

**Evidenz**

- `WebOverlay/WebOverlay.csproj:11-17` und `WebOverlay.Demo/WebOverlay.Demo.csproj:11-17` setzen Company, Version, AssemblyVersion und FileVersion.
- Beide Projekte sind klassische Nicht-SDK-Projekte und erzeugen daraus keine Assembly-Attribute.
- Frischer Release-Rebuild:
  - `Anvil-WebOverlay.dll`: Assembly/File/Product `0.0.0.0`, Company leer.
  - `Anvil-WebOverlayDemo.dll`: Assembly/File/Product `0.0.0.0`, Company leer.
- BepInEx zeigt nur wegen `Branding.PluginVersion` trotzdem `1.0.0` an.

**Auswirkung**

Pluginanzeige, Dateieigenschaften und Assembly-Identität widersprechen einander. Es gibt keine belastbare ABI-/Release-Baseline; die lokale Pflicht zur synchronen Versionierung ist verletzt. CraftQueue wurde bereits gegen `Anvil-WebOverlay, Version=0.0.0.0` kompiliert.

**Korrektur**

- Echte Assemblyattribute über `Properties/AssemblyInfo.cs` oder ein geeignet modernisiertes Projekt erzeugen.
- Plugin-, Assembly-, File- und ProductVersion aus einer einzigen Quelle speisen.
- Nach der Änderung CraftQueue neu bauen und im echten BepInEx/Mono-Loader testen.
- API-Baseline und Assembly-Metadaten automatisiert prüfen.

### WOV-06 – Kein installierbarer, lizenzvollständiger Releasekanal

**Evidenz**

- Das lokale Repository besitzt keinen Remote und keinen Tag.
- `https://github.com/maschine34675/WebOverlay` ist für `gh repo view` nicht auflösbar, wird aber in Company-Feld, CraftQueue-README und Linktext bereits als Bezugsquelle verwendet.
- Ein normaler Build liefert nur DLL/PDB; `OverlayHost.cs:163-170` verlangt `WebView2Loader.dll` direkt neben der Plugin-DLL.
- Nur das Deploy-Target kopiert Loader und Microsoft-Texte (`WebOverlay.csproj:57-66`).
- Das Deploy-Target kopiert die eigene MIT-`LICENSE` nicht; auch im Live-Ordner fehlt sie.
- README dokumentiert weder Installationsbaum noch reproduzierbaren Release-/ZIP-Befehl.

**Auswirkung**

CraftQueue-Nutzer können die bereits beworbene optionale Abhängigkeit nicht beziehen. Manuelle DLL-Kopie ohne Loader funktioniert nicht. Eine binäre Distribution ohne eigenen MIT-Text ist lizenzseitig unvollständig.

**Korrektur**

- Reproduzierbares Staging-/ZIP-Target mit exakt diesen Dateien:
  - `BepInEx/plugins/<finaler Name>/<Library>.dll`
  - `WebView2Loader.dll`
  - eigene `LICENSE`
  - `WebView2-LICENSE.txt`
  - `WebView2-NOTICE.txt`
- Öffentlichen Downloadpfad/Release erst anlegen, dann CraftQueue-Link prüfen.
- Paketmanifest automatisiert gegen Allowlist testen; keine Spiel-/BepInEx-Assemblies einpacken.

## 4. P2-Befunde

### WOV-07 – Gemeinsames, dauerhaftes Browserprofil ohne Isolation oder Lösch-API

**Evidenz:** `OverlayHost.cs:12-20,185-191` verwendet für alle Mods, Fenster, SPT-Installationen und Spielstarts `%LOCALAPPDATA%\WebOverlay\BrowserData`. Die wenigen Settings in `OverlayWindow.cs:127-148` ändern Profilpersistenz nicht.

**Risiko:** Cookies, Local Storage, Cache, Verlauf, Berechtigungsentscheidungen, Download-Historie und allgemeine Autofill-Daten bleiben erhalten. WebView2s allgemeines Autofill ist standardmäßig aktiv. Origins trennen Webstorage voneinander, aber die Library schafft keine Mod-/Profilgrenze und dokumentiert die persistente gemeinsame Datenhaltung nicht.

**Korrektur:** Mod-GUID/Profile-ID in `OverlayOptions`; getrennte WebView2-Profile, für reine Mod-Panels standardmäßig InPrivate/ephemer; allgemeines Autofill deaktivieren; Clear-/Delete-API und dokumentierte Lifecycle-Regeln. Microsoft beschreibt den persistierten Inhalt in [Manage user data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder).

### WOV-08 – Popup, Downloads, Berechtigungen und Browserdialoge bleiben unkontrolliert

**Evidenz:** `OverlayWindow.cs:127-148` setzt nur WebMessages, Statusbar, Kontextmenü und DevTools. Es gibt keine Handler für `NewWindowRequested`, `DownloadStarting`, `PermissionRequested` oder `ProcessFailed`, und keine Abschaltung für Scriptdialoge, Browser-Accelerators oder Autofill.

**Risiko:** Eine Seite kann Popupfenster öffnen; der Edge-Popupblocker ist in WebView2 dafür deaktiviert. Downloads landen ohne Hostentscheidung im Standardpfad, Permission-Prompts können erscheinen/persistieren, `alert`/`prompt` kann die Oberfläche blockieren, und Ctrl+P/F3/F5/Zoom bleiben aktiv.

**Korrektur:** Sichere Panel-Defaults: Popups, Downloads und Berechtigungen verweigern; Scriptdialoge, allgemeines Autofill und Browser-Accelerators deaktivieren; einzelne Fähigkeiten nur explizit über Optionen freischalten.

### WOV-09 – `ICoreWebView2` wird pro Overlay geleakt

**Evidenz:** `OverlayWindow.cs:92-100` erhält über `get_CoreWebView2` eine caller-eigene COM-Referenz. `OverlayWindow.cs:283-307` schließt/releases nur den Controller und setzt `webView` ohne `Marshal.Release` auf null.

**Auswirkung:** Wiederholtes Erstellen/Entsorgen hinterlässt COM-Objekte. `Controller.Close()` beendet zwar synchron Browserressourcen und Eventhandler, ersetzt aber nicht das Release der separat erworbenen Interface-Referenz.

**Korrektur:** Nach `Controller.Close()` die gespeicherte WebView-Referenz in einem robusten `finally` genau einmal releasen; danach nullen. Einen Create/Dispose-Stresstest mit Prozess-/Private-Bytes und COM-Zählung ergänzen.

### WOV-10 – `Post`, `ExecuteScript` und DevTools gehen vor Ready bzw. über Navigationen verloren

**Evidenz:** `WebOverlays.cs:194-202` stellt Aufrufe in die Hostqueue; `OverlayWindow.cs:210-249` puffert nur URL/HTML und verwirft die übrigen Methoden bei `webView == 0`. `README.md:20-29` zeigt genau den fehlerhaften Sofort-`Post` nach `Create`.

WebView2 sendet Hostnachrichten asynchron; findet vorher eine Navigation statt, wird die Nachricht laut API-Vertrag nicht zugestellt.

**Korrektur:** Ready- und NavigationCompleted-Signal, dokumentierter Page-Handshake und optionales bounded Outbox-Puffern. Das Beispiel darf erst nach Seitenbereitschaft posten.

### WOV-11 – Host-Startup/-Shutdown ist keine atomare Zustandsmaschine

**Evidenz:** `OverlayHost.cs:49-83,99-138`. `Shutdown()` setzt nur `running=false`; `run()` setzt es beim Eintritt wieder `true`. Es gibt keinen `Stopping`-/`Stopped`-Zustand, keinen Join und keine Sperre für spätere Posts. `thread` wird nie zurückgesetzt.

**Risiko:** Ein Shutdown während des Threadstarts kann verloren gehen. Während/ nach Stop angenommene Arbeit bleibt unabarbeitet und hält HTML-/Script-Closures fest. Ein Neustart ist unmöglich, aber nicht eindeutig als terminaler Zustand modelliert.

**Korrektur:** synchronisierte Zustände `NotStarted/Starting/Ready/Stopping/Stopped/Failed`; separates Shutdown-Signal; ab `Stopping` keine Registrierungen/Posts; begrenzter Join; Queue kontrolliert abbrechen oder leeren.

### WOV-12 – Browser-/Rendererfehler werden weder diagnostiziert noch wiederhergestellt

**Evidenz:** Es gibt keine `ProcessFailed`- oder `BrowserProcessExited`-Registrierung. Microsoft empfiehlt, mindestens Browserexit, Rendererexit und unresponsive Renderer zu behandeln: [Handling process-related events in WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-related-events).

**Auswirkung:** Nach Browsercrash ist das Handle geschlossen/tot, bleibt öffentlich aber scheinbar gültig; nach Renderercrash bleibt eine Fehlerseite. CraftQueue kann keinen Browser-Fallback auslösen.

**Korrektur:** ProcessFailed anbinden; Zustand/Fehler öffentlich melden; Reload für geeignete Rendererfehler, vollständige Environment-/Controller-Neuerzeugung für Browserexit; Recovery drosseln und loggen.

### WOV-13 – Sichtbare Fenster verfolgen EFT-Fenster, Displaymodus und HWND-Lebensdauer nicht

**Evidenz:** `WebOverlayPlugin.cs:17-24,40-55` sucht das Unity-HWND einmal in `Awake`. `OverlayWindow.cs:252-265,512-565` positioniert nur beim Show. Es gibt kein Owner-Move/Resize/Display-Change-Tracking.

**Auswirkung:** Bewegung, Monitor-/Auflösungswechsel oder Fullscreen-Umschaltung lassen Panel/HUD an falscher Position/Größe. Ein später ungültiges Owner-HWND wird nicht neu ermittelt. Ein Wechsel zu Exclusive Fullscreen wird bei bereits sichtbarem Overlay nicht abgefangen.

**Korrektur:** HWND vor Create/Show validieren und neu auflösen; Ownergröße/-position überwachen; sichtbare HUDs aktualisieren; bei Exclusive Fullscreen ausblenden/fehlschlagen.

### WOV-14 – Öffentlicher Sichtbarkeits-/Closed-/Toggle-Zustand ist inkonsistent

**Evidenz**

- `OverlayWindow.cs:55` wird auf dem Overlaythread geschrieben; `WebOverlays.cs:173` liest ohne `volatile`/Lock/Interlocked aus Unity.
- `OverlayWindow.cs:283-309` setzt beim terminalen Close `IsVisible` nicht false und feuert `Closed` nicht, obwohl `WebOverlays.cs:152-153` dies verspricht.
- `WebOverlays.cs:186-192` toggelt anhand von `IsVisible`, nicht `desiredVisible`.

**Konkreter Fehler:** Während der Controller noch startet, ist `IsVisible=false`, obwohl die gewünschte Standardsichtbarkeit bereits true ist. Ein zweiter CraftQueue-F9-Druck soll schließen, ruft aber nochmals `Show()` auf und kann ein leeres Startfenster anzeigen.

**Korrektur:** Ein atomar veröffentlichter State; `Toggle` muss den gewünschten Zustand toggeln; terminales Close setzt unsichtbar und feuert `Closed` genau einmal.

### WOV-15 – CraftQueue bewirbt zwei nicht funktionierende Shortcut-Pfade

**Evidenz**

- `CraftQueue.Client/Plugin.cs:59-63,76-79`: der Default `KeyboardShortcut(F9).IsDown()` verlangt in BepInEx 5.4.23.2 die **exakte** Kombination.
- `Plugin.cs:1182-1187`: der spätere Shift-Test ist daher mit Shift+F9 nie erreichbar.
- `WebOverlayGate.cs:43-51`: Titel und CloseKeys bleiben fest F9/Escape, obwohl der Öffnen-Shortcut frei konfigurierbar ist.
- `CraftQueue/README.md:81` bewirbt Shift+F9 und Schließen über den konfigurierbaren F9-Pfad.

**Auswirkung:** Shift+F9 öffnet nicht den Browser, sondern löst gar nichts aus. Nach Umbelegung erreicht die neue Taste das fokussierte Overlay nicht; bei rahmenlosem Modus bleibt nur Escape, entgegen Beschreibung.

**Korrektur:** eigener konfigurierbarer „extern öffnen“-Shortcut, Default Shift+F9, vor dem normalen Shortcut prüfen; `forceExternal` explizit weiterreichen. CloseKey aus dem konfigurierten MainKey sauber auf Win32-VK abbilden oder nur Escape fest zusagen.

### WOV-16 – Keine automatisierten Tests, API-Baseline oder beweiskräftige Runtime-Telemetrie

**Evidenz:** kein Testprojekt, keine CI, kein API-/Paket-Baseline-Test. Das vorhandene Demo ist manuell. Die Logzeile „toggled“ entsteht vor Controller-Completion und beweist keine sichtbare Seite.

**Auswirkung:** Ein falscher VTable-Slot kann laut eigener README den Prozess nativ beenden; API-/Packaging-Regressionen bleiben unbemerkt. Der aktuelle Laufzeitlog beweist nur Environmentstart und Consumer-Aufruf, nicht Controller, Navigation oder Darstellung.

**Korrektur:** ABI-Konstantentest gegen festgehaltene SDK-Version; isolierter Subprozess-Smoke-Test; API-Snapshot; Package-Allowlist; Consumer-Build; ControllerReady/NavigationCompleted/Failed-Telemetrie; CI-Rebuild.

### WOV-17 – Build und Deploy sind nur lokal reproduzierbar und unzureichend validiert

**Evidenz:** `LangVersion=latest`, kein `global.json`/Toolset-Pin, direkte Referenzen auf eine beliebige Live-SPT-Installation. `SptRoot` fällt auf `C:\SPT`; der Leerstring-Check im Deploy ist dadurch wirkungslos. Ein falscher Pfad kann einen neuen, falschen Verzeichnisbaum erzeugen.

**Korrektur:** Compiler-/SDK-Version festhalten; unterstützte SPT-/BepInEx-/EFT-Matrix dokumentieren; vor Deploy Markerdateien wie `BepInEx/core/BepInEx.dll` und EFT Managed Assemblies prüfen; Release und Deploy strikt getrennt halten.

## 5. P3-Hardening

### WOV-18 – CloseKeys werden in WebView2 nicht als verarbeitet markiert

`OverlayWindow.cs:155-207` versteckt das Fenster, setzt aber `AcceleratorKeyPressedEventArgs.Handled` nicht. Browser/Page können die Taste weiterverarbeiten; Auto-Repeat wird nicht gefiltert. `put_Handled` anbinden und bei einem übernommenen CloseKey sofort setzen.

### WOV-19 – HUD-Fail-Safe prüft nicht die eigentliche Win32-Transparenz

`OverlayWindow.cs:319-320,379-389` ignoriert Fehler von `CreateSolidBrush` und `SetLayeredWindowAttributes`. Scheitert einer davon, kann trotz dokumentiertem Fail-Safe eine dunkle, bildschirmfüllende Click-through-Fläche erscheinen. Rückgaben und Win32-Fehler prüfen; erst danach HUD verfügbar/sichtbar markieren.

### WOV-20 – Kritische HRESULTs und Eventtokens werden verworfen

`OverlayWindow.cs:94-96,127-175,177-196,467-509` ignoriert zahlreiche HRESULTs; Eventtokens werden nicht gespeichert. Besonders eine fehlgeschlagene Keyregistrierung kann ein rahmenloses Fenster ohne zugesicherten CloseKey erzeugen. Kritische Fehler müssen Creation auf `Failed` setzen; Tokens/HRESULTs diagnostisch erfassen.

### WOV-21 – Cleanup kann nach dem ersten Fehler alle weiteren Ressourcen überspringen

`OverlayHost.cs:269-293` umschließt das gesamte Teardown mit einem einzigen leeren Catch. Wirft ein Fenstercleanup, werden folgende Fenster, Environment und Dispatcher nicht mehr geschlossen und es gibt kein Log. Jede Ressource einzeln in `try/finally` schließen und Fehler aggregiert loggen.

### WOV-22 – Queue und Idle-Pump sind unnötig unbeschränkt

`OverlayHost.cs:24,78-83,118-123,228-240` leert eine unbegrenzte Queue vollständig vor dem nächsten Message-Pump und wacht auch idle alle fünf Millisekunden auf. Ein flutender Consumer kann alle Overlays aushungern; der Prozess hat dauerhaft rund 200 Poll-Zyklen pro Sekunde. Batchlimit/Coalescing und blockierendes Message-Wait verwenden.

### WOV-23 – Native Parent-Position-Notification fehlt

Nach Bewegung/Größenänderung des Hostfensters wird `ICoreWebView2Controller.NotifyParentWindowPositionChanged` nicht gerufen. WebView2 benötigt dies für korrekte Position von Dialogen, Tooltips und Accessibility. Zusammen mit WOV-13 ergänzen.

### WOV-24 – Release-/Consumer-Dokumentation ist unvollständig

- README zeigt kein `<Private>false</Private>` und warnt Consumer nicht davor, die Shared-Library in eigene ZIPs zu kopieren.
- Keine XML-Dokumentationsdatei für die gute öffentliche API-Dokumentation im Source.
- Debug und Release teilen Optimierung/`pdbonly`.
- `<Authors>maschine</Authors>` fehlt.
- Windows-x64-, SPT-, BepInEx- und getestete Runtime-Version sind nicht als Matrix dokumentiert.
- Die Aussage „Windows 10 dark mode“ gilt nicht für jede alte Windows-10-Build; Attribute 19/20 unterscheiden sich historisch. Kosmetisch, kein Laufzeitblocker.

## 6. Identitäts-/Namenskonvention: Entscheidung vor Release

Der aktuelle Stand verwendet:

- GUID `com.anvil.weboverlay` (`Branding.cs:13`)
- Plugin-/Assemblyname `Anvil-WebOverlay` (`Branding.cs:10-15`, `WebOverlay.csproj:10`)
- Demo `com.anvil.weboverlay.demo` / `Anvil-WebOverlayDemo`
- Company-Link auf den GitHub-Account `maschine34675`

Die verbindliche lokale Konvention verlangt für ein neues, unveröffentlichtes Einteilerprojekt:

- `com.maschine.WebOverlay`
- `maschine-WebOverlay`
- Company `https://github.com/maschine34675/WebOverlay`
- Authors `maschine`

WebOverlay besitzt noch keinen Remote/Tag/Release, ist aber bereits unter der Anvil-Identität in veröffentlichtem CraftQueue-Code und dessen README fest eingebaut. Deshalb ist dies ein **P1-Releaseentscheid, aber keine automatische Umbenennung**: Entweder die Abweichung ausdrücklich als neue Markenregel genehmigen oder koordiniert migrieren und CraftQueue inklusive Kompatibilitätsstrategie neu bauen/veröffentlichen. Ein stilles Rename würde die optionale Dependency vorhandener CraftQueue-Binaries brechen.

## 7. CraftQueue-Integration: geprüft

| Bereich | Ergebnis |
|---|---|
| Soft Dependency | gut: Plugin-Präsenzcheck, `NoInlining`, Bibliothekstypen hinter Gate |
| Assembly Copy | gut: `CraftQueue.Client.csproj:95-98` setzt `Private=false` |
| Unity-Thread | gut: CraftQueue registriert keine WebOverlay-Callbacks und berührt daher EFT-State nicht vom Overlaythread |
| Fallback bei fehlender Library/Environment | grundsätzlich vorhanden |
| Fallback bei Controller-/Fensterfehler | defekt durch WOV-03 |
| Erster Start | blockiert Unity bis zu 30 Sekunden, WOV-04 |
| Shift+F9 | unerreichbar, WOV-15 |
| umbelegter CloseKey | nicht an Overlay weitergegeben, WOV-15 |
| URL | `http://<SPT-host>:<port>/?token=...`; bei LAN/Remote unverschlüsselt. Origin-Allowlisting allein schützt nicht vor Manipulation innerhalb desselben HTTP-Origin |
| Native Message Bridge | aktuell nicht genutzt; WOV-02 ist für CraftQueue noch kein direkter Native-Command-Exploit |
| Shutdown | Consumer disposed sein Handle, aber Hostzustand/COM-Timeout bleiben Bibliotheksprobleme |

## 8. Verifizierte positive Befunde

1. **ABI:** Alle verwendeten IIDs, Basisschnittstellen-Slots, `Controller2`-Slot 27, Delegate-Signaturen und Strukturen stimmen mit `Microsoft.Web.WebView2` 1.0.3485.44 überein.
2. **Loader:** x64, Version 1.0.3485.44, gültig Microsoft-signiert, SHA-256-identisch zum lokalen offiziellen NuGet-Paket.
3. **Threadmodell:** eigener STA-Thread, eigener Message-Pump und serialisierte WebView-Aufrufe entsprechen dem Microsoft-Modell: [Threading model for WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model).
4. **Unity-Grenze:** Bibliotheksintern wird vom Overlaythread kein Unity-/EFT-State angefasst. Das Demo queued eingehende Nachrichten korrekt zurück nach `Update()`.
5. **COM-Teilerfolge:** gespeicherte Environment-/Controller-Pointer werden bewusst AddRef'd; Settings und `Controller2` werden korrekt released; `Controller.Close()` wird vor Callbackfreigabe synchron aufgerufen.
6. **Window-Proc:** statischer process-lifetime Thunk plus HWND-Routing beseitigt den früheren Window-Class-Delegate-Absturzpfad.
7. **Optionssnapshot:** inklusive Clone der CloseKeys; spätere Caller-Mutation verändert kein Livefenster halbseitig.
8. **HUD-Fensterflags:** `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE` und `SWP_NOACTIVATE` sind für einen komplett nicht interaktiven HUD-Modus schlüssig.
9. **Dark Frame:** DWM-Attribute und COLORREF-Werte des neuen Commits `e6213db` sind statisch korrekt; kein neuer Codefehler gefunden.
10. **Keine Host Objects/Folder Mapping:** Fremdseiten erhalten aktuell keine zusätzlichen nativen Objekte oder Dateisystem-Mappings.
11. **Lizenzquellen:** Microsoft-Loader, WebView2-Lizenz und Notice sind untereinander korrekt und bytegleich zum NuGet-Paket.
12. **Build:** Release-Rebuild von Library und Demo endet mit 0 Warnungen und 0 Fehlern.

## 9. Build-, Deploy- und Runtime-Evidenz

### Rebuild

```text
dotnet build WebOverlay.Demo\WebOverlay.Demo.csproj -t:Rebuild -c Release -p:DeployToSpt=false -p:SptRoot=C:\SPT
0 Warnungen, 0 Fehler
```

### Hashes des geprüften Stands

| Artefakt | SHA-256 |
|---|---|
| `WebOverlay/bin/Release/Anvil-WebOverlay.dll` | `49B8BAB120F288C82D1786D024C755D2A770E4A85C52682CD109128F5185D55C` |
| `WebOverlay.Demo/bin/Release/Anvil-WebOverlayDemo.dll` | `B4AF332C58B86CF34F1A72BDF34E9F3ED1246B2565C2A81337FB095F303D6322` |
| `ThirdParty/WebView2/WebView2Loader.dll` | `4CD3FFD5F52122432B6537B360C2317F96650928C927BB3CAA59D5807DA573A9` |
| live `Anvil-WebOverlay.dll` | identisch zum geprüften Build (`49B8...D55C`) |
| live CraftQueue Client | `CB75DCC3DA7D260C380F86A94A787AAFB75675B003F5B633CE4F95A0C30349DE` |

Die Live-DLL wurde während des Reviews extern aktualisiert; die Review-Builds selbst liefen ausdrücklich mit `DeployToSpt=false`.

### Was der vorhandene Laufzeitlog beweist

- BepInEx lud `Anvil-WebOverlay 1.0.0` und das Demo.
- WebOverlay erkannte WebView2 Runtime `150.0.4078.105`.
- CraftQueue rief den Toggle-Pfad auf.

### Was er nicht beweist

Der Log endet am 1. August 2026 um 17:25 und liegt vor dem Commit/Deploy `e6213db` um 17:44. Außerdem loggt CraftQueue den Toggle bereits vor dem asynchronen Controllerabschluss. Nicht bewiesen sind daher:

- erfolgreicher Controller und `get_CoreWebView2`;
- erfolgreiche Navigation und sichtbare Seite;
- Close-/Fokus-/Resize-Verhalten;
- transparenter HUD;
- neuer dunkler Titelrahmen;
- Shutdown ohne Leak/Crash.

Beim Abschluss des Reviews lief kein EFT-Prozess. Ein neuer In-Game-Test wurde deshalb nicht behauptet.

## 10. Erforderliche Testmatrix vor Release

1. Environment fehlt / Loader fehlt / UDF nicht schreibbar: schneller, nicht blockierender Fallback.
2. Controllerfehler und zerstörtes Owner-HWND: `Failed`, kein totes Handle, Browser-Fallback.
3. Fault Injection: Environment-Completion nach Timeout; kein Callback-UAF/Crash.
4. 1.000× Create/Ready/Dispose; stabile Handles, Private Bytes und WebView-Prozesse.
5. Zwei Mods mit mehreren Overlays gleichzeitig; faire Queue und unabhängige Zustände.
6. Redirect/Link/XSS-Test auf fremden Origin: Navigation bzw. Native Message muss blockiert werden.
7. Popup, Download, Kamera/Mikrofon/Standort, Scriptdialog, Autofill: sichere Defaults nachweisen.
8. Renderer-/Browserprozess gezielt beenden; Recovery/Fallback und Logs prüfen.
9. Fenster bewegen/resizen, Monitor/DPI/Auflösung wechseln, Borderless↔Exclusive wechseln.
10. Frameless, Transparenz, Opacity und fehlgeschlagene `SetLayeredWindowAttributes`-Fault-Injection.
11. CraftQueue F9, echtes Shift+F9, umbelegter Shortcut, Escape, Fokus in Seite und Titelrahmen aus.
12. Consumer vor/nach Library zerstören; Posts während Stop; sauberer Thread-/Browserexit.
13. Clean-PC-Installation ausschließlich aus finalem ZIP; keine Entwicklerpfade voraussetzen.
14. ZIP-Allowlist, eigene MIT-Lizenz, Microsoft-Lizenz/Notice, keine EFT-/BepInEx-DLLs.
15. BepInEx/Mono-Load nach echter AssemblyVersion-Korrektur mit neu gebautem CraftQueue.

## 11. Empfohlene Korrekturreihenfolge

1. **COM-Lebensdauer reparieren:** WOV-01, idempotenter Callback, Timeout ohne Free.
2. **Asynchronen Wahrheitszustand einführen:** WOV-03/04/10/11/14; Ready/Failed/Closed statt Schein-Erfolg und Mainthread-Wait.
3. **Sicherheitsgrenze definieren:** WOV-02/07/08; Origin-Allowlist, Source, sichere Browserdefaults, Profilisolation.
4. **Native Ressourcen und Recovery schließen:** WOV-09/12/13/18-23.
5. **CraftQueue korrigieren:** echter Browser-Shortcut, konfigurierbarer CloseKey, Failure-Fallback.
6. **Identität ausdrücklich entscheiden:** Anvil beibehalten und Ausnahme dokumentieren oder koordinierte Migration.
7. **Releasefundament bauen:** echte Assemblymetadaten, Tests/CI/API-Baseline, reproduzierbares lizenzvollständiges ZIP, funktionierender Downloadlink.
8. **In-Game-Matrix ausführen und Runtime-Evidenz festhalten.**

## 12. Releaseurteil

**Aktuell: BLOCKIERT.**

Der Code kompiliert sauber und die gefährlichste technische Annahme – die handgebundene WebView2-ABI – wurde gegen den passenden Microsoft-Header bestätigt. Das reicht hier jedoch nicht: WOV-01 kann den Prozess nativ beschädigen, WOV-02 schafft eine ungeschützte Web→Native-Vertrauensgrenze, WOV-03/04 brechen den zentralen Fallback-/Unity-Lifecycle-Vertrag, und Version/Paket/Download sind noch nicht veröffentlichungsfähig.

Nach Behebung der P1-Befunde sowie erfolgreicher In-Game-/Fault-Injection-Matrix ist ein erneutes fokussiertes Release-Review erforderlich.
