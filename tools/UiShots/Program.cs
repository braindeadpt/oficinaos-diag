using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OficinaDiag;
using OficinaDiag.Cloud;
using OficinaDiag.Devices;
using QRCoder;

namespace UiShots;

/// <summary>
/// Tira capturas das janelas reais em cada tema, para a verificação visual
/// deixar de depender de alguém com um Windows à frente.
///
/// Corre no CI (windows-2022) e grava PNGs que são publicados como artefacto
/// do run. Renderiza o conteúdo da janela com RenderTargetBitmap: não precisa
/// de desktop interactivo nem de ecrã, e o que sai é o layout a sério — os
/// templates, os DynamicResource e as fontes do tema.
///
/// O foco do teclado só aparece se a janela conseguir receber foco; numa
/// sessão sem desktop isso pode não acontecer, e nesse caso o log diz
/// "foco: não" e o PNG não serve para julgar esse ponto.
/// </summary>
internal static class Program
{
    private static int _falhas;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const uint KeyUp = 0x0002;
    private const byte VkMenu = 0x12; // Alt

    [STAThread]
    private static int Main(string[] args)
    {
        var destino = args.Length > 0 ? args[0] : "out-store/ui-shots";
        Directory.CreateDirectory(destino);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        L10n.Set("pt");

        var relatorio = RelatorioAtual();
        var anterior = RelatorioAnterior();

        foreach (var tema in ThemeManager.All)
        {
            ThemeManager.Apply(tema);
            Console.WriteLine($"== tema {tema} ==");

            // 1000x740 cabe na work area do runner (1024x768): o MainWindow
            // encolhe-se à work area no OnLoaded, e forçar depois do Show não
            // refazia o layout — só recortava a imagem.
            Capturar(() => new MainWindow(), Path.Combine(destino, $"main-{tema}.png"), 1000, 740);
            // 700x480 é o MinWidth/MinHeight: é onde o header estoura, se estourar.
            Capturar(() => new MainWindow(), Path.Combine(destino, $"main-{tema}-min.png"), 700, 480);
            Capturar(() => ComRelatorio(new MainWindow(), relatorio),
                Path.Combine(destino, $"resultados-{tema}.png"), 1100, 700);
            Capturar(() => new SettingsDialog(DiagConfig.Load()),
                Path.Combine(destino, $"opcoes-{tema}.png"), 420, 640);
            Capturar(() => new ChecklistDialog(relatorio),
                Path.Combine(destino, $"checklist-{tema}.png"), 560, 620);
            // tamanho por omissão do diálogo: é onde o utilizador o vê abrir
            Capturar(() => new ChecklistDialog(relatorio),
                Path.Combine(destino, $"checklist-{tema}-default.png"), 560, 620);
            // janela apertada: os itens têm de rolar e os botões continuar visíveis
            Capturar(() => new ChecklistDialog(relatorio),
                Path.Combine(destino, $"checklist-{tema}-baixa.png"), 560, 480);
            // ecrã curto: força o scroll das Opções (o que o SizeToContent escondia)
            Capturar(() => new SettingsDialog(DiagConfig.Load()),
                Path.Combine(destino, $"opcoes-{tema}-curta.png"), 420, 560, LimitarScroll);
            Capturar(() => new BenchWindow("Pixel 8 · android",
                    () => Task.FromResult<LiveTelemetry?>(null)),
                Path.Combine(destino, $"bench-{tema}.png"), 920, 620);
        }

        // O dropdown é um Popup — outra janela, fora do visual tree — por isso
        // nunca apareceu nas capturas. Aqui abre-se e dirige-se por teclado.
        foreach (var tema in new[] { ThemeManager.Terminal, ThemeManager.Win95 })
        {
            ThemeManager.Apply(tema);
            Console.WriteLine($"== combo por teclado ({tema}) ==");
            ProvarCombo(destino, tema);
        }

        ThemeManager.Apply(ThemeManager.Terminal);
        Console.WriteLine("== diálogos (tema terminal) ==");
        Capturar(() => new SendDialog(), Path.Combine(destino, "envio.png"), 420, 560);
        Capturar(() => new TokenDialog(), Path.Combine(destino, "token.png"), 380, 320);
        Capturar(() => new InsuranceDialog(DiagConfig.Load()),
            Path.Combine(destino, "seguradora.png"), 480, 640);
        Capturar(() => new CompareWindow(relatorio, [anterior]),
            Path.Combine(destino, "comparar.png"), 780, 560);
        using (var gen = new QRCodeGenerator())
        {
            var qr = gen.CreateQrCode("http://192.168.1.50:8099/test", QRCodeGenerator.ECCLevel.M);
            Capturar(() => new QrWindow(qr), Path.Combine(destino, "qr.png"), 340, 420);
        }

        app.Shutdown();
        Console.WriteLine($"capturas em {Path.GetFullPath(destino)}");
        if (_falhas > 0) Console.WriteLine($"FALHAS: {_falhas}");
        return _falhas == 0 ? 0 : 1;
    }

