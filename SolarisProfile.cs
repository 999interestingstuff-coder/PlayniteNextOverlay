using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>Everything the Solaris profile card shows, already resolved to ready-to-draw values.</summary>
    public class SolarisProfileData
    {
        public string UserName { get; set; }
        public string Title { get; set; }
        public bool ShowUserName { get; set; } = true;
        public bool ShowTitle { get; set; } = true;
        public ImageSource Avatar { get; set; }
        public ImageSource Background { get; set; }
    }

    /// <summary>
    /// Reads the Solaris fullscreen theme's profile (avatar, profile background, username, profile title) from the
    /// Theme Options extension, the same place Solaris reads it from.
    ///
    /// Theme Options keeps the values the user picked in "Theme Options" in a public dictionary
    /// (ThemeOptions.Settings.Settings.Options). It is read by reflection, so there is no compile-time dependency, and it
    /// also raises PropertyChanged, which lets the overlay follow changes made in Solaris while a game is running.
    ///
    /// Solaris keys used (see the theme's options.yaml):
    ///   TO_Avatar            preset number; 100 = custom image, anything else = Images/Avatar/NN.png in the theme folder
    ///   TO_ProfileBackground preset number; 50  = custom image, 2..27 = Images/Profile/NN.png, otherwise no image
    ///   TO_CustomAvatarPath / TO_CustomProfilePath   path of the custom images
    ///   TO_UserName / TO_UserTitle                   texts
    ///   TO_UserNameMenu / TO_UserTitleMenu           "Show username" / "Show profile title"
    /// </summary>
    public static class SolarisProfileReader
    {
        public const string SolarisThemeId = "Solaris_b6e50d04-24ae-4ecd-bd3a-080367930992";
        public static readonly Guid ThemeOptionsPluginId = Guid.Parse("904cbf3b-573f-48f8-9642-0a09d05c64ef");

        private const int CustomAvatarPreset = 100;
        private const int CustomProfileBackgroundPreset = 50;
        private const int DefaultAvatarPreset = 1;

        /// <summary>Theme Options keys that change what the card shows.</summary>
        public static readonly string[] WatchedKeys =
        {
            "TO_Avatar", "TO_ProfileBackground", "TO_CustomAvatarPath", "TO_CustomProfilePath",
            "TO_UserName", "TO_UserTitle", "TO_UserNameMenu", "TO_UserTitleMenu"
        };

        private const BindingFlags AnyMember =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        private static string cachedThemeId;
        private static string cachedThemeDirectory;

        /// <summary>True when Playnite is running in fullscreen mode with the Solaris theme selected.</summary>
        public static bool IsSolarisFullscreenActive(IPlayniteAPI api)
        {
            try
            {
                if (api == null || api.ApplicationInfo.Mode != ApplicationMode.Fullscreen)
                    return false;

                string themeId = api.ApplicationSettings.FullscreenTheme;
                return !string.IsNullOrEmpty(themeId) &&
                       (string.Equals(themeId, SolarisThemeId, StringComparison.OrdinalIgnoreCase) ||
                        themeId.StartsWith("Solaris_", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Theme Options' live option dictionary, or null when the extension is not there.</summary>
        private static object GetOptionsObject(IPlayniteAPI api)
        {
            try
            {
                var plugin = api.Addons.Plugins.FirstOrDefault(p => p.Id == ThemeOptionsPluginId);
                if (plugin == null)
                    return null;

                // ThemeOptions.Settings (static SettingsViewModel) -> .Settings (Settings) -> .Options (DynamicProperties)
                object viewModel = plugin.GetType().GetProperty("Settings", AnyMember)?.GetValue(plugin, null);
                object settings = viewModel?.GetType().GetProperty("Settings", AnyMember)?.GetValue(viewModel, null);
                return settings?.GetType().GetProperty("Options", AnyMember)?.GetValue(settings, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Change notifications of Theme Options' option dictionary, or null when unavailable.</summary>
        public static INotifyPropertyChanged GetOptionsNotifier(IPlayniteAPI api)
        {
            return GetOptionsObject(api) as INotifyPropertyChanged;
        }

        /// <summary>Reads the profile as Solaris would show it right now.</summary>
        public static SolarisProfileData Read(IPlayniteAPI api, ILogger log)
        {
            var options = GetOptionsObject(api) as IDictionary<string, object>;
            if (options == null)
                log?.Info("Solaris profile: Theme Options values not available; using the theme defaults.");

            var data = new SolarisProfileData
            {
                UserName = ToText(Get(options, "TO_UserName"), "Username"),
                Title = ToText(Get(options, "TO_UserTitle"), "Title"),
                ShowUserName = ToBool(Get(options, "TO_UserNameMenu"), true),
                ShowTitle = ToBool(Get(options, "TO_UserTitleMenu"), true)
            };

            string themeId = api.ApplicationSettings.FullscreenTheme;
            string themeDirectory = FindThemeDirectory(api, themeId);

            // Avatar: custom file, or one of the theme's own pictures
            int avatar = ToInt(Get(options, "TO_Avatar"), DefaultAvatarPreset);
            string avatarPath = avatar == CustomAvatarPreset
                ? ToText(Get(options, "TO_CustomAvatarPath"), null)
                : PresetImagePath(themeDirectory, "Avatar", avatar);
            data.Avatar = LoadImage(avatarPath, 256);

            // Profile background: custom file, one of the theme's own pictures, or nothing (Solaris' plain translucent panel)
            int background = ToInt(Get(options, "TO_ProfileBackground"), 0);
            string backgroundPath = null;
            if (background == CustomProfileBackgroundPreset)
                backgroundPath = ToText(Get(options, "TO_CustomProfilePath"), null);
            else if (background >= 2)
                backgroundPath = PresetImagePath(themeDirectory, "Profile", background);
            data.Background = LoadImage(backgroundPath, 800);

            return data;
        }

        private static string PresetImagePath(string themeDirectory, string folder, int number)
        {
            if (string.IsNullOrEmpty(themeDirectory) || number <= 0)
                return null;
            return Path.Combine(themeDirectory, "Images", folder, number.ToString("D2", CultureInfo.InvariantCulture) + ".png");
        }

        // ---- theme folder -------------------------------------------------------------------------------------------

        /// <summary>Folder of the installed fullscreen theme with this id (the one that holds theme.yaml and Images).</summary>
        public static string FindThemeDirectory(IPlayniteAPI api, string themeId)
        {
            if (string.IsNullOrEmpty(themeId))
                return null;

            if (cachedThemeId == themeId && cachedThemeDirectory != null && Directory.Exists(cachedThemeDirectory))
                return cachedThemeDirectory;

            try
            {
                var roots = new List<string>
                {
                    Path.Combine(api.Paths.ConfigurationPath, "Themes", "Fullscreen"),
                    Path.Combine(api.Paths.ApplicationPath, "Themes", "Fullscreen")
                };

                foreach (string root in roots)
                {
                    if (!Directory.Exists(root))
                        continue;

                    // Playnite normally names the folder after the theme id
                    string direct = Path.Combine(root, themeId);
                    if (File.Exists(Path.Combine(direct, "theme.yaml")))
                        return Remember(themeId, direct);

                    foreach (string directory in Directory.GetDirectories(root))
                    {
                        string manifest = Path.Combine(directory, "theme.yaml");
                        if (File.Exists(manifest) && ManifestHasId(manifest, themeId))
                            return Remember(themeId, directory);
                    }
                }
            }
            catch
            {
                // fall through: no theme folder, the preset pictures simply stay empty
            }

            return null;
        }

        private static string Remember(string themeId, string directory)
        {
            cachedThemeId = themeId;
            cachedThemeDirectory = directory;
            return directory;
        }

        private static bool ManifestHasId(string manifestPath, string themeId)
        {
            try
            {
                foreach (string raw in File.ReadLines(manifestPath))
                {
                    string line = raw.Trim().TrimStart('﻿');
                    if (!line.StartsWith("Id:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string value = line.Substring(3).Trim().Trim('\'', '"');
                    return string.Equals(value, themeId, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
            return false;
        }

        // ---- images -------------------------------------------------------------------------------------------------

        /// <summary>Loads a picture without keeping the file open; returns null when it is missing or unreadable.</summary>
        public static ImageSource LoadImage(string path, int decodePixelWidth)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (!File.Exists(path))
                    return null;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.StreamSource = stream;
                    if (decodePixelWidth > 0)
                        bitmap.DecodePixelWidth = decodePixelWidth;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---- value conversion (Theme Options values may be int, long, bool or string depending on where they came from) ----

        private static object Get(IDictionary<string, object> options, string key)
        {
            object value;
            return options != null && options.TryGetValue(key, out value) ? value : null;
        }

        private static string ToText(object value, string fallback)
        {
            if (value == null)
                return fallback;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return text ?? fallback;
        }

        private static int ToInt(object value, int fallback)
        {
            if (value == null)
                return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                int parsed;
                return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    ? parsed
                    : fallback;
            }
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null)
                return fallback;
            if (value is bool)
                return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }
    }

    /// <summary>
    /// Calls back (once, shortly after the last change) whenever a profile setting changes in Theme Options, so the card
    /// follows what the user picks in Solaris.
    /// </summary>
    public sealed class ThemeOptionsWatcher
    {
        private readonly IPlayniteAPI api;
        private readonly Action changed;
        private INotifyPropertyChanged source;
        private DispatcherTimer timer;

        public ThemeOptionsWatcher(IPlayniteAPI api, Action changed)
        {
            this.api = api;
            this.changed = changed;
        }

        public void Start()
        {
            Stop();
            try
            {
                source = SolarisProfileReader.GetOptionsNotifier(api);
                if (source != null)
                    source.PropertyChanged += OnOptionChanged;
            }
            catch
            {
                source = null;
            }
        }

        public void Stop()
        {
            if (source != null)
            {
                source.PropertyChanged -= OnOptionChanged;
                source = null;
            }
        }

        private void OnOptionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) && Array.IndexOf(SolarisProfileReader.WatchedKeys, e.PropertyName) < 0)
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            // Typing a path fires one change per letter: wait until it settles.
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (timer == null)
                {
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        changed?.Invoke();
                    };
                }
                timer.Stop();
                timer.Start();
            }));
        }
    }

    /// <summary>Data bound by the profile card under the Playtime panel.</summary>
    public class SolarisProfileViewModel : INotifyPropertyChanged
    {
        private bool _isVisible;
        private string _userName;
        private string _title;
        private bool _showUserNameOption = true;
        private bool _showTitleOption = true;
        private ImageSource _avatar;
        private ImageSource _background;

        /// <summary>The card is only drawn while this is true (game started through Solaris, Solaris active).</summary>
        public bool IsVisible
        {
            get => _isVisible;
            private set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        public string UserName
        {
            get => _userName;
            private set { _userName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowUserName)); }
        }

        public string Title
        {
            get => _title;
            private set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowTitle)); }
        }

        /// <summary>"Show username" option of Solaris and not empty.</summary>
        public bool ShowUserName => _showUserNameOption && !string.IsNullOrWhiteSpace(_userName);

        /// <summary>"Show profile title" option of Solaris and not empty (Solaris hides the chip when the title is empty).</summary>
        public bool ShowTitle => _showTitleOption && !string.IsNullOrWhiteSpace(_title);

        public ImageSource Avatar
        {
            get => _avatar;
            private set { _avatar = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAvatar)); }
        }

        public ImageSource Background
        {
            get => _background;
            private set { _background = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBackground)); }
        }

        public bool HasAvatar => _avatar != null;
        public bool HasBackground => _background != null;

        public void Apply(SolarisProfileData data)
        {
            if (data == null)
            {
                Clear();
                return;
            }

            _showUserNameOption = data.ShowUserName;
            _showTitleOption = data.ShowTitle;
            UserName = data.UserName;
            Title = data.Title;
            Avatar = data.Avatar;
            Background = data.Background;
            IsVisible = true;
        }

        public void Clear()
        {
            IsVisible = false;
            Avatar = null;
            Background = null;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
