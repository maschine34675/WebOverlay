# Vollständiges Code-Review: WebOverlay

> **Historical snapshot.** This report describes the commit named in its
> header, at that date. Many of its findings were addressed in the releases
> that followed - see `CHANGELOG.md`; the ones still open recur in newer
> reports until a release closes them. It is kept as evidence, not as a
> description of the current library.

> **Historischer Bericht.** Bezieht sich auf einen aelteren Commit; Befunde, Hashes und Laufzeitgrenzen sind ueberholt. Alle als valide bestaetigten Befunde saemtlicher Review-Runden wurden bis Commit `261f0af` umgesetzt; die zugehoerige Fault-Injection-Matrix ist in `docs/FAULT-TESTS.md` festgehalten.


**Projekt:** `C:\SPT\Development\WebOverlay`  
**Review-Datum:** 1. August 2026 (Abend)  
**Review-Basis:** `9dc43ea52e1f25230b1e8be700b3aa577744d41c` (`master`, Tag `v1.0.0`)  
**Consumer:** CraftQueue (`WebOverlayGate.cs` + Soft-Dependency)  
**Zielruntime:** SPT / EFT unter BepInEx 5, .NET Framework 4.7.2, Windows x64, WebView2 SDK 1.0.3485.44  
**Art:** vollständige Bibliotheksprüfung (Architektur, Sicherheit, Lifecycle, Interop, API, Packaging, Consumer) — **ohne Code-Änderungen**

Die älteren Berichte `CODE-REVIEW-2026-08-01.md`, `REGRESSION-REVIEW-2026-08-01.md` und `FOLLOW-UP-REVIEW-2026-08-01.md` beschreiben Zwischenstände. **Maßgeblich für den aktuellen Code ist dieser Bericht.**

---

## 1. Kurzfazit

WebOverlay ist eine bewusst schlanke Shared Library (handgebaute COM-VTables, ein STA-Thread, ein gemeinsames WebView2-Environment), die das Mono-`_VtblGap`-Problem umgeht und Mods HTML-Panels sowie click-through-HUDs über dem Spiel erlaubt.

Nach den Hardening-Runden des Tages (Security-Boundary, asynchroner Lifecycle, Outbox-Bindung, Shutdown-Verhalten, fail-closed Registrierungen) ist der Stand **deutlich releasefähig**. Es gibt **keinen P0** und **keinen klaren P1-Releaseblocker** mehr im Bibliothekscode.

Verbleibend sind vor allem **P2**-Punkte um die öffentliche `ExecuteScript`-API, stille Navigationsfehler, fehlende Automatisierungstests und den noch nicht existierenden GitHub-Remote, plus mehrere **P3**-Härten.

**Gesamturteil:** für den CraftQueue-Pfad (Navigate + Toggle + Failed-Fallback) praktisch brauchbar und nachvollziehbar gehärtet; vor breiter Drittmod-Verbreitung sollten mindestens WOV-A01 und WOV-A02 sowie ein Mindestmaß an Fault-Injection-Tests erledigt werden.

---

## 2. Schweregrade

| Stufe | Bedeutung |
|---|---|
| P0 | unmittelbarer Prozess-/Daten-/Sicherheitsnotfall |
| P1 | Releaseblocker: nativer Crash, Sicherheitsgrenze, zentraler API-Vertragsbruch |
| P2 | relevanter Fehler- oder Veröffentlichungsdefekt, der zeitnah behoben werden sollte |
| P3 | Härten, Diagnose, Dokumentation, begrenzter Randfall |

---

## 3. Architektur (Kurz)

```
Unity/BepInEx (Hauptthread)
    └─ WebOverlays / OverlayHandle  ──Post──►  OverlayHost (STA + Message-Pump)
                                                   ├─ WebView2 Environment (1×)
                                                   └─ OverlayWindow[] (HWND + Controller + ICoreWebView2)
```

Bewusste Designentscheidungen, die im Code und README konsistent begründet sind:

- **Ein** Browser-Environment für alle Mods (User-Data unter `%LOCALAPPDATA%\WebOverlay`).
- **Eigener STA-Thread**, weil Unitys Thread weder STA ist noch zuverlässig pumpt.
- **Owned Popups** statt Child-Windows (Flip-Model-Swapchain).
- **Raw COM-VTables** statt Microsofts Managed Wrapper (Mono ignoriert `_VtblGap`).
- **HUD = Chroma-Key** (`DefaultBackgroundColor` α=0 + `LWA_COLORKEY`), bewusst click-through.

---

## 4. Verifizierte Stärken

### Interop

