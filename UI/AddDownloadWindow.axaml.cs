using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TurboDL.Core;

namespace TurboDL.UI;

public partial class AddDownloadWindow : Window
{
    readonly AppSettings _settings = null!;

    public List<string> Urls { get; private set; } = new();
    public string? FileNameOverride { get; private set; }
    public string? Folder { get; private set; }     // null = automatic
    public int Connections { get; private set; }
    public bool StartNow { get; private set; }
    public bool Confirmed { get; private set; }

    public AddDownloadWindow()   // designer only
    {
        InitializeComponent();
    }

    public AddDownloadWindow(AppSettings settings, string? text, string? fileName)
    {
        InitializeComponent();
        _settings = settings;
        WindowHelper.ApplyChrome(this);

        AutoText.Text = "Automatic – " + settings.DownloadFolder + (settings.UseCategoryFolders ? "/<category>" : "");
        FolderBox.Text = settings.LastCustomFolder ?? settings.DownloadFolder;
        ConnSlider.Value = settings.ConnectionsPerDownload;
        UrlsBox.Text = text ?? "";
        FileNameBox.Text = fileName ?? "";

        UrlsBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) UpdateUrls(); };
        ConnSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateConn(); };
        FolderBox.GotFocus += (_, _) => CustomRadio.IsChecked = true;

        UpdateUrls();
        UpdateConn();

        Opened += (_, _) =>
        {
            Activate();
            UrlsBox.Focus();
            UrlsBox.CaretIndex = UrlsBox.Text?.Length ?? 0;
        };
    }

    void UpdateUrls()
    {
        Urls = MainWindow.ExtractUrls(UrlsBox.Text);
        UrlCountText.Text = Urls.Count switch
        {
            0 => "No valid http/https link found",
            1 => "1 link ready",
            _ => $"{Urls.Count} links ready",
        };
        FileNameBox.IsEnabled = Urls.Count <= 1;
        DownloadBtn.IsEnabled = AddPausedBtn.IsEnabled = Urls.Count > 0;
    }

    void UpdateConn() => ConnText.Text = ((int)ConnSlider.Value).ToString();

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string start = Directory.Exists(FolderBox.Text) ? FolderBox.Text! : _settings.DownloadFolder;
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where to save",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start),
            });
            if (result.Count > 0 && result[0].TryGetLocalPath() is string path)
            {
                FolderBox.Text = path;
                CustomRadio.IsChecked = true;
            }
        }
        catch (Exception ex) { App.Log("Folder picker: " + ex.Message); }
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
    void Download_Click(object? sender, RoutedEventArgs e) => Finish(true);
    void AddPaused_Click(object? sender, RoutedEventArgs e) => Finish(false);

    async void Finish(bool startNow)
    {
        UpdateUrls();
        if (Urls.Count == 0) return;

        if (CustomRadio.IsChecked == true)
        {
            string folder = (FolderBox.Text ?? "").Trim();
            if (folder.StartsWith("~/", StringComparison.Ordinal))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.Substring(2));
            try
            {
                if (folder.Length == 0 || !Path.IsPathRooted(folder)) throw new IOException("Please enter a full folder path.");
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                await MessageDialog.ShowAsync(this, "Cannot use this folder:\n" + ex.Message, "TurboDL");
                return;
            }
            Folder = folder;
            _settings.LastCustomFolder = folder;
        }
        else Folder = null;

        string name = (FileNameBox.Text ?? "").Trim();
        FileNameOverride = Urls.Count == 1 && name.Length > 0 ? name : null;
        Connections = (int)ConnSlider.Value;
        StartNow = startNow;
        Confirmed = true;
        Close();
    }
}
