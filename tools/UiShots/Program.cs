using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
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

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    private const uint KeyUp = 0x0002;
    private const byte VkMenu = 0x12; // Alt
    private const uint WmCommand = 0x0111;
    private const uint WmClose = 0x0010;
    private const int IdYes = 6;
    private const int IdNo = 7;

    [STAThread]
    private static int Main(string[] args)
    {
        var destino = args.Length > 0 ? args[0] : "out-store/ui-shots";
        Directory.CreateDirectory(destino);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Um clique real chega dentro de uma operação do dispatcher, onde o
        // SynchronizationContext é o do WPF. Este `Main` corre fora do loop, e
        // sem isto as continuações dos handlers async caíam no thread pool:
        // o `Start_Click` da bancada escrevia num TextBlock de outra thread e
        // rebentava o processo — bug que só existia no harness.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
            // 1366×768 a 125% = 614 DIP úteis: as ações têm de caber inteiras
            Capturar(() => new ChecklistDialog(relatorio),
                Path.Combine(destino, $"checklist-{tema}-614.png"), 560, 620,
                workArea: 614, acoes: ["ApplyBtn", "CancelBtn", "PhotoBtn"]);
            Capturar(() => new SettingsDialog(DiagConfig.Load()),
                Path.Combine(destino, $"opcoes-{tema}-614.png"), 420, 640,
                workArea: 614, acoes: ["SaveBtn", "CancelBtn"]);
            // A 614 as Opções ainda cabem inteiras; com o scroll forçado é que se
            // prova que os botões não vão com os campos.
            Capturar(() => new SettingsDialog(DiagConfig.Load()),
                Path.Combine(destino, $"opcoes-{tema}-614-curta.png"), 420, 640,
                LimitarScrollA(200), workArea: 614, acoes: ["SaveBtn", "CancelBtn"]);
            // Abaixo do MinHeight do checklist (480): aqui vê-se se o MinHeight
            // ganha ao limite da work area — se ganhar, as ações voltam a sair.
            Capturar(() => new ChecklistDialog(relatorio),
                Path.Combine(destino, $"checklist-{tema}-400.png"), 560, 620,
                workArea: 400, acoes: ["ApplyBtn", "CancelBtn", "PhotoBtn"]);
            Capturar(() => new SettingsDialog(DiagConfig.Load()),
                Path.Combine(destino, $"opcoes-{tema}-400.png"), 420, 640,
                workArea: 400, acoes: ["SaveBtn", "CancelBtn"]);

            // Os três diálogos que só eram capturados no terminal: abrir em cada
            // tema é o que o T-06 pede («abrem nos três temas»).
            Capturar(() => new InsuranceDialog(DiagConfig.Load()),
                Path.Combine(destino, $"seguradora-{tema}.png"), 480, 640);
            Capturar(() => new SendDialog(), Path.Combine(destino, $"envio-{tema}.png"), 420, 560);
            Capturar(() => new TokenDialog(), Path.Combine(destino, $"token-{tema}.png"), 380, 320);
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

        // Troca de tema com as janelas já abertas: antes só se provava abrir
        // já no tema; o T-06 pede a troca a quente.
        ProvarTrocaTema(destino, relatorio);

        ThemeManager.Apply(ThemeManager.Terminal);
        ProvarPensDoScope(destino);
        ProvarFechoAPairar();
        ProvarAvisoDaBancada();

        ThemeManager.Apply(ThemeManager.Terminal);
        Console.WriteLine("== diálogos (tema terminal) ==");
        // envio/token/seguro são capturados dentro do ciclo dos temas.
        Capturar(() => new InsuranceDialog(DiagConfig.Load()),
            Path.Combine(destino, "seguradora-614.png"), 480, 640,
            workArea: 614, acoes: ["GenerateBtn", "CancelBtn"]);
        Capturar(() => new InsuranceDialog(DiagConfig.Load()),
            Path.Combine(destino, "seguradora-400.png"), 480, 640,
            workArea: 400, acoes: ["GenerateBtn", "CancelBtn"]);
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
    /// `ScopeView` guarda as quatro `Pen` em cache por tema: entre frames do
    /// mesmo tema não pode alocar nenhuma. Mede-se a diferença entre um frame
    /// redesenhado e um frame sem `InvalidateVisual` (o que isola o custo do
    /// `OnRender` do custo do `RenderTargetBitmap`) e compara-se com o custo
    /// real de uma `Pen` medido no próprio runtime — sem limiar mágico.
    /// </summary>
    private static void ProvarPensDoScope(string destino)
    {
        Console.WriteLine("== ScopeView: nenhuma Pen por frame ==");
        ThemeManager.Apply(ThemeManager.Terminal);

        var scope = new ScopeView();
        for (var i = 0; i < 40; i++)
            scope.AddSample(new PowerSample(i * 0.5, 1, 500 + i * 7, 4.0 + i * 0.01, 31.5, 80, true, null));
        scope.Width = 400;
        scope.Height = 200;
        scope.Measure(new Size(400, 200));
        scope.Arrange(new Rect(0, 0, 400, 200));

        Gravar(scope, Path.Combine(destino, "scope.png"));
        Console.WriteLine("  scope.png");

        var bmp = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(scope); // aquece: OnRender corre e preenche a cache

        var antes = PensDoScope();
        Verificar("ScopeView: as quatro Pen ficaram em cache", antes.All(p => p is not null));

        scope.InvalidateVisual();
        bmp.Render(scope);
        var depois = PensDoScope();
        Verificar("ScopeView: o frame seguinte reutiliza as mesmas Pen",
            antes.Length == depois.Length && antes.Zip(depois).All(p => ReferenceEquals(p.First, p.Second)));

        // Calibração: quanto custa UMA Pen congelada neste runtime.
        var c0 = GC.GetAllocatedBytesForCurrentThread();
        var calibra = new Pen(Brushes.Gray, 1);
        var custoPen = GC.GetAllocatedBytesForCurrentThread() - c0;
        GC.KeepAlive(calibra);

        var a1 = GC.GetAllocatedBytesForCurrentThread();
        bmp.Render(scope); // sem InvalidateVisual: o OnRender não corre
        var semRedesenho = GC.GetAllocatedBytesForCurrentThread() - a1;

        scope.InvalidateVisual();
        var a2 = GC.GetAllocatedBytesForCurrentThread();
        bmp.Render(scope); // com redesenho: inclui o OnRender
        var comRedesenho = GC.GetAllocatedBytesForCurrentThread() - a2;

        var onRender = comRedesenho - semRedesenho;
        Verificar($"ScopeView: OnRender aloca {onRender} B por frame (uma Pen custa {custoPen} B)",
            onRender < custoPen);
    }

    /// <summary>As quatro Pen em cache no `ScopeView` (campos estáticos privados).</summary>
    private static Pen?[] PensDoScope()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        return new[] { "_gridPen", "_ampPen", "_voltPen", "_tempPen" }
            .Select(n => typeof(ScopeView).GetField(n, flags)?.GetValue(null) as Pen)
            .ToArray();
    }

    /// <summary>
    /// Fechar as Opções a meio de um pedido de emparelhamento: o `await` tem de
    /// retomar sem rebentar (o handler é `async void` — uma exceção não apanhada
    /// derrubava a app) e sem tocar em `_config`. O servidor é falso e nunca
    /// responde, que é a única forma de o pedido estar mesmo a meio.
    /// </summary>
    private static void ProvarFechoAPairar()
    {
        Console.WriteLine("== fechar as Opções a meio do pairing ==");
        ThemeManager.Apply(ThemeManager.Terminal);

        var servidor = new ServidorPendurado();
        var config = DiagConfig.Load();
        var urlAntes = config.CloudUrl;
        var tokenAntes = config.ShopTokenProtected;

        var janela = new SettingsDialog(config)
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
            (janela.FindName("CloudBox") as TextBox)!.Text = servidor.Url;
            (janela.FindName("TokenBox") as PasswordBox)!.Password = "ABCD-1234";
            (janela.FindName("SaveBtn") as Button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Verificar("pairing: o pedido chegou ao servidor falso",
                Esperar(() => servidor.Pedidos > 0, 5000));

            Verificar("pairing: o pedido está a meio (_saving ligado)",
                SavingDaJanela(janela) == true);

            janela.Close(); // a meio do pedido
            Verificar("pairing: a janela fechou a meio do pedido",
                Esperar(() => !janela.IsLoaded, 2000));

            // Espera pelo estado, não por um sleep: o `finally` do handler só
            // corre quando o await retoma (cancelamento). Se nunca retomar, o
            // teste falha — que é o que se quer saber. E se o await rebentar,
            // o processo morre e o job fica vermelho.
            Verificar("pairing: o await retomou e saiu do finally",
                Esperar(() => SavingDaJanela(janela) == false, 5000));

            Verificar("pairing: fechar a meio não grava a URL", config.CloudUrl == urlAntes);
            Verificar("pairing: fechar a meio não grava token",
                config.ShopTokenProtected == tokenAntes);
        }
        finally
        {
            try { janela.Close(); } catch { }
            servidor.Dispose();
        }
    }

    /// <summary>O `_saving` do `SettingsDialog` — prova que o pedido está a meio / já saiu.</summary>
    private static bool? SavingDaJanela(Window janela) =>
        typeof(SettingsDialog).GetField("_saving", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(janela) as bool?;

    /// <summary>Texto sem espaços, para comparar com o que a MessageBox parte em linhas.</summary>
    private static string SemEspacos(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// Encontra o `MessageBox` da app por UIAutomation, lê o corpo e responde.
    /// Corre numa thread de fundo: o `MessageBox.Show` bloqueia a thread da UI
    /// com o diálogo aberto, logo ninguém a pode ler de lá.
    /// </summary>
    private static Task<string?> ResponderMessageBox(string resposta, int timeoutMs = 15_000) => Task.Run(() =>
    {
        var fim = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < fim)
        {
            // FindWindow, não UIAutomation: a árvore de automação pode bloquear
            // e, como a thread da UI está dentro do Show, quem fica preso é o
            // run inteiro. Aqui encontrar é Win32 puro.
            var hwnd = FindWindow("#32770", null);
            if (hwnd == IntPtr.Zero)
            {
                Thread.Sleep(100);
                continue;
            }

            // Ler o texto por UIA, mas com prazo: se a automação não responder,
            // fecha-se na mesma e o teste diz que não leu o corpo.
            var leitura = Task.Run(() => TextoDoDialogo(hwnd));
            var corpo = leitura.Wait(4000) ? leitura.Result : null;

            // WM_COMMAND com IDYES/IDNO é o caminho nativo do MessageBox.
            PostMessage(hwnd, WmCommand,
                new IntPtr(resposta == "sim" ? IdYes : IdNo), IntPtr.Zero);
            if (!Sumiu(hwnd, 3000))
                PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

            return corpo;
        }
        return null;
    });

    /// <summary>
    /// Rede de segurança contra um run pendurado: se o `Close()` ficar preso
    /// dentro de um diálogo que ninguém respondeu, isto liberta-o. Cancela-se
    /// quando a resposta normal chegou, para não fechar um diálogo alheio.
    /// </summary>
    private static Task VigiaDoMessageBox(CancellationToken token, int depoisDeMs = 20_000) => Task.Run(async () =>
    {
        try { await Task.Delay(depoisDeMs, token); }
        catch (OperationCanceledException) { return; }
        var fim = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < fim)
        {
            var hwnd = FindWindow("#32770", null);
            if (hwnd == IntPtr.Zero) return;
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(250);
        }
    });

    /// <summary>Texto do corpo do diálogo por UIAutomation (null se não der).</summary>
    private static string? TextoDoDialogo(IntPtr hwnd)
    {
        try
        {
            var dialogo = AutomationElement.FromHandle(hwnd);
            return string.Join(" ", dialogo
                .FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>()
                .Select(e => e.Current.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>O diálogo já desapareceu? (poling curto, sem sleep fixo)</summary>
    private static bool Sumiu(IntPtr hwnd, int timeoutMs)
    {
        var fim = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < fim)
        {
            if (!IsWindow(hwnd)) return true;
            Thread.Sleep(50);
        }
        return !IsWindow(hwnd);
    }

    /// <summary>
    /// O aviso de perda de trabalho da bancada: o `MessageBox` é janela nativa,
    /// não entra no `RenderTargetBitmap`. Lido e respondido por UIAutomation, e
    /// as duas respostas são verificadas — «Não» mantém a janela, «Sim» fecha.
    /// </summary>
    private static void ProvarAvisoDaBancada()
    {
        Console.WriteLine("== aviso ao fechar a bancada a meio da sessão ==");
        ThemeManager.Apply(ThemeManager.Terminal);

        var janela = new BenchWindow("Pixel 8 · android", () => Task.FromResult<LiveTelemetry?>(null))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20_000,
            Top = -20_000,
            ShowInTaskbar = false,
            Width = 920,
            Height = 620,
        };
        janela.Show();
        Respirar();
        try
        {
            var comecar = janela.FindName("StartBtn") as Button;
            comecar!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Verificar("bancada: sessão arrancou (SCAN+ desativado)",
                Esperar(() => !comecar.IsEnabled, 5000));

            using var vigia = new CancellationTokenSource();
            var rede = VigiaDoMessageBox(vigia.Token);
            var primeira = ResponderMessageBox("nao");
            janela.Close(); // bloqueia no MessageBox
            var corpo = primeira.GetAwaiter().GetResult();
            vigia.Cancel();
            rede.Wait(500);

            Verificar("bancada: o aviso apareceu", corpo is not null);
            var esperado = SemEspacos(L10n.T("bench.confirm.abort"));
            Verificar($"bancada: o texto é o do L10n ('{corpo}')",
                corpo is not null && SemEspacos(corpo).Contains(esperado[..30]));
            Verificar("bancada: responder Não mantém a janela aberta", janela.IsLoaded);

            using var vigia2 = new CancellationTokenSource();
            var rede2 = VigiaDoMessageBox(vigia2.Token);
            var segunda = ResponderMessageBox("sim");
            janela.Close();
            var corpo2 = segunda.GetAwaiter().GetResult();
            vigia2.Cancel();
            rede2.Wait(500);
            Verificar("bancada: responder Sim fecha a janela",
                corpo2 is not null && Esperar(() => !janela.IsLoaded, 3000));
        }
        finally
        {
            try { janela.Close(); } catch { }
        }
    }

    /// <summary>Servidor TCP que aceita ligações e nunca responde — prende o pedido a meio.</summary>
    private sealed class ServidorPendurado : IDisposable
    {
        private readonly TcpListener _escutador = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clientes = new();
        private int _pedidos;

        public ServidorPendurado()
        {
            _escutador.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_escutador.LocalEndpoint).Port}";
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var cliente = await _escutador.AcceptTcpClientAsync(_cts.Token);
                        lock (_clientes) _clientes.Add(cliente);
                        Interlocked.Increment(ref _pedidos);
                    }
                    catch { break; }
                }
            });
        }

        public string Url { get; }

        public int Pedidos => Volatile.Read(ref _pedidos);

        public void Dispose()
        {
            _cts.Cancel();
            _escutador.Stop();
            lock (_clientes)
            {
                foreach (var c in _clientes)
                {
                    try { c.Dispose(); } catch { }
                }
                _clientes.Clear();
            }
            _cts.Dispose();
        }
    }

    /// <summary>Cor sólida de um brush (null se for gradiente ou nulo).</summary>
    private static Color? Cor(Brush? brush) => brush is SolidColorBrush s ? s.Color : null;

    /// <summary>
    /// Troca de tema com a janela **já aberta**: prova que os DynamicResource
    /// chegam à ResultsList e aos cinco diálogos sem os reabrir. Antes só se
    /// sabia que abrem já no tema — o T-06 pede a troca a quente.
    /// </summary>
    private static void ProvarTrocaTema(string destino, DeviceReport relatorio)
    {
        Console.WriteLine("== troca de tema com a janela aberta ==");

        ThemeManager.Apply(ThemeManager.Terminal);
        var janela = ComRelatorio(new MainWindow(), relatorio);
        janela.WindowStartupLocation = WindowStartupLocation.Manual;
        janela.ShowInTaskbar = false;
        janela.Left = -20_000;
        janela.Top = -20_000;
        janela.Width = 1000;
        janela.Height = 700;
        janela.Show();
        Respirar();

        try
        {
            if (VisualTreeHelper.GetChildrenCount(janela) == 0) return;
            if (VisualTreeHelper.GetChild(janela, 0) is not FrameworkElement raiz) return;
            raiz.UpdateLayout();
            Respirar();

            var fundoAntes = Cor(Application.Current.TryFindResource("BgBrush") as Brush);
            // A lista é reconstruída na troca de tema (RefreshResults), por isso o
            // TextBlock tem de ser buscado de novo — segurar a referência antiga
            // media um elemento já fora da árvore e dava um falso negativo.
            var itemAntes = janela.FindName("ResultsList") is ItemsControl lista
                ? Primeiro<TextBlock>(lista)
                : null;
            var textoAntes = Cor(itemAntes?.Foreground);
            var tagAntes = itemAntes?.Text;

            Gravar(raiz, Path.Combine(destino, "troca-terminal.png"));
            Console.WriteLine("  troca-terminal.png");

            ThemeManager.Apply(ThemeManager.Win95);
            Respirar();
            raiz.UpdateLayout();
            Respirar();

            var fundoDepois = Cor(Application.Current.TryFindResource("BgBrush") as Brush);
            var itemDepois = janela.FindName("ResultsList") is ItemsControl listaDepois
                ? Primeiro<TextBlock>(listaDepois)
                : null;
            var textoDepois = Cor(itemDepois?.Foreground);
            var tagDepois = itemDepois?.Text;

            Verificar($"troca: fundo muda (terminal {fundoAntes} → win95 {fundoDepois})",
                fundoAntes is not null && fundoDepois is not null && fundoAntes != fundoDepois);
            Verificar("troca: janela já aberta apanha o BgBrush novo",
                Cor(janela.Background) is { } fundoJanela && fundoJanela == fundoDepois);
            Verificar($"troca: ResultsList repinta sem reabrir ({textoAntes} → {textoDepois})",
                textoAntes is not null && textoDepois is not null && textoAntes != textoDepois);
            // Formato: o terminal pinta `[pass]`, os outros temas `pass`.
            Verificar($"troca: ResultsList muda de formato ('{tagAntes}' → '{tagDepois}')",
                tagAntes?.StartsWith('[') == true && tagDepois?.StartsWith('[') == false);

            Gravar(raiz, Path.Combine(destino, "troca-win95.png"));
            Console.WriteLine("  troca-win95.png");
        }
        finally { janela.Close(); }

        // Os cinco diálogos do T-06, cada um aberto uma vez e trocado a quente.
        var dialogos = new (string Nome, Func<Window> Criar)[]
        {
            ("checklist", () => new ChecklistDialog(relatorio)),
            ("seguro", () => new InsuranceDialog(DiagConfig.Load())),
            ("envio", () => new SendDialog()),
            ("token", () => new TokenDialog()),
            ("opcoes", () => new SettingsDialog(DiagConfig.Load())),
        };

        foreach (var (nome, criar) in dialogos)
        {
            ThemeManager.Apply(ThemeManager.Terminal);
            var dialogo = criar();
            dialogo.WindowStartupLocation = WindowStartupLocation.Manual;
            dialogo.Left = -20_000;
            dialogo.Top = -20_000;
            dialogo.ShowInTaskbar = false;
            dialogo.Show();
            Respirar();
            try
            {
                dialogo.UpdateLayout();
                var antes = Cor(dialogo.Background);

                ThemeManager.Apply(ThemeManager.Fluent);
                Respirar();
                dialogo.UpdateLayout();

                var depois = Cor(dialogo.Background);
                var esperado = Cor(Application.Current.TryFindResource("BgBrush") as Brush);
                Verificar($"troca: {nome} repinta aberto ({antes} → {depois})",
                    antes is not null && depois is not null && antes != depois && depois == esperado);

                if (nome == "opcoes" && VisualTreeHelper.GetChildrenCount(dialogo) > 0
                    && VisualTreeHelper.GetChild(dialogo, 0) is FrameworkElement raizDialogo)
                {
                    Gravar(raizDialogo, Path.Combine(destino, "troca-opcoes-fluent.png"));
                    Console.WriteLine("  troca-opcoes-fluent.png");
                }
            }
            finally { dialogo.Close(); }
        }

        ThemeManager.Apply(ThemeManager.Terminal);
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
                var ficheiro = Path.Combine(destino, $"combo-{tema}-aberto.png");
                var (pw, ph) = Gravar(painel, ficheiro);
                Console.WriteLine($"  {Path.GetFileName(ficheiro),-26} {pw}x{ph}");
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

    /// <summary>
    /// Grava um elemento em PNG sobre o fundo do tema. Único sítio que escreve
    /// imagens — havia dois e só um pintava o fundo opaco.
    /// </summary>
    private static (int W, int H) Gravar(FrameworkElement raiz, string ficheiro)
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
        return (bmp.PixelWidth, bmp.PixelHeight);
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
    /// As ações nomeadas estão inteiras dentro do cliente? É o que prova que
    /// num ecrã baixo os botões não ficam meio cortados.
    /// </summary>
    private static void VerificarAcoes(Window janela, FrameworkElement raiz, string rotulo,
        double? workArea, string[] nomes)
    {
        if (workArea is { } limite)
            Verificar($"{rotulo}: cabe em {limite:0} DIP (janela {janela.ActualHeight:0})",
                janela.ActualHeight <= limite + 0.5);

        foreach (var nome in nomes)
        {
            if (janela.FindName(nome) is not FrameworkElement botao)
            {
                Verificar($"{rotulo}: {nome} existe", false);
                continue;
            }
            Rect r;
            try
            {
                r = botao.TransformToAncestor(raiz).TransformBounds(
                    new Rect(0, 0, botao.ActualWidth, botao.ActualHeight));
            }
            catch
            {
                Verificar($"{rotulo}: {nome} ligado à árvore", false);
                continue;
            }
            Verificar($"{rotulo}: {nome} inteiro (topo {r.Top:0}, base {r.Bottom:0} de {raiz.ActualHeight:0})",
                botao.IsVisible && r.Top >= -0.5 && r.Bottom <= raiz.ActualHeight + 0.5);
        }
    }

    /// <summary>
    /// Mostra a janela fora do ecrã, mede/arranja o conteúdo e grava o PNG.
    /// `Show()` é best-effort: serve para os templates assentarem e para tentar
    /// o foco; se o ambiente não o permitir, mede-se o conteúdo à mão.
    /// </summary>
    /// <summary>Encolhe o ScrollViewer para provar que a barra aparece.</summary>
    private static void LimitarScroll(Window janela) => LimitarScrollA(360)(janela);

    /// <summary>
    /// O mesmo com um limite à escolha. Com as ações fora do scroll, 360 já não
    /// aperta os campos das Opções (~342) — para os apertar é preciso menos.
    /// </summary>
    private static Action<Window> LimitarScrollA(double max) => janela =>
    {
        if (Primeiro<ScrollViewer>(janela) is { } sv) sv.MaxHeight = max;
    };

    private static void Capturar(Func<Window> criar, string ficheiro, double largura, double altura,
        Action<Window>? ajustar = null, double? workArea = null, string[]? acoes = null)
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
            // Work area simulada *antes* do Show: a janela já nasce limitada.
            // Depois do Show isto só recortava a imagem (o layout não refazia
            // até chegar o WM_SIZE). O clamp da app é Min(), por isso respeita
            // um limite mais apertado do que a work area real.
            if (workArea is { } limite) janela.MaxHeight = limite;

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

            if (acoes is { Length: > 0 })
                VerificarAcoes(janela, raiz, Path.GetFileNameWithoutExtension(ficheiro), workArea, acoes);

            var foco = mostrou ? TentarFoco(janela) : "não";
            var (w, h) = Gravar(raiz, ficheiro);
            // Diagnóstico: se o fundo de um descendente passar da altura do
            // conteúdo *e ele não estiver dentro de uma área rolável*, é a app
            // que arruma fora da janela — defeito. O que fica atrás de um
            // ScrollViewer com scroll não conta: chega-se lá a rolar.
            double fundo = 0, fora = 0;
            var rolavel = 0;
            foreach (var e in Descendentes<FrameworkElement>(raiz))
            {
                try
                {
                    var p = e.TransformToAncestor(raiz).Transform(new Point(0, e.ActualHeight));
                    fundo = Math.Max(fundo, p.Y);
                    if (p.Y <= raiz.ActualHeight + 1) continue;
                    if (DentroDeAreaRolavel(e, raiz)) rolavel++;
                    else fora = Math.Max(fora, p.Y);
                }
                catch { /* ainda não ligado à árvore */ }
            }
            var onde = fora > raiz.ActualHeight + 1 ? "FORA DA JANELA"
                     : rolavel > 0 ? "resto rolável" : "";
            Console.WriteLine($"  {Path.GetFileName(ficheiro),-26} {w}x{h}  " +
                              $"janela {janela.ActualHeight:0} foco: {foco}  fundo {fundo:0} {onde}");
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

    /// <summary>O elemento está dentro de um ScrollViewer que efetivamente rola?</summary>
    private static bool DentroDeAreaRolavel(DependencyObject elemento, FrameworkElement raiz)
    {
        for (var atual = VisualTreeHelper.GetParent(elemento); atual is not null && atual != raiz;
             atual = VisualTreeHelper.GetParent(atual))
        {
            if (atual is ScrollViewer { ScrollableHeight: > 0.5 }) return true;
        }
        return false;
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