- Alle in `WebView2Api.cs` genutzten **IIDs** und **VTable-Slots** wurden gegen `WebView2.h` aus NuGet `Microsoft.Web.WebView2 1.0.3485.44` geprüft — **vollständig deckungsgleich** (Environment, Controller, Controller2, Settings/3/4, EventArgs, ICoreWebView2-Methoden inklusive FrameNavigation, ProcessFailed, OpenDevTools).
- `ComCallback` hält Delegates als Felder, ist idempotent, leaked bewusst wenn native Refs bleiben, und schluckt Exceptions an der nativen Grenze (korrekt, um Unwinding in Chromium zu verhindern).
- Statischer, prozesslebenslanger Window-Proc + `byHandle`-Map verhindert Delegate-Use-after-free bei der Window-Class.

### Sicherheit (Default)

- Navigation (Top-Level **und** Frame) fail-closed auf Allowlist.
- `LoadHtml`/`NavigateToString` über One-Shot für `data:` bzw. `about:blank` nur bei `htmlLoaded`.
- WebMessages nur von erlaubter Source; Outbox/Live-Sends nur solange das Top-Level-Dokument dem Mod-Ziel entspricht (`currentDocumentIsTarget`).
- Popups handled, Permissions denied, Script-Dialoge aus, Passwort/Autofill aus (Settings4, best-effort), Accelerator-Keys an `DevTools` gekoppelt.

### Lifecycle

- `Create` blockiert den Unity-Thread nicht; `Ready`/`Failed` sind gelatcht und Handler-isoliert.
- Dispose während Controller-Create behandelt `closed` vor Fehler-HRESULT (kein falsches `Failed` bei Absicht).
- Shutdown setzt `stopping`; `fail()` unterdrückt Consumer-`Failed` während Game-Exit (verhindert CraftQueue-Browser-Popup beim Beenden).
- `acceptingWork` verhindert Drain von Overlay-Arbeit während der Environment-Erzeugung.
- Renderer-Recovery begrenzt; danach `Failed`. Browser-Exit → `Failed`.

### Packaging / Consumer

- Release-Skript prüft Manifest (keine Game-/BepInEx-Assemblies im Zip).
- Build: **0 Warnungen, 0 Fehler**.
- Live-Plugin und `artifacts/Anvil-WebOverlay-v1.0.0.zip` sind SHA-256-identisch mit dem aktuellen Release-Build (`DA58B0B5…CA5BBB`).
- CraftQueue: Soft-Dependency, `NoInlining`-Gate, Display-Mode-Check, latched `Failed` mit Fallback — passt zum Bibliotheksvertrag.

---

## 5. Befunde

### P2

#### WOV-A01 — `ExecuteScript` übergibt `IntPtr.Zero` als Completion-Handler und ignoriert das HRESULT

**Evidenz:** `WebOverlay/OverlayWindow.cs` (`ExecuteScript`), `WebOverlay/Interop/WebView2Api.cs` (`ExecuteScriptDelegate`).

Die öffentliche API wirbt mit `ExecuteScript` zum Pushen von Live-Werten. Der Aufruf lautet sinngemäß `ExecuteScript(script, nullptr)` ohne Auswertung des Rückgabewerts. Offizielle Samples liefern immer einen `ICoreWebView2ExecuteScriptCompletedHandler`. Ob `nullptr` vom Runtime akzeptiert wird, ist nicht spezifiziert; schlimmstenfalls schlägt jeder Aufruf fehl oder ist undefiniert — und der Consumer merkt nichts.

CraftQueue und die Demo nutzen den Pfad derzeit nicht (nur `Post`), deshalb kein akuter Produktionsblocker für den bestehenden Consumer. Für Drittmods, die der README folgen, ist die API aber unzuverlässig.

**Korrektur:** No-Op-`ComCallback` mit IID des ExecuteScript-Completed-Handlers übergeben, HRESULT prüfen, bei Fehler loggen (analog `checkNavigationResult`).

---

#### WOV-A02 — Synchron abgelehnte Navigation endet nicht in einem terminalen Zustand

**Evidenz:** `checkNavigationResult` loggt nur; `pageReady` bleibt `false`; Outbox wächst bis `OutboxLimit`.

Konkrete Fälle: ungültige URL, Inline-HTML über dem WebView2-Limit (~2 MiB), andere synchronen `E_*`-Antworten von `Navigate` / `NavigateToString`. Der Consumer erhält weder `Failed` noch ein Navigations-Event; weitere `Post`/`ExecuteScript` verschwinden still in der Outbox.

