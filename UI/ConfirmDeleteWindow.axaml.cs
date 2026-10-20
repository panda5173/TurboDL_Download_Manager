using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TurboDL.UI;

public partial class ConfirmDeleteWindow : Window
{
    public bool DeleteFiles { get; private set; }
    public bool Confirmed { get; private set; }

    public ConfirmDeleteWindow()   // designer only
    {
        InitializeComponent();
    }

    public ConfirmDeleteWindow(int count, bool anyCompleted)
    {
        InitializeComponent();
        WindowHelper.ApplyChrome(this);
        TitleText.Text = count == 1 ? "Remove 1 download from the list?" : $"Remove {count} downloads from the list?";
        DeleteFilesBox.IsEnabled = anyCompleted;
    }

    void Ok_Click(object? sender, RoutedEventArgs e)
    {
        DeleteFiles = DeleteFilesBox.IsChecked == true;
        Confirmed = true;
        Close();
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
