using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Views;

public sealed partial class BindingFlyout : UserControl
{
    BindingEditorViewModel? _editor;
    bool _capturing;

    public BindingFlyout()
    {
        InitializeComponent();
        KeyCapture.LostFocus += (_, _) => StopCapture();
        KeyCapture.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => ObserveEditor(DataContext as BindingEditorViewModel);
        DetachedFromVisualTree += (_, _) => ObserveEditor(null);
        DataContextChanged += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) != null)
                ObserveEditor(DataContext as BindingEditorViewModel);
        };
    }

    void ObserveEditor(BindingEditorViewModel? editor)
    {
        if (ReferenceEquals(_editor, editor))
            return;
        StopCapture();
        if (_editor != null)
            _editor.PropertyChanged -= EditorChanged;
        _editor = editor;
        if (editor == null)
            return;
        editor.PropertyChanged += EditorChanged;
        FocusWhenVisible(AssignmentType, editor);
    }

    void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BindingEditorViewModel.SelectedFunction) or nameof(BindingEditorViewModel.IsSaving))
            StopCapture();
        if (e.PropertyName == nameof(BindingEditorViewModel.SaveError) && _editor is { SaveError: not null } editor)
            FocusWhenVisible(SaveBinding, editor);
    }

    void FocusWhenVisible(Control target, BindingEditorViewModel editor) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(editor, _editor) && target.IsEffectivelyVisible && target.IsEffectivelyEnabled &&
            TopLevel.GetTopLevel(this) != null)
            target.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    void ArmCapture(object? sender, RoutedEventArgs e)
    {
        if (_capturing)
            return;
        _capturing = true;
        CaptureInstructions.IsVisible = true;
        KeyCapture.Content = "Listening…";
        KeyCapture.Focus(NavigationMethod.Tab);
    }

    void StopCapture()
    {
        _capturing = false;
        CaptureInstructions.IsVisible = false;
        KeyCapture.Content = "Capture key";
    }

    void ChooseTab(object? sender, RoutedEventArgs e) => Choose(PhysicalKey.Tab);
    void ChooseEscape(object? sender, RoutedEventArgs e) => Choose(PhysicalKey.Escape);

    void Choose(PhysicalKey key)
    {
        StopCapture();
        if (_editor is { Kind: FunctionKind.Keyboard, IsSaving: false } editor)
        {
            editor.CapturedKey = PhysicalKeys.ToLinux(key);
            FocusWhenVisible(ChooseKey, editor);
        }
    }

    void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (!_capturing || _editor is not { } editor)
            return;
        if (e.Key is Key.Tab or Key.Escape)
        {
            StopCapture();
            e.Handled = e.Key == Key.Escape;
            return;
        }
        var code = PhysicalKeys.ToLinux(e.PhysicalKey);
        if (code == 0)
            return;
        editor.CapturedKey = code;
        StopCapture();
        e.Handled = true;
    }

    async void Browse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BindingEditorViewModel editor || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a program", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            editor.Command = path.Contains(' ') ? $"\"{path}\"" : path;
    }
}
