using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JapaneseTranscriptionPrototype.Services;
using JapaneseTranscriptionPrototype.ViewModels;

namespace JapaneseTranscriptionPrototype;

public partial class CaptionOverlay : Window
{
    public bool AllowClose { get; set; }
    private readonly TimelineFollowState _timelineFollow = new();
    private INotifyCollectionChanged? _timelineCollection;
    private ScrollViewer? _timelineScrollViewer;

    public CaptionOverlay() => InitializeComponent();

    private void Overlay_Loaded(object sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model || _timelineCollection is not null)
            return;

        _timelineCollection = model.Captions;
        _timelineCollection.CollectionChanged += TimelineCollectionChanged;
        CaptionTimeline.UpdateLayout();
        _timelineScrollViewer = FindVisualChild<ScrollViewer>(CaptionTimeline);
        if (_timelineScrollViewer is not null)
            _timelineScrollViewer.ScrollChanged += TimelineScrollChanged;
        ScrollToLatestCaption();
    }

    private void Overlay_Closed(object? sender, EventArgs args)
    {
        if (_timelineCollection is not null)
            _timelineCollection.CollectionChanged -= TimelineCollectionChanged;
        if (_timelineScrollViewer is not null)
            _timelineScrollViewer.ScrollChanged -= TimelineScrollChanged;
        _timelineCollection = null;
        _timelineScrollViewer = null;
    }

    private void DragCaption(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseCaption(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel model)
            model.ShowOverlay = false;
    }

    private void HideCaption(object? sender, CancelEventArgs args)
    {
        if (AllowClose) return;
        args.Cancel = true;
        if (DataContext is MainViewModel model) model.ShowOverlay = false;
        Hide();
    }

    private void TimelineCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_timelineFollow.IsFollowingTail)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToLatestCaption));
    }

    private void TimelineScrollChanged(object sender, ScrollChangedEventArgs args) =>
        _timelineFollow.OnScroll(
            args.VerticalOffset, args.ExtentHeight - args.ViewportHeight, args.ExtentHeightChange != 0);

    private void ScrollToLatestCaption()
    {
        if (_timelineFollow.IsFollowingTail && CaptionTimeline.Items.Count > 0)
            CaptionTimeline.ScrollIntoView(CaptionTimeline.Items[CaptionTimeline.Items.Count - 1]);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
                return result;
            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }
}
