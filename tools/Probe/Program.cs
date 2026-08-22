// The probe host: it drives the real Anvil-WebOverlay.dll outside the game, in
// a plain .NET process, and every row of docs/FAULT-TESTS.md is one of its
// modes. It is also how every hand-bound vtable slot in this library was
// proven before it was trusted - a slot is a guess until something observable
// changes because of it.
//
//   dotnet run --project tools/Probe -c Release -- <mode> [arguments]
//
// `preview` is the mode meant for people building pages rather than the
// library; the rest are the fault matrix. See tools/Probe/README.md.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using WebOverlay;

// Drives the REAL Anvil-WebOverlay.dll outside the game: creates an overlay,
// loads inline HTML (the path the in-game report showed as blocked), and
// verifies by screen pixels and a round-trip message that the origin filter
// lets the mod's own content through.
internal static class Program
{
    private static int failures;

    private static readonly System.Collections.Generic.List<string> warnings = new System.Collections.Generic.List<string>();

    [STAThread]
    private static void Main(string[] args)
    {
        // The library logs through internal static delegates; hook them so
        // blocked navigation would be visible here.
        Type host = typeof(WebOverlays).Assembly.GetType("WebOverlay.OverlayHost");
        host.GetField("LogInfo", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, (Action<string>)(line => Console.WriteLine("[info] " + line)));
        host.GetField("LogWarning", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, (Action<string>)(line =>
            {
                lock (warnings) warnings.Add(line);
                Console.WriteLine("[warn] " + line);
            }));

        string mode = args.Length > 0 ? args[0] : "normal";
        switch (mode)
        {
            case "fault-loader": faultLoader(); return;
            case "fault-bightml": faultBigHtml(); return;
            case "fault-dispose-race": faultDisposeRace(); return;
            case "fault-redirect": faultRedirect(); return;
            case "script-roundtrip": scriptRoundTrip(); return;
            case "bounds-save": boundsSave(); return;
            case "bounds-verify": boundsVerify(); return;
            case "latency": latency(); return;
            case "glass": glass(); return;
            case "glass-click": glassClick(); return;
            case "cube": cubeProbe(args.Length > 1 ? args[1] : null); return;
            case "storage": storageProbe(); return;
            case "vhost": NewApi.VirtualHost(args.Length > 1 ? args[1] : null); return;
            case "dispatch": NewApi.Dispatch(); return;
            case "failure-kind": NewApi.FailureKind(); return;
            case "vhost-fail": NewApi.VirtualHostFailure(); return;
            case "nav-reject": NewApi.RejectedNavigation(); return;
            case "script-result": NewApi.ScriptResult(); return;
            case "visibility": NewApi.Visibility(); return;
            case "close-race": NewApi.CloseRace(); return;
            case "shutdown-quiet": NewApi.ShutdownQuiet(); return;
            case "channels": NewApi.Channels(); return;
            case "shape": NewApi.Shape(); return;
            case "bounds-api": NewApi.BoundsApi(); return;
            case "shape-guards": NewApi.ShapeGuards(); return;
            case "api17": NewApi.ConsumerApi17(); return;
            case "mixed": NewApi.Mixed(); return;
            case "mixed-reverse": NewApi.MixedReverse(); return;
            case "dcomp-first": NewApi.DcompFirst(); return;
            case "footprint": NewApi.Footprint(); return;
            case "spare-browser": NewApi.SpareBrowser(); return;
            case "spare-folder": NewApi.SpareFolder(); return;
            case "retained": NewApi.Retained(args.Length > 1 ? args[1] : null); return;
            case "latest-only": NewApi.LatestOnly(); return;
            case "manual-pump": NewApi.ManualPump(); return;
            case "failed-nav": NewApi.FailedNavigation(args.Length > 1 ? args[1] : null); return;
            case "generation": NewApi.Generation(args.Length > 1 ? args[1] : null); return;

            // Not a fault-matrix row: the mode for looking at your own page.
            case "preview":
                Preview.Run(args.Length > 1 ? args[1..] : new string[0]);
                return;
        }

        bool ready = false, failed = false, gotMessage = false;

        var overlay = WebOverlays.Create("LibraryProbe", new OverlayOptions
        {
            Width = 800,
            Height = 600,
            Frame = false,
        });
        if (overlay == null)
        {
            Console.WriteLine("FAIL Create returned null");
            Environment.Exit(1);
        }

        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m => { if (m == "hello-from-page") gotMessage = true; };

        overlay.LoadHtml("<!doctype html><html style='background:#0000FF'>" +
            "<body style='margin:0;background:#0000FF'></body>" +
            "<script>window.chrome.webview.postMessage('hello-from-page');</script></html>");

        wait(() => ready || failed, 20000);
        check("R1 overlay reports Ready", ready && !failed, "ready=" + ready + " failed=" + failed);

        // Give rendering and the message pump a moment.
        Thread.Sleep(3000);

        IntPtr overlayWindow = FindWindow(null, "LibraryProbe");
        RECT rect = default;
        if (overlayWindow == IntPtr.Zero || !GetWindowRect(overlayWindow, out rect))
        {
            Console.WriteLine("FAIL overlay window not found on screen");
            Environment.Exit(1);
        }
        Console.WriteLine("window at " + rect.left + "," + rect.top + " - " + rect.right + "," + rect.bottom
            + " visible=" + IsWindowVisible(overlayWindow));
        int x = (rect.left + rect.right) / 2;
        int y = (rect.top + rect.bottom) / 2;
        Color center = samplePixel(x, y);
        check("R2 the inline page actually rendered", center.B > 200 && center.R < 60 && center.G < 60, "rgb(" + center.R + "," + center.G + "," + center.B + ") at " + x + "," + y);
        check("R3 the page's message passed the source filter", gotMessage, "gotMessage=" + gotMessage);

        overlay.Dispose();
        Thread.Sleep(500);

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ---- fault-injection matrix -----------------------------------------

    /// <summary>Missing WebView2Loader.dll: Failed must latch, no hang, second Create returns null.</summary>
    private static void faultLoader()
    {
        using (new HiddenLoader())
        {
            bool failed = false;
            var overlay = WebOverlays.Create("FaultLoader");
            if (overlay == null)
            {
                // Also acceptable: the host may already know it cannot start.
                check("F1 create refused or failed", true, "null handle");
            }
            else
            {
                overlay.Failed += () => failed = true;
                wait(() => failed, 15000);
                check("F1 Failed latched without the loader", failed, "failed=" + failed);
                overlay.Dispose();
            }
            var second = WebOverlays.Create("FaultLoader2");
            check("F2 second create returns null after start failure", second == null, second == null ? "null" : "handle");
        }
        finish();
    }

    /// <summary>Inline HTML over the 2 MB limit: rejected with a log, no crash, buffered sends dropped.</summary>
    private static void faultBigHtml()
    {
        bool ready = false, failed = false;
        var overlay = WebOverlays.Create("FaultBigHtml", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.LoadHtml("<!doctype html><html><body>" + new string('x', 3 * 1024 * 1024) + "</body></html>");
        overlay.Post("goes-nowhere");
        wait(() => ready || failed, 20000);
        Thread.Sleep(2000);
        check("B1 process survived the oversized inline page", true, "alive");
        bool rejectedLogged;
        lock (warnings)
            rejectedLogged = warnings.Exists(w => w.Contains("rejected"));
        check("B2 the rejection was logged", rejectedLogged, rejectedLogged ? "logged" : "silent");
        overlay.Dispose();
        finish();
    }

    /// <summary>Create immediately followed by Dispose, repeatedly; then one normal create still works.</summary>
    private static void faultDisposeRace()
    {
        for (int i = 0; i < 10; i++)
        {
            var racer = WebOverlays.Create("Race" + i, new OverlayOptions { Width = 300, Height = 200 });
            racer?.LoadHtml("<p>race</p>");
            racer?.Dispose();
        }
        Thread.Sleep(3000);
        check("D1 process survived 10 create/dispose races", true, "alive");

        bool ready = false, failed = false, got = false;
        var overlay = WebOverlays.Create("AfterRace", new OverlayOptions { Width = 300, Height = 200 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null after races"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m => { if (m == "ok") got = true; };
        overlay.LoadHtml("<script>window.chrome.webview.postMessage('ok');</script>");
        wait(() => got || failed, 20000);
        check("D2 a normal overlay still works afterwards", got && !failed, "ready=" + ready + " got=" + got + " failed=" + failed);
        overlay.Dispose();
        finish();
    }

    /// <summary>
    /// Redirect between two allowed origins: buffered sends for target A must
    /// not reach B, while B itself loads and can message the host.
    /// </summary>
    private static void faultRedirect()
    {
        int portA = 47311, portB = 47312;
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + portA + "/");
        listener.Prefixes.Add("http://127.0.0.2:" + portB + "/");
        listener.Start();
        string echoed = null;
        var serverThread = new Thread(() =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                if (ctx.Request.Url.Port == portA)
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = "http://127.0.0.2:" + portB + "/landing";
                    ctx.Response.Close();
                }
                else
                {
                    byte[] page = System.Text.Encoding.UTF8.GetBytes(
                        "<!doctype html><script>" +
                        "window.chrome.webview.addEventListener('message', e => window.chrome.webview.postMessage('echo:' + e.data));" +
                        "window.chrome.webview.postMessage('b-loaded');" +
                        "</script>B");
                    ctx.Response.ContentType = "text/html";
                    ctx.Response.OutputStream.Write(page, 0, page.Length);
                    ctx.Response.Close();
                }
            }
        }) { IsBackground = true };
        serverThread.Start();

        bool bLoaded = false, failed = false;
        var overlay = WebOverlays.Create("FaultRedirect", new OverlayOptions
        {
            Width = 400,
            Height = 300,
            AllowedOrigins = new[] { "http://127.0.0.2:" + portB },
        });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m =>
        {
            if (m == "b-loaded") bLoaded = true;
            else if (m.StartsWith("echo:")) echoed = m;
        };
        overlay.Navigate("http://127.0.0.1:" + portA + "/start");
        overlay.Post("secret-for-A");

        wait(() => bLoaded || failed, 20000);
        check("R1 the redirect target loaded and can message the host", bLoaded && !failed, "bLoaded=" + bLoaded + " failed=" + failed);
        Thread.Sleep(2000);
        check("R2 the buffered send for A never reached B", echoed == null, echoed ?? "nothing echoed");
        bool dropLogged;
        lock (warnings)
            dropLogged = warnings.Exists(w => w.Contains("buffered send"));
        check("R3 the drop was logged", dropLogged, dropLogged ? "logged" : "silent");
        overlay.Dispose();
        listener.Stop();
        finish();
    }