**Korrektur:** Bei synchronem Fehler Outbox verwerfen, optional `Failed` oder ein dediziertes Navigationsfehler-Signal; mindestens hartes Log + öffentlicher Vertrag in der README.

---

#### WOV-A03 — Veröffentlichungsziel `github.com/maschine34675/WebOverlay` existiert nicht

**Evidenz:** `AssemblyInfo`/`FORGE.md`/CraftQueue-README verlinken das Repo; `gh repo view maschine34675/WebOverlay` → *Could not resolve*. Lokal kein `git remote`.

**Korrektur:** Repo anlegen und pushen, oder Links/Metadaten auf den tatsächlichen Ort ändern, bevor Forge/CraftQueue darauf verweisen.

---

#### WOV-A04 — Keine automatisierten Tests für die riskanten Pfade

Es gibt keine Unit-/Integrationstests und keine CI. CraftQueue-Tests decken die Gate-Kompilierung ab, nicht aber:

- Redirect zwischen zwei erlaubten Origins + Outbox  
- fehlende Runtime / fehlender Loader  
- Shutdown während Environment-/Controller-Start  
- Renderer-Crash / Unresponsive  
- `LoadHtml` > 2 MiB  
- Dispose-Race während Create  

**Korrektur:** Mindestens eine Fault-Injection-Matrix (auch manuell dokumentiert und einmal durchgespielt) vor breiter Veröffentlichung; mittelfristig Host-seitige Tests mit Fake-COM wo machbar.

---

### P3

#### WOV-A05 — Environment-Timeout kann spät noch ein Environment setzen, während `startFailed` latched bleibt

Nach 30 s Timeout ist `startFailed = true`, `EnsureStarted` liefert dauerhaft `false`. Ein verspäteter Completion-Callback kann trotzdem noch `environment` setzen und einen Browserprozess hinterlassen, den niemand mehr nutzt, bis `closeEverything` (Thread-Ende) läuft.

**Korrektur:** Callback nach Timeout ignorieren/sofort releasen, oder Timeout nur soft behandeln.

---

#### WOV-A06 — Volle Outbox verwirft Einträge ohne Log

`OutboxLimit = 100`; darüber werden `Post`/`ExecuteScript` still verworfen.

**Korrektur:** Einmalig warnen inkl. Zähler.

---

#### WOV-A07 — `IsAvailable` startet als Property den Host

`WebOverlays.IsAvailable => OverlayHost.EnsureStarted()` hat Seiteneffekte. Überraschend für Caller, die nur „fragen“ wollen.

**Korrektur:** Umbenennen/dokumentieren oder von einer reinen Query trennen.

---

#### WOV-A08 — `AreHostObjectsAllowed` bleibt auf dem Runtime-Default (`true`)

Es werden keine Host Objects registriert, das Default ist aber fail-open. Hardening: explizit `false` setzen (Settings-Slot 16), analog zu den anderen Chrome-Features.

---

#### WOV-A09 — Key-Handler-Registrierung ist nicht fail-closed

Scheitert `add_AcceleratorKeyPressed`, bleibt das Overlay „Ready“, aber `CloseKeys` greifen nicht. Mit `Frame = false` kann der User stecken bleiben.

**Korrektur:** HRESULT prüfen; bei Frameless und fehlgeschlagenen Keys → `Failed` oder erzwungenes Frame.

---

#### WOV-A10 — Öffentliche Methoden validieren `null`/leere Argumente nicht

`Navigate(null)`, `LoadHtml(null)`, `Post(null)` laufen bis zur nativen Grenze.

**Korrektur:** Früh no-op oder loggen.

---

#### WOV-A11 — Eventname `Closed` feuert bei jedem `Hide`

API-Vertrag ist dokumentiert, aber der Name legt „zerstört“ nahe. Fußangel für neue Consumer.

---

#### WOV-A12 — Demo-Version ist literal, nicht an `Branding` gebunden

`WebOverlay.Demo/Properties/AssemblyInfo.cs` trägt `"1.0.0"` fest; die Library bezieht die Version aus `Branding.PluginVersion`. Drift-Risiko.

---

#### WOV-A13 — Residualrisiko Same-Origin-Redirect

Die Outbox ist an **Origin** (bzw. Inline-Dokument) gebunden, nicht an `NavigationId`/konkrete URL. Ein Redirect `https://a/x` → `https://a/y` erhält weiterhin gepufferte Sends. Das entspricht dem klassischen Origin-Modell und ist für CraftQueue (eine App-Origin) akzeptabel; Mods mit mehreren Pfaden unter derselben Origin sollten das kennen.

---

#### WOV-A14 — Gemeinsames Browserprofil über alle Mods

