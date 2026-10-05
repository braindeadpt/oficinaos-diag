using System.Windows;

namespace OficinaDiag;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // tema + idioma antes de qualquer janela — DynamicResource resolve no load
        var cfg = Cloud.DiagConfig.Load();
        ThemeManager.Apply(cfg.Theme ?? ThemeManager.Terminal);
        L10n.Set(cfg.Language ?? L10n.AutoDetect());

        Log.AppLog.Write("> oficinaos-diag arrancou");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.AppLog.Exception("UI", args.Exception);
            args.Handled = true; // regista e deixa a app viva — o utilizador vê o erro na consola
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.AppLog.Exception("fatal", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.AppLog.Exception("background", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