    /// <summary>ExecuteScript with the real completion handler runs in the page.</summary>
    private static void scriptRoundTrip()
    {
        bool loaded = false, scriptRan = false, failed = false;
        var overlay = WebOverlays.Create("ScriptRoundTrip", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m =>
        {
            if (m == "loaded") loaded = true;
            else if (m == "script-ran") scriptRan = true;
        };
        overlay.LoadHtml("<script>window.chrome.webview.postMessage('loaded');</script>");
        wait(() => loaded || failed, 20000);
        check("S1 the page loaded", loaded && !failed, "loaded=" + loaded + " failed=" + failed);
        overlay.ExecuteScript("window.chrome.webview.postMessage('script-ran');");
        wait(() => scriptRan, 10000);
        check("S2 ExecuteScript ran in the page", scriptRan, "scriptRan=" + scriptRan);
        overlay.Dispose();
        finish();
    }

    /// <summary>Composed display-only HUD through the library: true alpha against a green backdrop.</summary>
    private static void glass()
    {
        IntPtr backdrop = createBackdrop();
        bool ready = false, failed = false;
        var hud = WebOverlays.Create("GlassProbe", new OverlayOptions { Transparent = true });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Ready += () => ready = true;
        hud.Failed += () => failed = true;
        // Solid blue at screen (500..700, 220..300); 50% red at (500..700, 320..420).
        hud.LoadHtml("<!doctype html><html><body style='margin:0'>" +
            "<div style='position:absolute;left:500px;top:220px;width:200px;height:80px;background:#0000FF'></div>" +
            "<div style='position:absolute;left:500px;top:320px;width:200px;height:100px;background:rgba(255,0,0,0.5)'></div>" +
            "</body></html>");
        wait(() => ready || failed, 25000);
        check("G1 composed HUD reports Ready", ready && !failed, "ready=" + ready + " failed=" + failed);
        Thread.Sleep(2500);

        Color through = sampleScreen(230, 230);
        check("G2 unpainted pixels show the backdrop (true alpha)", isGreenC(through), describeC(through));
        Color solid = sampleScreen(600, 260);
        check("G3 painted pixels keep their color", solid.B > 200 && solid.R < 60 && solid.G < 60, describeC(solid));
        Color blended = sampleScreen(600, 370);
        bool blendOk = blended.R > 90 && blended.R < 170 && blended.G > 90 && blended.G < 170 && blended.B < 60;
        check("G4 rgba() really blends with the backdrop", blendOk, describeC(blended));

        // The display-only HUD must be click-through EVERYWHERE - hit-testing
        // over it has to land on whatever is behind.
        IntPtr hudWindow = FindWindow(null, "GlassProbe");
        IntPtr hit = WindowFromPointP(230, 230);
        IntPtr hitRoot = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestorP(hit, 2);
        var cls = new System.Text.StringBuilder(256);
        GetClassNameP(hitRoot, cls, cls.Capacity);
        string who = hitRoot == backdrop ? "backdrop"
            : hitRoot == hudWindow ? "THE HUD ITSELF"
            : "other(" + cls + ")";
        check("G5 the display-only HUD is click-through", hitRoot == backdrop, "hit " + who);

        hud.Dispose();
        DestroyWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    /// <summary>Interactive composed HUD: a REAL SendInput click lands on an HTML button.</summary>
    private static void glassClick()
    {
        IntPtr backdrop = createBackdrop();
        bool ready = false, failed = false, clicked = false;
        var hud = WebOverlays.Create("GlassClickProbe", new OverlayOptions
        {
            Transparent = true,
            Interactive = true,
            Width = 400,
            Height = 300,
        });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Ready += () => ready = true;
        hud.Failed += () => failed = true;
        hud.MessageReceived += m => { if (m == "clicked") clicked = true; };
        hud.LoadHtml("<!doctype html><html><body style='margin:0'>" +
            "<button style='position:absolute;left:50px;top:80px;width:120px;height:60px' " +
            "onclick=\"window.chrome.webview.postMessage('clicked')\">press</button>" +
            "</body></html>");
        wait(() => ready || failed, 25000);
        check("I1 interactive HUD reports Ready", ready && !failed, "ready=" + ready + " failed=" + failed);
        Thread.Sleep(1500);

        // The sized HUD sits at the game picture's top-left; without a game
        // window that is the primary screen's origin, so client == screen.
        // Button center: (110, 110).
        sendRealClick(110, 110);
        wait(() => clicked, 5000);
        check("I2 a real mouse click reaches the HTML button", clicked, "clicked=" + clicked);

        hud.Dispose();
        DestroyWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    /// <summary>
    /// The real Three.js demo page in a display HUD: WebGL context creation,
    /// an advancing render loop, cube pixels on screen, intact transparency,
    /// the camera-coupling orientation mapping, and click-through.
    /// </summary>
    private static void cubeProbe(string repoRoot)
    {
        string root = repoRoot ?? @"D:\SPT41\Development\WebOverlay";
        string html = System.IO.File.ReadAllText(System.IO.Path.Combine(root, @"WebOverlay.Demo\web\cube.html"));
        string three = System.IO.File.ReadAllText(System.IO.Path.Combine(root, @"WebOverlay.Demo\web\three.min.js"));
        string page = html.Replace("/*!THREE_MIN_JS!*/", three);
        Console.WriteLine("assembled page: " + page.Length + " chars");

        IntPtr backdrop = createBackdrop();
        bool ready = false, failed = false;
        string stateJson = null;
        var hud = WebOverlays.Create("CubeProbe", new OverlayOptions { Transparent = true });
        if (hud == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        hud.Ready += () => ready = true;
        hud.Failed += () => failed = true;
        hud.MessageReceived += m => { if (m.StartsWith("cube:")) stateJson = m.Substring(5); };
        hud.LoadHtml(page);
        wait(() => ready || failed, 25000);
        check("C1 cube HUD reports Ready", ready && !failed, "ready=" + ready + " failed=" + failed);
        Thread.Sleep(3000);

        Func<string> query = () =>
        {
            stateJson = null;
            hud.Post("probe:state");
            wait(() => stateJson != null, 5000);
            return stateJson;
        };
        Func<string, string, string> field = (json, name) =>
        {
            if (json == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + name + "\":(\"(?<s>[^\"]*)\"|(?<v>[^,}]+))");
            return !m.Success ? null : m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["v"].Value;
        };

        string first = query();
        check("C2 the page answers state queries", first != null, first ?? "no answer");
        check("C3 a WebGL context was created", field(first, "webgl") == "true",
            "webgl=" + field(first, "webgl") + " error=" + (field(first, "error") ?? "-"));
        Console.WriteLine("renderer: " + field(first, "renderer")
            + " (webgl2=" + field(first, "webgl2") + ", webgpu=" + field(first, "webgpu") + ")");

        long frames1 = long.Parse(field(first, "frames") ?? "0");
        Thread.Sleep(1500);
        string second = query();
        long frames2 = second == null ? 0 : long.Parse(field(second, "frames") ?? "0");
        check("C4 the render loop advances", second != null && frames2 > frames1,
            "frames " + frames1 + " -> " + frames2 + " at " + field(second, "fps") + " fps");

        // Pixel checks: the page reports its canvas rectangle; the full-size
        // HUD sits at the screen origin, so client equals screen coordinates.
        // The cube's silhouette always covers the canvas center.
        var rectMatch = System.Text.RegularExpressions.Regex.Match(second ?? "",
            "\"rect\":\\[(-?\\d+),(-?\\d+),(\\d+),(\\d+)\\]");
        check("C5 the page reports its canvas rectangle", rectMatch.Success, second ?? "no state");
        if (rectMatch.Success)
        {
            int cx = int.Parse(rectMatch.Groups[1].Value) + int.Parse(rectMatch.Groups[3].Value) / 2;
            int cy = int.Parse(rectMatch.Groups[2].Value) + int.Parse(rectMatch.Groups[4].Value) / 2;
            Color backdropColor = sampleScreen(600, 250);
            check("C6 transparency stays intact around the stage", isGreenC(backdropColor), describeC(backdropColor) + " at 600,250");
            Color cubeColor = sampleScreen(cx, cy);
            check("C7 the cube renders on screen", !isGreenC(cubeColor), describeC(cubeColor) + " at " + cx + "," + cy);
        }

        // Orientation mapping through the real message path. Feeding a view
        // pauses the free spin, so the quaternion holds still for the query.
        double time = 1;
        Func<double, double, string> faceFor = (yaw, pitch) =>
        {
            hud.Post(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "view:{0};{1};10;20;30;{2}", yaw, pitch, time++));
            Thread.Sleep(150);
            return field(query(), "face");
        };
        string north = faceFor(0, 0);
        check("C8 yaw 0 shows N", north == "N", "face=" + (north ?? "-"));
        string east = faceFor(90, 0);
        check("C9 yaw 90 shows E", east == "E", "face=" + (east ?? "-"));
        string down = faceFor(0, 89);
        check("C10 pitch 89 (looking down) shows DOWN", down == "DOWN", "face=" + (down ?? "-"));

        // The cube HUD is display-only; hit-testing must land behind it.
        IntPtr hudWindow = FindWindow(null, "CubeProbe");
        IntPtr hit = WindowFromPointP(600, 250);
        IntPtr hitRoot = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestorP(hit, 2);
        check("C11 the cube HUD is click-through", hitRoot == backdrop,
            hitRoot == hudWindow ? "hit THE HUD ITSELF" : hitRoot == backdrop ? "hit backdrop" : "hit other");

        hud.Dispose();
        DestroyWindow(backdrop);
        Thread.Sleep(300);
        finish();
    }

    private static void capture(int x, int y, int width, int height, string file)
    {
        using (var bitmap = new System.Drawing.Bitmap(width, height))
        {
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    /// <summary>
    /// What web storage an inline LoadHtml page actually gets: the wishlist
    /// assumes every mod's page shares one origin and therefore collides in
    /// localStorage. Ask the page itself.
    /// </summary>
    private static void storageProbe()
    {
        string answer = null;
        var overlay = WebOverlays.Create("StorageProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        bool failed = false;
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m => { if (m.StartsWith("storage:")) answer = m.Substring(8); };
        overlay.LoadHtml(@"<!doctype html><html><body><script>
          function probe(name, fn) { try { return name + '=' + fn(); } catch (e) { return name + '=THROWS(' + e.name + ')'; } }
          var out = [
            probe('origin', function () { return window.origin; }),
            probe('url', function () { return String(document.URL).slice(0, 40); }),
            probe('localStorage', function () { localStorage.setItem('k', 'v'); return 'works:' + localStorage.getItem('k'); }),
            probe('sessionStorage', function () { sessionStorage.setItem('k', 'v'); return 'works:' + sessionStorage.getItem('k'); }),
            probe('indexedDB', function () { return indexedDB ? 'present' : 'absent'; }),
            probe('cookie', function () { document.cookie = 'k=v'; return document.cookie === '' ? 'blocked' : 'works'; })
          ].join(' | ');
          window.chrome.webview.postMessage('storage:' + out);
        </script></body></html>");
        wait(() => answer != null || failed, 20000);
        Console.WriteLine("page reports: " + (answer ?? "no answer"));
        check("W1 the page answered", answer != null && !failed, answer ?? "failed=" + failed);
        // Documented behaviour, not a wish: an inline page has an opaque
        // origin, so web storage is absent rather than shared between mods.
        // A page that needs storage takes a virtual host instead.
        check("W2 an inline page has no web storage (opaque origin)",
            answer != null && answer.Contains("localStorage=THROWS") && answer.Contains("origin=null"),
            answer ?? "-");
        overlay.Dispose();
        finish();
    }

    // ---- shared with NewApi.cs -------------------------------------------

    private static int backdropClicks;

    /// <summary>
    /// The backdrop lives on this thread, and its window procedure only runs
    /// when someone dispatches. Without this the counter stays at zero however
    /// the click was routed - which says nothing about the overlay.
    /// </summary>
    internal static void PumpBackdrop(int milliseconds)
    {
        int elapsed = 0;
        while (elapsed < milliseconds)
        {
            while (PeekMessageP(out MSGP message, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessageP(ref message);
                DispatchMessageP(ref message);
            }
            System.Threading.Thread.Sleep(20);
            elapsed += 20;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSGP
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageP(out MSGP message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool TranslateMessageP(ref MSGP message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageP(ref MSGP message);

    internal static int BackdropClicks => backdropClicks;

    internal struct RECTP { public int left, top, right, bottom; }

    internal static RECTP GetRegionBox(IntPtr window)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        GetWindowRgn(window, region);
        GetRgnBox(region, out RECT rect);
        DeleteObjectP(region);
        return new RECTP { left = rect.left, top = rect.top, right = rect.right, bottom = rect.bottom };
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr region, out RECT rect);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObjectP(IntPtr handle);

    internal static RECTP GetRect(IntPtr window)
    {
        GetWindowRect(window, out RECT rect);
        return new RECTP { left = rect.left, top = rect.top, right = rect.right, bottom = rect.bottom };
    }

    internal static IntPtr CreateBackdrop() => createBackdrop();

    internal static Color SampleScreenPublic(int x, int y) => sampleScreen(x, y);

    internal static string DescribeColor(Color c) => describeC(c);

    internal static IntPtr CreateRectRgnPublic(int left, int top, int right, int bottom) =>
        CreateRectRgn(left, top, right, bottom);

    internal static int SetWindowRgnPublic(IntPtr window, IntPtr region, bool redraw) =>
        SetWindowRgn(window, region, redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    internal static void DestroyProbeWindow(IntPtr window) => DestroyWindow(window);

    internal static void SendRealClick(int x, int y) => sendRealClick(x, y);

    internal static IntPtr WindowFromPointPublic(int x, int y) => WindowFromPointP(x, y);

    internal static IntPtr GetAncestorPublic(IntPtr hwnd, uint flags) => GetAncestorP(hwnd, flags);

    internal static IntPtr FindWindowByTitle(string title) => FindWindow(null, title);

    private static IntPtr createBackdrop()
    {
        // A solid green reference surface behind the overlay.
        return createProbeWindow("ProbeBackdrop", 0x0000FF00, 150, 150, 900, 700);
    }

    private static WndProcDelegateP backdropProc;

    private static IntPtr createProbeWindow(string className, uint brush, int x, int y, int w, int h)
    {
        if (backdropProc == null)
            backdropProc = (hw, m, wp, lp) =>
            {
                if (m == 0x0201)
                    System.Threading.Interlocked.Increment(ref backdropClicks);
                return DefWindowProc(hw, m, wp, lp);
            };
        var wc = new WNDCLASSEXP
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXP>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(backdropProc),
            hInstance = GetModuleHandleP(null),
            hbrBackground = CreateSolidBrush(brush),
            lpszClassName = className,
        };
        RegisterClassExP(ref wc);
        return CreateWindowExP(0, className, className, 0x80000000 | 0x10000000, x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static Color sampleScreen(int x, int y)
    {
        IntPtr screen = GetDCP(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, 1, 1);
        IntPtr previous = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, 1, 1, screen, x, y, 0x00CC0020 | 0x40000000);
        uint colorRef = GetPixelP(memory, 0, 0);
        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDCP(IntPtr.Zero, screen);
        return Color.FromArgb((int)(colorRef & 0xFF), (int)((colorRef >> 8) & 0xFF), (int)((colorRef >> 16) & 0xFF));
    }

    private static void sendRealClick(int screenX, int screenY)
    {
        // Absolute coordinates are normalized to 0..65535.
        int nx = screenX * 65535 / (GetSystemMetrics(0) - 1);
        int ny = screenY * 65535 / (GetSystemMetrics(1) - 1);
        var inputs = new INPUT[3];
        inputs[0] = mouseInput(nx, ny, 0x8000 | 0x0001);            // ABSOLUTE | MOVE
        inputs[1] = mouseInput(nx, ny, 0x8000 | 0x0002);            // ABSOLUTE | LEFTDOWN
        inputs[2] = mouseInput(nx, ny, 0x8000 | 0x0004);            // ABSOLUTE | LEFTUP
        SendInput(1, new[] { inputs[0] }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(150);
        SendInput(1, new[] { inputs[1] }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
        SendInput(1, new[] { inputs[2] }, Marshal.SizeOf<INPUT>());
    }

    private static INPUT mouseInput(int x, int y, uint flags)
    {
        return new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags },
        };
    }

    private static bool isGreenC(Color c) => c.G > 200 && c.R < 60 && c.B < 60;

    private static string describeC(Color c) => "rgb(" + c.R + "," + c.G + "," + c.B + ")";

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegateP(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXP
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTP { public int x, y; }

    private static IntPtr WindowFromPointP(int x, int y)
    {
        return WindowFromPointNative(new POINTP { x = x, y = y });
    }

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointNative(POINTP point);

    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr GetAncestorP(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameP(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassExP(ref WNDCLASSEXP wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowExP(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandleP(string module);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr GetDCP(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDCP(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int dx, int dy, int w, int h, IntPtr source, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", EntryPoint = "GetPixel")]
    private static extern uint GetPixelP(IntPtr dc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    /// <summary>Measures the full mod-to-page-to-mod message round trip and the burst throughput.</summary>
    private static void latency()
    {
        bool loaded = false, failed = false;
        int echoes = 0;
        var stamp = new System.Diagnostics.Stopwatch();
        var samples = new System.Collections.Generic.List<double>();
        object gate = new object();

        var overlay = WebOverlays.Create("LatencyProbe", new OverlayOptions { Width = 400, Height = 300 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m =>
        {
            if (m == "loaded") { loaded = true; return; }
            lock (gate)
            {
                if (m.StartsWith("echo:"))
                {
                    samples.Add(stamp.Elapsed.TotalMilliseconds);
                    echoes++;
                }
                else if (m == "burst")
                {
                    echoes++;
                }
            }
        };
        overlay.LoadHtml("<script>" +
            "window.chrome.webview.addEventListener('message', e => {" +
            "  const d = String(e.data);" +
            "  if (d.startsWith('ping:')) window.chrome.webview.postMessage('echo:' + d.slice(5));" +
            "  else if (d === 'burst-ping') window.chrome.webview.postMessage('burst');" +
            "});" +
            "window.chrome.webview.postMessage('loaded');" +
            "</script>");
        wait(() => loaded || failed, 20000);
        if (!loaded) { Console.WriteLine("FAIL page never loaded"); Environment.Exit(1); }

        // Warmup, then sequential round trips.
        for (int i = 0; i < 20; i++) { overlay.Post("ping:w" + i); Thread.Sleep(10); }
        Thread.Sleep(300);
        lock (gate) { samples.Clear(); echoes = 0; }

        const int Rounds = 200;
        for (int i = 0; i < Rounds; i++)
        {
            int before;
            lock (gate) before = echoes;
            stamp.Restart();
            overlay.Post("ping:" + i);
            wait(() => { lock (gate) return echoes > before; }, 2000);
        }
        double[] sorted;
        lock (gate) sorted = samples.ToArray();
        Array.Sort(sorted);
        if (sorted.Length < Rounds * 9 / 10) { Console.WriteLine("FAIL only " + sorted.Length + " echoes"); Environment.Exit(1); }
        Console.WriteLine("RTT ms over " + sorted.Length + " round trips: min=" + sorted[0].ToString("0.00")
            + " median=" + sorted[sorted.Length / 2].ToString("0.00")
            + " p95=" + sorted[(int)(sorted.Length * 0.95)].ToString("0.00")
            + " max=" + sorted[sorted.Length - 1].ToString("0.00"));

        // Burst throughput: fire everything, count arrivals.
        lock (gate) echoes = 0;
        const int Burst = 1000;
        var burstClock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < Burst; i++)
            overlay.Post("burst-ping");
        wait(() => { lock (gate) return echoes >= Burst; }, 15000);
        burstClock.Stop();
        int arrived;
        lock (gate) arrived = echoes;
        Console.WriteLine("burst: " + arrived + "/" + Burst + " round trips in "
            + burstClock.ElapsedMilliseconds + " ms ("
            + (arrived * 1000.0 / Math.Max(1, burstClock.ElapsedMilliseconds)).ToString("0") + " msg/s)");

        overlay.Dispose();
        Thread.Sleep(300);
        Console.WriteLine("ALL PASS");
        Environment.Exit(0);
    }

    /// <summary>Moves the window like a player would and checks the spot survives hide/show; the save lands in the store file.</summary>
    private static void boundsSave()
    {
        bool ready = false, failed = false;
        var overlay = WebOverlays.Create("BoundsProbe", new OverlayOptions { Width = 500, Height = 400 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.LoadHtml("<p>bounds</p>");
        wait(() => ready || failed, 20000);
        Thread.Sleep(500);

        IntPtr hwnd = FindWindow(null, "BoundsProbe");
        if (hwnd == IntPtr.Zero) { Console.WriteLine("FAIL window not found"); Environment.Exit(1); }
        SetWindowPos(hwnd, IntPtr.Zero, 222, 111, 520, 410, 0x0004 /*NOZORDER*/);
        SendMessage(hwnd, 0x0232 /*WM_EXITSIZEMOVE*/, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(500);

        overlay.Toggle();               // hide
        Thread.Sleep(300);
        overlay.Toggle();               // show again - must NOT recenter
        Thread.Sleep(500);
        GetWindowRect(hwnd, out RECT rect);
        check("P1 the spot survives hide and show", rect.left == 222 && rect.top == 111, rect.left + "," + rect.top);
        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    /// <summary>Fresh process: the overlay must reopen at the spot the previous run saved.</summary>
    private static void boundsVerify()
    {
        bool ready = false, failed = false;
        var overlay = WebOverlays.Create("BoundsProbe", new OverlayOptions { Width = 500, Height = 400 });
        if (overlay == null) { Console.WriteLine("FAIL create returned null"); Environment.Exit(1); }
        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.LoadHtml("<p>bounds</p>");
        wait(() => ready || failed, 20000);
        Thread.Sleep(500);

        IntPtr hwnd = FindWindow(null, "BoundsProbe");
        RECT rect = default;
        bool found = hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
        check("P2 a new process reopens at the remembered spot",
            found && rect.left == 222 && rect.top == 111 && rect.right - rect.left == 520 && rect.bottom - rect.top == 410,
            found ? rect.left + "," + rect.top + " " + (rect.right - rect.left) + "x" + (rect.bottom - rect.top) : "window not found");
        overlay.Dispose();
        Thread.Sleep(300);
        finish();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
            Thread.Sleep(100);
            elapsed += 100;
        }
    }

    private static Color samplePixel(int x, int y)
    {
        // BitBlt with CAPTUREBLT captures DWM-composited content that plain
        // GetPixel misses (it reads the redirection surface).
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, 1, 1);
        IntPtr previous = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, 1, 1, screen, x, y, 0x00CC0020 | 0x40000000);
        uint colorRef = GetPixel(memory, 0, 0);       // COLORREF 0x00BBGGRR
        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        return Color.FromArgb((int)(colorRef & 0xFF), (int)((colorRef >> 8) & 0xFF), (int)((colorRef >> 16) & 0xFF));
    }







    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    private static void check(string label, bool passed, string detail)
    {
        if (!passed)
            failures++;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + label + " [" + detail + "]");
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
