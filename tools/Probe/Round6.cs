using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using WebOverlay;

// Probes for 1.12.0: a frameless window moved and resized from its own page
// (rows 93-99 and 108-111), SetBounds before the window exists and across
// Show (rows 100-102, 112 and 113), and which keys a focused overlay can act
// on at all (rows 103-107, 114 and 115).
// drag and keys send real input, so they guard every press against a window
// that is not this probe's - a press that would land elsewhere ends the mode
// as SKIP rather than in somebody's application.
internal static partial class NewApi
{
    private const string DragPage = "<!doctype html><html><body style='margin:0;background:#202830;height:100vh;overflow:hidden'>"
        + "<div id='bar' style='position:absolute;left:0;top:0;right:0;height:60px;background:#5080c0'></div>"
        + "<div id='corner' style='position:absolute;right:0;bottom:0;width:60px;height:60px;background:#c08050'></div>"
        + "<button id='btn' style='position:absolute;left:150px;top:150px;width:120px;height:60px'>press</button>"
        + "<script>"
        + "function ended(r) { overlay.send('dragend', String(r)); }"
        + "document.getElementById('bar').addEventListener('pointerdown', function (e) { if (e.button === 0) overlay.beginMove().then(ended); });"
        + "document.getElementById('corner').addEventListener('pointerdown', function (e) { if (e.button === 0) overlay.beginResize('bottom-right').then(ended); });"
        + "document.getElementById('btn').addEventListener('click', function () { overlay.send('clicked', '1'); });"
        + "['pointerup', 'pointercancel', 'lostpointercapture'].forEach(function (t) {"
        + "  document.getElementById('bar').addEventListener(t, function () { overlay.send('ptr', t); }); });"
        + "overlay.on('late', function () { overlay.beginMove().then(ended); });"
        + "overlay.on('odd', function () { overlay.beginResize('diagonal').then(ended); });"
        + "</script></body></html>";

    private static int rectWidth(Program.RECTP r) => r.right - r.left;

    private static int rectHeight(Program.RECTP r) => r.bottom - r.top;

    private static string describeRect(Program.RECTP r) =>
        r.left + "," + r.top + " " + rectWidth(r) + "x" + rectHeight(r);

    private static void setDiagnose(bool on) =>
        hostType().GetField("Diagnose", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, on);

    // ---- frameless move and resize (rows 93-99, 108-111) ------------------

