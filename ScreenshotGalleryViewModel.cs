using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.SDK;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// MainLayer.IsEnabled = (main layer enabled) AND (screenshot gallery closed).
    /// Replaces the XAML binding so OverlayWindow.xaml itself does not need to change.
    /// </summary>
    internal class MainLayerEnabledConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool main = values != null && values.Length > 0 && values[0] is bool && (bool)values[0];
            bool gallery = values != null && values.Length > 1 && values[1] is bool && (bool)values[1];
            return main && !gallery;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Loads the full-size image for the viewer (decoded once, file never kept locked).</summary>
    public static class ScreenshotImageLoader
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Images wider than this are decoded at this width to keep memory down.</summary>
        public const int MaxViewerWidth = 1920;

        public static ImageSource LoadFull(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    // First pass: read only the header for the real pixel size.
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = decoder.Frames[0].PixelWidth;
                    height = decoder.Frames[0].PixelHeight;

                    // Second pass: decode (downscaled when very large).
                    fs.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = fs;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (width > MaxViewerWidth)
                        bmp.DecodePixelWidth = MaxViewerWidth;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch (Exception ex)
            {
                try { logger.Warn($"Could not load screenshot '{path}': {ex.Message}"); } catch { }
                return null;
            }
        }
    }

    public class ScreenshotListItem
    {
        /// <summary>Used only when no Screenshots Visualizer thumbnail exists and the full image is shown instead.</summary>
        public const int FallbackThumbDecodeWidth = 320;

        private bool thumbLoaded;
        private ImageSource thumb;

        public ScreenshotListItem(ScreenshotInfo info)
        {
            Info = info;
        }

        public ScreenshotInfo Info { get; }

        public string FilePath => Info.FilePath;
        public string FileName => Path.GetFileName(Info.FilePath);

        public string DateText => Info.Modified == DateTime.MinValue
            ? string.Empty
            : Info.Modified.ToString("g", CultureInfo.CurrentCulture);

        public string SizeText => FormatSize(Info.FileSize);

        /// <summary>Loaded lazily the first time the card is shown.</summary>
        public ImageSource Thumbnail
        {
            get
            {
                if (!thumbLoaded)
                {
                    thumbLoaded = true;
                    bool hasThumb = !string.IsNullOrEmpty(Info.ThumbnailPath) && File.Exists(Info.ThumbnailPath);
                    string source = hasThumb ? Info.ThumbnailPath : Info.FilePath;
                    thumb = AchievementIconLoader.Load(source, false, hasThumb ? 0 : FallbackThumbDecodeWidth);
                }
                return thumb;
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return string.Empty;
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0
                ? bytes + " B"
                : value.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    public class ScreenshotGalleryViewModel : INotifyPropertyChanged
    {
        /// <summary>Cards per row. The card width in the XAML is chosen so exactly this many fit.</summary>
        public const int GridColumns = 4;

        /// <summary>Only the newest captures are shown, like the "Last captures" view of other overlays.</summary>
        public const int MaxItems = 48;

        public const string SourceName = "Screenshots Visualizer";

        private bool isOpen;
        private bool isLoading;
        private bool isViewerOpen;
        private string gameName;
        private string emptyText = string.Empty;
        private string gamePauseStatus;
        private IList<ScreenshotListItem> items = new List<ScreenshotListItem>();
        private int totalCount;
        private int loadVersion;
        private int viewerIndex = -1;
        private ImageSource viewerImage;
        private string viewerInfo = string.Empty;

        public ScreenshotGalleryViewModel()
        {
            BackCommand = new RelayCommand(_ =>
            {
                if (IsViewerOpen) CloseViewer();
                else CloseRequested?.Invoke();
            });
            RefreshCommand = new RelayCommand(_ => RefreshRequested?.Invoke());
            OpenFolderCommand = new RelayCommand(_ => OpenFolderRequested?.Invoke());
            ViewerPreviousCommand = new RelayCommand(_ => ViewerStep(-1));
            ViewerNextCommand = new RelayCommand(_ => ViewerStep(1));
        }

        public ICommand BackCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ViewerPreviousCommand { get; }
        public ICommand ViewerNextCommand { get; }

        /// <summary>Back pressed while the grid (not the viewer) is showing: the window closes the gallery.</summary>
        public event Action CloseRequested;
        public event Action RefreshRequested;
        public event Action OpenFolderRequested;
        /// <summary>Raised when the viewer moves to another image, so the grid selection can follow.</summary>
        public event Action<int> ViewerIndexChanged;

        // ---- state ----

        public bool IsOpen
        {
            get => isOpen;
            private set { isOpen = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => isLoading;
            private set
            {
                isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ShowEmptyText));
                OnPropertyChanged(nameof(ShowingText));
                OnPropertyChanged(nameof(HeaderCountText));
            }
        }

        public bool IsViewerOpen
        {
            get => isViewerOpen;
            private set { isViewerOpen = value; OnPropertyChanged(); }
        }

        public string GameName
        {
            get => gameName;
            private set { gameName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ViewerTitle)); }
        }

        public IList<ScreenshotListItem> Items
        {
            get => items;
            private set
            {
                items = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ShowEmptyText));
                OnPropertyChanged(nameof(ShowingText));
            }
        }

        public int TotalCount
        {
            get => totalCount;
            private set
            {
                totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderCountText));
                OnPropertyChanged(nameof(HeaderCountCaption));
                OnPropertyChanged(nameof(ShowingText));
            }
        }

        public bool IsEmpty => items.Count == 0;
        public bool ShowEmptyText => IsEmpty && !isLoading;

        public string EmptyText
        {
            get => emptyText;
            private set { emptyText = value; OnPropertyChanged(); }
        }

        public string HeaderCountText => isLoading ? "\u2026" : totalCount.ToString(CultureInfo.CurrentCulture);
        public string HeaderCountCaption => totalCount == 1 ? "capture" : "captures";

        public string ShowingText
        {
            get
            {
                if (isLoading) return "Scanning screenshot folders\u2026";
                if (items.Count == 0) return string.Empty;
                return totalCount > items.Count
                    ? $"Showing the newest {items.Count} of {totalCount}"
                    : "Newest first";
            }
        }

        /// <summary>"Game paused" / "Game not paused (...)" chip, set by the overlay window.</summary>
        public string GamePauseStatus
        {
            get => gamePauseStatus;
            set { gamePauseStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasGamePauseStatus)); }
        }

        public bool HasGamePauseStatus => !string.IsNullOrEmpty(gamePauseStatus);

        // ---- viewer ----

        public string ViewerTitle => gameName;

        public string ViewerSubtitle
        {
            get
            {
                if (viewerIndex < 0 || viewerIndex >= items.Count) return string.Empty;
                var item = items[viewerIndex];
                return string.IsNullOrEmpty(item.DateText)
                    ? SourceName
                    : item.DateText + "  \u2022  " + SourceName;
            }
        }

        public string ViewerCounter => viewerIndex < 0 ? string.Empty : $"{viewerIndex + 1} / {items.Count}";

        public string ViewerInfo
        {
            get => viewerInfo;
            private set { viewerInfo = value; OnPropertyChanged(); }
        }

        public ImageSource ViewerImage
        {
            get => viewerImage;
            private set { viewerImage = value; OnPropertyChanged(); }
        }

        // ---- lifecycle ----

        /// <summary>Opens the gallery (empty, loading state). Call <see cref="BeginLoad"/> next.</summary>
        public void Open(string game)
        {
            GameName = game;
            viewerIndex = -1;
            IsViewerOpen = false;
            ViewerImage = null;
            GamePauseStatus = null;
            IsOpen = true;
        }

        /// <summary>Clears the list, shows the scanning state and returns a token for <see cref="CompleteLoad"/>.</summary>
        public int BeginLoad()
        {
            loadVersion++;
            Items = new List<ScreenshotListItem>();
            TotalCount = 0;
            EmptyText = string.Empty;
            IsLoading = true;
            return loadVersion;
        }

        /// <summary>
        /// Applies a finished scan. Returns false (and changes nothing) when the gallery was closed or reloaded
        /// since <see cref="BeginLoad"/>, so a late result never overwrites newer state.
        /// </summary>
        public bool CompleteLoad(int version, ScreenshotReadResult result)
        {
            if (version != loadVersion || !isOpen || result == null)
                return false;

            var list = result.Items.Select(i => new ScreenshotListItem(i)).ToList();
            EmptyText = BuildEmptyText(result);
            Items = list;
            TotalCount = Math.Max(result.TotalCount, list.Count);
            IsLoading = false;
            return true;
        }

        /// <summary>Closes everything and drops the list (and its decoded images).</summary>
        public void Close()
        {
            loadVersion++;
            IsViewerOpen = false;
            viewerIndex = -1;
            ViewerImage = null;
            IsLoading = false;
            Items = new List<ScreenshotListItem>();
            TotalCount = 0;
            EmptyText = string.Empty;
            GamePauseStatus = null;
            IsOpen = false;
        }

        public void OpenViewer(int index)
        {
            if (index < 0 || index >= items.Count) return;
            ShowViewerIndex(index);
            IsViewerOpen = true;
        }

        public void CloseViewer()
        {
            if (!isViewerOpen) return;
            IsViewerOpen = false;
            viewerIndex = -1;
            ViewerImage = null;
            ViewerInfo = string.Empty;
            RaiseViewerTextChanged();
        }

        /// <summary>Moves the viewer by <paramref name="delta"/> images (clamped). Returns false at either end.</summary>
        public bool ViewerStep(int delta)
        {
            if (!isViewerOpen || items.Count == 0) return false;
            int next = Math.Max(0, Math.Min(items.Count - 1, viewerIndex + delta));
            if (next == viewerIndex) return false;
            ShowViewerIndex(next);
            return true;
        }

        private void ShowViewerIndex(int index)
        {
            viewerIndex = index;
            var item = items[index];

            int width, height;
            var image = ScreenshotImageLoader.LoadFull(item.FilePath, out width, out height);
            ViewerImage = image;
            ViewerInfo = image == null
                ? item.FileName + " | could not be loaded"
                : $"{item.FileName} | {width} x {height}";

            RaiseViewerTextChanged();
            ViewerIndexChanged?.Invoke(index);
        }

        private void RaiseViewerTextChanged()
        {
            OnPropertyChanged(nameof(ViewerSubtitle));
            OnPropertyChanged(nameof(ViewerCounter));
        }

        private static string BuildEmptyText(ScreenshotReadResult result)
        {
            switch (result.Status)
            {
                case ScreenshotReadStatus.NotInstalled:
                    return "Screenshots Visualizer is not installed or not enabled.";
                case ScreenshotReadStatus.Failed:
                    return "Could not read Screenshots Visualizer data. Details are in the Playnite log.";
                case ScreenshotReadStatus.NoFoldersConfigured:
                    return "No screenshot folder is set up for this game in Screenshots Visualizer. Add one in its game settings, then press Y to rescan.";
                default:
                    return "No screenshots found for this game.";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
