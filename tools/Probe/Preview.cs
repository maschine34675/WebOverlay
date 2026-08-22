using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using WebOverlay;

/// <summary>
/// The one mode here meant for people building pages rather than for the
/// library itself: it shows a page in a real overlay - same window, same
/// transparency, same message bridge - so the layout can be judged without
/// starting the game.
/// </summary>
internal static class Preview
{
    private const string Title = "WebOverlayPreview";

    internal static void Run(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] == "--help")
        {
            usage();
            return;
        }

        string page = arguments[0];
        bool transparent = false, interactive = false, devTools = false, theme = false;
        int width = 900, height = 600, seconds = 10;
        string screenshot = null, host = "preview.local";
        var posts = new List<string[]>();
        var sends = new List<string>();

        try
        {
            for (int i = 1; i < arguments.Length; i++)
            {
                switch (arguments[i])
                {
                    case "--transparent": transparent = true; break;
                    case "--interactive": transparent = true; interactive = true; break;
                    case "--devtools": devTools = true; break;
                    case "--theme": theme = true; break;
                    case "--backdrop": transparent = true; break;
                    case "--size":
                        string[] parts = arguments[++i].Split('x');
                        width = int.Parse(parts[0]);
                        height = int.Parse(parts[1]);
                        break;
                    case "--host": host = arguments[++i]; break;
                    case "--post": posts.Add(new[] { arguments[++i], arguments[++i] }); break;
                    case "--send": sends.Add(arguments[++i]); break;
                    case "--screenshot": screenshot = arguments[++i]; break;
                    case "--seconds": seconds = int.Parse(arguments[++i]); break;
                    default:
                        Console.WriteLine("unknown argument: " + arguments[i]);
                        usage();
                        return;
                }
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("could not read the arguments: " + error.Message);
            usage();
            return;
        }

        var options = new OverlayOptions
        {
            Width = width,
            Height = height,
            Transparent = transparent,
            Interactive = interactive,
            DevTools = devTools,
            Frame = !transparent,
            InjectTheme = theme,
            RememberBounds = false,
        };

        string url;
        bool isUrl = page.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || page.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isUrl)
        {
            url = page;
        }
        else
        {
            string full = Path.GetFullPath(page);
            if (!File.Exists(full))
            {
                Console.WriteLine("no such page: " + full);
                return;
            }
            // Served from its folder rather than inlined, so relative scripts,
            // images and fonts resolve exactly as they will in a mod - and so
            // the page gets a real origin, where localStorage works.
            options.VirtualHosts = new[] { new VirtualHost(host, Path.GetDirectoryName(full)) };
            url = "https://" + host + "/" + Path.GetFileName(full);
        }

        // Something to be transparent against, so a HUD is judged over a
        // surface rather than over the desktop.
        IntPtr backdrop = transparent ? Program.CreateBackdrop() : IntPtr.Zero;

        bool ready = false, failed = false;
        IWebOverlay overlay = WebOverlays.Create(Title, options);
        if (overlay == null)
        {
            Console.WriteLine("overlays are unavailable - is the WebView2 runtime installed?");
            return;
        }

        overlay.Ready += () => ready = true;
        overlay.Failed += () => failed = true;
        overlay.MessageReceived += m => Console.WriteLine("  page -> mod: " + m);
        overlay.ChannelMessage += (c, p) => Console.WriteLine("  page -> mod [" + c + "]: " + p);
        // So a page can be tried out against a mod that is not written yet.
        overlay.OnRequest("preview", payload =>
        {
            Console.WriteLine("  page asked [preview]: " + payload);
            return "\"the mod is not running - this is tools/Probe\"";
        });

        overlay.Navigate(url);
        // Away from the top-left corner, so a transparent overlay lands on the
        // backdrop rather than half off it.
        overlay.SetBounds(200, 150, null, null);
        overlay.Show();

        wait(() => ready || failed, 30000);
        if (failed)
        {
            Console.WriteLine("the overlay failed: " + overlay.Failure + " - " + overlay.FailureMessage);
            overlay.Dispose();
            if (backdrop != IntPtr.Zero)
                Program.DestroyProbeWindow(backdrop);
            return;
        }

        Console.WriteLine("showing " + url);
        Console.WriteLine("transparency: " + overlay.Transparency
            + (transparent && overlay.Transparency == OverlayTransparency.ChromaKey
                ? " (no per-pixel alpha here - the page sees the wo-chroma class)" : ""));
        if (!wait(() => overlay.IsPageLoaded, 20000))
            Console.WriteLine("the page did not report itself loaded - showing it anyway.");

        foreach (string[] post in posts)
        {
            Console.WriteLine("  mod -> page [" + post[0] + "]: " + post[1]);
            overlay.Post(post[0], post[1]);
        }
        foreach (string text in sends)
        {
            Console.WriteLine("  mod -> page: " + text);
            overlay.Post(text);
        }
        if (devTools)
            overlay.OpenDevTools();

        if (screenshot != null)
        {
            Thread.Sleep(1500);
            string saved = capture(screenshot);
            Console.WriteLine(saved != null ? "screenshot: " + saved : "the overlay window could not be found to capture.");
        }

        Console.WriteLine("closing in " + seconds + " s (Ctrl+C to stop sooner).");
        Thread.Sleep(seconds * 1000);
        overlay.Dispose();
        if (backdrop != IntPtr.Zero)
            Program.DestroyProbeWindow(backdrop);
        Thread.Sleep(300);
    }

    /// <summary>Saves what is on screen where the overlay window is.</summary>
    private static string capture(string path)
    {
        IntPtr window = Program.FindWindowByTitle(Title);
        if (window == IntPtr.Zero)
            return null;
        Program.RECTP rect = Program.GetRect(window);
        int width = rect.right - rect.left, height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
            return null;

        string full = Path.GetFullPath(path);
        using (var bitmap = new Bitmap(width, height))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(width, height));
            bitmap.Save(full, ImageFormat.Png);
        }
        return full;
    }

    private static bool wait(Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(25);
        return done();
    }

    private static void usage()
    {
        Console.WriteLine("usage: preview <page.html|https://...> [options]");
        Console.WriteLine();
        Console.WriteLine("  --transparent        no frame, alpha against a backdrop");
        Console.WriteLine("  --interactive        transparent and clickable (a HUD that takes input)");
        Console.WriteLine("  --size WxH           default 900x600");
        Console.WriteLine("  --host <name>        serve the folder under this host name instead of");
        Console.WriteLine("                       preview.local - match your mod's own VirtualHost when");
        Console.WriteLine("                       the page uses absolute URLs or localStorage");
        Console.WriteLine("  --post <ch> <text>   send on a channel once the page has loaded (repeatable)");
        Console.WriteLine("  --send <text>        the same without a channel (repeatable)");
        Console.WriteLine("  --screenshot <file>  save a PNG of the window, then keep showing it");
        Console.WriteLine("  --seconds <n>        how long to leave it open (default 10)");
        Console.WriteLine("  --theme              inject the --wo-* colour tokens");
        Console.WriteLine("  --devtools           open the browser developer tools");
        Console.WriteLine();
        Console.WriteLine("A local file is served from its own folder as https://preview.local/, so");
        Console.WriteLine("relative assets and storage work as they do in a mod. Anything the page");
        Console.WriteLine("sends back is printed here, and overlay.request('preview', ...) is answered.");
        Console.WriteLine();
        Console.WriteLine("A page the mod assembles at run time - one with placeholders spliced in");
        Console.WriteLine("before LoadHtml - cannot be shown as it sits on disk. Preview the file the");
        Console.WriteLine("mod would produce, or keep the parts separate and let the page fetch them.");
    }
}
