using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JapaneseTranscriptionPrototype.ViewModels;
using JapaneseTranscriptionPrototype.Services;

namespace JapaneseTranscriptionPrototype;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _allowClose;
    private bool _closing;
    private readonly CaptionOverlay _overlay;
    private readonly TimelineFollowState _timelineFollow = new();
    private ScrollViewer? _timelineScrollViewer;
    private INotifyCollectionChanged? _timelineCollection;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _overlay = new CaptionOverlay { DataContext = _viewModel };
        Loaded += (_, _) => _overlay.Owner = this;
        Loaded += (_, _) => AttachTimeline();
        Loaded += Window_Loaded;
        Closed += (_, _) => DetachTimeline();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.ShowOverlay) || _closing) return;
            if (_viewModel.ShowOverlay) _overlay.Show();
            else _overlay.Hide();
        };
    }

    private async void Window_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        if (_viewModel.UnpersistedCaptionCount > 0 &&
            MessageBox.Show(
                $"当前有 {_viewModel.UnpersistedCaptionCount} 条字幕尚未写入历史文件，关闭后不会保留。仍要关闭吗？",
                "字幕尚未保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }

        eventArgs.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"清理未能在限定时间内完成，程序仍保持打开。请检查错误后重试关闭。{Environment.NewLine}{exception.Message}",
                "关闭未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _closing = false;
            IsEnabled = true;
            return;
        }
        _overlay.AllowClose = true;
        _overlay.Close();
        _allowClose = true;
        Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"初始化音频设备失败：{exception.Message}",
                "初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AttachTimeline()
    {
        _timelineCollection = _viewModel.Captions;
        _timelineCollection.CollectionChanged += TimelineCollectionChanged;
        CaptionTimeline.UpdateLayout();
        _timelineScrollViewer = FindVisualChild<ScrollViewer>(CaptionTimeline);
        if (_timelineScrollViewer is not null)
            _timelineScrollViewer.ScrollChanged += TimelineScrollChanged;
        ScrollToLatestCaption();
    }

    private void DetachTimeline()
    {
        if (_timelineCollection is not null)
            _timelineCollection.CollectionChanged -= TimelineCollectionChanged;
        if (_timelineScrollViewer is not null)
            _timelineScrollViewer.ScrollChanged -= TimelineScrollChanged;
        _timelineCollection = null;
        _timelineScrollViewer = null;
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
