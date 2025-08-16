using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace RaOverlay.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private OverlayServer? _server;
    private string _soundPath = "";
    private bool _uiReady;
    private OverlaySettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        ApplySettingsToUi(_settings);

        _uiReady = true;
        UpdateUrl();
    }

    private void ApplySettingsToUi(OverlaySettings s)
    {
        // Credentials
        UserBox.Text = s.Username;
        KeyBox.Password = SettingsStore.Unprotect(s.ApiKeyCipher);
        PortBox.Text = s.Port.ToString();

        // Options
        SelectCombo(PosBox, s.Position);
        Alpha.Value = Clamp(s.Opacity, 0, 1);
        Blur.Value  = s.Blur;
        Scale.Value = Clamp(s.Scale, 0.6, 1.4);
        MinWidthBox.Text = s.MinWidth.ToString();
        RgbBox.Text = string.IsNullOrWhiteSpace(s.BackgroundRgb) ? "32,34,38" : s.BackgroundRgb;
        SelectCombo(SortBox, s.NextSort);

        // Sound
        _soundPath = s.SoundPath ?? "";
        if (!string.IsNullOrEmpty(_soundPath))
            SoundLabel.Text = Path.GetFileName(_soundPath);
    }

    private OverlaySettings ReadSettingsFromUi()
    {
        var s = new OverlaySettings
        {
            Username = UserBox?.Text?.Trim() ?? "",
            ApiKeyCipher = SettingsStore.Protect(KeyBox?.Password?.Trim() ?? ""),
            Port = int.TryParse(PortBox?.Text, out var p) ? p : 4050,

            Position = GetCombo(PosBox) ?? "tl",
            Opacity  = Alpha?.Value ?? 0.75,
            Blur     = (int)(Blur?.Value ?? 8),
            Scale    = Scale?.Value ?? 1.0,
            MinWidth = int.TryParse(MinWidthBox?.Text, out var mw) ? mw : 560,
            BackgroundRgb = (RgbBox?.Text ?? "32,34,38").Trim(),
            NextSort = GetCombo(SortBox) ?? "list",

            SoundPath = _soundPath
        };
        return s;
    }

    private void SaveSettings()
    {
        if (!_uiReady) return;
        _settings = ReadSettingsFromUi();
        SettingsStore.Save(_settings);
    }

    // ---------- overlay server options ----------
    private RaOptions ReadOptions() => new()
    {
        Username  = UserBox?.Text?.Trim() ?? "",
        WebApiKey = KeyBox?.Password?.Trim() ?? "",
        PollSeconds = 10,
        NextSort  = GetCombo(SortBox) ?? "list"
    };

    private int ReadPort() => int.TryParse(PortBox?.Text, out var p) ? p : 4050;

    // ---------- URL building ----------
    private void UpdateUrl()
    {
        var pos   = GetCombo(PosBox) ?? "tl";
        var alpha = (Alpha?.Value ?? 0.75).ToString("0.00");
        var blur  = ((int)(Blur?.Value ?? 8)).ToString();
        var bg    = (RgbBox?.Text ?? "32,34,38").Trim();
        var scale = (Scale?.Value ?? 1.00).ToString("0.00");
        var width = (MinWidthBox?.Text ?? "560").Trim();

        var baseUrl = _server?.BaseUrl ?? $"http://localhost:{ReadPort()}";

        var qs = new StringBuilder();
        void add(string k, string v)
        {
            if (qs.Length == 0) qs.Append('?'); else qs.Append('&');
            qs.Append(k).Append('=').Append(Uri.EscapeDataString(v));
        }

        add("pos",   pos);
        add("alpha", alpha);
        add("blur",  blur);
        add("bg",    bg);
        add("scale", scale);
        add("width", width);

        if (UrlBox is not null) UrlBox.Text = $"{baseUrl}/overlay{qs}";
    }

    // ---------- buttons ----------
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(www);

            if (!string.IsNullOrEmpty(_soundPath) && File.Exists(_soundPath))
                File.Copy(_soundPath, Path.Combine(www, "unlock.mp3"), overwrite: true);

            if (_server is not null)
            {
                await _server.StopAsync();
                await _server.DisposeAsync();
                _server = null;
            }

            SaveSettings(); // persist before starting
            _server = new OverlayServer(ReadOptions(), ReadPort());
            await _server.StartAsync();

            UpdateUrl();
            MessageBox.Show($"Overlay server running at {_server.BaseUrl}", "Started",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_server is null) return;
            await _server.StopAsync();
            await _server.DisposeAsync();
            _server = null;
            MessageBox.Show("Server stopped.");
            UpdateUrl();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Failed to stop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UrlBox?.Text))
            Clipboard.SetText(UrlBox.Text);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = UrlBox?.Text;
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void SoundBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MP3 Files|*.mp3",
            Title  = "Choose unlock sound"
        };
        if (dlg.ShowDialog() == true)
        {
            _soundPath = dlg.FileName;
            if (SoundLabel is not null)
                SoundLabel.Text = Path.GetFileName(_soundPath);
            SaveSettings();
        }
    }

    // ---------- live URL updates + autosave ----------
    private void TextChanged_UpdateUrl(object? s, System.Windows.Controls.TextChangedEventArgs e)
    { if (_uiReady) { UpdateUrl(); SaveSettings(); } }

    private void PasswordChanged_UpdateUrl(object? s, RoutedEventArgs e)
    { if (_uiReady) { UpdateUrl(); SaveSettings(); } }

    private void SelectionChanged_UpdateUrl(object? s, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (_uiReady) { UpdateUrl(); SaveSettings(); } }

    private void SliderChanged_UpdateUrl(object? s, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    { if (_uiReady) { UpdateUrl(); SaveSettings(); } }

    // graceful teardown
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        SaveSettings();
        if (_server is not null)
        {
            try { await _server.StopAsync(); await _server.DisposeAsync(); } catch { }
            _server = null;
        }
    }

    // ---------- small UI helpers ----------
    private static void SelectCombo(System.Windows.Controls.ComboBox box, string value)
    {
        foreach (var item in box.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem cbi &&
                string.Equals(cbi.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = cbi; return; }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private static string? GetCombo(System.Windows.Controls.ComboBox? box)
        => (box?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}