    /// <summary>
    /// Abre o ComboBox com F4, navega com Down, confirma com Enter e fecha com
    /// Esc — as teclas que a caixa promete — e grava o popup em PNG.
    /// </summary>
    private static void ProvarCombo(string destino, string tema)
    {
        var janela = new SettingsDialog(DiagConfig.Load())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            ShowInTaskbar = false,
        };
        janela.Show();
        Respirar();
        try
        {
            // Sem activação a janela não recebe foco de teclado e as teclas
            // não chegam ao combo.
            SetForegroundWindow(new WindowInteropHelper(janela).Handle);
            janela.Activate();
            Respirar();

            if (Primeiro<ComboBox>(janela) is not { } combo)
            {
                Verificar($"{tema}: combo encontrado", false);
                return;
            }

            combo.Focus();
            Keyboard.Focus(combo);
            Respirar();
            Console.WriteLine($"  combo foco: {(combo.IsKeyboardFocused ? "sim" : "não")}");

            Verificar($"{tema}: começa fechado", !combo.IsDropDownOpen);

            Verificar($"{tema}: dropdown abre por teclado", AbrirPorTeclado(combo));

            var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
            var painel = popup?.Child as FrameworkElement;
            Verificar($"{tema}: popup aberto",
                Esperar(() => popup is { IsOpen: true } && painel is { IsVisible: true }, 2000));
            if (painel is not null)
            {
                painel.UpdateLayout();
                Gravar(painel, Path.Combine(destino, $"combo-{tema}-aberto.png"));
            }

            // Down destaca o item seguinte; Enter confirma. Espera-se pelo
            // destaque em vez de dormir — era isso que tornava isto intermitente.
            var antes = combo.SelectedIndex;
            var esperado = Math.Min(antes + 1, combo.Items.Count - 1);
            var alvo = combo.ItemContainerGenerator.ContainerFromIndex(esperado) as ComboBoxItem;
            Tecla(Key.Down);
            Esperar(() => alvo?.IsHighlighted == true, 2000);
            Tecla(Key.Enter);
            Verificar($"{tema}: Enter fecha", Esperar(() => !combo.IsDropDownOpen, 2000));
            Verificar($"{tema}: Down+Enter seleciona o item {esperado} (de {antes} para {combo.SelectedIndex})",
                combo.SelectedIndex == esperado);

            var depois = combo.SelectedIndex;
            Verificar($"{tema}: abre outra vez", AbrirPorTeclado(combo));
            Tecla(Key.Escape);
            Verificar($"{tema}: Esc fecha", Esperar(() => !combo.IsDropDownOpen, 2000));
            Verificar($"{tema}: Esc não muda a seleção", combo.SelectedIndex == depois);
        }
        finally { janela.Close(); }
    }

    /// <summary>
    /// Tecla real: `keybd_event` entra pela mesma fila de mensagens que o
    /// teclado físico. `InputManager.ProcessInput` não chega ao ComboBox —
    /// os eventos sintéticos não passam pelo roteamento do HwndSource.
    /// </summary>
    private static void Tecla(Key tecla, bool comAlt = false)
    {
        var vk = (byte)KeyInterop.VirtualKeyFromKey(tecla);
        if (comAlt) keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
        if (comAlt) keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
    }

    /// <summary>
    /// F4 é o acelerador documentado; Alt+Down é o outro. Devolve se o
    /// dropdown ficou aberto, esperando pela condição em vez de dormir.
    /// </summary>
    private static bool AbrirPorTeclado(ComboBox combo)
    {
        Tecla(Key.F4);
        if (Esperar(() => combo.IsDropDownOpen, 1500)) return true;
        Tecla(Key.Down, comAlt: true);
        return Esperar(() => combo.IsDropDownOpen, 1500);
    }

    /// <summary>
    /// Espera que a condição fique verdadeira, bombando mensagens, até ao
    /// timeout. Pausas fixas davam um teste intermitente em runner carregado:
    /// 200ms chegavam numa corrida e não na seguinte.
    /// </summary>
    private static bool Esperar(Func<bool> condicao, int timeoutMs)
    {
        var fim = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < fim)
        {
            if (condicao()) return true;
            Pausa(20);
        }
        return condicao();
    }

    /// <summary>Bombear o dispatcher/mensagens durante N ms (as teclas entram por WM_KEYDOWN).</summary>
    private static void Pausa(int ms)
    {
        var fim = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < fim)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void Verificar(string nome, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "FALHA")}] {nome}");
        if (!ok) _falhas++;
    }

    private static void Gravar(FrameworkElement raiz, string ficheiro)
    {
        var w = Math.Max(1, (int)Math.Ceiling(raiz.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(raiz.ActualHeight));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Fundo(w, h));
        bmp.Render(raiz);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(ficheiro);
        enc.Save(fs);
        Console.WriteLine($"  {Path.GetFileName(ficheiro),-26} {bmp.PixelWidth}x{bmp.PixelHeight}");
    }

    /// <summary>
    /// Injeta um relatório na janela principal para fotografar o painel de
    /// resultados (a coluna de estado e o `[skipped]`). O campo é privado e
    /// não há construtor que o aceite: reflexão aqui é mais honesta do que
    /// alargar a API da app só para tirar uma captura.
    /// </summary>
    private static MainWindow ComRelatorio(MainWindow janela, DeviceReport relatorio)
    {
        try
        {
            typeof(MainWindow)
                .GetField("_report", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(janela, relatorio);
            janela.Loaded += (_, _) =>
            {
                try
                {
                    typeof(MainWindow)
                        .GetMethod("RefreshResults", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(janela, null);
                }
                catch (Exception ex) { Console.WriteLine($"  resultados: {ex.GetType().Name}"); }
            };
        }
        catch (Exception ex) { Console.WriteLine($"  injeção do relatório: {ex.GetType().Name}"); }
        return janela;
    }

    private static DeviceReport RelatorioAtual()
    {
        var r = new DeviceReport
        {
            Platform = "android",
            Device =
            {
                Brand = "Google", Model = "Pixel 8", MarketingName = "Pixel 8",
                Os = "Android", OsVersion = "14", Serial = "3B1F0A2C9D",
            },
        };
        r.Set("battery.health", "pass", "94%", "capacidade estimada");
        r.Set("battery.serial", "pass", "0x9c3a", "série da célula");
        r.Set("thermal.soc", "warn", "41,2 °C", "acima do esperado em repouso");
        r.Set("screen.deadpixels", "fail", "2", "dois pixéis mortos no canto inferior");
        r.Set("checklist.sim", "skipped", "não testado", "gaveta SIM");
        r.Set("usb.negotiated", "info", "5V / 1.5A", "cabo do banco");
        return r;
    }

    private static DeviceReport RelatorioAnterior()
    {
        var r = new DeviceReport
        {
            Platform = "android",
            CollectedAt = DateTimeOffset.UtcNow.AddDays(-21),
            Device = { Brand = "Google", Model = "Pixel 8", Serial = "3B1F0A2C9D" },
        };
        r.Set("battery.health", "warn", "78%", "capacidade estimada");
        r.Set("battery.serial", "pass", "0x11b4", "série da célula");
        r.Set("thermal.soc", "pass", "36,0 °C", "em repouso");
        r.Set("checklist.sim", "skipped", "não testado", "gaveta SIM");
        return r;
    }

    /// <summary>
    /// Mostra a janela fora do ecrã, mede/arranja o conteúdo e grava o PNG.
    /// `Show()` é best-effort: serve para os templates assentarem e para tentar
    /// o foco; se o ambiente não o permitir, mede-se o conteúdo à mão.
    /// </summary>
    /// <summary>Encolhe o ScrollViewer para provar que a barra aparece.</summary>
    private static void LimitarScroll(Window janela)
    {
        if (Primeiro<ScrollViewer>(janela) is { } sv) sv.MaxHeight = 360;
    }

    private static void Capturar(Func<Window> criar, string ficheiro, double largura, double altura,
        Action<Window>? ajustar = null)
    {
        Window? janela = null;
        try
        {
            janela = criar();
            janela.WindowStartupLocation = WindowStartupLocation.Manual;
            janela.ShowInTaskbar = false;
            janela.Left = -20_000;
            janela.Top = -20_000;
            janela.Width = largura;
            janela.Height = altura;

            var mostrou = true;
            try { janela.Show(); }
            catch (Exception ex)
            {
                mostrou = false;
                Console.WriteLine($"  show indisponível ({ex.GetType().Name}) — a medir sem mostrar");
            }

            // Raiz do cliente da janela, não o Content: o Content tem Margin 20
            // e ao renderizá-lo só o offset dele se perdia, o que fazia os
            // botões de baixo parecerem cortados. O primeiro filho visual é a
            // área do cliente, já com as margens dentro.
            if (VisualTreeHelper.GetChildrenCount(janela) == 0) return;
            if (VisualTreeHelper.GetChild(janela, 0) is not FrameworkElement raiz) return;
            // Nada de redimensionar depois do Show: o WPF só refaz o layout
            // quando processa o WM_SIZE, por isso o conteúdo ficava arrumado
            // para o tamanho antigo e a imagem saía cortada em baixo (foi o
            // que fez o APLICAR parecer quebrado duas vezes).
            ajustar?.Invoke(janela);
            raiz.UpdateLayout();
            Respirar();

            var foco = mostrou ? TentarFoco(janela) : "não";
            var w = Math.Max(1, (int)Math.Ceiling(raiz.ActualWidth));
            var h = Math.Max(1, (int)Math.Ceiling(raiz.ActualHeight));
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            // Fundo opaco do tema primeiro: sem ele o PNG fica com alpha e cada
            // visualizador mostra a cor que quer — foi o "branco" do painel
            // de resultados vazio.
            bmp.Render(Fundo(w, h));
            bmp.Render(raiz);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(ficheiro)) enc.Save(fs);
            // Diagnóstico: se o fundo dos descendentes passar da altura do
            // conteúdo, é a app que arruma fora da janela; se ficar dentro, o
            // corte é da captura.
            double fundo = 0;
            foreach (var e in Descendentes<FrameworkElement>(raiz))
            {
                try
                {
                    var p = e.TransformToAncestor(raiz).Transform(new Point(0, e.ActualHeight));
                    fundo = Math.Max(fundo, p.Y);
                }
                catch { /* ainda não ligado à árvore */ }
            }
            Console.WriteLine($"  {Path.GetFileName(ficheiro),-26} {bmp.PixelWidth}x{bmp.PixelHeight}  " +
                              $"janela {janela.ActualHeight:0} foco: {foco}  fundo {fundo:0} " +
                              (fundo > raiz.ActualHeight + 1 ? "FORA DA JANELA" : ""));
        }
        catch (Exception ex)
        {
            // Uma janela que não abre não pode derrubar as outras capturas.
            Console.WriteLine($"  {Path.GetFileName(ficheiro),-26} FALHOU: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { janela?.Close(); } catch { }
        }
    }

    /// <summary>Retângulo opaco com o fundo do tema atual.</summary>
    private static DrawingVisual Fundo(int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(
                Application.Current.TryFindResource("BgBrush") as Brush ?? Brushes.Black,
                null, new Rect(0, 0, w, h));
        return visual;
    }

    /// <summary>Põe o foco no primeiro botão e diz se ele ficou com foco de teclado.</summary>
    private static string TentarFoco(Window janela)
    {
        try
        {
            janela.Activate();
            var botao = Primeiro<Button>(janela);
            if (botao is null) return "sem botões";
            botao.Focus();
            Respirar();
            return botao.IsKeyboardFocused ? "sim" : "não";
        }
        catch { return "não"; }
    }

    private static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(raiz);
        for (var i = 0; i < n; i++)
        {
            var filho = VisualTreeHelper.GetChild(raiz, i);
            if (filho is T t) yield return t;
            foreach (var neto in Descendentes<T>(filho)) yield return neto;
        }
    }

    private static T? Primeiro<T>(DependencyObject raiz) where T : DependencyObject
    {
        if (raiz is T encontrado) return encontrado;
        var n = VisualTreeHelper.GetChildrenCount(raiz);
        for (var i = 0; i < n; i++)
            if (Primeiro<T>(VisualTreeHelper.GetChild(raiz, i)) is { } filho)
                return filho;
        return null;
    }

    /// <summary>Deixa o dispatcher esvaziar a fila (layout, templates, bindings).</summary>
    private static void Respirar()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
