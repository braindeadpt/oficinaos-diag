using System.Windows;

namespace OficinaDiag;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Mantém o mutex de instância única vivo enquanto a app corre — se o
    /// GC o recolhesse, o handle fechava e uma segunda instância entrava.
    /// </summary>
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Instância única (#62): durante o relançamento tardio do updater
        // correram duas cópias em simultâneo, a disputar o adb e os
        // dispositivos. O namespace Local é por sessão — outro utilizador
        // no mesmo PC não é bloqueado. A segunda instância sai em silêncio:
        // a janela da primeira já está aberta e é essa que interessa.
        _singleInstance = new Mutex(true, @"Local\OficinaDiag.SingleInstance", out var primeira);
        if (!primeira)
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        // tema + idioma antes de qualquer janela — DynamicResource resolve no load
        var cfg = Cloud.DiagConfig.Load();
        ThemeManager.Apply(cfg.Theme ?? ThemeManager.Fluent);
        L10n.Set(cfg.Language ?? L10n.AutoDetect());

        Log.AppLog.Write("> oficinaos-diag arrancou");

        DispatcherUnhandledException += (_, args) =>
        {
            // O `MainWindow` sabe qual foi o botão do clique (o handler global
            // não recebe o remetente): à terceira falha na mesma ação avisa o
            // técnico e desliga o botão, em vez de deixar um botão que não faz
            // nada. Fora de uma ação dele, fica só no log.
            if (Current.MainWindow is MainWindow janela) janela.RegistarFalha(args.Exception);
            else Log.AppLog.Exception("UI", args.Exception);
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