Dokumentiert und bewusst. Cookies/LocalStorage/Passwortstore (auch wenn Autosave aus) teilen sich den Ordner. Vertrauensmodell: installierte Mods sind vertrauenswürdig.

---

#### WOV-A15 — `WebView2Api.Method<T>` erzeugt bei jedem Aufruf einen neuen Delegate

Funktional ok (Sync-Call rootet den Delegate), aber unnötig allokativ auf dem Overlay-Thread. Caching pro `(vtable, slot)` wäre billiger.

---

## 6. Abgleich mit früheren Reviews

| Früherer Befund | Status in `9dc43ea` |
|---|---|
| COM-Callback-UAF nach Environment-Timeout | behoben (Callback wird nicht mehr disposed) |
| Fehlende Origin-/Navigationsgrenze | behoben |
| `Create` meldet Erfolg vor Ready/Failed | behoben (asynchroner Vertrag) |
| Unity-Thread blockiert bis 30 s | behoben |
| Assembly 0.0.0.0 | behoben (`Branding` + AssemblyInfo) |
| Handle ohne Ready/Failed nach frühem Hostfehler | behoben (Thread bleibt am Leben) |
| Outbox-Reihenfolge / Cross-Origin-Flush | behoben (`currentDocumentIsTarget`) |
| Dispose während Create → falsches Failed | behoben |
| Shutdown draint Create gegen fehlendes Environment | behoben (`acceptingWork`) |
| NavigationCompleted nicht fail-closed | behoben |
| Renderer-Exhaustion ohne Failed | behoben |
| Demo NRE nach latched Failed | behoben |
| Inline `data:`-Filter | behoben |

Offen bzw. nur teilweise: synchroner Navigationsfehler (jetzt Log, kein Terminalzustand) → WOV-A02; GitHub → WOV-A03; Tests → WOV-A04.

---

## 7. CraftQueue-Vertrag (Consumer-Sicht)

`CraftQueue.Client/UI/WebOverlayGate.cs` ist vorbildlich an den Bibliotheksvertrag angepasst:

- Soft-Dependency + `NoInlining`
- Latched `Failed` nur über `created`-Local
- Fallback-Browser nur wenn nie `Ready`
- URL-Retarget bei Tokenwechsel
- Plugin prüft Exclusive Fullscreen selbst

Kein Bibliotheksbefund aus dem CraftQueue-Pfad; CraftQueue braucht `ExecuteScript` nicht.

---

## 8. Test- und Artefaktstatus

| Check | Ergebnis |
|---|---|
| `dotnet build` Release | 0 Warnungen, 0 Fehler |
| Tag `v1.0.0` | zeigt auf `9dc43ea` |
| Zip-DLL ≡ Build-DLL ≡ Live-Plugin | SHA-256 `DA58B0B5EC3C70445E309528A839EB0AAF88743279483729F34D967DD3CA5BBB` |
| VTable/IID vs. SDK 1.0.3485.44 | ok |
| Automatisierte WebOverlay-Tests | fehlen |
| GitHub-Remote | fehlt / Repo nicht auflösbar |

Bekannte manuelle Evidenz aus dem Tagesverlauf (Log ~20:16): Runtime `150.0.4078.105`, CraftQueue-Toggle, Demo-Messages — Normalpfad ok. Fault-Pfade siehe WOV-A04.

---

## 9. Empfohlene Reihenfolge

1. **WOV-A01** — `ExecuteScript` mit echtem Completion-Handler + HRESULT  
2. **WOV-A02** — synchrone Navigationsfehler terminal behandeln  
3. **WOV-A03** — GitHub/Forge-Links wahr machen  
4. **WOV-A04** — Fault-Injection-Matrix einmal hart durchspielen, Ergebnisse festhalten  
5. P3-Härten nach Bedarf (A05–A15)

---

## 10. Schlussurteil

Die Library hat den Sprung von „interessanter Prototyp mit gefährlichen COM-/Security-Kanten“ zu einer **für Shared-Mod-Dependency brauchbaren v1** geschafft. Architektur, Interop-Slots, Navigations-/Message-Boundary und asynchroner Lifecycle wirken durchdacht und sind gegen SDK und Consumer geprüft.

**Freigabeempfehlung:** CraftQueue-Nutzung und kontrollierte Veröffentlichung ok, sobald WOV-A03 (sichtbare Download-/Repo-URL) geklärt ist. Für eine allgemeine „install once, many mods“-Empfehlung an Drittautoren vorher WOV-A01/A02 schließen und die Fehlerpfade aus Abschnitt 8 zumindest manuell abhaken.
