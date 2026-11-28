using System.Windows;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    private Cloud.UpdateChecker.UpdateInfo? _update;

    /// <summary>
    /// Os dois passos de uma atualização que uma prova de UI tem de substituir:
    /// descarregar a release a sério gasta 100 MB de rede, e o
    /// `Application.Current.Shutdown()` mata o processo do harness a meio. Foi
    /// por isso que o aviso de reinício nunca teve prova. Os valores por
    /// omissão são exactamente os de produção — o handler em si não mudou.
    /// </summary>
    internal Func<Cloud.UpdateChecker.UpdateInfo, Version, Task<(bool ok, string message)>> Baixar =
        BaixarDeProducao;

    internal Action Terminar = TerminarDeProducao;

    /// <summary>
    /// Os defeitos por omissão em métodos com nome. Um campo com um lambda era
    /// invisível: a prova substituía-o e mostrava que o aviso funciona, nunca
    /// que o caminho de produção é este. Com nome, a prova compara o
    /// `Method.Name` do delegate e chama o resto directamente.
    /// </summary>
    internal static string PastaDeInstalacao => AppContext.BaseDirectory;

    internal static Task<(bool ok, string message)> BaixarDeProducao(
        Cloud.UpdateChecker.UpdateInfo info, Version current) =>
        BaixarPara(PastaDeInstalacao, info, current);

    /// <summary>O mesmo com a pasta à escolha — é assim que a prova vê o argumento chegar.</summary>
    internal static Task<(bool ok, string message)> BaixarPara(
        string appDir, Cloud.UpdateChecker.UpdateInfo info, Version current) =>
        Cloud.UpdateChecker.DownloadAndStageAsync(info, appDir, current);

    internal static void TerminarDeProducao() => Application.Current.Shutdown();

    private async Task CheckForUpdateAsync()
    {
        // A versão comparada é a do assembly — nos builds dev é o <Version>
        // do csproj, que tem de ser >= o último tag (tools/version-check.mjs
        // falha o CI se ficar para trás). Não se podia usar o sufixo +sha do
        // InformationalVersion para detetar dev builds: as releases também
        // o trazem (v0.4.0 sai como "0.4.0+bbd9b6f…"), por isso essa via
        // congelava as actualizações de todas as instalações (#62).
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0);
        var info = await Cloud.UpdateChecker.CheckAsync(current);
        if (info is null) return;
        _update = info;
        var label = L10n.F("btn.update", info.Tag);
        UpdateButton.Content = ThemeManager.IsTerminal ? $"[ {label} ]" : label;
        UpdateButton.Visibility = Visibility.Visible;
        Log(L10n.F("log.update.avail", info.Tag));
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        // Aplicar a atualização encerra a app à força — o shutdown do updater
        // não respeita o cancelamento do OnClosing. Com uma sessão de teste
        // aberta isso perdia as medições e deixava o Android em modo de
        // carga: recusar até o técnico gravar ou fechar a sessão.
        if (HasInteractive)
        {
            Log(L10n.T("log.update.interactive"));
            return;
        }
        UpdateButton.IsEnabled = false;
        Log(L10n.F("log.update.dl", _update.Tag));
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);
            var (ok, msg) = await Baixar(_update, current);
            Log(ok ? $"> {msg}" : L10n.F("log.update.fail", msg));
            if (ok)
            {
                // Antes a janela desaparecia sem explicação — a meio de uma sessão
                // de bancada parecia uma falha da app.
                MessageBox.Show(this, L10n.T("log.update.ready"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Terminar();
            }
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
        finally { UpdateButton.IsEnabled = true; }
    }
}