    /// <summary>
    /// A frameless window moved and resized by its own page, with real
    /// mouse input. Counterproofs: drop the drag request's routing and
    /// nothing moves; drop the minimum in the resize and D2b shrinks past
    /// it; drop saveBounds at the end of the gesture and D3 finds nothing
    /// remembered; drop the button check and D5a never logs its refusal;
    /// save unconditionally and D3b stores the mod's spot; drop the settle at
    /// the end and D4b never hears 'moved'; drop the guard for an empty
    /// SetBounds and D8 keeps the dragged spot.
    /// </summary>
    internal static void Drag()
    {
        var warnings = hookWarnings();
        setDiagnose(true);
        string key = "probe/drag-" + Guid.NewGuid().ToString("N");
        bool loaded = false, failed = false;
        int clicks = 0;
        var pointerEvents = new List<string>();
        var ends = new List<string>();
        var overlay = WebOverlays.Create("DragProbe", new OverlayOptions
        {
            Width = 520,
            Height = 400,
            MinWidth = 420,
            MinHeight = 320,
            Frame = false,
            PersistenceKey = key,
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.ChannelMessage += (c, p) =>
        {
            if (c == "clicked") Interlocked.Increment(ref clicks);
            if (c == "ptr") lock (pointerEvents) pointerEvents.Add(p);
            if (c == "dragend") lock (ends) ends.Add(p);
        };
        // The answers the page's promises got since the last look - exactly
        // one per gesture is the contract.
        int endsSeen = 0;
        string newEnds()
        {
            wait(() => { lock (ends) return ends.Count > endsSeen; }, 3000);
            Thread.Sleep(300);
            lock (ends)
            {
                string got = string.Join(",", ends.GetRange(endsSeen, ends.Count - endsSeen));
                endsSeen = ends.Count;
                return got;
            }
        }
        overlay.SetBounds(240, 160, null, null);
        overlay.LoadHtml(DragPage);
        wait(() => loaded || failed, 25000);
        Thread.Sleep(800);
        IntPtr window = Program.FindWindowByTitle("DragProbe");
        check("D0 the frameless panel loaded", loaded && !failed && window != IntPtr.Zero, "loaded=" + loaded);

        // D1: the bar moves the window by exactly what the cursor moved.
        Program.RECTP before = Program.GetRect(window);
        dragBy(before.left + 200, before.top + 30, 60, 36);
        Program.RECTP moved = Program.GetRect(window);
        check("D1 the page's beginMove moves the window with the cursor",
            Math.Abs(moved.left - before.left - 60) <= 3 && Math.Abs(moved.top - before.top - 36) <= 3
            && rectWidth(moved) == rectWidth(before) && rectHeight(moved) == rectHeight(before),
            describeRect(before) + " -> " + describeRect(moved));

        // D4b: the page's promise is the end of the gesture - once.
        string firstEnd = newEnds();
        check("D4b the page's beginMove promise settles once, as 'moved'", firstEnd == "moved", firstEnd);

        // D4c: and it is the only thing that tells the page. Measured on
        // runtime 151: once the library holds the mouse, the browser sends
        // the page no pointerup, pointercancel or lostpointercapture. Should
        // a later runtime start sending one, this fails and the recipe gets
        // a sentence fewer.
        string seen;
        lock (pointerEvents) seen = string.Join(",", pointerEvents);
        check("D4c the page sees no pointer event for the press it handed over", seen.Length == 0,
            seen.Length == 0 ? "nothing" : seen);

        // D3: the end of the gesture is remembered, as a frame drag is.
        check("D3 the spot the window was moved to is remembered",
            storedBounds(key, out int sx, out int sy, out int sw, out int sh)
            && sx == moved.left && sy == moved.top && sw == rectWidth(moved) && sh == rectHeight(moved),
            "stored=" + sx + "," + sy + " " + sw + "x" + sh);

        // D3b: a press on the bar that never moves is a click, and a click
        // writes nothing - not even a spot the mod chose, which SetBounds
        // promises is never persisted.
        overlay.SetBounds(moved.left + 40, moved.top + 20, null, null);
        Thread.Sleep(600);
        Program.RECTP placed = Program.GetRect(window);
        dragBy(placed.left + 200, placed.top + 30, 0, 0);
        string clickEnd = newEnds();
        check("D3b a press that never moves saves nothing, and settles as 'unchanged'",
            storedBounds(key, out int cx, out int cy, out int cw, out int ch)
            && cx == moved.left && cy == moved.top && cw == rectWidth(moved) && ch == rectHeight(moved)
            && Program.GetRect(window).left == placed.left && clickEnd == "unchanged",
            "stored=" + cx + "," + cy + " " + cw + "x" + ch + " window=" + describeRect(Program.GetRect(window))
            + " end=" + clickEnd);

        // D2: the corner resizes, the top-left corner stays.
        moved = Program.GetRect(window);
        dragBy(moved.right - 30, moved.bottom - 30, -60, -40);
        Program.RECTP sized = Program.GetRect(window);
        check("D2 the page's beginResize sizes the window from that corner",
            sized.left == moved.left && sized.top == moved.top
            && Math.Abs(rectWidth(sized) - (rectWidth(moved) - 60)) <= 3
            && Math.Abs(rectHeight(sized) - (rectHeight(moved) - 40)) <= 3,
            describeRect(moved) + " -> " + describeRect(sized));

        // D2b: and stops at the minimum the mod set.
        dragBy(sized.right - 30, sized.bottom - 30, -200, -200);
        Program.RECTP clamped = Program.GetRect(window);
        check("D2b a resize stops at MinWidth and MinHeight",
            rectWidth(clamped) == 420 && rectHeight(clamped) == 320 && clamped.left == sized.left && clamped.top == sized.top,
            describeRect(clamped));
        string resizeEnds = newEnds();
        check("D2c each resize settles its promise once, as 'moved'", resizeEnds == "moved,moved", resizeEnds);

        // D4: the page itself still works after the mouse was taken from it.
        int clicksBefore = clicks;
        Program.SendRealClick(clamped.left + 210, clamped.top + 180);
        wait(() => clicks > clicksBefore, 4000);
        check("D4 the page still takes a click after a move and a resize", clicks > clicksBefore,
            "clicks=" + (clicks - clicksBefore));

        // D5: a request that arrives with no button held is not a drag.
        Program.ClearLog();
        Program.RECTP still = Program.GetRect(window);
        overlay.Post("late", "go");
        Thread.Sleep(600);
        check("D5a a request arriving after the button was released is refused",
            Program.LogContains("primary mouse button not down"), "");
        mouseTo(still.left + 200, still.top + 200);
        Thread.Sleep(100);
        mouseTo(still.left + 260, still.top + 240);
        Thread.Sleep(400);
        Program.RECTP after = Program.GetRect(window);
        check("D5b the window does not follow a cursor nobody is pressing",
            after.left == still.left && after.top == still.top, describeRect(still) + " -> " + describeRect(after));
        string lateEnd = newEnds();
        check("D5c the refused request's promise settles as 'refused'", lateEnd == "refused", lateEnd);

        // D7: a request the shim would never produce is named, not guessed at.
        overlay.Post("odd", "go");
        Thread.Sleep(600);
        string oddEnd = newEnds();
        check("D7 an unknown edge is refused with a warning, and its promise says so",
            warned(warnings, "malformed move or resize request") && oddEnd == "refused", oddEnd);
        overlay.Dispose();
        Thread.Sleep(500);

        // D6: a HUD follows the game window; its page cannot move it.
        bool hudLoaded = false;
        var hud = WebOverlays.Create("DragHudProbe", new OverlayOptions { Transparent = true, Width = 400, Height = 300 });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        string hudEnd = null;
        hud.PageLoaded += () => hudLoaded = true;
        hud.ChannelMessage += (c, p) => { if (c == "dragend") hudEnd = p; };
        hud.LoadHtml(DragPage);
        wait(() => hudLoaded, 25000);
        Thread.Sleep(500);
        hud.Post("late", "go");
        wait(() => hudEnd != null, 3000);
        check("D6 a HUD's page cannot move it, is told why in the log, and its promise says 'refused'",
            warned(warnings, "is a HUD") && hudEnd == "refused", "end=" + (hudEnd ?? "none"));
        hud.Dispose();
        Thread.Sleep(500);

        // D8: with RememberBounds off a window re-centres on Show, and a
        // SetBounds that names nothing must not switch that off.
        bool secondLoaded = false;
        var second = WebOverlays.Create("DragProbe2", new OverlayOptions
        {
            Width = 520,
            Height = 400,
            Frame = false,
            RememberBounds = false,
        });
        if (second == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        second.PageLoaded += () => secondLoaded = true;
        second.LoadHtml(DragPage);
        wait(() => secondLoaded, 25000);
        Thread.Sleep(800);
        IntPtr secondWindow = Program.FindWindowByTitle("DragProbe2");
        Program.RECTP home = Program.GetRect(secondWindow);
        dragBy(home.left + 200, home.top + 30, 60, 36);
        Program.RECTP dragged = Program.GetRect(secondWindow);
        second.SetBounds(null, null, null, null);
        second.Hide();
        Thread.Sleep(500);
        second.Show();
        Thread.Sleep(800);
        Program.RECTP reopened = Program.GetRect(secondWindow);
        check("D8 a SetBounds that names nothing leaves re-centring on Show alone",
            dragged.left != home.left && reopened.left == home.left && reopened.top == home.top,
            describeRect(home) + " -> " + describeRect(dragged) + " -> " + describeRect(reopened));
        second.Dispose();
        setDiagnose(false);
        Thread.Sleep(300);
        finish();
    }

    /// <summary>Press at a point, move by (dx, dy) in six steps, release.</summary>
    private static void dragBy(int x, int y, int dx, int dy)
    {
        Program.RefuseForeignTarget(x, y);
        mouseTo(x, y);
        Thread.Sleep(150);
        mouseButton(down: true);
        // The page's pointerdown has to reach the library first.
        Thread.Sleep(400);
        for (int i = 1; i <= 6; i++)
        {
            mouseTo(x + dx * i / 6, y + dy * i / 6);
            Thread.Sleep(40);
        }
        Thread.Sleep(200);
        mouseButton(down: false);
        Thread.Sleep(500);
    }

    private static bool storedBounds(string key, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        Type store = typeof(WebOverlays).Assembly.GetType("WebOverlay.BoundsStore");
        MethodInfo tryGet = store.GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Static);
        var args = new object[] { key, null };
        if (!(bool)tryGet.Invoke(null, args))
            return false;
        object bounds = args[1];
        Type type = bounds.GetType();
        x = (int)type.GetField("X").GetValue(bounds);
        y = (int)type.GetField("Y").GetValue(bounds);
        width = (int)type.GetField("Width").GetValue(bounds);
        height = (int)type.GetField("Height").GetValue(bounds);
        return true;
    }

    // ---- SetBounds before the window and across Show (rows 100-102, 112-113)

    /// <summary>
    /// What a mod that places its own window relies on. Counterproofs: drop
    /// the pending bounds and E1 opens centred; restore the old Show
    /// condition and E2 re-centres; clamp to the library floor instead of
    /// MinWidth and E3 comes out 200x150; overwrite instead of merge and E1b
    /// loses its position; drop the off-screen fallback and E4 opens at
    /// -9000,-9000.
    /// </summary>
    internal static void BoundsEarly()
    {
        bool loaded = false, failed = false;
        var overlay = WebOverlays.Create("BoundsEarlyProbe", new OverlayOptions
        {
            Width = 500,
            Height = 400,
            MinWidth = 300,
            MinHeight = 250,
            RememberBounds = false,
            // Frameless on purpose: Windows enforces a framed window's
            // minimum track size on SetWindowPos as well, which would hide
            // the library's own clamp from E3 - measured, the first version
            // of this row passed with the clamp removed.
            Frame = false,
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        // Right after Create: the window does not exist yet.
        overlay.SetBounds(260, 180, 560, 420);
        overlay.LoadHtml("<p>early</p>");
        wait(() => loaded || failed, 25000);
        Thread.Sleep(800);
        IntPtr window = Program.FindWindowByTitle("BoundsEarlyProbe");
        Program.RECTP first = Program.GetRect(window);
        check("E1 SetBounds made before the window exists is applied when it appears",
            first.left == 260 && first.top == 180 && rectWidth(first) == 560 && rectHeight(first) == 420,
            describeRect(first));

        overlay.Hide();
        Thread.Sleep(500);
        overlay.Show();
        Thread.Sleep(800);
        Program.RECTP again = Program.GetRect(window);
        check("E2 with RememberBounds off, the mod's spot survives Hide and Show",
            again.left == 260 && again.top == 180 && rectWidth(again) == 560 && rectHeight(again) == 420,
            describeRect(again));

        overlay.SetBounds(null, null, 100, 100);
        Thread.Sleep(800);
        Program.RECTP floor = Program.GetRect(window);
        check("E3 SetBounds holds to MinWidth and MinHeight",
            rectWidth(floor) == 300 && rectHeight(floor) == 250, describeRect(floor));
        overlay.Dispose();
        Thread.Sleep(500);

        // E1b: two early calls naming different halves - each fills in only
        // what it names, as on a live window.
        Program.RECTP merged = openEarly("BoundsMergeProbe", o =>
        {
            o.SetBounds(260, 180, null, null);
            o.SetBounds(null, null, 560, 420);
        });
        check("E1b two early SetBounds calls each keep what the other named",
            merged.left == 260 && merged.top == 180 && rectWidth(merged) == 560 && rectHeight(merged) == 420,
            describeRect(merged));

        // E4: an early spot on no screen falls back to the default, the rule
        // a remembered spot has always followed - a frameless window opened
        // where nobody can see it could not even be dragged back.
        Program.RECTP rescued = openEarly("BoundsOffScreenProbe", o => o.SetBounds(-9000, -9000, null, null));
        check("E4 an early SetBounds to a spot on no screen opens at the default instead",
            rescued.left > -1000 && rescued.top > -1000, describeRect(rescued));
        finish();
    }

    /// <summary>Creates a frameless overlay, lets <paramref name="early"/> run before its window exists, reports where it opened.</summary>
    private static Program.RECTP openEarly(string title, Action<IWebOverlay> early)
    {
        bool loaded = false, failed = false;
        // Held, so the calls below are queued before the creation can start:
        // with the browser already up, an idle overlay thread would create
        // the window at once, and the calls would take the live path instead
        // - measured, the first version of E1b and E4 passed with their
        // faults put back.
        stallOverlayThread(1000);
        var overlay = WebOverlays.Create(title, new OverlayOptions { Width = 500, Height = 400, Frame = false, RememberBounds = false });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        early(overlay);
        overlay.LoadHtml("<p>early</p>");
        wait(() => loaded || failed, 25000);
        Thread.Sleep(800);
        Program.RECTP rect = Program.GetRect(Program.FindWindowByTitle(title));
        overlay.Dispose();
        Thread.Sleep(500);
        return rect;
    }

    // ---- which keys a focused overlay can act on (rows 103-107, 114-115) --

    /// <summary>
    /// A measurement of the browser, not of a fix: WebView2 reports a key to
    /// its host only when it types no character or comes with Ctrl or Alt.
    /// The CloseKeys and KeyPressed documentation rests on these rows.
    /// </summary>
    internal static void Keys()
    {
        bool loaded = false, failed = false;
        var visible = new List<bool>();
        var pressed = new List<int>();
        var typed = new List<string>();
        var overlay = WebOverlays.Create("KeysProbe", new OverlayOptions
        {
            Width = 520,
            Height = 360,
            CloseKeys = new[] { 0x1B, 0x78, 0x47, 0x79 },   // Escape, F9, G, F10
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.PageLoaded += () => loaded = true;
        overlay.VisibilityChanged += v => { lock (visible) visible.Add(v); };
        overlay.KeyPressed += k => { lock (pressed) pressed.Add(k); };
        overlay.ChannelMessage += (c, p) => { if (c == "typed") lock (typed) typed.Add(p); };
        overlay.LoadHtml("<!doctype html><html><body style='margin:0;height:100vh;background:#334'><script>"
            + "document.addEventListener('keydown', function (e) { overlay.send('typed', e.key); });"
            + "</script></body></html>");
        wait(() => loaded || failed, 25000);
        Thread.Sleep(800);
        IntPtr window = Program.FindWindowByTitle("KeysProbe");
        check("K0 the panel loaded", loaded && !failed && window != IntPtr.Zero, "loaded=" + loaded);

        bool isVisible() { lock (visible) return visible.Count > 0 && visible[visible.Count - 1]; }
        bool pressedKey(int key) { lock (pressed) return pressed.Contains(key); }
        bool typedKey(string key) { lock (typed) return typed.Contains(key); }

        focus(window);
        sendKeys(window, 0x47);
        Thread.Sleep(600);
        check("K1 a plain letter close key reaches the page as typing and does not close the overlay",
            typedKey("g") && isVisible() && !pressedKey(0x47), "typed g=" + typedKey("g") + " visible=" + isVisible());

        sendKeys(window, 0x11, 0x47);
        Thread.Sleep(800);
        check("K2 the same letter with Ctrl held closes it", !isVisible(), "visible=" + isVisible());

        overlay.Show();
        Thread.Sleep(800);
        focus(window);
        sendKeys(window, 0x12, 0x47);
        Thread.Sleep(800);
        check("K2b the same letter with Alt held closes it too", !isVisible(), "visible=" + isVisible());

        overlay.Show();
        Thread.Sleep(800);
        focus(window);
        sendKeys(window, 0x77);
        Thread.Sleep(600);
        check("K3 a function key that closes nothing arrives in KeyPressed", pressedKey(0x77) && isVisible(),
            "pressed F8=" + pressedKey(0x77));

        sendKeys(window, 0x25);
        Thread.Sleep(600);
        check("K3b an arrow key arrives in KeyPressed", pressedKey(0x25) && isVisible(),
            "pressed Left=" + pressedKey(0x25));

        sendKeys(window, 0x78);
        Thread.Sleep(800);
        check("K4 a function key close key closes a focused overlay", !isVisible(), "visible=" + isVisible());

        // F10 is a system key to Windows - it arms the menu bar - and the
        // README's quickstart uses it, so it is measured rather than assumed.
        overlay.Show();
        Thread.Sleep(800);
        focus(window);
        sendKeys(window, 0x79);
        Thread.Sleep(800);
        check("K5 F10, a system key, closes a focused overlay too", !isVisible(), "visible=" + isVisible());
        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>A guarded click into the page, so the overlay holds the keyboard.</summary>
    private static void focus(IntPtr window)
    {
        Program.RECTP r = Program.GetRect(window);
        Program.SendRealClick((r.left + r.right) / 2, (r.top + r.bottom) / 2);
        Thread.Sleep(500);
    }

    /// <summary>
    /// Presses the keys in order and releases them in reverse - a chord for
    /// more than one. Refuses when the keyboard is not this probe's: a key
    /// sent then would type into somebody else's application.
    /// </summary>
    private static void sendKeys(IntPtr window, params ushort[] keys)
    {
        IntPtr foreground = GetForegroundWindowR();
        IntPtr root = foreground == IntPtr.Zero ? IntPtr.Zero : GetAncestorR(foreground, 2);
        GetWindowThreadProcessIdR(root, out uint owner);
        if (root != window || owner != (uint)Environment.ProcessId)
            throw new Program.DesktopBusyException("refusing to type: the keyboard belongs to process "
                + owner + ", not to this probe's overlay. Something took the focus - run the mode again.");
        foreach (ushort key in keys)
        {
            keyEvent(key, up: false);
            Thread.Sleep(40);
        }
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            keyEvent(keys[i], up: true);
            Thread.Sleep(40);
        }
    }

    // ---- raw input ---------------------------------------------------------

    private static void mouseTo(int x, int y)
    {
        int nx = x * 65535 / (GetSystemMetricsR(0) - 1);
        int ny = y * 65535 / (GetSystemMetricsR(1) - 1);
        sendInput(new INPUTR { type = 0, mi = new MOUSEINPUTR { dx = nx, dy = ny, dwFlags = 0x8000 | 0x0001 } });
    }

    private static void mouseButton(bool down)
    {
        sendInput(new INPUTR { type = 0, mi = new MOUSEINPUTR { dwFlags = down ? 0x0002u : 0x0004u } });
    }

    private static void keyEvent(ushort key, bool up)
    {
        sendInput(new INPUTR { type = 1, ki = new KEYBDINPUTR { wVk = key, dwFlags = up ? 0x0002u : 0u } });
    }

    private static void sendInput(INPUTR input) =>
        SendInputR(1, new[] { input }, Marshal.SizeOf<INPUTR>());

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTR
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUTR mi;
        [FieldOffset(8)] public KEYBDINPUTR ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUTR
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUTR
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint SendInputR(uint count, INPUTR[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetricsR(int index);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowR();

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestorR(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdR(IntPtr window, out uint processId);
}
