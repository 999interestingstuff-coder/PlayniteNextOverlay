using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    public class ScreenshotInfo
    {
        public string FilePath { get; set; }
        /// <summary>Small cached thumbnail made by Screenshots Visualizer (may be null).</summary>
        public string ThumbnailPath { get; set; }
        public DateTime Modified { get; set; }
        public long FileSize { get; set; }
    }

    public enum ScreenshotReadStatus
    {
        Ok,
        NotInstalled,
        NoFoldersConfigured,
        Failed
    }

    public class ScreenshotReadResult
    {
        public ScreenshotReadStatus Status { get; set; } = ScreenshotReadStatus.Ok;
        public List<ScreenshotInfo> Items { get; } = new List<ScreenshotInfo>();
        /// <summary>Number of usable images found (Items is capped to the newest ones).</summary>
        public int TotalCount { get; set; }
        public bool Scanned { get; set; }
        public bool UsedFallback { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Integration with the "Screenshots Visualizer" extension (Id: playnite-screenshotsvisualizer-plugin,
    /// plugin GUID c6c8276f-91bf-48e5-a1d1-4bee0b493488).
    ///
    /// Both plugins live in Playnite's process, so the overlay asks Screenshots Visualizer to scan the game's
    /// folders by calling the same two methods its own OnGameStopped handler uses
    /// (GetGameSettings + SetDataFromSettings), then reads the refreshed list from its in-memory database.
    /// That is done through reflection, so the overlay needs no reference to its assembly.
    /// If reflection fails (for example after a rename in a future version), the list is read from the
    /// plugin's JSON cache in ExtensionsData instead, without scanning.
    /// </summary>
    public static class ScreenshotsVisualizerReader
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static readonly Guid PluginId = Guid.Parse("c6c8276f-91bf-48e5-a1d1-4bee0b493488");

        // Scans must not overlap: Screenshots Visualizer replaces the game's whole entry on each scan.
        private static readonly object ScanLock = new object();

        // Formats WPF can decode without extra codecs. Videos and webp/avif are skipped.
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
        };

        private class Candidate
        {
            public object Source;
            public ScreenshotInfo Info;
        }

        public static bool IsInstalled(IPlayniteAPI api)
        {
            try
            {
                return api != null && api.Addons.Plugins.Any(p => p.Id == PluginId);
            }
            catch (Exception ex)
            {
                logger.Warn($"Screenshots Visualizer check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Optionally makes Screenshots Visualizer scan the game's screenshot folders, then returns the newest
        /// <paramref name="maxItems"/> images. Safe to call from a background thread. Never throws.
        /// </summary>
        public static ScreenshotReadResult ScanAndRead(IPlayniteAPI api, Guid gameId, bool scan, int maxItems)
        {
            var result = new ScreenshotReadResult();
            try
            {
                if (api == null || gameId == Guid.Empty)
                {
                    result.Status = ScreenshotReadStatus.Failed;
                    result.Message = "No game is associated with the overlay.";
                    return result;
                }

                var plugin = api.Addons.Plugins.FirstOrDefault(p => p.Id == PluginId);
                if (plugin == null)
                {
                    result.Status = ScreenshotReadStatus.NotInstalled;
                    return result;
                }

                lock (ScanLock)
                {
                    object db = null;
                    try { db = FindDatabase(plugin); }
                    catch (Exception ex) { logger.Warn($"Screenshots Visualizer: could not look up its database: {ex.Message}"); }

                    if (db != null)
                    {
                        try
                        {
                            if (scan)
                            {
                                result.Scanned = ScanGame(db, gameId);
                                if (!result.Scanned)
                                    result.Status = ScreenshotReadStatus.NoFoldersConfigured;
                            }

                            int total;
                            result.Items.AddRange(ReadFromDatabase(db, gameId, maxItems, out total));
                            result.TotalCount = total;
                            logger.Info($"Screenshots Visualizer: {total} image(s) for game {gameId} (scanned: {result.Scanned})");
                            return result;
                        }
                        catch (Exception ex)
                        {
                            logger.Warn($"Screenshots Visualizer: live read failed ({ex.Message}); falling back to its JSON cache.");
                            result.Items.Clear();
                            result.TotalCount = 0;
                            result.Scanned = false;
                            result.Status = ScreenshotReadStatus.Ok;
                        }
                    }
                    else
                    {
                        logger.Warn("Screenshots Visualizer: its database object was not found; falling back to its JSON cache.");
                    }

                    int fallbackTotal;
                    var fallback = ReadFromJsonCache(api, gameId, maxItems, out fallbackTotal);
                    result.UsedFallback = true;
                    if (fallback == null)
                    {
                        result.Status = ScreenshotReadStatus.Failed;
                        result.Message = "Could not read Screenshots Visualizer data.";
                        return result;
                    }
                    result.Items.AddRange(fallback);
                    result.TotalCount = fallbackTotal;
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Screenshots Visualizer integration failed.");
                result.Status = ScreenshotReadStatus.Failed;
                result.Message = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// The plugin keeps its database in a static member named PluginDatabase (declared on its plugin base class).
        /// Looks it up on the plugin type and its base types, as a property or a field.
        /// </summary>
        private static object FindDatabase(object plugin)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (Type t = plugin.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var prop = t.GetProperty("PluginDatabase", flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    var getter = prop.GetGetMethod(true);
                    if (getter != null)
                    {
                        var value = getter.Invoke(getter.IsStatic ? null : plugin, null);
                        if (value != null) return value;
                    }
                }

                var field = t.GetField("PluginDatabase", flags);
                if (field != null)
                {
                    var value = field.GetValue(field.IsStatic ? null : plugin);
                    if (value != null) return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Same steps as the plugin's own OnGameStopped / ActionAfterRefresh: GetGameSettings(id) then
        /// SetDataFromSettings(settings). Returns false (and does nothing) when the game has no screenshot folder,
        /// because a scan without folders would only wipe the game's entry.
        /// </summary>
        private static bool ScanGame(object db, Guid gameId)
        {
            var dbType = db.GetType();
            var getSettings = dbType.GetMethod("GetGameSettings", new[] { typeof(Guid) });
            var setData = dbType.GetMethods().FirstOrDefault(m => m.Name == "SetDataFromSettings" && m.GetParameters().Length == 1);
            if (getSettings == null || setData == null)
                throw new MissingMethodException("ScreenshotsVisualizerDatabase.GetGameSettings / SetDataFromSettings");

            var settings = Invoke(getSettings, db, new object[] { gameId });
            if (settings == null) return false;

            var folders = settings.GetType().GetProperty("ScreenshotsFolders")?.GetValue(settings, null) as ICollection;
            if (folders == null || folders.Count == 0) return false;

            Invoke(setData, db, new[] { settings });
            return true;
        }

        private static List<ScreenshotInfo> ReadFromDatabase(object db, Guid gameId, int maxItems, out int total)
        {
            total = 0;
            var list = new List<ScreenshotInfo>();

            var get = db.GetType().GetMethod("Get", new[] { typeof(Guid), typeof(bool), typeof(bool) });
            if (get == null)
                throw new MissingMethodException("ScreenshotsVisualizerDatabase.Get(Guid, bool, bool)");

            object data = Invoke(get, db, new object[] { gameId, true, false });
            if (data == null) return list;

            var enumerable = data.GetType().GetProperty("Items")?.GetValue(data, null) as IEnumerable;
            if (enumerable == null) return list;

            PropertyInfo fileProp = null, modifiedProp = null, videoProp = null, thumbProp = null;
            var candidates = new List<Candidate>();

            foreach (var source in enumerable)
            {
                if (source == null) continue;
                if (fileProp == null)
                {
                    var t = source.GetType();
                    fileProp = t.GetProperty("FileName");
                    modifiedProp = t.GetProperty("Modifed");   // sic, that is the property name in the plugin
                    videoProp = t.GetProperty("IsVideo");
                    thumbProp = t.GetProperty("ImageThumbnail");
                }

                string path = fileProp?.GetValue(source, null) as string;
                if (string.IsNullOrEmpty(path) || !IsSupportedImage(path) || !File.Exists(path)) continue;

                object isVideo = videoProp?.GetValue(source, null);
                if (isVideo is bool && (bool)isVideo) continue;

                DateTime modified = DateTime.MinValue;
                object modifiedValue = modifiedProp?.GetValue(source, null);
                if (modifiedValue is DateTime) modified = (DateTime)modifiedValue;
                else modified = SafeLastWrite(path);

                candidates.Add(new Candidate { Source = source, Info = new ScreenshotInfo { FilePath = path, Modified = modified } });
            }

            total = candidates.Count;

            // Only the newest ones get their thumbnail path read (reading it may generate the thumbnail).
            foreach (var c in candidates.OrderByDescending(x => x.Info.Modified).Take(maxItems))
            {
                try { c.Info.FileSize = new FileInfo(c.Info.FilePath).Length; } catch { }
                try { c.Info.ThumbnailPath = thumbProp?.GetValue(c.Source, null) as string; } catch { }
                list.Add(c.Info);
            }
            return list;
        }

        /// <summary>
        /// Fallback: reads ExtensionsData\{plugin guid}\ScreenshotsVisualizer\{gameId}.json directly.
        /// Returns null when the file is missing or has an unexpected shape.
        /// </summary>
        private static List<ScreenshotInfo> ReadFromJsonCache(IPlayniteAPI api, Guid gameId, int maxItems, out int total)
        {
            total = 0;
            try
            {
                string path = Path.Combine(api.Paths.ExtensionsDataPath, PluginId.ToString(), "ScreenshotsVisualizer", gameId.ToString() + ".json");
                if (!File.Exists(path))
                {
                    logger.Info($"Screenshots Visualizer JSON cache not found: {path}");
                    return null;
                }

                var all = new List<ScreenshotInfo>();
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    JsonElement items;
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("Items", out items) ||
                        items.ValueKind != JsonValueKind.Array)
                    {
                        logger.Warn("Screenshots Visualizer JSON cache has an unexpected layout.");
                        return null;
                    }

                    foreach (var el in items.EnumerateArray())
                    {
                        JsonElement fileEl;
                        if (!el.TryGetProperty("FileName", out fileEl) || fileEl.ValueKind != JsonValueKind.String) continue;
                        string file = fileEl.GetString();
                        if (string.IsNullOrEmpty(file) || !IsSupportedImage(file) || !File.Exists(file)) continue;

                        DateTime modified;
                        JsonElement modEl;
                        if (!(el.TryGetProperty("Modifed", out modEl) && modEl.ValueKind == JsonValueKind.String &&
                              DateTime.TryParse(modEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out modified)))
                        {
                            modified = SafeLastWrite(file);
                        }

                        all.Add(new ScreenshotInfo { FilePath = file, Modified = modified });
                    }
                }

                total = all.Count;
                var top = all.OrderByDescending(x => x.Modified).Take(maxItems).ToList();
                foreach (var info in top)
                {
                    try { info.FileSize = new FileInfo(info.FilePath).Length; } catch { }
                }
                return top;
            }
            catch (Exception ex)
            {
                logger.Warn($"Reading the Screenshots Visualizer JSON cache failed: {ex.Message}");
                return null;
            }
        }

        private static bool IsSupportedImage(string path)
        {
            try { return ImageExtensions.Contains(Path.GetExtension(path)); }
            catch { return false; }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static object Invoke(MethodInfo method, object target, object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                throw new InvalidOperationException(inner.Message, inner);
            }
        }
    }
}
