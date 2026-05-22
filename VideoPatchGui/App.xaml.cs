using Microsoft.UI.Xaml;

namespace VideoPatchGui;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteStartupError(ex);
            throw;
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteStartupError(e.Exception);
    }

    private static void WriteStartupError(Exception exception)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), exception.ToString());
        }
        catch
        {
            // Avoid masking the original startup failure.
        }
    }
}
