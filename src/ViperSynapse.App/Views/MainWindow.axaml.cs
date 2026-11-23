using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ViperSynapse.App.Services;

namespace ViperSynapse.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // For screenshots of pages taller than the default window.
        if (int.TryParse(System.Environment.GetEnvironmentVariable("VIPER_SYNAPSE_HEIGHT"), out var height) && height > 0)
            Height = height;
        TopBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (e.ClickCount == 2) ToggleMaximized();
            else BeginMoveDrag(e);
        };
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
        Edge(EdgeLeft, WindowEdge.West);
        Edge(EdgeRight, WindowEdge.East);
        Edge(EdgeBottom, WindowEdge.South);
        Edge(EdgeBottomRight, WindowEdge.SouthEast);
        Edge(EdgeBottomLeft, WindowEdge.SouthWest);
    }

    async void CopyDocumentationLink(object? sender, RoutedEventArgs e)
    {
        var link = ProjectLinks.Documentation.AbsoluteUri;
        var message = link;
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(link);
                message = "Documentation link copied.";
            }
        }
        catch (Exception)
        {
            // The link remains selectable when the platform clipboard is unavailable.
        }
        HelpLinkStatus.Text = message;
        HelpLinkStatus.IsVisible = true;
    }

    void Edge(Control handle, WindowEdge edge) => handle.PointerPressed += (_, e) =>
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            BeginResizeDrag(edge, e);
    };

    void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
