using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace WebOverlay
{
    /// <summary>
    /// Remembers where the player put each overlay window, so it reopens there
    /// instead of resetting to the centered default. One text file next to the
    /// shared browser data, one line per overlay: "x y w h key", the key last
    /// because it may contain spaces.
    ///
    /// The file is shared by every mod and potentially several game processes,
    /// so a save never trusts a stale snapshot: it re-reads the file under a
    /// cross-process mutex, applies its one change and writes atomically. A
    /// store that cannot be read is left alone - degrading to "not saved this
    /// time" is recoverable, wiping other overlays' entries is not.
    /// </summary>
    internal static class BoundsStore
    {
        internal struct StoredBounds
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        // Cache for TryGet; null means the last read failed and the next call
        // should try again rather than trust a failed snapshot.
        private static Dictionary<string, StoredBounds> entries;

        private static string filePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebOverlay", "window-bounds.txt");

        internal static bool TryGet(string key, out StoredBounds bounds)
        {
            key = sanitize(key);
            if (entries == null)
                entries = readFile();
            if (entries == null)
            {
                bounds = default(StoredBounds);
                return false;
            }
            return entries.TryGetValue(key, out bounds);
        }

        internal static void Save(string key, StoredBounds bounds)
        {
            key = sanitize(key);
            try
            {
                // One mutex across processes: two game instances saving at the
                // same moment must not clobber each other's entries.
                using (var mutex = new Mutex(false, "Local\\WebOverlay.BoundsStore"))
                {
                    bool owned;
                    try
                    {
                        owned = mutex.WaitOne(2000);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous holder died; the wait DID acquire, and
                        // the file is ours now.
                        owned = true;
                    }

                    // Not acquired is not a license to write anyway: a write
                    // without the lock is exactly the torn file the mutex
                    // exists to prevent, and it also made the release below
                    // throw for a lock never taken. Skip rather than retry -
                    // this runs on the shared overlay thread inside a modal
                    // move/size, so a second two-second wait would freeze
                    // every overlay in the process, and the next move saves
                    // the same bounds anyway.
                    if (!owned)
                    {
                        OverlayHost.LogWarning("window bounds not saved; the store was held elsewhere for over two seconds.");
                        return;
                    }

                    try
                    {
                        Dictionary<string, StoredBounds> table = readFile();
                        if (table == null)
                        {
                            OverlayHost.LogWarning("window bounds not saved; the store file could not be read.");
                            return;
                        }
                        table[key] = bounds;
                        writeFile(table);
                        entries = table;
                    }
                    finally
                    {
                        try
                        {
                            mutex.ReleaseMutex();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OverlayHost.LogWarning("window bounds could not be saved (" + ex.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Keys end up as the last field of a text line; a stray newline would
        /// split the line and hijack a truncated key. Spaces are already legal.
        /// </summary>
        private static string sanitize(string key)
        {
            if (key == null)
                return string.Empty;
            return key.Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>Missing file is an empty store; a failed read is null.</summary>
        private static Dictionary<string, StoredBounds> readFile()
        {
            var table = new Dictionary<string, StoredBounds>(StringComparer.Ordinal);
            try
            {
                // A crash between writing the temp file and moving it leaves a
                // complete .tmp and no main file - adopt it.
                string temporary = filePath + ".tmp";
                if (!File.Exists(filePath) && File.Exists(temporary))
                    File.Move(temporary, filePath);

                if (!File.Exists(filePath))
                    return table;
                foreach (string line in File.ReadAllLines(filePath))
                {
                    string[] parts = line.Split(new[] { ' ' }, 5);
                    if (parts.Length != 5)
                        continue;
                    if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                        && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                        && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                        table[parts[4]] = new StoredBounds { X = x, Y = y, Width = width, Height = height };
                }
                return table;
            }
            catch (Exception ex)
            {
                OverlayHost.LogWarning("window bounds could not be read (" + ex.GetType().Name + ").");
                return null;
            }
        }

        private static void writeFile(Dictionary<string, StoredBounds> table)
        {
            string directory = Path.GetDirectoryName(filePath);
            Directory.CreateDirectory(directory);
            var lines = new List<string>();
            foreach (KeyValuePair<string, StoredBounds> entry in table)
                lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4}",
                    entry.Value.X, entry.Value.Y, entry.Value.Width, entry.Value.Height, entry.Key));

            string temporary = filePath + ".tmp";
            File.WriteAllLines(temporary, lines.ToArray());
            if (File.Exists(filePath))
            {
                try
                {
                    // Atomic on NTFS: a concurrent reader never sees a missing
                    // or half-written file.
                    File.Replace(temporary, filePath, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(filePath);
                    File.Move(temporary, filePath);
                }
            }
            else
            {
                File.Move(temporary, filePath);
            }
        }
    }
}
