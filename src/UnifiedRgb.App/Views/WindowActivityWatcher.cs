using System.Windows;

namespace UnifiedRgb.App.Views;

/// <summary>
/// Tracks whether a WPF element is actually visible in a non-minimized, visible host Window.
/// Pauses background UI timers / high-frequency rendering while hidden or minimized to tray,
/// and signals when the view becomes active again so an immediate refresh can run.
/// </summary>
internal sealed class WindowActivityWatcher
{
    private readonly FrameworkElement _element;
    private readonly Action<bool, bool> _onChanged;
    private Window? _window;
    private bool _hasBeenActive;
    private bool _isActive;

    public WindowActivityWatcher(FrameworkElement element, Action<bool, bool> onChanged)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _element.Loaded += OnElementChanged;
        _element.Unloaded += OnElementUnloaded;
        _element.IsVisibleChanged += OnElementVisibilityChanged;
    }

    public bool IsActive => _isActive;

    public void Update()
    {
        AttachWindow();
        var next = _element.IsLoaded
            && _element.IsVisible
            && (_window is null || (_window.IsVisible && _window.WindowState != WindowState.Minimized));
        if (next == _isActive)
        {
            return;
        }

        var resumed = next && _hasBeenActive;
        _isActive = next;
        if (next)
        {
            _hasBeenActive = true;
        }

        _onChanged(next, resumed);
    }

    private void OnElementChanged(object sender, RoutedEventArgs e) => Update();

    private void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWindow();
        if (_isActive)
        {
            _isActive = false;
            _onChanged(false, false);
        }
    }

    private void OnElementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();

    private void OnWindowChanged(object? sender, EventArgs e) => Update();

    private void OnWindowVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();

    private void AttachWindow()
    {
        var current = _element.IsLoaded ? Window.GetWindow(_element) : null;
        if (ReferenceEquals(current, _window))
        {
            return;
        }

        DetachWindow();
        _window = current;
        if (_window is not null)
        {
            _window.IsVisibleChanged += OnWindowVisibleChanged;
            _window.StateChanged += OnWindowChanged;
        }
    }

    private void DetachWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.IsVisibleChanged -= OnWindowVisibleChanged;
        _window.StateChanged -= OnWindowChanged;
        _window = null;
    }
}
