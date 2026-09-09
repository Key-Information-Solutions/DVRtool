using System.Windows;
using System.Windows.Controls;

namespace DVRTool.App;

/// <summary>
/// Fullscreen live view: the same window with everything but the video taken off it.
/// </summary>
/// <remarks>
/// <para>
/// A tech at a site is usually looking for one thing in a picture, and the chrome around it —
/// a 290 px device list, two toolbars, the tab strip, the status bar — is most of a laptop
/// screen. So fullscreen is a plain toggle on the Live tab (the button, <c>F11</c>, and
/// <c>Esc</c> to come back), and it composes with the grid: a full page of sixteen fills the
/// screen, and maximizing one of them fills it with that camera.
/// </para>
/// <para>
/// The load-bearing decision is that <em>nothing moves in the visual tree</em>. It would be
/// natural to reparent the video into a borderless window of its own, and that is exactly
/// what must not happen: a <c>VideoView</c> is a hosted child window, LibVLC is rendering
/// into that window's handle, and moving the host to another window destroys the handle and
/// recreates it — a black pane, or a player pointed at a window that no longer exists, for
/// every tile at once. Instead the window itself goes borderless and maximized and the chrome
/// around the video is collapsed in place: the video's own hierarchy, its handles and its
/// streams are untouched, so entering and leaving fullscreen costs a layout pass and never a
/// reconnect.
/// </para>
/// <para>
/// The tab strip goes away by giving each <see cref="TabItem"/> an empty control template.
/// A tab's own template draws nothing but its header — the selected tab's <em>content</em> is
/// hosted by the <c>TabControl</c>'s template, in a presenter this never touches — so an empty
/// one leaves a header of no size and the video where it was. The two obvious alternatives are
/// both wrong here: swapping the <c>TabControl</c>'s template rebuilds that content presenter
/// and reparents the video after all, and collapsing the tabs themselves was tried and leaves
/// the pane blank, because a <c>TabControl</c> whose selected item is collapsed has no selected
/// content to show.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private bool _fullScreen;

    // What the window looked like before, restored verbatim on the way out.
    private WindowStyle _preFullScreenStyle;
    private WindowState _preFullScreenState;
    private ResizeMode _preFullScreenResize;
    private GridLength _preFullScreenSideWidth;
    private GridLength _preFullScreenSplitterWidth;
    private Thickness _preFullScreenTabMargin;
    private string _preFullScreenStatus = "";

    // The fisheye mode's own toolbars and hint line: shown or not depending on the mode, so
    // what they were is remembered rather than assumed.
    private Visibility _preFullScreenDewarpChrome;
    private Visibility _preFullScreenDewarpHint;

    /// <summary>A tab header that draws nothing and takes no room.</summary>
    private static readonly ControlTemplate NoTabHeader = new(typeof(TabItem));

    private void OnLiveFullScreen(object sender, RoutedEventArgs e) => ToggleLiveFullScreen();

    /// <summary>
    /// Fullscreen on or off. Only from the Live tab — the button and the key live there, and
    /// a tab whose content is a form has nothing to gain from hiding the chrome around it.
    /// </summary>
    private void ToggleLiveFullScreen()
    {
        if (_fullScreen)
        {
            ExitLiveFullScreen();
            return;
        }
        if (!ReferenceEquals(MainTabs.SelectedItem, LiveTab))
        {
            SetStatus("Fullscreen is the Live tab's — select it first.");
            return;
        }
        EnterLiveFullScreen();
    }

    private void EnterLiveFullScreen()
    {
        if (_fullScreen || _cleanupStarted)
            return;
        _fullScreen = true;

        _preFullScreenStyle = WindowStyle;
        _preFullScreenState = WindowState;
        _preFullScreenResize = ResizeMode;
        _preFullScreenSideWidth = SidePanelColumn.Width;
        _preFullScreenSplitterWidth = SidePanelSplitterColumn.Width;
        _preFullScreenTabMargin = MainTabs.Margin;
        _preFullScreenStatus = StatusText.Text;
        _preFullScreenDewarpChrome = DewarpToolBars.Visibility;
        _preFullScreenDewarpHint = DewarpStatusText.Visibility;

        // Everything but the picture. The columns go to zero as well as the panel being
        // collapsed: a collapsed child does not shrink a fixed-width column.
        SidePanel.Visibility = Visibility.Collapsed;
        SidePanelSplitter.Visibility = Visibility.Collapsed;
        SidePanelColumn.Width = new GridLength(0);
        SidePanelSplitterColumn.Width = new GridLength(0);
        MainStatusBar.Visibility = Visibility.Collapsed;
        LiveToolBar.Visibility = Visibility.Collapsed;
        DewarpToolBars.Visibility = Visibility.Collapsed;
        DewarpStatusText.Visibility = Visibility.Collapsed;
        MainTabs.Margin = new Thickness(0);
        foreach (var item in MainTabs.Items.OfType<TabItem>())
            item.Template = NoTabHeader;

        // Normal first: changing the style while maximized leaves the window sized to the
        // work area it was maximized into, so it would keep a taskbar-shaped gap.
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        SetStatus("Fullscreen — F11 or Esc to come back" +
            (_gridMode ? ", PgUp/PgDn to turn the page." : "."));
        UpdateLiveViewLabel();
    }

    private void ExitLiveFullScreen()
    {
        if (!_fullScreen)
            return;
        _fullScreen = false;

        WindowState = WindowState.Normal;
        WindowStyle = _preFullScreenStyle;
        ResizeMode = _preFullScreenResize;
        WindowState = _preFullScreenState;

        // ClearValue, not null: a local null is still a local value and beats the theme
        // style's setter, which would leave every tab with no header at all.
        foreach (var item in MainTabs.Items.OfType<TabItem>())
            item.ClearValue(TemplateProperty);
        MainTabs.Margin = _preFullScreenTabMargin;
        DewarpStatusText.Visibility = _preFullScreenDewarpHint;
        DewarpToolBars.Visibility = _preFullScreenDewarpChrome;
        LiveToolBar.Visibility = Visibility.Visible;
        MainStatusBar.Visibility = Visibility.Visible;
        SidePanelColumn.Width = _preFullScreenSideWidth;
        SidePanelSplitterColumn.Width = _preFullScreenSplitterWidth;
        SidePanelSplitter.Visibility = Visibility.Visible;
        SidePanel.Visibility = Visibility.Visible;

        // The status line said "F11 or Esc to come back", which is no longer true. The
        // grid's summary is the more useful thing to land back on; otherwise whatever was
        // there before fullscreen.
        if (_maxTile is null && _gridMode && _gridStatus.Length > 0)
            SetStatus(_gridStatus);
        else if (_preFullScreenStatus.Length > 0)
            SetStatus(_preFullScreenStatus);
        UpdateLiveViewLabel();
    }

    /// <summary>Leaving the Live tab leaves fullscreen with it, whatever got us there.</summary>
    private void ExitFullScreenIfLeavingLiveTab()
    {
        if (_fullScreen && !ReferenceEquals(MainTabs.SelectedItem, LiveTab))
            ExitLiveFullScreen();
    }
}
