namespace RaOverlay.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => ShowAndLog("AppDomain", ex.ExceptionObject as Exception);
        this.DispatcherUnhandledException += (_, ex) => { ShowAndLog("Dispatcher", ex.Exception); ex.Handled = true; };

        base.OnStartup(e);
        try
        {
            var win = new MainWindow();
            win.Show();
        }
        catch (Exception ex)
        {
            ShowAndLog("Startup", ex);
            Shutdown(-1);
        }
    }

    private static void ShowAndLog(string stage, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "raoverlay-crash.txt");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] {stage}: {ex}\n\n");
        }
        catch { /* ignore */ }

        System.Windows.MessageBox.Show($"{stage} error:\n\n{ex}", "RA Overlay crash", 
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}