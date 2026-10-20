using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace TurboDL.UI;

/// <summary>Converts an icon key (e.g. "IcPause") to the StreamGeometry in Themes/Icons.axaml.</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && key.Length > 0 && Application.Current is { } app &&
            app.TryFindResource(key, out var res))
            return res;
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public static class DialogHost
{
    /// <summary>
    /// Shows a dialog modally over <paramref name="owner"/>. When the main window is hidden in the tray
    /// (e.g. a browser download arrives) the dialog is shown on its own, centred and on top.
    /// </summary>
    public static async Task ShowAsync(Window dialog, Window? owner)
    {
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await dialog.ShowDialog(owner);
            return;
        }
        var tcs = new TaskCompletionSource();
        dialog.Closed += (_, _) => tcs.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowInTaskbar = true;
        dialog.Topmost = true;
        dialog.Show();
        dialog.Activate();
        await tcs.Task;
    }

    public static WindowIcon AppIcon() => new(AssetLoader.Open(new Uri("avares://TurboDL/Assets/turbodl.png")));
}

/// <summary>Simple themed message box (Avalonia has none built in).</summary>
public sealed class MessageDialog : Window
{
    bool _result;

    MessageDialog(string title, string text, string okText, string? cancelText)
    {
        Title = title;
        Icon = DialogHost.AppIcon();
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowHelper.ApplyChrome(this);

        var ok = new Button
        {
            Content = okText,
            IsDefault = true,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => { _result = true; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        if (cancelText != null)
        {
            var cancel = new Button
            {
                Content = cancelText,
                IsCancel = true,
                MinWidth = 96,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            cancel.Classes.Add("secondary");
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(24, 22), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        panel.Children.Add(buttons);
        Content = panel;
    }

    /// <summary>Returns true if the OK/Yes button was pressed.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string text, string title, string okText = "OK", string? cancelText = null)
    {
        var dlg = new MessageDialog(title, text, okText, cancelText);
        await DialogHost.ShowAsync(dlg, owner);
        return dlg._result;
    }
}
