using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PlayniteGameOverlay
{
    /// <summary>
    /// In-overlay screenshot gallery (card grid + full-size viewer). The OverlayWindow owns the input handling
    /// (controller, keyboard, hold-to-repeat) and drives this view through the small API below.
    /// </summary>
    public partial class ScreenshotGalleryView : UserControl
    {
        public ScreenshotGalleryView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private ScreenshotGalleryViewModel ViewModel
        {
            get { return DataContext as ScreenshotGalleryViewModel; }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var old = e.OldValue as ScreenshotGalleryViewModel;
            if (old != null) old.ViewerIndexChanged -= SelectIndex;

            var vm = e.NewValue as ScreenshotGalleryViewModel;
            if (vm != null) vm.ViewerIndexChanged += SelectIndex;
        }

        public int ItemCount
        {
            get { return ScreenshotListBox.Items.Count; }
        }

        public int SelectedIndex
        {
            get { return ScreenshotListBox.SelectedIndex; }
        }

        /// <summary>Height of one card row: 138 thumbnail + about 44 text + 16 padding + 4 border + 10 margin.</summary>
        private const double CardRowHeight = 212;

        /// <summary>Rows that are fully visible in the grid right now (at least 1); used for page up / page down.</summary>
        public int VisibleRows
        {
            get { return Math.Max(1, (int)(ScreenshotListBox.ActualHeight / CardRowHeight)); }
        }

        /// <summary>Selects a card and scrolls it into view. Out-of-range indexes are clamped.</summary>
        public void SelectIndex(int index)
        {
            int count = ItemCount;
            if (count == 0 || index < 0) return;
            if (index >= count) index = count - 1;
            ScreenshotListBox.SelectedIndex = index;
            ScreenshotListBox.ScrollIntoView(ScreenshotListBox.Items[index]);
        }

        public void FocusList()
        {
            ScreenshotListBox.Focus();
            // The layer may only just have become visible; focus again once layout has run.
            Dispatcher.BeginInvoke(new Action(() => ScreenshotListBox.Focus()), DispatcherPriority.Loaded);
        }

        /// <summary>Mouse / touch: clicking a card opens it in the viewer.</summary>
        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var container = sender as ListBoxItem;
            var vm = ViewModel;
            if (container == null || vm == null) return;

            int index = ScreenshotListBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0) return;

            ScreenshotListBox.SelectedIndex = index;
            vm.OpenViewer(index);
            e.Handled = true;
        }
    }
}
