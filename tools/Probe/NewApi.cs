using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using WebOverlay;

// Probes for the v1.4 consumer API: virtual hosts (which is also the only
// proof that ICoreWebView2_3 slot 71 is the right slot), the page-loaded
// signal, main-thread dispatch, and the classified failure reason.
internal static partial class NewApi
{
    private static int failures;

    private static void check(string label, bool passed, string detail)
    {
        if (!passed)
            failures++;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + label + " [" + detail + "]");
    }

    private static void finish()
    {
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    private static void wait(Func<bool> done, int timeoutMs)
    {
        int elapsed = 0;
        while (!done() && elapsed < timeoutMs)
        {
            Thread.Sleep(50);
            elapsed += 50;
        }
    }

    // ---- virtual hosts, page-loaded signal ------------------------------

    /// <summary>
    /// Serves a folder as https://probe.assets/ and navigates there. The page
    /// loading at all proves the mapping call reached the right vtable slot;
    /// what it reports proves the rest: a real origin, a sub-resource loaded
    /// from disk, and working localStorage - none of which an inline page has.
    /// </summary>
    internal static void VirtualHost(string scratch)
    {
        string folder = Path.Combine(scratch ?? Path.GetTempPath(), "vhost-probe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"),
            "<!doctype html><html><body style='margin:0;background:#0000FF'>"
            + "<script src='app.js'></script></body></html>");
        File.WriteAllText(Path.Combine(folder, "app.js"),
            "function probe(name, fn) { try { return name + '=' + fn(); } catch (e) { return name + '=THROWS(' + e.name + ')'; } }\n"
            + "window.chrome.webview.postMessage('vhost:' + [\n"
            + "  'asset=loaded',\n"
            + "  probe('origin', function () { return window.origin; }),\n"
            + "  probe('localStorage', function () { localStorage.setItem('k', 'v42'); return localStorage.getItem('k'); })\n"
            + "].join(' | '));");

        bool ready = false, failed = false, pageLoadedFired = false;
        string answer = null;
        var overlay = WebOverlays.Create("VirtualHostProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            VirtualHosts = new[] { new VirtualHost("probe.assets", folder) },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => pageLoadedFired = true;
        overlay.MessageReceived += m => { if (m.StartsWith("vhost:")) answer = m.Substring(6); };

        check("V0 no page is loaded before navigating", !overlay.IsPageLoaded, "IsPageLoaded=" + overlay.IsPageLoaded);
        overlay.Navigate("https://probe.assets/index.html");
        wait(() => answer != null || failed, 25000);

        // The positive mate of V7 below, which asserts a broken mapping fails:
        // a good one must reach Ready and stay there.
        check("V0b a good mapping starts the overlay rather than failing it",
            ready && !failed, "ready=" + ready + " failed=" + failed);
        check("V1 the mapped page loaded", answer != null && !failed,
            answer == null ? ("failed=" + failed + " reason=" + overlay.Failure + " " + overlay.FailureMessage) : "loaded");
        Console.WriteLine("page reports: " + (answer ?? "-"));
        check("V2 a sub-resource came from the mapped folder",
            answer != null && answer.Contains("asset=loaded"), answer ?? "-");
        check("V3 the page has the mapped origin",
            answer != null && answer.Contains("origin=https://probe.assets"), answer ?? "-");
        check("V4 localStorage works on a mapped origin",
            answer != null && answer.Contains("localStorage=v42"), answer ?? "-");

        wait(() => pageLoadedFired, 5000);
        check("V5 PageLoaded fired and IsPageLoaded is set",
            pageLoadedFired && overlay.IsPageLoaded,
            "fired=" + pageLoadedFired + " IsPageLoaded=" + overlay.IsPageLoaded);
        check("V6 no failure was reported", overlay.Failure == OverlayFailure.Unknown && overlay.FailureMessage == null,
            overlay.Failure + " " + (overlay.FailureMessage ?? "-"));

        // A mapping that did not take is terminal (see vhost-fail for why):
        // the page cannot work, and continuing would put its host name on the
        // network.
        var bad = WebOverlays.Create("BadHostProbe", new OverlayOptions
        {
            Width = 300,
            Height = 200,
            VirtualHosts = new[]
            {
                new VirtualHost("https://not-a-host/", folder),
                new VirtualHost("missing.assets", Path.Combine(folder, "does-not-exist")),
            },
        });
        bool badReady = false, badFailed = false;
        bad.Ready += () => badReady = true;
        bad.Failed += () => badFailed = true;
        bad.LoadHtml("<p>still fine</p>");
        wait(() => badReady || badFailed, 20000);
        check("V7 a bad mapping fails the overlay", badFailed && !badReady
            && bad.Failure == OverlayFailure.VirtualHostFailed,
            "ready=" + badReady + " failed=" + badFailed + " reason=" + bad.Failure);

        overlay.Dispose();
        bad.Dispose();
        Thread.Sleep(300);
        finish();
    }

    // ---- main-thread dispatch -------------------------------------------

    /// <summary>
    /// There is no Unity here, so the probe plays the part of the plugin: it
    /// declares a pump and drains it from its own main thread. What must hold:
    /// nothing is delivered until the pump runs, and then it runs on the
    /// pumping thread.
    /// </summary>
    internal static void Dispatch()
    {
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        PropertyInfo available = host.GetProperty("MainThreadPumpAvailable", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo pump = host.GetMethod("PumpMainThread", BindingFlags.NonPublic | BindingFlags.Static);
        int mainThread = Thread.CurrentThread.ManagedThreadId;

        // First without a pump: the option must degrade, not swallow.
        available.SetValue(null, false);
        bool gotUndispatched = false;
        var plain = WebOverlays.Create("DispatchOffProbe", new OverlayOptions
        {
            Width = 300,
            Height = 200,
            DispatchOnMainThread = true,
        });
        if (plain == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        plain.MessageReceived += _ => gotUndispatched = true;
        plain.LoadHtml("<script>window.chrome.webview.postMessage('hi');</script>");
        wait(() => gotUndispatched, 20000);
        check("T1 without a pump the event still arrives", gotUndispatched, "got=" + gotUndispatched);
        plain.Dispose();

        // Now with one.
        available.SetValue(null, true);
        int handlerThread = 0;
        int deliveries = 0;
        bool loaded = false;
        var overlay = WebOverlays.Create("DispatchProbe", new OverlayOptions
        {
            Width = 300,
            Height = 200,
            DispatchOnMainThread = true,
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.PageLoaded += () => loaded = true;
        overlay.MessageReceived += _ =>
        {
            handlerThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref deliveries);
        };
        overlay.LoadHtml("<script>window.chrome.webview.postMessage('one');</script>");

        // Deliberately not pumping yet: the page has long since posted.
        Thread.Sleep(6000);
        check("T2 nothing is delivered while the pump is idle", deliveries == 0, "deliveries=" + deliveries);

        pump.Invoke(null, null);
        check("T3 pumping delivers the queued event", deliveries == 1, "deliveries=" + deliveries);
        check("T4 the handler ran on the pumping thread", handlerThread == mainThread,
            "handler=" + handlerThread + " main=" + mainThread);

        // Latched events dispatch the same way.
        bool readyThreadOk = false, readySeen = false;
        overlay.Ready += () =>
        {
            readySeen = true;
            readyThreadOk = Thread.CurrentThread.ManagedThreadId == mainThread;
        };
        check("T5 a late latched handler waits for the pump too", !readySeen, "seen=" + readySeen);
        pump.Invoke(null, null);
        check("T6 the pump delivers it on the main thread", readySeen && readyThreadOk,
            "seen=" + readySeen + " onMain=" + readyThreadOk);

        // PageLoaded went through the same path.
        pump.Invoke(null, null);
        check("T7 PageLoaded was dispatched as well", loaded, "loaded=" + loaded);

        // After Dispose, queued events are dropped rather than run against a
        // consumer that has let go.
        int before = deliveries;
        overlay.Post("ignored");
        overlay.Dispose();
        pump.Invoke(null, null);
        Thread.Sleep(500);
        pump.Invoke(null, null);
        check("T8 nothing is delivered after Dispose", deliveries == before, "deliveries=" + deliveries);

        available.SetValue(null, false);
        finish();
    }

    // ---- classified failure ---------------------------------------------

    /// <summary>
    /// Run with WebView2Loader.dll removed: the handle must say what is wrong
    /// instead of just "something failed".
    /// </summary>
    internal static void FailureKind()
    {
        // This mode needs an incomplete plugin folder, so it makes one.
        using (new HiddenLoader())
        {
            var overlay = WebOverlays.Create("FailureKindProbe", new OverlayOptions { Width = 300, Height = 200 });
            if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
            bool failed = false;
            overlay.Failed += () => failed = true;
            wait(() => failed, 20000);
            check("K1 the overlay failed as expected", failed, "failed=" + failed);
            check("K2 the reason is the missing loader", overlay.Failure == OverlayFailure.LibraryIncomplete,
                "Failure=" + overlay.Failure);
            check("K3 the exact sentence is readable from the handle",
                overlay.FailureMessage != null && overlay.FailureMessage.Contains("WebView2Loader.dll"),
                overlay.FailureMessage ?? "<null>");
            overlay.Dispose();
        }
        finish();
    }

    // ---- review follow-ups (WOV-1401, WOV-1403) --------------------------

    /// <summary>
    /// A virtual host whose folder is missing, pointed at a name that really
    /// resolves. The overlay must refuse to start and must never navigate:
    /// the alternative is a live internet page inside an origin the mod
    /// believes is its own folder, with the message bridge open.
    /// </summary>
    internal static void VirtualHostFailure()
    {
        var warnings = new System.Collections.Generic.List<string>();
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        FieldInfo logWarning = host.GetField("LogWarning", BindingFlags.NonPublic | BindingFlags.Static);
        var previous = (Action<string>)logWarning.GetValue(null);
        logWarning.SetValue(null, (Action<string>)(line =>
        {
            lock (warnings) warnings.Add(line);
            previous(line);
        }));

        bool failed = false, ready = false, pageLoaded = false;
        bool gotMessage = false;
        var overlay = WebOverlays.Create("VirtualHostFailureProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            // example.com resolves and answers over HTTPS - exactly the case
            // where a best-effort mapping would have gone to the network.
            VirtualHosts = new[] { new VirtualHost("example.com", Path.Combine(Path.GetTempPath(), "no-such-folder-42")) },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => pageLoaded = true;
        overlay.MessageReceived += _ => gotMessage = true;
        overlay.Navigate("https://example.com/");

        wait(() => failed, 25000);
        check("X1 the overlay failed instead of starting", failed && !ready,
            "failed=" + failed + " ready=" + ready);
        check("X2 the reason names the virtual host", overlay.Failure == OverlayFailure.VirtualHostFailed,
            "Failure=" + overlay.Failure + " message=" + (overlay.FailureMessage ?? "-"));

        // Give a network navigation every chance to complete before judging.
        Thread.Sleep(6000);
        check("X3 no page was loaded", !pageLoaded && !overlay.IsPageLoaded && !gotMessage,
            "PageLoaded=" + pageLoaded + " IsPageLoaded=" + overlay.IsPageLoaded + " message=" + gotMessage);
        // Creation failed before any navigation was attempted, which is why
        // nothing loaded above. The origin filter is the second line: the
        // handle is still alive, so a mod that ignores Failed and navigates
        // anyway must still be refused rather than served from the network.
        lock (warnings)
            warnings.Clear();
        overlay.Navigate("https://example.com/");
        Thread.Sleep(6000);
        bool blocked;
        lock (warnings)
            blocked = warnings.Exists(w => w.Contains("blocked navigation to https://example.com"));
        check("X4 navigating anyway is refused by the origin filter", blocked,
            blocked ? "blocked" : "warnings: " + string.Join(" / ", warnings.ToArray()));
        check("X5 still nothing loaded", !pageLoaded && !overlay.IsPageLoaded && !gotMessage,
            "PageLoaded=" + pageLoaded + " IsPageLoaded=" + overlay.IsPageLoaded + " message=" + gotMessage);

        overlay.Dispose();
        logWarning.SetValue(null, previous);
        finish();
    }

    /// <summary>
    /// A rejected LoadHtml (over the browser's 2 MB document limit) leaves the
    /// previous page on screen, so the overlay must still call itself loaded
    /// and must still talk to that page - not buffer into nothing forever.
    /// </summary>
    internal static void RejectedNavigation()
    {
        bool loaded = false, failed = false;
        int echoes = 0;
        var overlay = WebOverlays.Create("RejectedNavProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.MessageReceived += m => { if (m == "echo") Interlocked.Increment(ref echoes); };
        overlay.LoadHtml("<script>window.chrome.webview.addEventListener('message',"
            + " function () { window.chrome.webview.postMessage('echo'); });</script>");
        wait(() => loaded || failed, 20000);
        check("N1 the first page loaded", loaded && overlay.IsPageLoaded && !failed,
            "loaded=" + loaded + " IsPageLoaded=" + overlay.IsPageLoaded);

        overlay.LoadHtml("<!doctype html><html><body>" + new string('x', 3 * 1024 * 1024) + "</body></html>");
        Thread.Sleep(2500);
        check("N2 the overlay is still loaded after a rejected LoadHtml", overlay.IsPageLoaded,
            "IsPageLoaded=" + overlay.IsPageLoaded);

        overlay.Post("ping");
        wait(() => echoes > 0, 5000);
        check("N3 sends still reach the page that is actually shown", echoes > 0, "echoes=" + echoes);

        overlay.Dispose();
        finish();
    }

    // ---- script results, visibility (wishlist 5 and 6) -------------------

    /// <summary>
    /// ExecuteScript with a result: values come back, every caller is answered
    /// exactly once even when the script cannot run, overlapping calls do not
    /// cross, and a burst of one-shot COM handlers neither leaks nor crashes.
    /// </summary>
    internal static void ScriptResult()
    {
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("ScriptResultProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;

        // Queued before the overlay has been given a page at all. There is no
        // earlier page for it to belong to, so it belongs to the one that is
        // about to be named and must run on it.
        string beforeAnyPage = "pending";
        overlay.ExecuteScript("1 + 1", r => beforeAnyPage = r);

        overlay.LoadHtml("<!doctype html><html><body><div id='x'>hello</div></body></html>");

        // The supported order: sent right after the page was set, while it is
        // still loading. This one waits in the outbox and must run for real.
        string early = "pending";
        overlay.ExecuteScript("1 + 1", r => early = r);

        wait(() => loaded || failed, 20000);
        check("S1 the page loaded", loaded && !failed, "loaded=" + loaded);
        wait(() => early != "pending", 8000);
        check("S2 a script buffered while the page loads runs and answers", early == "2", "result=" + (early ?? "<null>"));
        wait(() => beforeAnyPage != "pending", 3000);
        check("S2b a script queued before the first page runs on it",
            beforeAnyPage == "2", "result=" + (beforeAnyPage ?? "<null>"));


        string text = null, number = null, missing = null;
        overlay.ExecuteScript("document.getElementById('x').textContent", r => text = r);
        overlay.ExecuteScript("40 + 2", r => number = r);
        overlay.ExecuteScript("undefined", r => missing = r);
        wait(() => text != null && number != null && missing != null, 10000);
        check("S3 a string result comes back as JSON", text == "\"hello\"", "result=" + (text ?? "<null>"));
        check("S4 overlapping calls do not cross", number == "42", "result=" + (number ?? "<null>"));
        check("S5 no value is answered as JSON null", missing == "null", "result=" + (missing ?? "<null>"));

        // A burst: every one of these allocates its own COM handler, which the
        // completion has to hand back. Crashing or hanging here is the failure.
        int answered = 0;
        int wrong = 0;
        for (int i = 0; i < 200; i++)
        {
            int expected = i;
            overlay.ExecuteScript(expected.ToString(), r =>
            {
                if (r != expected.ToString())
                    Interlocked.Increment(ref wrong);
                Interlocked.Increment(ref answered);
            });
        }
        wait(() => answered >= 200, 30000);
        check("S6 200 scripts with results all answered, each with its own value",
            answered == 200 && wrong == 0, "answered=" + answered + " wrong=" + wrong);

        // A script that cannot run answers too. WebView2 reports a failing
        // script as the JSON null rather than an error, so that is what a
        // consumer sees - it cannot be told apart from a script that really
        // evaluated to null.
        string broken = "pending";
        overlay.ExecuteScript("this is not javascript(", r => broken = r);
        wait(() => broken != "pending", 8000);
        check("S7 a failing script answers instead of hanging", broken == "null" || broken == null,
            "result=" + (broken ?? "<null>"));

        // Disposing while a script is in flight must not leave a caller
        // hanging. What the value ends up being depends on how far the script
        // got before the close - the contract is one answer, not which one
        // (close-race covers the value side with a script that cannot finish).
        int closeAnswers = 0;
        string afterDispose = "pending";
        overlay.ExecuteScript("123", r => { afterDispose = r; Interlocked.Increment(ref closeAnswers); });
        overlay.Dispose();
        wait(() => closeAnswers > 0, 8000);
        Thread.Sleep(1500);
        check("S8 a caller is answered exactly once when the overlay closes",
            closeAnswers == 1, "answers=" + closeAnswers + " result=" + (afterDispose ?? "<null>"));

        Thread.Sleep(500);
        check("S9 the process survived the burst", true, "alive");

        // A real retarget, though, drops what was meant for the page being
        // left - and the caller has to hear about it. All three run back to
        // back on the overlay thread, so the script is still in the outbox
        // when the second page replaces the first.
        string dropped = "pending";
        overlay.LoadHtml("<!doctype html><html><body>second</body></html>");
        overlay.ExecuteScript("2 + 2", r => dropped = r);
        overlay.LoadHtml("<!doctype html><html><body>third</body></html>");
        wait(() => dropped != "pending", 8000);
        check("S2c a script dropped by a retarget is answered, not left hanging",
            dropped == null, "result=" + (dropped ?? "<null>"));

        finish();
    }

    /// <summary>
    /// VisibilityChanged must report state - only real transitions, no
    /// duplicates - which is what Closed (firing on every Hide) cannot.
    /// </summary>
    internal static void Visibility()
    {
        var seen = new System.Collections.Generic.List<bool>();
        int closedCount = 0;
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("VisibilityProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.VisibilityChanged += visible => { lock (seen) seen.Add(visible); };
        overlay.Closed += () => Interlocked.Increment(ref closedCount);
        overlay.LoadHtml("<p>visible</p>");
        wait(() => loaded || failed, 20000);
        Thread.Sleep(1000);

        string trail() { lock (seen) return string.Join(",", seen.ConvertAll(v => v ? "on" : "off").ToArray()); }
        int count() { lock (seen) return seen.Count; }

        check("B1 becoming visible is reported once", count() == 1 && overlay.IsVisible, "trail=" + trail());

        overlay.Show();
        Thread.Sleep(800);
        check("B2 showing an already visible overlay reports nothing", count() == 1, "trail=" + trail());

        overlay.Hide();
        wait(() => count() >= 2, 5000);
        Thread.Sleep(500);
        check("B3 hiding reports false once", count() == 2 && !overlay.IsVisible, "trail=" + trail());

        overlay.Hide();
        Thread.Sleep(800);
        check("B4 hiding twice reports nothing extra", count() == 2, "trail=" + trail());
        check("B5 Closed still fires on every hide (unchanged until 2.0)", closedCount >= 2,
            "closed=" + closedCount);

        overlay.Show();
        wait(() => count() >= 3, 5000);
        Thread.Sleep(500);
        check("B6 showing again reports true", count() == 3, "trail=" + trail());

        overlay.Dispose();
        Thread.Sleep(1000);
        check("B7 destroying a visible overlay reports false", count() == 4, "trail=" + trail());
        finish();
    }

    // ---- review follow-ups (WOV-1501, WOV-1502, WOV-1503) ----------------

    /// <summary>
    /// A script still in flight when the overlay is disposed. The script
    /// deliberately occupies the renderer for seconds, so its completion
    /// cannot beat the close - the close itself has to answer the caller.
    /// Run twice: straight through, and with main-thread dispatch where the
    /// handle is disposed before anything is pumped.
    /// </summary>
    internal static void CloseRace()
    {
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        PropertyInfo available = host.GetProperty("MainThreadPumpAvailable", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo pump = host.GetMethod("PumpMainThread", BindingFlags.NonPublic | BindingFlags.Static);
        const string slow = "(function () { var t = Date.now(); while (Date.now() - t < 4000) {} return 7; })()";

        available.SetValue(null, false);
        bool loaded = false, failed = false;
        int answers = 0;
        string value = "pending";
        var overlay = WebOverlays.Create("CloseRaceProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.LoadHtml("<p>close race</p>");
        wait(() => loaded || failed, 20000);
        check("R1 the page loaded", loaded && !failed, "loaded=" + loaded);

        overlay.ExecuteScript(slow, r => { value = r; Interlocked.Increment(ref answers); });
        Thread.Sleep(300);
        overlay.Dispose();
        wait(() => answers > 0, 10000);
        check("R2 a script in flight is answered when the overlay closes", answers == 1,
            "answers=" + answers + " value=" + (value ?? "<null>"));
        Thread.Sleep(4000);
        check("R3 and answered exactly once, even if the completion still lands later",
            answers == 1, "answers=" + answers);

        // Now with dispatch: the handle is disposed before anything is pumped,
        // which is the case where an event would be dropped on purpose.
        available.SetValue(null, true);
        int dispatched = 0;
        string dispatchedValue = "pending";
        bool loaded2 = false, failed2 = false;
        var second = WebOverlays.Create("CloseRaceDispatchProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            DispatchOnMainThread = true,
        });
        if (second == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        second.Failed += () => failed2 = true;
        second.PageLoaded += () => loaded2 = true;
        second.LoadHtml("<p>close race</p>");
        for (int i = 0; i < 200 && !loaded2 && !failed2; i++)
        {
            pump.Invoke(null, null);
            Thread.Sleep(50);
        }
        check("R4 the second page loaded", loaded2 && !failed2, "loaded=" + loaded2);

        second.ExecuteScript("6 * 7", r => { dispatchedValue = r; Interlocked.Increment(ref dispatched); });
        second.Dispose();
        for (int i = 0; i < 100 && dispatched == 0; i++)
        {
            pump.Invoke(null, null);
            Thread.Sleep(50);
        }
        check("R5 a result is delivered even though the handle was disposed first",
            dispatched == 1, "answers=" + dispatched + " value=" + (dispatchedValue ?? "<null>"));

        // Asking a disposed handle still answers, exactly once.
        int afterDispose = 0;
        second.ExecuteScript("1", _ => Interlocked.Increment(ref afterDispose));
        for (int i = 0; i < 40 && afterDispose == 0; i++)
        {
            pump.Invoke(null, null);
            Thread.Sleep(50);
        }
        check("R6 a call on a disposed handle is answered too", afterDispose == 1, "answers=" + afterDispose);

        available.SetValue(null, false);
        finish();
    }

    /// <summary>
    /// While the game is shutting down the library keeps quiet, so a consumer
    /// cannot start a fallback on the way out. VisibilityChanged has to follow
    /// that rule, not just Failed.
    /// </summary>
    internal static void ShutdownQuiet()
    {
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        MethodInfo shutdown = host.GetMethod("Shutdown", BindingFlags.NonPublic | BindingFlags.Static);

        var seen = new System.Collections.Generic.List<bool>();
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("ShutdownQuietProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.VisibilityChanged += v => { lock (seen) seen.Add(v); };
        overlay.LoadHtml("<p>quiet</p>");
        wait(() => loaded || failed, 20000);
        Thread.Sleep(800);
        int before;
        lock (seen) before = seen.Count;
        check("Q1 the overlay became visible", before == 1 && overlay.IsVisible, "events=" + before);

        // The game is quitting: the host tears every overlay down.
        shutdown.Invoke(null, null);
        Thread.Sleep(3000);
        int after;
        lock (seen) after = seen.Count;
        check("Q2 shutdown raises no visibility event", after == before, "events=" + after);
        finish();
    }

    // ---- named channels and request/reply --------------------------------

    /// <summary>
    /// The whole channel protocol against a real page: both directions of
    /// fire-and-forget, both directions of request/reply, a request nobody
    /// answers, and the promise that plain messages are still plain. That the
    /// page can use window.overlay in its very first script is also the proof
    /// that the shim reached the right vtable slot.
    /// </summary>
    internal static void Channels()
    {
        var channelMessages = new System.Collections.Generic.List<string>();
        var rawMessages = new System.Collections.Generic.List<string>();
        bool loaded = false, failed = false;

        var overlay = WebOverlays.Create("ChannelProbe", new OverlayOptions { Width = 500, Height = 400 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.ChannelMessage += (c, p) => { lock (channelMessages) channelMessages.Add(c + "|" + p); };
        overlay.MessageReceived += m => { lock (rawMessages) rawMessages.Add(m); };
        overlay.OnRequest("mod-question", p => "answer-to-" + p);

        overlay.LoadHtml(@"<!doctype html><html><body><script>
          window.chrome.webview.postMessage('shim=' + (typeof window.overlay));
          overlay.on('tick', function (p) { overlay.send('echo', p); });
          overlay.onRequest('state', function (p) { return 'state-for-' + p; });
          overlay.onRequest('later', function (p) {
            return new Promise(function (r) { setTimeout(function () { r('eventually-' + p); }, 300); });
          });
          overlay.onRequest('never', function () { return new Promise(function () { }); });
          overlay.on('ask', function () {
            overlay.request('mod-question', 'from-page').then(function (v) { overlay.send('answered', String(v)); });
          });
          overlay.on('ask-nobody', function () {
            overlay.request('no-such-channel', '').then(function (v) { overlay.send('answered', 'nobody:' + v); });
          });
          window.chrome.webview.postMessage('plain hello');
          window.chrome.webview.postMessage(JSON.stringify({ hello: 'json', nested: { a: 1 } }));
        </script></body></html>");

        wait(() => loaded || failed, 25000);
        check("H1 the page loaded", loaded && !failed, "loaded=" + loaded + " failed=" + failed);
        Thread.Sleep(700);

        Func<System.Collections.Generic.List<string>, string, bool> has = (list, needle) =>
        {
            lock (list) return list.Exists(x => x == needle);
        };
        Func<System.Collections.Generic.List<string>, string> dump = list =>
        {
            lock (list) return list.Count == 0 ? "<empty>" : string.Join(" / ", list.ToArray());
        };

        check("H2 the shim exists before the page's own script runs",
            has(rawMessages, "shim=object"), dump(rawMessages));

        // A payload that would break naive framing: quotes, a newline, a
        // backslash, a non-ASCII character.
        const string tricky = "say \"hi\"\nand \\ end \u00fcber";
        overlay.Post("tick", tricky);
        wait(() => has(channelMessages, "echo|" + tricky), 5000);
        check("H3 a channel message survives the round trip verbatim",
            has(channelMessages, "echo|" + tricky), dump(channelMessages));

        string state = "pending";
        overlay.Request("state", "abc", v => state = v);
        wait(() => state != "pending", 5000);
        check("H4 the mod can ask the page and get an answer", state == "state-for-abc", "answer=" + (state ?? "<null>"));

        string later = "pending";
        overlay.Request("later", "xyz", v => later = v);
        wait(() => later != "pending", 5000);
        check("H5 a page answering with a promise works too", later == "eventually-xyz", "answer=" + (later ?? "<null>"));

        int neverAnswers = 0;
        string never = "pending";
        overlay.Request("never", "", v => { never = v; Interlocked.Increment(ref neverAnswers); }, 1200);
        wait(() => neverAnswers > 0, 6000);
        check("H6 a question the page never answers times out with null",
            never == null && neverAnswers == 1, "answers=" + neverAnswers + " value=" + (never ?? "<null>"));
        Thread.Sleep(1500);
        check("H7 and is answered exactly once", neverAnswers == 1, "answers=" + neverAnswers);

        overlay.Post("ask", "");
        wait(() => has(channelMessages, "answered|answer-to-from-page"), 5000);
        check("H8 the page can ask the mod and get an answer",
            has(channelMessages, "answered|answer-to-from-page"), dump(channelMessages));

        overlay.Post("ask-nobody", "");
        wait(() => has(channelMessages, "answered|nobody:null"), 5000);
        check("H9 a channel with no handler answers null instead of hanging the page",
            has(channelMessages, "answered|nobody:null"), dump(channelMessages));

        check("H10 plain messages still arrive untouched",
            has(rawMessages, "plain hello"), dump(rawMessages));
        check("H11 the page's own JSON is not mistaken for protocol traffic",
            has(rawMessages, "{\"hello\":\"json\",\"nested\":{\"a\":1}}"), dump(rawMessages));

        bool leaked;
        lock (rawMessages)
            leaked = rawMessages.Exists(m => m.Contains("__wo"));
        check("H12 protocol traffic never reaches the plain message event", !leaked, dump(rawMessages));

        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    // ---- shape ------------------------------------------------------------

    /// <summary>
    /// The shape contract: an interactive HUD cut down to the rectangles it
    /// draws in. Inside them it paints and takes the mouse; outside, the click
    /// reaches the window behind and nothing is painted. Both halves are one
    /// mechanism - see the SetShape documentation for why - so this checks
    /// both, in both states.
    /// </summary>
    internal static void Shape()
    {
        IntPtr backdrop = Program.CreateBackdrop();
        bool loaded = false, failed = false;
        int clicks = 0;
        string ready = null;

        var hud = WebOverlays.Create("ShapeProbe", new OverlayOptions
        {
            Transparent = true,
            Interactive = true,
            Width = 600,
            Height = 400,
        });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Failed += () => failed = true;
        hud.PageLoaded += () => loaded = true;
        hud.MessageReceived += m => { if (m == "clicked") Interlocked.Increment(ref clicks); };
        hud.ChannelMessage += (c, p) => { if (c == "ready") ready = p; };

        // A button inside the shape, and a red block outside it.
        hud.LoadHtml(@"<!doctype html><html><body style='margin:0'>
          <button id='b' style='position:absolute;left:50px;top:80px;width:120px;height:60px'
                  onclick=""window.chrome.webview.postMessage('clicked')"">press</button>
          <div style='position:absolute;left:250px;top:150px;width:200px;height:200px;background:#FF0000'></div>
          <script>
            overlay.on('shape', function () { overlay.setShape([document.getElementById('b')]); });
            overlay.on('unshape', function () { overlay.setShape(null); });
            overlay.send('ready', String(window.devicePixelRatio));
          </script>
        </body></html>");

        wait(() => loaded || failed, 25000);
        check("P1 the shaped HUD loaded", loaded && !failed, "loaded=" + loaded + " failed=" + failed);
        wait(() => ready != null, 5000);
        Thread.Sleep(900);

        Color outside = Program.SampleScreenPublic(350, 250);
        check("P2 unshaped, the overlay paints everywhere it wants",
            outside.R > 200 && outside.G < 60, Program.DescribeColor(outside));

        int behind = Program.BackdropClicks;
        Program.SendRealClick(350, 250);
        Program.PumpBackdrop(1200);
        check("P3 unshaped, the overlay keeps the mouse over its whole rectangle",
            Program.BackdropClicks == behind, "behind=" + (Program.BackdropClicks - behind));

        // Now cut it down to the button.
        hud.Post("shape", "");
        Thread.Sleep(1200);

        Color clipped = Program.SampleScreenPublic(350, 250);
        check("P4 shaped, what is cut away is no longer painted",
            !(clipped.R > 200 && clipped.G < 60), Program.DescribeColor(clipped));

        behind = Program.BackdropClicks;
        Program.SendRealClick(350, 250);
        Program.PumpBackdrop(1500);
        check("P5 shaped, a click outside the shape reaches the game",
            Program.BackdropClicks == behind + 1, "behind=" + (Program.BackdropClicks - behind));
        check("P6 and never reaches the page", clicks == 0, "clicks=" + clicks);

        behind = Program.BackdropClicks;
        Program.SendRealClick(110, 110);
        Program.PumpBackdrop(1500);
        wait(() => clicks > 0, 3000);
        check("P7 shaped, a click inside the shape still works the page", clicks == 1, "clicks=" + clicks);
        check("P8 and does not fall through", Program.BackdropClicks == behind,
            "behind=" + (Program.BackdropClicks - behind));

        // And back.
        hud.Post("unshape", "");
        Thread.Sleep(1200);
        Color restored = Program.SampleScreenPublic(350, 250);
        check("P9 clearing the shape restores the picture",
            restored.R > 200 && restored.G < 60, Program.DescribeColor(restored));
        behind = Program.BackdropClicks;
        Program.SendRealClick(350, 250);
        Program.PumpBackdrop(1200);
        check("P10 and the mouse", Program.BackdropClicks == behind,
            "behind=" + (Program.BackdropClicks - behind));

        // The mod side of the same contract.
        hud.SetShape(new[] { new OverlayRegion(50, 80, 120, 60) });
        Thread.Sleep(1000);
        Color viaApi = Program.SampleScreenPublic(350, 250);
        check("P11 the mod can shape the overlay itself",
            !(viaApi.R > 200 && viaApi.G < 60), Program.DescribeColor(viaApi));

        hud.Dispose();
        Program.DestroyProbeWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    // ---- can a window region carve out the mouse without carving out the
    //      picture? ---------------------------------------------------------

    /// <summary>
    /// HTTRANSPARENT only passes a click to windows of the same thread, which
    /// the game never is. The remaining candidate is SetWindowRgn, which does
    /// route clicks across processes - the question is whether it also clips
    /// what the composed overlay paints. Measured here rather than assumed.
    /// </summary>
    internal static void RegionShape()
    {
        IntPtr backdrop = Program.CreateBackdrop();
        bool loaded = false, failed = false;
        var hud = WebOverlays.Create("ShapeProbe", new OverlayOptions
        {
            Transparent = true,
            Interactive = true,
            Width = 600,
            Height = 400,
        });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Failed += () => failed = true;
        hud.PageLoaded += () => loaded = true;
        // A solid red block well outside the button, to sample.
        hud.LoadHtml(@"<!doctype html><html><body style='margin:0'>
          <div style='position:absolute;left:250px;top:150px;width:200px;height:200px;background:#FF0000'></div>
          <button style='position:absolute;left:50px;top:80px;width:120px;height:60px'>press</button>
        </body></html>");
        wait(() => loaded || failed, 25000);
        check("S1 the shape HUD loaded", loaded && !failed, "loaded=" + loaded);
        Thread.Sleep(1200);

        Color painted = Program.SampleScreenPublic(350, 250);
        check("S2 the page paints outside the button area",
            painted.R > 200 && painted.G < 60, Program.DescribeColor(painted));

        IntPtr hudWindow = Program.FindWindowByTitle("ShapeProbe");
        check("S3 the overlay window was found", hudWindow != IntPtr.Zero, hudWindow.ToString());

        // Shape the window down to the button rectangle alone.
        IntPtr region = Program.CreateRectRgnPublic(50, 80, 170, 140);
        int applied = Program.SetWindowRgnPublic(hudWindow, region, true);
        check("S4 the window region was applied", applied != 0, "result=" + applied);
        Thread.Sleep(1000);

        Color afterShape = Program.SampleScreenPublic(350, 250);
        bool stillPainted = afterShape.R > 200 && afterShape.G < 60;
        check("S5 the picture survives the region (this is what decides the feature)",
            stillPainted, Program.DescribeColor(afterShape) + (stillPainted ? "" : " - clipped away"));

        int before = Program.BackdropClicks;
        Program.SendRealClick(350, 250);
        Program.PumpBackdrop(1200);
        check("S6 a click outside the region reaches the window behind",
            Program.BackdropClicks == before + 1, "behind=" + (Program.BackdropClicks - before));

        Program.SetWindowRgnPublic(hudWindow, IntPtr.Zero, true);
        hud.Dispose();
        Program.DestroyProbeWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// Runtime geometry: the mod moves and resizes its own panel, and the
    /// remembered-bounds store stays out of it.
    /// </summary>
    internal static void BoundsApi()
    {
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("BoundsApiProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            RememberBounds = false,
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.LoadHtml("<p>bounds</p>");
        wait(() => loaded || failed, 20000);
        check("B1 the panel loaded", loaded && !failed, "loaded=" + loaded);
        Thread.Sleep(600);

        IntPtr window = Program.FindWindowByTitle("BoundsApiProbe");
        check("B2 the window is on screen", window != IntPtr.Zero, window.ToString());

        overlay.SetBounds(300, 200, 640, 480);
        Thread.Sleep(900);
        Program.RECTP after = Program.GetRect(window);
        check("B3 SetBounds moved and resized the window",
            after.left == 300 && after.top == 200
            && after.right - after.left == 640 && after.bottom - after.top == 480,
            after.left + "," + after.top + " " + (after.right - after.left) + "x" + (after.bottom - after.top));

        // Only the size, leaving the position alone.
        overlay.SetBounds(null, null, 800, 300);
        Thread.Sleep(900);
        Program.RECTP resized = Program.GetRect(window);
        check("B4 null arguments keep what they were",
            resized.left == 300 && resized.top == 200
            && resized.right - resized.left == 800 && resized.bottom - resized.top == 300,
            resized.left + "," + resized.top + " " + (resized.right - resized.left) + "x" + (resized.bottom - resized.top));

        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    // ---- review follow-ups (WOV-1601 to WOV-1603) ------------------------

    /// <summary>
    /// The three things a shape must not do: forget itself when the page
    /// sends nonsense, let the library's own channels reach the mod, and cut
    /// the title bar off a framed window.
    /// </summary>
    internal static void ShapeGuards()
    {
        IntPtr backdrop = Program.CreateBackdrop();
        bool loaded = false, failed = false;
        var seenChannels = new System.Collections.Generic.List<string>();
        var seenRequests = new System.Collections.Generic.List<string>();
        string answered = null;

        var hud = WebOverlays.Create("ShapeGuardProbe", new OverlayOptions
        {
            Transparent = true,
            Interactive = true,
            Width = 600,
            Height = 400,
        });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Failed += () => failed = true;
        hud.PageLoaded += () => loaded = true;
        hud.ChannelMessage += (c, p) => { lock (seenChannels) seenChannels.Add(c); };
        hud.OnRequest("__wo.shape", _ => { lock (seenRequests) seenRequests.Add("shape"); return "should-not-happen"; });
        hud.ChannelMessage += (c, p) => { if (c == "answered") answered = p; };

        hud.LoadHtml(@"<!doctype html><html><body style='margin:0'>
          <button id='b' style='position:absolute;left:50px;top:80px;width:120px;height:60px'>press</button>
          <div style='position:absolute;left:250px;top:150px;width:200px;height:200px;background:#FF0000'></div>
          <script>
            overlay.on('shape', function () { overlay.setShape([document.getElementById('b')]); });
            // A DOMRect-shaped object without x/w: the shim turns it into NaN.
            overlay.on('bad-shape', function () { overlay.setShape([{ left: 1, top: 1, width: 1, height: 1 }]); });
            overlay.on('reserved', function () {
              overlay.send('__wo.something', 'should-not-arrive');
              overlay.request('__wo.shape', 'q').then(function (v) { overlay.send('answered', String(v)); });
            });
          </script>
        </body></html>");

        wait(() => loaded || failed, 25000);
        check("Q1 the guard HUD loaded", loaded && !failed, "loaded=" + loaded + " failed=" + failed);
        Thread.Sleep(700);

        hud.Post("shape", "");
        Thread.Sleep(1000);
        Color shaped = Program.SampleScreenPublic(350, 250);
        check("Q2 the shape applied", !(shaped.R > 200 && shaped.G < 60), Program.DescribeColor(shaped));

        // The page now sends a shape that cannot be read. The old one has to
        // stay: clearing it would hand the whole screen back to the overlay.
        hud.Post("bad-shape", "");
        Thread.Sleep(1200);
        Color afterBad = Program.SampleScreenPublic(350, 250);
        check("Q3 a malformed shape leaves the old one alone",
            !(afterBad.R > 200 && afterBad.G < 60), Program.DescribeColor(afterBad));
        int behind = Program.BackdropClicks;
        Program.SendRealClick(350, 250);
        Program.PumpBackdrop(1200);
        check("Q4 and the mouse still belongs to the game there",
            Program.BackdropClicks == behind + 1, "behind=" + (Program.BackdropClicks - behind));

        // Reserved channels are the library's, in both directions.
        hud.Post("reserved", "");
        wait(() => answered != null, 6000);
        check("Q5 a reserved channel never reaches the mod",
            !seenChannels.Contains("__wo.something"), string.Join(" / ", seenChannels.ToArray()));
        check("Q6 nor does a request on one", seenRequests.Count == 0, "requests=" + seenRequests.Count);
        check("Q7 and the page is answered rather than left waiting",
            answered == "null", "answer=" + (answered ?? "<none>"));

        hud.Dispose();

        // A framed window: the shape must be measured from the page, so the
        // title bar survives whatever the page asks for.
        bool framedLoaded = false;
        var panel = WebOverlays.Create("ShapeFrameProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            RememberBounds = false,
        });
        panel.PageLoaded += () => framedLoaded = true;
        panel.LoadHtml("<p>framed</p>");
        wait(() => framedLoaded, 20000);
        Thread.Sleep(600);
        panel.SetShape(new[] { new OverlayRegion(0, 0, 200, 100) });
        Thread.Sleep(900);

        IntPtr panelWindow = Program.FindWindowByTitle("ShapeFrameProbe");
        Program.RECTP box = Program.GetRegionBox(panelWindow);
        check("Q8 a framed window's shape starts below its title bar",
            box.top > 0 && box.left > 0,
            "region box " + box.left + "," + box.top + " - " + box.right + "," + box.bottom);

        panel.Dispose();
        Program.DestroyProbeWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    // ---- v1.7 consumer API -----------------------------------------------

    /// <summary>
    /// The four things of this round that do not need Unity: an answer that
    /// arrives long after the handler returned, the transparency the page is
    /// told about, the optional palette, and Show() refusing a display mode it
    /// cannot work in.
    /// </summary>
    internal static void ConsumerApi17()
    {
        var answers = new System.Collections.Generic.List<string>();
        bool loaded = false, failed = false;
        string report = null;

        // Windowed first, on purpose: a live composed HUD stops a windowed
        // controller from being created at all, which the `mixed` mode
        // measures. This mode is about the new API, not about that.
        bool plainLoaded = false;
        string plainReport = null;
        var plain = WebOverlays.Create("Api17PlainProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (plain == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        plain.PageLoaded += () => plainLoaded = true;
        plain.ChannelMessage += (c, p) => { if (c == "report") plainReport = p; };
        plain.LoadHtml(@"<!doctype html><html><body><script>
          overlay.on('describe', function () {
            var style = getComputedStyle(document.documentElement);
            overlay.send('report', 'class=' + document.documentElement.className
              + ' | gold=' + (style.getPropertyValue('--wo-gold').trim() || '<unset>'));
          });
        </script></body></html>");
        wait(() => plainLoaded, 20000);
        check("A0 the ordinary overlay loaded", plainLoaded, "loaded=" + plainLoaded
            + " reason=" + plain.Failure + " " + (plain.FailureMessage ?? ""));

        var hud = WebOverlays.Create("Api17Probe", new OverlayOptions
        {
            Transparent = true,
            Width = 500,
            Height = 400,
            InjectTheme = true,
        });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Failed += () => failed = true;
        hud.PageLoaded += () => loaded = true;
        hud.ChannelMessage += (c, p) => { lock (answers) if (c == "answer") answers.Add(p); };
        hud.ChannelMessage += (c, p) => { if (c == "report") report = p; };

        // A question the mod cannot answer on the spot: the reply comes from a
        // background thread a second later, long after the handler returned.
        hud.OnRequest("slow-work", (payload, reply) =>
        {
            var worker = new Thread(() =>
            {
                Thread.Sleep(1000);
                reply("done-" + payload);
            });
            worker.IsBackground = true;
            worker.Start();
        });
        // A handler that throws before replying still answers.
        hud.OnRequest("throws", (payload, reply) => throw new InvalidOperationException("boom"));

        hud.LoadHtml(@"<!doctype html><html><body style='margin:0'>
          <script>
            overlay.on('go', function () {
              overlay.request('slow-work', 'x', 8000).then(function (v) { overlay.send('answer', String(v)); });
              overlay.request('throws', '', 4000).then(function (v) { overlay.send('answer', 'threw:' + String(v)); });
            });
            overlay.on('describe', function () {
              var style = getComputedStyle(document.documentElement);
              overlay.send('report', [
                'class=' + document.documentElement.className,
                'env=' + (overlay.env ? overlay.env.transparency : '<none>'),
                'gold=' + style.getPropertyValue('--wo-gold').trim()
              ].join(' | '));
            });
          </script>
        </body></html>");

        wait(() => loaded || failed, 25000);
        check("A1 the page loaded", loaded && !failed, "loaded=" + loaded + " failed=" + failed);
        Thread.Sleep(600);

        hud.Post("describe", "");
        wait(() => report != null, 5000);
        check("A2 the page is told which transparency it got",
            report != null && report.Contains("class=wo-composed") && report.Contains("env=composition"),
            report ?? "<none>");
        check("A3 the handle agrees", hud.Transparency == OverlayTransparency.Composition,
            hud.Transparency.ToString());
        check("A4 the palette is on the page when asked for",
            report != null && report.Contains("gold=#c2ad6d"), report ?? "<none>");

        hud.Post("go", "");
        wait(() => answers.Count >= 2, 12000);
        lock (answers)
        {
            check("A5 an answer that arrives a second later still reaches the page",
                answers.Contains("done-x"), string.Join(" / ", answers.ToArray()));
            check("A6 a handler that throws answers null instead of hanging the page",
                answers.Contains("threw:null"), string.Join(" / ", answers.ToArray()));
        }

        // Without the theme, nothing is set - a mod with its own look is left
        // alone.
        plain.Post("describe", "");
        wait(() => plainReport != null, 5000);
        check("A7 an opaque overlay says so and carries no palette",
            plainReport != null && plainReport.Contains("class=wo-opaque") && plainReport.Contains("gold=<unset>"),
            plainReport ?? "<none>");
        check("A8 and reports no transparency on the handle",
            plain.Transparency == OverlayTransparency.None, plain.Transparency.ToString());

        // Show() has to refuse a display mode a window cannot live in, whatever
        // the consumer forgot to check.
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        FieldInfo probe = host.GetField("DisplayModeProbe", BindingFlags.NonPublic | BindingFlags.Static);
        probe.SetValue(null, (Func<bool>)(() => false));
        bool wentInvisible = false;
        plain.VisibilityChanged += visible => { if (!visible) wentInvisible = true; };
        plain.Hide();
        Thread.Sleep(500);
        wentInvisible = false;
        plain.Show();
        Thread.Sleep(1200);
        check("A9 Show refuses a display mode that cannot host a window",
            !plain.IsVisible, "IsVisible=" + plain.IsVisible);
        // And says nothing about it: the overlay was already hidden, so a
        // refusal is not a transition. A consumer trusting VisibilityChanged
        // as state must not see one invented here.
        check("A9b and reports no visibility change for the refusal",
            !wentInvisible, "wentInvisible=" + wentInvisible);
        probe.SetValue(null, null);
        plain.Show();
        Thread.Sleep(1200);
        check("A10 and shows again once the mode is fine", plain.IsVisible, "IsVisible=" + plain.IsVisible);

        hud.Dispose();
        plain.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// Does a composed HUD stop a second, ordinary overlay from being created
    /// while it is alive? Asked because the v1.7 probe hit ERROR_INVALID_STATE
    /// on exactly that combination, and a HUD plus a panel is what two mods
    /// running together look like.
    /// </summary>
    internal static void Mixed()
    {
        Func<string, OverlayOptions, string> open = (title, options) =>
        {
            bool ready = false, failed = false;
            var o = WebOverlays.Create(title, options);
            if (o == null) return "create returned null";
            o.Ready += () => ready = true;
            o.Failed += () => failed = true;
            o.LoadHtml("<p>" + title + "</p>");
            wait(() => ready || failed, 20000);
            string state = ready && !failed ? "ready" : "FAILED " + o.Failure + " " + o.FailureMessage;
            openedForMixed.Add(o);
            return state;
        };

        string hud = open("MixedHud", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        check("M1 a composed HUD comes up", hud == "ready", hud);

        string panel = open("MixedPanel", new OverlayOptions { Width = 400, Height = 300 });
        check("M2 an ordinary overlay comes up while the HUD is alive", panel == "ready", panel);

        string second = open("MixedHud2", new OverlayOptions { Transparent = true, Width = 300, Height = 200 });
        check("M3 a second composed HUD comes up too", second == "ready", second);

        foreach (var o in openedForMixed) o.Dispose();
        Thread.Sleep(500);
        string after = open("MixedAfter", new OverlayOptions { Width = 300, Height = 200 });
        check("M4 and one more after disposing them all", after == "ready", after);

        foreach (var o in openedForMixed) o.Dispose();
        finish();
    }

    private static readonly System.Collections.Generic.List<IWebOverlay> openedForMixed =
        new System.Collections.Generic.List<IWebOverlay>();

    /// <summary>The other order, and whether the block is sticky.</summary>
    internal static void MixedReverse()
    {
        Func<string, OverlayOptions, string> open = (title, options) =>
        {
            bool ready = false, failed = false;
            var o = WebOverlays.Create(title, options);
            if (o == null) return "create returned null";
            o.Ready += () => ready = true;
            o.Failed += () => failed = true;
            o.LoadHtml("<p>" + title + "</p>");
            wait(() => ready || failed, 20000);
            openedForMixed.Add(o);
            return ready && !failed ? "ready" : "FAILED " + o.Failure;
        };

        string panel = open("RevPanel", new OverlayOptions { Width = 400, Height = 300 });
        check("N1 an ordinary overlay first", panel == "ready", panel);

        string hud = open("RevHud", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        check("N2 a composed HUD while it is alive", hud == "ready", hud);

        string second = open("RevPanel2", new OverlayOptions { Width = 300, Height = 200 });
        check("N3 another ordinary overlay after the HUD exists", second == "ready", second);

        // Dispose only the HUD: is the block tied to a live composed
        // controller, or to the environment having ever made one?
        foreach (var o in openedForMixed)
            if (o != null && o.Transparency == OverlayTransparency.Composition)
                o.Dispose();
        Thread.Sleep(1500);
        string afterHud = open("RevPanel3", new OverlayOptions { Width = 300, Height = 200 });
        check("N4 an ordinary overlay once every composed one is gone", afterHud == "ready", afterHud);

        foreach (var o in openedForMixed) o.Dispose();
        finish();
    }

    /// <summary>
    /// Is it the environment that blocks a windowed controller, or the
    /// DirectComposition device the composed path creates on this thread?
    /// This makes the device without any overlay at all and then asks for a
    /// perfectly ordinary window.
    /// </summary>
    internal static void DcompFirst()
    {
        Guid desktopDevice = new Guid("5F4633FE-1E08-4CB8-8C75-CE24333F5602");
        int hr = DCompositionCreateDevice2(IntPtr.Zero, ref desktopDevice, out IntPtr device);
        check("D1 a DirectComposition device was created", hr == 0 && device != IntPtr.Zero,
            "hr=0x" + hr.ToString("X8"));

        bool ready = false, failed = false;
        var overlay = WebOverlays.Create("DcompFirstProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.LoadHtml("<p>plain</p>");
        wait(() => ready || failed, 25000);
        check("D2 an ordinary overlay comes up with a DComp device on the thread",
            ready && !failed, ready ? "ready" : "FAILED " + overlay.Failure + " " + overlay.FailureMessage);

        overlay.Dispose();
        finish();
    }

    [System.Runtime.InteropServices.DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    /// <summary>
    /// What the second browser actually costs, measured rather than guessed:
    /// processes and memory with a windowed overlay alone, then with a
    /// transparent one added.
    /// </summary>
    internal static void Footprint()
    {
        Func<string> browsers = () =>
        {
            var all = System.Diagnostics.Process.GetProcessesByName("msedgewebview2");
            long bytes = 0;
            foreach (var p in all) { try { bytes += p.WorkingSet64; } catch { } }
            return all.Length + " processes, " + (bytes / (1024 * 1024)) + " MB";
        };

        Console.WriteLine("before any overlay: " + browsers());

        bool ready = false;
        var panel = WebOverlays.Create("FootprintPanel", new OverlayOptions { Width = 400, Height = 300 });
        panel.Ready += () => ready = true;
        panel.LoadHtml("<p>panel</p>");
        wait(() => ready, 25000);
        Thread.Sleep(2500);
        string windowedOnly = browsers();
        Console.WriteLine("windowed overlay only: " + windowedOnly);

        bool hudReady = false;
        var hud = WebOverlays.Create("FootprintHud", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        hud.Ready += () => hudReady = true;
        hud.LoadHtml("<p>hud</p>");
        wait(() => hudReady, 25000);
        Thread.Sleep(3500);
        Console.WriteLine("plus a transparent overlay: " + browsers());

        check("F1 both are up at once", ready && hudReady, "panel=" + ready + " hud=" + hudReady);
        panel.Dispose();
        hud.Dispose();
        finish();
    }

    // ---- the second browser's failure paths ------------------------------

    /// <summary>
    /// An overlay that is already up must stay answerable while a second
    /// browser is starting for someone else, and a second browser that cannot
    /// start must not be remembered as "the main one will do" - that would
    /// turn one bad moment into the old defect for the rest of the session.
    /// </summary>
    internal static void SpareBrowser()
    {
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        FieldInfo spare = host.GetField("spareEnvironment", BindingFlags.NonPublic | BindingFlags.Static);

        // A transparent overlay first, so the next windowed one needs a spare.
        bool hudLoaded = false;
        int echoes = 0;
        var hud = WebOverlays.Create("SpareHud", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.PageLoaded += () => hudLoaded = true;
        hud.MessageReceived += m => { if (m == "echo") Interlocked.Increment(ref echoes); };
        hud.LoadHtml(@"<script>window.chrome.webview.addEventListener('message',
            function () { window.chrome.webview.postMessage('echo'); });</script>");
        wait(() => hudLoaded, 25000);
        check("S1 the transparent overlay is up", hudLoaded, "loaded=" + hudLoaded);

        // Open a windowed overlay - that starts the second browser - and talk
        // to the first overlay while it does.
        bool panelReady = false, panelFailed = false;
        var panel = WebOverlays.Create("SparePanel", new OverlayOptions { Width = 400, Height = 300 });
        panel.Ready += () => panelReady = true;
        panel.Failed += () => panelFailed = true;
        panel.LoadHtml("<p>panel</p>");
        int before = echoes;
        hud.Post("ping-while-starting");
        wait(() => echoes > before, 6000);
        check("S2 an overlay stays answerable while the second browser starts",
            echoes > before, "echoes=" + (echoes - before));

        wait(() => panelReady || panelFailed, 25000);
        check("S3 and the windowed overlay comes up", panelReady && !panelFailed,
            panelReady ? "ready" : "FAILED " + panel.Failure);
        check("S4 in a browser of its own", (IntPtr)spare.GetValue(null) != IntPtr.Zero,
            "spare=" + spare.GetValue(null));
        panel.Dispose();
        hud.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// A second browser whose data folder cannot be made. The browser must
    /// never be asked - told to use a folder it cannot create, it puts a modal
    /// error box on the player's screen - and the failure must not be
    /// remembered, so the next overlay tries again.
    /// </summary>
    internal static void SpareFolder()
    {
        var warnings = new System.Collections.Generic.List<string>();
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        FieldInfo logWarning = host.GetField("LogWarning", BindingFlags.NonPublic | BindingFlags.Static);
        var previous = (Action<string>)logWarning.GetValue(null);
        logWarning.SetValue(null, (Action<string>)(line =>
        {
            lock (warnings) warnings.Add(line);
            previous(line);
        }));
        FieldInfo spare = host.GetField("spareEnvironment", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo folder = host.GetField("userDataFolder", BindingFlags.NonPublic | BindingFlags.Static);

        bool hudLoaded = false;
        var hud = WebOverlays.Create("FolderHud", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.PageLoaded += () => hudLoaded = true;
        hud.LoadHtml("<p>hud</p>");
        wait(() => hudLoaded, 25000);
        check("Y1 the transparent overlay is up", hudLoaded, "loaded=" + hudLoaded);

        // A folder name the file system rejects outright, so the failure
        // happens before anything asks a browser for anything.
        string realFolder = (string)folder.GetValue(null);
        folder.SetValue(null, "C:\\web|overlay");
        bool ready = false, failed = false;
        var panel = WebOverlays.Create("FolderPanel", new OverlayOptions { Width = 400, Height = 300 });
        panel.Ready += () => ready = true;
        panel.Failed += () => failed = true;
        panel.LoadHtml("<p>panel</p>");
        wait(() => ready || failed, 30000);
        check("Y2 the overlay fails instead of hanging", failed && !ready,
            "ready=" + ready + " failed=" + failed + " reason=" + panel.Failure);

        bool refused, announced;
        lock (warnings)
        {
            refused = warnings.Exists(w => w.Contains("as a browser data folder"));
            announced = warnings.Exists(w => w.Contains("its data folder could not be created"));
        }
        check("Y3 the folder was refused by the library, not by the browser", refused && announced,
            "refused=" + refused + " announced=" + announced);
        check("Y4 and no browser was left behind", (IntPtr)spare.GetValue(null) == IntPtr.Zero,
            "spare=" + spare.GetValue(null));
        panel.Dispose();

        // With a usable folder again, the next windowed overlay gets its
        // second browser - the failure was not latched.
        folder.SetValue(null, realFolder);
        bool retryReady = false, retryFailed = false;
        var retry = WebOverlays.Create("FolderRetry", new OverlayOptions { Width = 300, Height = 200 });
        retry.Ready += () => retryReady = true;
        retry.Failed += () => retryFailed = true;
        retry.LoadHtml("<p>retry</p>");
        wait(() => retryReady || retryFailed, 30000);
        check("Y5 the next windowed overlay tries again and succeeds",
            retryReady && !retryFailed, retryReady ? "ready" : "FAILED " + retry.Failure);
        check("Y6 in a browser of its own", (IntPtr)spare.GetValue(null) != IntPtr.Zero,
            "spare=" + spare.GetValue(null));

        retry.Dispose();
        hud.Dispose();
        logWarning.SetValue(null, previous);
        Thread.Sleep(300);
        finish();
    }

    // ---- retained state and latest-wins -----------------------------------

    /// <summary>
    /// A page that reloads must get the mod's configuration back without the
    /// mod noticing the reload - that is what retained messages are for, and
    /// a reload is exactly what the library does by itself after a renderer
    /// crash. Served from a real origin, because reloading an inline document
    /// is a navigation the origin filter refuses on purpose.
    /// </summary>
    internal static void Retained(string scratch)
    {
        string folder = Path.Combine(scratch ?? Path.GetTempPath(), "retain-probe");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), @"<!doctype html><html><body><script>
          var seen = [];
          overlay.on('config', function (p) { seen.push('config=' + p); });
          overlay.on('once', function (p) { seen.push('once=' + p); });
          overlay.on('report', function () { overlay.send('report', seen.join(' | ')); });
          overlay.on('reload', function () { location.reload(); });
          overlay.send('loaded', String(seen.length));
        </script></body></html>");

        string report = null;
        var loads = new System.Collections.Generic.List<string>();
        bool failed = false;
        var overlay = WebOverlays.Create("RetainProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            VirtualHosts = new[] { new VirtualHost("retain.assets", folder) },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.ChannelMessage += (c, p) =>
        {
            if (c == "report") report = p;
            else if (c == "loaded") { lock (loads) loads.Add(p); }
        };

        // Sent before the page exists: it has to arrive anyway, retained or
        // not, because the outbox holds it.
        overlay.Post("config", "first", PostOptions.Retain);
        overlay.Navigate("https://retain.assets/index.html");
        wait(() => loads.Count > 0 || failed, 25000);
        check("R1 the page loaded", loads.Count > 0 && !failed, failed ? "failed" : "loads=" + loads.Count);

        overlay.Post("config", "second", PostOptions.Retain);
        overlay.Post("once", "not-retained");
        Thread.Sleep(700);
        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        check("R2 both kinds of message arrive normally, each once",
            report != null && report.Contains("config=first") && report.Contains("config=second")
            && report.Contains("once=not-retained")
            && report.Split(new[] { "config=first" }, StringSplitOptions.None).Length == 2,
            report ?? "<none>");

        // Now the page reloads, as it would after a renderer crash.
        overlay.Post("reload", "");
        wait(() => loads.Count > 1, 20000);
        check("R3 the page came back", loads.Count > 1, "loads=" + loads.Count);
        Thread.Sleep(900);
        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        check("R4 the fresh page has the retained value, and only the newest",
            report != null && report.Contains("config=second") && !report.Contains("config=first"),
            report ?? "<none>");
        check("R5 and not the message that was never retained",
            report != null && !report.Contains("once="), report ?? "<none>");

        // Retargeting forgets: the state belonged to the page that is gone.
        overlay.LoadHtml(@"<!doctype html><html><body><script>
          var seen = [];
          overlay.on('config', function (p) { seen.push('config=' + p); });
          overlay.on('report', function () { overlay.send('report', seen.join(' | ') || '<nothing>'); });
          overlay.send('loaded', 'inline');
        </script></body></html>");
        wait(() => loads.Count > 2, 20000);
        Thread.Sleep(700);
        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        check("R6 a page the mod retargeted to starts clean",
            report == "<nothing>", report ?? "<none>");

        // WOV-1801: a retarget that is REJECTED leaves the old page on screen,
        // so the state that belongs to that page has to survive with it. Back
        // to the real page first, since only a page with an origin can reload
        // itself the way a renderer crash makes the library reload it.
        int before = loads.Count;
        overlay.Navigate("https://retain.assets/index.html");
        wait(() => loads.Count > before, 20000);
        overlay.Post("config", "third", PostOptions.Retain);
        Thread.Sleep(500);

        // Rejected synchronously: over the browser's 2 MB limit for inline
        // markup. Nothing navigates; the page from a moment ago is still live.
        overlay.LoadHtml("<!doctype html><html><body>"
            + new string('x', 3 * 1024 * 1024) + "</body></html>");
        Thread.Sleep(500);
        check("R7 a rejected retarget leaves the page live", overlay.IsPageLoaded,
            "IsPageLoaded=" + overlay.IsPageLoaded);

        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        check("R8 and the page still answers", report != null && report.Contains("config=third"),
            report ?? "<none>");

        // The point of the row: the rejection must not have taken the retained
        // state with it, or the next reload hands the page its defaults while
        // the mod still believes its configuration is up.
        before = loads.Count;
        overlay.Post("reload", "");
        wait(() => loads.Count > before, 20000);
        Thread.Sleep(900);
        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        // Exactly once, and only through the replay: a rejection must put
        // nothing back on the wire to the page that never went away, or a page
        // that never reloads would see its configuration arrive twice.
        check("R9 retained state survives a rejected retarget and replays after a reload, once",
            report != null && report.Contains("config=third")
            && report.Split(new[] { "config=third" }, StringSplitOptions.None).Length == 2,
            report ?? "<none>");

        overlay.Dispose();
        Thread.Sleep(300);

        // The same question one layer down. A page named before the browser is
        // up is not navigated to on the spot - it is recorded and replayed by
        // startPendingNavigation once the view exists. If THAT attempt is
        // rejected, the overlay must be back to "no page named": otherwise the
        // target stays pointing at a page that can never load, and the mod's
        // next LoadHtml looks like a retarget away from it - which throws away
        // state that never belonged to any page at all.
        // The control first, so the comparison is honest: the same sequence
        // without the rejection, on an inline page.
        string control = null;
        bool controlLoaded = false;
        var plain = WebOverlays.Create("RetainProbe0", new OverlayOptions { Width = 400, Height = 300 });
        if (plain == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        plain.ChannelMessage += (c, p) => { if (c == "report") control = p; };
        plain.PageLoaded += () => controlLoaded = true;
        plain.Post("early", "plain");
        plain.Post("kept", "retained", PostOptions.Retain);
        plain.LoadHtml(@"<!doctype html><html><body><script>
          var seen = [];
          overlay.on('early', function (p) { seen.push('early=' + p); });
          overlay.on('kept', function (p) { seen.push('kept=' + p); });
          overlay.on('report', function () { overlay.send('report', seen.join(' | ') || '<nothing>'); });
        </script></body></html>");
        wait(() => controlLoaded, 20000);
        Thread.Sleep(700);
        plain.Post("report", "");
        wait(() => control != null, 5000);
        check("R10a control: state set up before the first inline page reaches it",
            control != null && control.Contains("early=plain") && control.Contains("kept=retained"),
            control ?? "<none>");
        plain.Dispose();
        Thread.Sleep(300);

        string second = null;
        bool secondLoaded = false, secondFailed = false;
        var fresh = WebOverlays.Create("RetainProbe2", new OverlayOptions { Width = 400, Height = 300 });
        if (fresh == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        fresh.Failed += () => secondFailed = true;
        fresh.ChannelMessage += (c, p) => { if (c == "report") second = p; };
        fresh.PageLoaded += () => secondLoaded = true;

        fresh.Post("early", "plain");
        fresh.Post("kept", "retained", PostOptions.Retain);

        // Rejected: over the 2 MB limit, and no page was ever named.
        fresh.LoadHtml("<!doctype html><html><body>"
            + new string('x', 3 * 1024 * 1024) + "</body></html>");
        Thread.Sleep(400);

        fresh.LoadHtml(@"<!doctype html><html><body><script>
          var seen = [];
          overlay.on('early', function (p) { seen.push('early=' + p); });
          overlay.on('kept', function (p) { seen.push('kept=' + p); });
          overlay.on('report', function () { overlay.send('report', seen.join(' | ') || '<nothing>'); });
        </script></body></html>");
        wait(() => secondLoaded || secondFailed, 20000);
        Thread.Sleep(700);
        fresh.Post("report", "");
        wait(() => second != null, 5000);
        check("R10 a navigation rejected while the browser was starting leaves no target behind",
            second != null && second.Contains("early=plain") && second.Contains("kept=retained"),
            (second ?? "<none>") + " loaded=" + secondLoaded + " failed=" + secondFailed
                + " IsPageLoaded=" + fresh.IsPageLoaded);

        fresh.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// Latest-wins, where the library can actually deliver it: a burst sent
    /// before the page is ready collapses to the newest, and a page that asks
    /// for it gets one payload per frame instead of the whole backlog.
    /// </summary>
    internal static void LatestOnly()
    {
        string report = null;
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("LatestProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.ChannelMessage += (c, p) => { if (c == "report") report = p; };

        // Queued before the page can receive anything: 200 frames on one
        // channel, and one ordinary message that must survive untouched.
        for (int i = 1; i <= 200; i++)
            overlay.Post("frame", i.ToString(), PostOptions.LatestOnly);
        overlay.Post("keep", "kept");
        overlay.LoadHtml(@"<!doctype html><html><body><script>
          var frames = [], keeps = [], coalesced = [];
          overlay.on('frame', function (p) { frames.push(p); });
          overlay.on('keep', function (p) { keeps.push(p); });
          overlay.on('stream', function (p) { coalesced.push(p); }, { latest: true });
          overlay.on('report', function () {
            overlay.send('report', 'frames=' + frames.length + ':' + frames.join(',')
              + ' keeps=' + keeps.join(',') + ' coalesced=' + coalesced.length + ':' + coalesced.join(','));
          });
        </script></body></html>");

        wait(() => loaded || failed, 25000);
        check("L1 the page loaded", loaded && !failed, "loaded=" + loaded);
        Thread.Sleep(700);
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        check("L2 a burst held by the library collapses to the newest",
            report != null && report.Contains("frames=1:200"), report ?? "<none>");
        check("L3 while an ordinary message is untouched",
            report != null && report.Contains("keeps=kept"), report ?? "<none>");

        // The page's own side: 50 sent in one go, delivered as the newest per
        // frame rather than all fifty.
        for (int i = 1; i <= 50; i++)
            overlay.Post("stream", i.ToString());
        Thread.Sleep(1500);
        report = null;
        overlay.Post("report", "");
        wait(() => report != null, 5000);
        bool coalesced = false;
        if (report != null)
        {
            int at = report.IndexOf("coalesced=");
            string tail = at < 0 ? "" : report.Substring(at + "coalesced=".Length);
            int count = int.Parse(tail.Split(':')[0]);
            coalesced = count > 0 && count < 50 && tail.Contains("50");
        }
        check("L4 a page that asks for the newest gets that, not the backlog",
            coalesced, report ?? "<none>");

        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// Manual dispatch: nothing arrives until the consumer asks, and then it
    /// arrives on the asking thread - which is the whole point, since that is
    /// whose frame budget it spends.
    /// </summary>
    internal static void ManualPump()
    {
        int mainThread = Thread.CurrentThread.ManagedThreadId;
        int handlerThread = 0, messages = 0;
        bool loaded = false;
        var overlay = WebOverlays.Create("ManualProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            Dispatch = EventDispatch.Manual,
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.PageLoaded += () => loaded = true;
        overlay.ChannelMessage += (c, p) =>
        {
            handlerThread = Thread.CurrentThread.ManagedThreadId;
            Interlocked.Increment(ref messages);
        };
        overlay.LoadHtml(@"<!doctype html><html><body><script>
          overlay.on('go', function () { overlay.send('back', '1'); });
          overlay.send('ready', '');
        </script></body></html>");

        // Nothing is delivered while nobody pumps - including PageLoaded.
        Thread.Sleep(9000);
        check("U1 nothing arrives before the consumer pumps",
            !loaded && messages == 0, "loaded=" + loaded + " messages=" + messages);

        overlay.PumpEvents();
        check("U2 pumping delivers what was waiting", loaded && messages > 0,
            "loaded=" + loaded + " messages=" + messages);
        check("U3 on the pumping thread", handlerThread == mainThread,
            "handler=" + handlerThread + " main=" + mainThread);

        int before = messages;
        overlay.Post("go", "");
        Thread.Sleep(1500);
        check("U4 and still nothing without a pump", messages == before, "messages=" + messages);
        overlay.PumpEvents();
        check("U5 until the next one", messages > before, "messages=" + messages);

        // The promise a Manual overlay must still keep. A script result is
        // owed to one caller, and queueing it behind a pump nobody will ever
        // call again is the one way "answered exactly once" becomes never - so
        // disposing has to hand out what it owes on the spot.
        string answered = "pending";
        overlay.ExecuteScript("1 + 1", r => answered = r);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (answered == "pending" && clock.ElapsedMilliseconds < 8000)
        {
            overlay.PumpEvents();
            Thread.Sleep(25);
        }
        check("U6 a live Manual overlay answers a script through the pump",
            answered == "2", "result=" + (answered ?? "<null>"));

        // And the two cases a queue cannot serve, because nobody pumps a handle
        // they have thrown away. First: an answer that was ready and waiting in
        // the queue when the handle was disposed. Events are dropped there on
        // purpose; an answer must not be.
        string queued = "pending";
        overlay.ExecuteScript("6 * 7", r => queued = r);
        Thread.Sleep(800);
        check("U7 an answer waits for the pump like everything else",
            queued == "pending", "result=" + (queued ?? "<null>"));

        // Second: one still owed, from a script the renderer is still running.
        string owed = "pending";
        int owedAnswers = 0;
        overlay.ExecuteScript("var t = Date.now(); while (Date.now() - t < 4000) {} 'late'",
            r => { owed = r; Interlocked.Increment(ref owedAnswers); });
        Thread.Sleep(500);
        overlay.Dispose();

        wait(() => queued != "pending", 10000);
        check("U8 and disposing hands over what was already waiting",
            queued == "42", "result=" + (queued ?? "<null>"));
        // Whichever of the two gets there first - the library settling the
        // call as it closes, or the browser's own completion for a script cut
        // off mid-document - the contract is the same: answered, once.
        wait(() => owed != "pending", 12000);
        Thread.Sleep(2000);
        check("U9 as well as what it still owed", owed != "pending",
            "result=" + (owed ?? "<null reference>"));
        check("U10 exactly once", owedAnswers == 1, "answers=" + owedAnswers);

        Thread.Sleep(300);
        finish();
    }


    /// <summary>
    /// A question belongs to the document that asked it. The page numbers its
    /// questions from 1 again in every new document and matches an answer on
    /// that number alone, so a reply the mod takes its time over would - with
    /// nothing else in the way - resolve whichever question the NEXT document
    /// happens to have numbered the same.
    /// </summary>
    internal static void Generation(string scratch)
    {
        string folder = Path.Combine(scratch ?? Path.GetTempPath(), "generation-probe");
        Directory.CreateDirectory(folder);

        // Asks one question as its very first act, and reports whatever comes
        // back. Its id is 1, as it is in every fresh document.
        File.WriteAllText(Path.Combine(folder, "a.html"), @"<!doctype html><html><body>A<script>
          overlay.request('slow', 'from-a', 20000).then(function (v) {
            overlay.send('answered', 'a:' + String(v));
          });
          overlay.send('loaded', 'a');
        </script></body></html>");

        File.WriteAllText(Path.Combine(folder, "b.html"), @"<!doctype html><html><body>B<script>
          overlay.request('quick', 'from-b', 20000).then(function (v) {
            overlay.send('answered', 'b:' + String(v));
          });
          overlay.send('loaded', 'b');
        </script></body></html>");

        var loaded = new System.Collections.Generic.List<string>();
        var answers = new System.Collections.Generic.List<string>();
        Action<string> deferred = null;
        bool failed = false;

        var overlay = WebOverlays.Create("GenerationProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            VirtualHosts = new[] { new VirtualHost("gen.assets", folder) },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.ChannelMessage += (c, p2) =>
        {
            if (c == "loaded") { lock (loaded) loaded.Add(p2); }
            else if (c == "answered") { lock (answers) answers.Add(p2); }
        };
        // Held rather than answered: this is the deferred form, and the whole
        // point is that the answer is still in the mod's hands when the
        // document changes.
        overlay.OnRequest("slow", (payload, reply) => deferred = reply);
        overlay.OnRequest("quick", (payload, reply) => reply("\"for-b\""));

        overlay.Navigate("https://gen.assets/a.html");
        wait(() => loaded.Count > 0 || failed, 25000);
        check("G1 the first page loaded and asked", loaded.Count > 0 && !failed,
            failed ? "failed" : "loaded=" + loaded.Count);
        wait(() => deferred != null, 8000);
        check("G2 the mod is holding its answer", deferred != null,
            deferred != null ? "held" : "never asked");

        // The document changes while that answer is still owed.
        overlay.Navigate("https://gen.assets/b.html");
        wait(() => loaded.Count > 1, 25000);
        Thread.Sleep(800);

        // Now the mod answers - the page it was talking to is gone.
        deferred("\"for-a\"");
        Thread.Sleep(2500);

        string got;
        lock (answers)
            got = string.Join(" | ", answers.ToArray());
        check("G3 the second page got its own answer", got.Contains("b:\"for-b\""), got);
        check("G4 and not the one owed to the first page",
            !got.Contains("for-a"), got);

        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>Reads the number out of "web error status N" in a log line.</summary>
    private static int statusIn(string line)
    {
        if (line == null)
            return -1;
        int at = line.IndexOf("status ", StringComparison.Ordinal);
        if (at < 0)
            return -1;
        string tail = line.Substring(at + "status ".Length);
        int end = tail.IndexOfAny(new[] { ';', '.', ' ' });
        if (end > 0)
            tail = tail.Substring(0, end);
        return int.TryParse(tail, out int value) ? value : -1;
    }

    /// <summary>
    /// A navigation that fails outright - the page is simply not there - used
    /// to produce no output at all, which made a typo in a file name look
    /// exactly like a slow load. Also the empirical proof for the
    /// NavigationCompleted args slot the error status is read from: a wrong
    /// slot cannot produce a plausible status for a case that really failed.
    /// </summary>
    internal static void FailedNavigation(string scratch)
    {
        var warnings = new System.Collections.Generic.List<string>();
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        FieldInfo logWarning = host.GetField("LogWarning", BindingFlags.NonPublic | BindingFlags.Static);
        var previous = (Action<string>)logWarning.GetValue(null);
        logWarning.SetValue(null, (Action<string>)(line =>
        {
            lock (warnings) warnings.Add(line);
            previous(line);
        }));

        string folder = Path.Combine(scratch ?? Path.GetTempPath(), "failed-nav-probe");
        // (the local list above is what the checks read)
        Directory.CreateDirectory(folder);
        // Echoes whatever reaches it, so the row can prove the buffered send
        // really arrives rather than merely that a page loaded.
        File.WriteAllText(Path.Combine(folder, "there.html"),
            "<!doctype html><html><body>here<script>"
            + "window.chrome.webview.addEventListener('message', function (e) {"
            + "  window.chrome.webview.postMessage('echo:' + String(e.data)); });"
            + "window.overlay.on('cfg', function (p) { window.__cfg = p; });"
            + "</script></body></html>");

        bool ready = false, failed = false;
        var overlay = WebOverlays.Create("FailedNavProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            VirtualHosts = new[] { new VirtualHost("missing.assets", folder) },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        wait(() => ready || failed, 25000);
        check("N1 the overlay came up", ready && !failed, failed ? "failed" : "ready=" + ready);

        // Nothing at this name; the folder is mapped, the file is not there.
        overlay.Post("waiting-for-a-page");
        overlay.Navigate("https://missing.assets/not-there.html");
        Thread.Sleep(4000);

        string reported;
        lock (warnings)
            reported = warnings.Find(w => w.Contains("did not load"));
        check("N2 the failure is reported instead of passing in silence",
            reported != null, reported ?? "<silent>");
        check("N3 and names the page that failed",
            reported != null && reported.Contains("not-there.html"), reported ?? "<silent>");

        // The status is read through a hand-bound vtable slot, so it has to be
        // shown doing something a wrong slot could not. A missing file behind a
        // mapped folder reports UNKNOWN (0) - true, but 0 proves nothing. A
        // connection nothing will answer has a status of its own, and port 1 on
        // the loopback address refuses without a name to resolve or a network
        // to reach.
        lock (warnings) warnings.Clear();
        overlay.Navigate("https://127.0.0.1:1/nothing");
        Thread.Sleep(6000);
        string refused;
        lock (warnings)
            refused = warnings.Find(w => w.Contains("did not load"));
        check("N4 a refused connection is reported too", refused != null, refused ?? "<silent>");
        // The status itself is not deterministic per URL - a missing file
        // behind a mapped folder reports UNKNOWN (0), a refused connection
        // CONNECTION_ABORTED (9), and a retry can turn one into the other. So
        // the row asserts only that a status came through and is inside the
        // documented range; that the slot is the status rather than the
        // navigation id next to it was settled by measurement, and is recorded
        // in docs/FAULT-TESTS.md.
        check("N5 carrying a web error status from the bound args slot",
            statusIn(refused) >= 0 && statusIn(refused) <= 18, refused ?? "<silent>");

        check("N6 the page is not claimed as loaded", !overlay.IsPageLoaded,
            "IsPageLoaded=" + overlay.IsPageLoaded);

        // The target stands, so the mod can simply name a page that exists.
        string echoed = null;
        overlay.MessageReceived += m => { if (m != null && m.StartsWith("echo:")) echoed = m; };
        overlay.Navigate("https://missing.assets/there.html");
        wait(() => overlay.IsPageLoaded, 20000);
        check("N7 and a working page afterwards still loads", overlay.IsPageLoaded,
            "IsPageLoaded=" + overlay.IsPageLoaded);

        // Not delivered, and deliberately so: the send was addressed to the
        // page the mod named at the time, and naming a different page is a
        // retarget - which forgets what was meant for the page being left,
        // whether or not that page ever managed to load. The failure did not
        // drop it (the warning above counts it as still buffered); the move
        // away from it did.
        Thread.Sleep(2500);
        check("N8 but a send addressed to the abandoned page does not follow the mod elsewhere",
            echoed == null, echoed ?? "<nothing echoed, as documented>");

        // The neighbouring question, because the answer is not obvious: an
        // explicit re-navigation to the page that is ALREADY the target. A
        // page reloading itself keeps its retained state - that is what row 40
        // measures - so a mod asking for the same page again should not be
        // treated as walking away from it.
        overlay.Post("cfg", "value", PostOptions.Retain);
        Thread.Sleep(600);
        echoed = null;
        overlay.Navigate("https://missing.assets/there.html");
        wait(() => overlay.IsPageLoaded, 20000);
        Thread.Sleep(1200);
        string seen = null;
        overlay.ExecuteScript("window.__cfg || 'none'", r => seen = r);
        wait(() => seen != null, 6000);
        check("N9 re-navigating to the page already showing keeps its retained state",
            seen != null && seen.Contains("value"), "page saw " + (seen ?? "<null>"));

        overlay.Dispose();
        logWarning.SetValue(null, previous);
        Thread.Sleep(300);
        finish();
    }
}
