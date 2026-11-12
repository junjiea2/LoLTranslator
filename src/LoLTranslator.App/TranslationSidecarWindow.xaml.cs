using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LoLTranslator.App;

/// <summary>DisplayedNow is a legacy name for UI submission, not a compositor/screen acknowledgement.</summary>
public sealed record TranslationSidecarApplyResult(
    int DisplayedNow,
    int Enqueued,
    int MissingSafePrefix,
    int UntrustedPresentation,
    int DuplicateSuppressed)
{
    public static TranslationSidecarApplyResult Empty { get; } = new(0, 0, 0, 0, 0);
}

public partial class TranslationSidecarWindow : Window
{
    private static readonly TimeSpan MessageDisplayDuration = TimeSpan.FromSeconds(10);
    private sealed record TimedEntry(string EventId, long Sequence, TextBlock View, DateTimeOffset ExpiresAt);
    private readonly List<TimedEntry> _messages = new();
    private long _retiredSequence;
    private readonly RuntimeViewTracking _viewTracking = new();
    public event Action<RuntimeViewReceipt>? ViewReceipt;
    private readonly DispatcherTimer _expiryTimer;
    private bool _userPositioned;

    public TranslationSidecarWindow()
    {
        InitializeComponent();
        _viewTracking.Receipt += receipt => ViewReceipt?.Invoke(receipt);
        CompositionTarget.Rendering += OnRenderOpportunity;
        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _expiryTimer.Tick += (_, _) => RefreshEntries();
        _expiryTimer.Start();
        RefreshEntries();
    }

    public void PositionNear(Rect region)
    {
        if (_userPositioned) return;
        if (TranslationSidecarPositionStore.TryLoad(SystemParameters.WorkArea, Width, Height, 1.0, out Point saved))
        { Left = saved.X; Top = saved.Y; _userPositioned = true; return; }
        DockNear(region);
    }

    public void DockNear(Rect region)
    {
        Rect position = TranslationSidecarPositioner.Calculate(region, SystemParameters.WorkArea, Width, Height);
        Left = position.Left; Top = position.Top; _userPositioned = false;
    }

    public void SetAdjusting(bool adjusting)
    {
        DragHandle.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;
        RefreshEntries();
        if (adjusting) ShowWithoutActivation();
    }

    public void ResetSession()
    {
        _viewTracking.Reset();
        _messages.Clear();
        _retiredSequence = 0;
        RefreshEntries();
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible) Show();
        _ = Dispatcher.BeginInvoke(() =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Draw only a prevalidated, player-ID-free segment contract. No parsing, inference or translation queue.</summary>
    public TranslationSidecarApplyResult Present(SafeRuntimeDisplayMessage message)
    {
        RefreshEntries();
        if (_messages.Any(entry => entry.EventId == message.EventId || entry.Sequence == message.Sequence))
            return new(0, 0, 0, 0, 1);
        _viewTracking.Submitted(message);
        if (message.Sequence <= _retiredSequence ||
            (_messages.Count >= 8 && message.Sequence < _messages[0].Sequence))
        {
            _viewTracking.Removed(message.EventId, "outside_visible_sequence_window");
            return new(0, 0, 0, 1, 0);
        }
        int index = _messages.FindIndex(entry => entry.Sequence > message.Sequence);
        if (index < 0) index = _messages.Count;
        _messages.Insert(index, new TimedEntry(message.EventId, message.Sequence,
            CreateMessageView(message.Display), DateTimeOffset.Now.Add(MessageDisplayDuration)));
        while (_messages.Count > 8) Retire(_messages[0], "capacity_evicted");
        RefreshEntries();
        return new(1, 1, 0, 0, 0);
    }

    private void RefreshEntries()
    {
        // Sequence order and expiry order differ when a model finishes late.
        foreach (TimedEntry entry in _messages.Where(entry => entry.ExpiresAt <= DateTimeOffset.Now).ToArray())
            Retire(entry, "expired");
        TranslationItems.ItemsSource = _messages.Select(entry => entry.View).ToArray();
        bool hasVisibleContent = _messages.Count > 0 || DragHandle.Visibility == Visibility.Visible;
        RootBorder.Opacity = hasVisibleContent ? 0.92 : 0;
        if (!hasVisibleContent && IsVisible) Hide();
    }

    private void Retire(TimedEntry entry, string reason)
    {
        _messages.Remove(entry);
        _retiredSequence = Math.Max(_retiredSequence, entry.Sequence);
        _viewTracking.Removed(entry.EventId, reason);
    }

    private void OnRenderOpportunity(object? sender, EventArgs args)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || RootBorder.Opacity <= 0) return;
        var viewport = new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight);
        var visible = new List<string>();
        foreach (TimedEntry entry in _messages)
        {
            if (!entry.View.IsVisible || entry.View.ActualHeight <= 0 || entry.View.ActualWidth <= 0 ||
                !RootBorder.IsAncestorOf(entry.View)) continue;
            Rect bounds = entry.View.TransformToAncestor(RootBorder)
                .TransformBounds(new Rect(entry.View.RenderSize));
            if (viewport.Contains(bounds)) visible.Add(entry.EventId);
        }
        _viewTracking.RenderOpportunity(visible);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderOpportunity;
        _expiryTimer.Stop();
        _viewTracking.Reset();
        base.OnClosed(e);
    }

    private static TextBlock CreateMessageView(TranslationSidecarDisplayLine display)
    {
        var block = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 2, Opacity = 0.8 }
        };
        foreach (TranslationSidecarSegment segment in display.Segments)
        {
            block.Inlines.Add(new Run(segment.Text) { Foreground = BrushFor(segment.Role) });
        }
        return block;
    }

    private static Brush BrushFor(TranslationSidecarTextRole role) => role switch
    {
        TranslationSidecarTextRole.AlliedIdentity => new SolidColorBrush(Color.FromRgb(76, 181, 255)),
        TranslationSidecarTextRole.EnemyIdentity => new SolidColorBrush(Color.FromRgb(255, 101, 101)),
        TranslationSidecarTextRole.Item => new SolidColorBrush(Color.FromRgb(234, 203, 120)),
        TranslationSidecarTextRole.System => new SolidColorBrush(Color.FromRgb(238, 174, 55)),
        TranslationSidecarTextRole.Muted => new SolidColorBrush(Color.FromRgb(163, 176, 194)),
        _ => new SolidColorBrush(Color.FromRgb(229, 231, 235))
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        // Translated output may sit next to or partly over the selected chat
        // area. Excluding it prevents translations from triggering OCR again.
        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        Rect workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        _userPositioned = true;
        TranslationSidecarPositionStore.Save(Left, Top, 1.0);
    }

    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
