using System;
using System.IO;
using System.Reflection;
using System.Threading;
using WebOverlay;

// Probes for the 1.11.0 members: TryPost (rows 79-80), the per-window share
// of the command queue (rows 81-83) and the visibility answers (rows 84-92).
internal static partial class NewApi
{
    // A page that echoes every message back with a prefix, so a probe can
    // count exactly which of its sends arrived.
    private const string EchoPage = "<!doctype html><html><body><script>"
        + "window.chrome.webview.addEventListener('message', function (e) {"
        + "  window.chrome.webview.postMessage('echo:' + e.data);"
        + "});</script></body></html>";

    private static Type hostType() => typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");

    /// <summary>
    /// Occupies the overlay thread with one legitimate, non-droppable item,
    /// so that everything posted meanwhile has to wait in the queue. The
    /// seam row 78 already uses.
    /// </summary>
    private static void stallOverlayThread(int milliseconds)
    {
        MethodInfo hostPost = hostType().GetMethod("Post", BindingFlags.NonPublic | BindingFlags.Static);
        // Waited for, not slept for: a flood posted before the stall has
        // started would drain live and the counts would be off.
        var entered = new ManualResetEventSlim(false);
        hostPost.Invoke(null, new object[] { (Action)(() => { entered.Set(); Thread.Sleep(milliseconds); }) });
        if (!entered.Wait(5000))
            Console.WriteLine("WARN the stall did not start within five seconds");
    }

