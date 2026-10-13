using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Jsr4Z8.UI.Views;

/// <summary>
/// Read-only log panel (Task 13): renders a bound ObservableCollection of lines in a
/// scrollable, max-height viewer and auto-scrolls to the newest line ONLY while the
/// user is already at the bottom — scrolling up pins the view so streaming backend
/// chatter cannot yank it around. Expansion stays manual: the owning card's ▾日志
/// toggle governs visibility; this panel adds no collapse UI. CollectionChanged is
/// expected on the UI thread (QueueMediator marshals); the scroll itself is posted
/// defensively.
/// </summary>
public partial class LogPanel : UserControl
{
    /// <summary>Extents/viewport bookkeeping tolerance for the at-bottom check (px).</summary>
    private const double BottomTolerance = 4.0;

    public static readonly StyledProperty<ObservableCollection<string>?> LogLinesProperty =
        AvaloniaProperty.Register<LogPanel, ObservableCollection<string>?>(nameof(LogLines));

    public LogPanel()
    {
        InitializeComponent();
    }

    /// <summary>The log lines to render; observable so streaming appends follow live.</summary>
    public ObservableCollection<string>? LogLines
    {
        get => GetValue(LogLinesProperty);
        set => SetValue(LogLinesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LogLinesProperty)
        {
            if (change.OldValue is ObservableCollection<string> old)
                old.CollectionChanged -= OnLinesChanged;
            if (change.NewValue is ObservableCollection<string> neu)
                neu.CollectionChanged += OnLinesChanged;
        }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !IsAtBottom())
            return;
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd());
    }

    private bool IsAtBottom() =>
        Scroller.Extent.Height - Scroller.Viewport.Height - Scroller.Offset.Y <= BottomTolerance;
}
