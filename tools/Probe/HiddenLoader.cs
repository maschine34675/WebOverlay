using System;
using System.IO;
using WebOverlay;

/// <summary>
/// Moves WebView2Loader.dll aside for the length of a `using` block, so the
/// two modes that test an incomplete plugin folder can make one for
/// themselves instead of relying on how the harness happens to be laid out.
///
/// It has to happen before the first overlay exists: that is when the library
/// goes looking for the loader, and once it has failed to start it stays
/// failed for the rest of the process.
/// </summary>
internal sealed class HiddenLoader : IDisposable
{
    private readonly string loader;
    private readonly string hidden;
    private readonly bool moved;

    internal HiddenLoader()
    {
        loader = Path.Combine(
            Path.GetDirectoryName(typeof(WebOverlays).Assembly.Location), "WebView2Loader.dll");
        hidden = loader + ".hidden";

        // A run that was killed mid-block leaves the loader parked; put it
        // back before deciding what to do, or every later run starts broken.
        if (File.Exists(hidden) && !File.Exists(loader))
            File.Move(hidden, loader);

        moved = File.Exists(loader);
        if (!moved)
            return;
        if (File.Exists(hidden))
            File.Delete(hidden);
        File.Move(loader, hidden);
    }

    public void Dispose()
    {
        if (!moved || !File.Exists(hidden))
            return;
        if (File.Exists(loader))
            File.Delete(hidden);   // the build put a fresh one back in the meantime
        else
            File.Move(hidden, loader);
    }
}