    private static int windowShare() =>
        (int)hostType().GetField("WindowShare", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static IWebOverlay createLoaded(string title, out Func<int> echoes, string echoPrefix)
    {
        bool loaded = false;
        int count = 0;
        var overlay = WebOverlays.Create(title, new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.PageLoaded += () => loaded = true;
        overlay.MessageReceived += m => { if (m.StartsWith("echo:" + echoPrefix)) Interlocked.Increment(ref count); };
        overlay.LoadHtml(EchoPage);
        wait(() => loaded, 25000);
        Thread.Sleep(300);
        echoes = () => count;
        return overlay;
    }

    // ---- TryPost (rows 79-80) --------------------------------------------

    /// <summary>
    /// A post refused by the queue tells its caller so; a post that entered
    /// the queue reaches the page - and true is admission, not delivery,
    /// which the outbox proves. Counterproof: make the handle discard the
    /// host's answer, and T1 and T3 fail together.
    /// </summary>
    internal static void TryPostProbe()
    {
        var warnings = hookWarnings();
        int share = windowShare();
        var overlay = createLoaded("TryPostProbe", out Func<int> echoes, "flood");

        stallOverlayThread(1500);
        int trues = 0, falses = 0;
        for (int i = 0; i < 6000; i++)
        {
            if (overlay.TryPost("flood " + i))
                trues++;
            else
                falses++;
        }
        check("T1 a flood past this overlay's share is refused, and the caller is told",
            falses > 0 && trues + falses == 6000 && trues <= share,
            "trues=" + trues + " falses=" + falses + " share=" + share);
        check("T2 with a warning that names the overlay",
            warned(warnings, "TryPostProbe") && warned(warnings, "further ones are being dropped"), "");

        // After the stall the accepted ones drain into a healthy loaded page:
        // exactly those arrive, no more and no fewer.
        wait(() => echoes() >= trues, 10000);
        Thread.Sleep(800);
        check("T3 after the stall the page received exactly the accepted posts",
            echoes() == trues, "echoes=" + echoes() + " trues=" + trues);

        // True is not delivery: before the page exists, sends wait in the
        // outbox, which holds a hundred - the hundred-and-first is admitted
        // by the queue and then dropped by the outbox, with a warning.
        bool freshLoaded = false;
        int early = 0;
        var fresh = WebOverlays.Create("TryPostOutboxProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (fresh == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        fresh.PageLoaded += () => freshLoaded = true;
        fresh.MessageReceived += m => { if (m.StartsWith("echo:early")) Interlocked.Increment(ref early); };
        int earlyTrues = 0;
        for (int i = 0; i < 101; i++)
        {
            if (fresh.TryPost("early " + i))
                earlyTrues++;
        }
        check("T4 before the page exists every post enters the queue - true is admission, not delivery",
            earlyTrues == 101, "trues=" + earlyTrues);
        fresh.LoadHtml(EchoPage);
        wait(() => freshLoaded, 25000);
        wait(() => early >= 100, 10000);
        Thread.Sleep(800);
        check("T5 and the page received the outbox's hundred, the extra one announced as dropped",
            early == 100 && warned(warnings, "outbox is full"), "early=" + early);

        fresh.Dispose();
        Thread.Sleep(500);
        check("T6 a disposed handle answers false", !fresh.TryPost("late"), "");

        // Last, because the host does not come back from it: during shutdown
        // the library accepts everything and does nothing, so the answer is
        // true - the one answer every path here gives on the way out.
        MethodInfo shutdown = hostType().GetMethod("Shutdown", BindingFlags.NonPublic | BindingFlags.Static);
        shutdown.Invoke(null, null);
        Thread.Sleep(300);
        check("T7 during shutdown the answer is true, like every other call's silent acceptance",
            overlay.TryPost("gone"), "");
        finish();
    }

    // ---- the per-window share (rows 81-83) --------------------------------

    /// <summary>
    /// One mod's flood no longer refuses another mod's commands: the flooder
    /// is refused at its own share and named, the neighbour's commands still
    /// enter and are delivered once the backlog drains, disposing the flooder
    /// still works, and the queue-wide ceiling still holds above every share.
    /// Counterproof: set the share to the ceiling, and S2/S3 show 1.10.0's
    /// behaviour - the neighbour's script answered null-by-refusal.
    /// </summary>
    internal static void Share()
    {
        var warnings = hookWarnings();
        var flooder = createLoaded("ShareFlooder", out Func<int> flooderEchoes, "flood");
        var neighbour = createLoaded("ShareNeighbour", out Func<int> neighbourEchoes, "ping");

        stallOverlayThread(1500);
        for (int i = 0; i < 6000; i++)
            flooder.Post("flood " + i);
        check("S1 the flooder is refused at its share, and the warning names it",
            warned(warnings, "ShareFlooder") && warned(warnings, "further ones are being dropped"), "");

        bool accepted = neighbour.TryPost("ping");
        string answer = "unanswered";
        neighbour.ExecuteScript("1 + 1", v => answer = v ?? "null-by-refusal");
        VisibilityOutcome? hidden = null;
        neighbour.Hide(o => hidden = o);
        check("S2 the neighbour's commands still enter the queue during the flood", accepted, "");

        wait(() => answer != "unanswered" && hidden != null && neighbourEchoes() > 0, 12000);
        Thread.Sleep(500);
        check("S3 and are delivered once the flooder's backlog has drained - admitted, not unaffected",
            answer == "2" && hidden == VisibilityOutcome.Applied && neighbourEchoes() == 1,
            "answer=" + answer + " hide=" + hidden + " echoes=" + neighbourEchoes());
        check("S4 the warning never names the neighbour", !warned(warnings, "ShareNeighbour"), "");

        // Disposal is an obligation, outside the share: a flooder over its
        // share can still be closed.
        int flooderClosed = 0;
        flooder.VisibilityChanged += v => { if (!v) Interlocked.Increment(ref flooderClosed); };
        stallOverlayThread(1500);
        for (int i = 0; i < 1100; i++)
            flooder.Post("again " + i);
        flooder.Dispose();
        wait(() => flooderClosed > 0, 10000);
        check("S5 disposing the flooder while it is over its share still closes it",
            flooderClosed == 1, "closed=" + flooderClosed);
        // The second flood came seconds after the first: named once, not
        // once per burst. (Naming it again an hour later is the same stamp
        // read the other way, and not worth an hour of the probe's time.)
        int named;
        lock (warnings) named = warnings.FindAll(w => w.Contains("ShareFlooder")).Count;
        check("S5b a second flood within the minute is not warned about again",
            named == 1, "warnings naming the flooder=" + named);

        // The ceiling above every share: five overlays flooding together
        // reach the queue-wide bound, and that is refused with its own
        // warning. Their pages do not matter - admission is what is counted.
        var crowd = new IWebOverlay[5];
        for (int i = 0; i < crowd.Length; i++)
        {
            crowd[i] = WebOverlays.Create("ShareCrowd" + i, new OverlayOptions { Width = 300, Height = 200 });
            if (crowd[i] == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        }
        Thread.Sleep(3000);
        stallOverlayThread(1500);
        foreach (IWebOverlay member in crowd)
        {
            for (int i = 0; i < 1100; i++)
                member.Post("crowd " + i);
        }
        check("S6 the ceiling still holds above every share, with the queue-wide warning",
            warned(warnings, "command queue is full"), "");

        Thread.Sleep(3000);
        foreach (IWebOverlay member in crowd)
            member.Dispose();
        neighbour.Dispose();
        finish();
    }

    // ---- visibility answers (rows 84-92) ----------------------------------

    /// <summary>
    /// Every way a Show or Hide with an answer can end, each pinned to the
    /// outcome the contract names - and the shutdown row last, because the
    /// host does not come back from it.
    /// </summary>
    internal static void VisibilityResult()
    {
        Type host = hostType();
        MethodInfo shutdown = host.GetMethod("Shutdown", BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo displayProbe = host.GetField("DisplayModeProbe", BindingFlags.NonPublic | BindingFlags.Static);

        // R1: a Show queued behind a stall, and disposed before it ran.
        // Counterproof: without the run-time disposed check the window is
        // shown for a handle that can no longer see it, and R1b fails.
        var a = createLoaded("VisResultProbe", out _, "none");
        a.Hide();
        wait(() => !a.IsVisible, 5000);
        Thread.Sleep(300);
        int shownAfterDispose = 0;
        a.VisibilityChanged += v => { if (v) Interlocked.Increment(ref shownAfterDispose); };
        VisibilityOutcome? r1 = null;
        stallOverlayThread(1500);
        a.Show(o => r1 = o);
        a.Dispose();
        wait(() => r1 != null, 8000);
        Thread.Sleep(2000);
        check("R1 a Show queued behind a stall and disposed before it ran answers Disposed",
            r1 == VisibilityOutcome.Disposed, "outcome=" + r1);
        check("R1b and the window is never shown for a handle that can no longer see it",
            shownAfterDispose == 0, "shownAfterDispose=" + shownAfterDispose);

        // R2: the creation gap, made deterministic by the stall - every
        // command below is queued before the creation runs, which is also
        // the ordinary order of a consumer's first open.
        stallOverlayThread(1500);
        var b = WebOverlays.Create("VisGapProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (b == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        VisibilityOutcome? c1 = null, c2 = null, c3 = null;
        int bTransitions = 0;
        bool bLoaded = false;
        b.VisibilityChanged += v => Interlocked.Increment(ref bTransitions);
        b.PageLoaded += () => bLoaded = true;
        b.Show(o => c1 = o);
        b.Hide(o => c2 = o);
        b.Show(o => c3 = o);
        b.LoadHtml("<p>gap</p>");
        wait(() => bLoaded && c3 != null, 25000);
        Thread.Sleep(500);
        check("R2 creation gap Show, Hide, Show: superseded, already there, applied",
            c1 == VisibilityOutcome.Superseded && c2 == VisibilityOutcome.AlreadyThere && c3 == VisibilityOutcome.Applied,
            "c1=" + c1 + " c2=" + c2 + " c3=" + c3);
        check("R2b with exactly one visibility transition, and the window showing",
            bTransitions == 1 && b.IsVisible, "transitions=" + bTransitions + " visible=" + b.IsVisible);
        b.Dispose();

        // R3: a Hide alone in the creation gap.
        stallOverlayThread(1500);
        var c = WebOverlays.Create("VisHideGapProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (c == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        VisibilityOutcome? h3 = null;
        int cTransitions = 0;
        bool cLoaded = false;
        c.VisibilityChanged += v => Interlocked.Increment(ref cTransitions);
        c.PageLoaded += () => cLoaded = true;
        c.Hide(o => h3 = o);
        c.LoadHtml("<p>hidden</p>");
        wait(() => cLoaded && h3 != null, 25000);
        Thread.Sleep(500);
        check("R3 a Hide before the view exists is already there, and the window stays hidden through creation",
            h3 == VisibilityOutcome.AlreadyThere && !c.IsVisible && cTransitions == 0,
            "outcome=" + h3 + " visible=" + c.IsVisible + " transitions=" + cTransitions);

        // R5: the display-mode refusal, answered, and Toggle afterwards
        // choosing Show - the window's own desired state is what Toggle reads.
        displayProbe.SetValue(null, (Func<bool>)(() => false));
        VisibilityOutcome? r5 = null;
        c.Show(o => r5 = o);
        wait(() => r5 != null, 5000);
        Thread.Sleep(300);
        check("R5 a Show refused by the display mode answers RefusedFullscreen, with no transition and no failure",
            r5 == VisibilityOutcome.RefusedFullscreen && !c.IsVisible && cTransitions == 0
            && c.Failure == OverlayFailure.Unknown,
            "outcome=" + r5 + " visible=" + c.IsVisible + " transitions=" + cTransitions);
        displayProbe.SetValue(null, null);
        c.Toggle();
        wait(() => c.IsVisible, 5000);
        check("R5b and Toggle afterwards shows, because the refusal reset the desired state",
            c.IsVisible, "visible=" + c.IsVisible);
        c.Dispose();

        // R4: a Show parked on a creation that then fails, disposed from the
        // Failed handler - the ordinary consumer shape - answered exactly once.
        string missing = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"));
        var f = WebOverlays.Create("VisFailProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            VirtualHosts = new[] { new VirtualHost("example.com", missing) },
        });
        if (f == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        int settled = 0;
        VisibilityOutcome? r4 = null;
        bool fFailed = false;
        f.Failed += () => { fFailed = true; f.Dispose(); };
        f.Show(o => { Interlocked.Increment(ref settled); r4 = o; });
        wait(() => fFailed && r4 != null, 25000);
        Thread.Sleep(1500);
        check("R4 a Show parked on a creation that fails answers Failed, exactly once, through the handler's own Dispose",
            r4 == VisibilityOutcome.Failed && settled == 1, "outcome=" + r4 + " settled=" + settled);

        // R6: requests made after the failure answer at once rather than
        // wait for a view that will never come. Counterproof: without the
        // failed-first check the Show parks on a view that never comes - the
        // row's own Hide answers it as Superseded - and the Hide answers
        // AlreadyThere for a dead window.
        var g = WebOverlays.Create("VisFailedProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            VirtualHosts = new[] { new VirtualHost("example.com", missing) },
        });
        if (g == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        bool gFailed = false;
        g.Failed += () => gFailed = true;
        wait(() => gFailed, 25000);
        Thread.Sleep(300);
        VisibilityOutcome? s6 = null, h6 = null;
        g.Show(o => s6 = o);
        g.Hide(o => h6 = o);
        wait(() => s6 != null && h6 != null, 5000);
        check("R6 Show and Hide on a failed overlay answer Failed at once",
            s6 == VisibilityOutcome.Failed && h6 == VisibilityOutcome.Failed, "show=" + s6 + " hide=" + h6);
        g.Dispose();

        // R7: manual dispatch delivers answers before events in one pump -
        // the documented order, asserted so it cannot drift unnoticed.
        var order = new System.Collections.Generic.List<string>();
        bool mLoaded = false;
        var m = WebOverlays.Create("VisManualProbe", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            Dispatch = EventDispatch.Manual,
        });
        if (m == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        m.PageLoaded += () => mLoaded = true;
        m.VisibilityChanged += v => { lock (order) order.Add("event:" + v); };
        m.LoadHtml("<p>manual</p>");
        for (int i = 0; i < 500 && !mLoaded; i++)
        {
            m.PumpEvents();
            Thread.Sleep(50);
        }
        Thread.Sleep(500);
        m.PumpEvents();
        lock (order) order.Clear();
        m.Hide(o => { lock (order) order.Add("answer:" + o); });
        Thread.Sleep(1000);
        m.PumpEvents();
        string trail;
        lock (order) trail = string.Join(",", order.ToArray());
        check("R7 under manual dispatch one pump delivers the answer and the event, the answer first",
            trail == "answer:Applied,event:False", "trail=" + trail);
        m.Dispose();

        // R8: refused by the queue - answered at once, on the calling thread.
        var q = createLoaded("VisFloodProbe", out _, "none");
        stallOverlayThread(1500);
        for (int i = 0; i < 1100; i++)
            q.Post("flood " + i);
        int caller = Thread.CurrentThread.ManagedThreadId;
        int answeredOn = -1;
        VisibilityOutcome? r8 = null;
        q.Show(o => { r8 = o; answeredOn = Thread.CurrentThread.ManagedThreadId; });
        check("R8 a Show refused by the queue answers QueueRefused at once, on the calling thread",
            r8 == VisibilityOutcome.QueueRefused && answeredOn == caller, "outcome=" + r8);
        Thread.Sleep(2500);
        q.Dispose();

        // R9: shutdown. A Show in flight is never answered, and closing the
        // visible window on the way out raises neither Closed nor
        // VisibilityChanged - Closed being the one that used to fire.
        var z = createLoaded("VisShutdownProbe", out _, "none");
        int zClosed = 0, zEvents = 0, zAnswers = 0;
        z.Closed += () => Interlocked.Increment(ref zClosed);
        z.VisibilityChanged += v => Interlocked.Increment(ref zEvents);
        stallOverlayThread(1500);
        z.Show(o => Interlocked.Increment(ref zAnswers));
        shutdown.Invoke(null, null);
        Thread.Sleep(3500);
        check("R9 a Show in flight at shutdown is never answered, and the close on the way out raises nothing",
            zAnswers == 0 && zClosed == 0 && zEvents == 0,
            "answers=" + zAnswers + " closed=" + zClosed + " events=" + zEvents);
        bool scriptAnswered = false;
        z.ExecuteScript("1", v => scriptAnswered = true);
        Thread.Sleep(1500);
        check("R9b a script asked for after shutdown is never answered either", !scriptAnswered, "");
        finish();
    }
}
