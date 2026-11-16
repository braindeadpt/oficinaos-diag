using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
using OficinaDiag.History;
using OficinaDiag.Log;
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

    // Onde este run deixa o registo das verificações que correu, uma por
    // linha. O `tools/proofs-check.mjs` compara-o com as declaradas no
    // código: um `[OK]` que não aparece é uma prova saltada, e essa é a
    // forma que as três provas falsas desta série tiveram.
    private static string? _provas;

    // O fecho do harness é o `Terminar` de produção, chamado no fim do
    // `Corpo`. O vigia só dispara se ele não fechar nada (mutante) — é isso
    // que distingue «fechou o `Shutdown`» de «o harness fechou-se sozinho».
    private static volatile bool _terminouPorProducao;
    private static volatile bool _fimForcado;

    private static readonly MethodInfo OnClickDeBotao =
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Onde está o harness agora. Um run pendurado não diz nada no log — com um
    // vigia, o vigia diz exatamente onde e fecha o processo, em vez de deixar o
    // job a arder até ao limite do GitHub.
    private static volatile string _marcador = "(arranque)";

    private static void Marcar(string onde)
    {
        _marcador = onde;
        Console.WriteLine($"  · {onde}");
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder nome, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly uint MeuPid = (uint)Environment.ProcessId;

    /// <summary>
    /// O `#32770` do **nosso** processo. Um `FindWindow("#32770", null)` apanha
    /// o primeiro diálogo do sistema que apareça no runner — aconteceu com um
    /// «Windows created a temporary paging file…»: o responder respondeu a esse
    /// e o `MessageBox` da app ficou aberto a bloquear a thread da UI até ao
    /// vigia. Filtrar pelo PID é o que torna isto determinístico.
    /// </summary>
    private static IntPtr DialogoDaApp()
    {
        var encontrado = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var nome = new StringBuilder(16);
            GetClassName(hwnd, nome, nome.Capacity);
            if (nome.ToString() != "#32770") return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != MeuPid) return true;
            encontrado = hwnd;
            return false;
        }, IntPtr.Zero);
        return encontrado;
    }

    private const uint KeyUp = 0x0002;
    private const byte VkMenu = 0x12; // Alt
    private const uint WmCommand = 0x0111;
    private const uint WmClose = 0x0010;
    private const int IdYes = 6;
    private const int IdNo = 7;
    private const int IdOk = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        var destino = args.Length > 0 ? args[0] : "out-store/ui-shots";
        Directory.CreateDirectory(destino);
        _provas = Path.Combine(destino, "provas.txt");
        try { File.Delete(_provas); } catch { }
        File.WriteAllText(_provas, "");

        // O config do harness vive na pasta de saída: o GUARDAR das Opções
        // grava-o, e o config de quem corre isto não pode ser o alvo.
        DiagConfig.RedirectTo(Path.Combine(destino, "config"));

        // O log da rede vai para o artefacto antes de o `App` escrever a
        // primeira linha («> oficinaos-diag arrancou», no `OnStartup`).
        var logUi = Path.Combine(destino, "diag.log");
        AppLog.RedirectTo(logUi);

        // O `App` a sério, e não uma `Application` paralela. O handler global
        // (`DispatcherUnhandledException` → `RegistarFalha` → `Falhou`) é o
        // caminho que escondeu o checklist morto durante dois lotes; com uma
        // `Application` própria, o harness provava o efeito do `Falhou` mas
        // nunca quem lá chega. As três vias (UI, background, fatal) passam a
        // ser as do `App`, e o `MainWindow` que ele abre no `OnStartup` é a
        // janela onde o handler cai — é nela que corre a prova de produção.
        // O `App.xaml` não traz recursos nem `StartupUri` (os temas entram pelo
        // `ThemeManager`), por isso basta instanciá-lo: o `OnStartup` dele é que
        // faz o resto, incluindo abrir a `MainWindow`.
        var app = new OficinaDiag.App();
        // O `App` deixa o WPF fechar quando a última janela sai; aqui há janelas
        // a abrir e a fechar todo o tempo e o fim é explícito.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Um clique real chega dentro de uma operação do dispatcher, onde o
        // SynchronizationContext é o do WPF. Este `Main` corre fora do loop, e
        // sem isto as continuações dos handlers async caíam no thread pool:
        // o `Start_Click` da bancada escrevia num TextBlock de outra thread e
        // rebentava o processo — bug que só existia no harness.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(180));
            Console.WriteLine($"[FALHA] harness pendurado em: {_marcador}");
            Console.Out.Flush();
            Environment.Exit(4);
        }) { IsBackground = true }.Start();
        // `Application.Run` e não um `Main` a empurrar frames: é o modelo em
        // que a app real corre e o que faz uma janela contar como diálogo.
        // Sem loop, o `DialogResult` do APLICAR/GUARDAR/OK rebentava, e por isso
        // o lado que escreve dados nunca tinha prova nenhuma. O corpo entra na
        // fila antes de o loop arrancar — a fila só é servida dentro do `Run`,
        // por isso não há corrida entre os dois.
        var codigo = 1;
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { codigo = Corpo(destino, logUi); }
            catch (Exception ex)
            {
                // Um corpo que rebenta saía com exit 1 e **nenhuma** linha
                // `[FALHA]` — ficava um vermelho sem dono, e um mutante que só
                // mata o harness não se distinguia de um verde. Diz-se o que
                // rebentou e corre-se a verificação que disto é dona.
                Anotar("UI", ex);
                Console.WriteLine($"  [FALHA] o corpo do harness rebentou: {ex.GetType().Name}: {ex.Message}");
                try { VerificarUiSemExcecoes(logUi); } catch { }
                codigo = 1;
            }
            // Se o `Terminar` de produção chegou a correr, quem fecha é ele —
            // ou o vigia, que marca `_fimForcado`. Fechar aqui escondia o
            // defeito que a prova procura: um `Terminar` inerte nunca chegava
            // a fazer disparar o vigia, e a app fechava-se por fora com todos
            // os sinais a dizer «fechou bem». Aqui só se fecha o que ficou
            // aberto por o corpo ter rebentado antes do último passo.
            finally
            {
                if (!_terminouPorProducao && !app.Dispatcher.HasShutdownStarted) app.Shutdown();
            }
        }));
        app.Run();
        // Só se o `Terminar` de produção chegou a correr: se o corpo rebentou
        // antes, isto não diz nada e não vale uma falha a mais.
        if (_terminouPorProducao)
            Verificar("produção: o Terminar por omissão fechou a aplicação (sem vigia)",
                !_fimForcado && app.Dispatcher.HasShutdownStarted);
        if (_falhas > 0) Console.WriteLine($"FALHAS: {_falhas}");
        return _falhas == 0 ? codigo : 1;
    }

    /// <summary>
    /// O corpo do harness — capturas e provas — já dentro do loop do
    /// `Application.Run`, para os modais poderem ser dirigidos de dentro.
    /// </summary>
    private static int Corpo(string destino, string logUi)
    {
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
        Marcar("ScopeView sem Pen por frame");
        ProvarPensDoScope(destino);
        Marcar("fecho a meio do pairing");
        ProvarFechoAPairar();
        Marcar("aviso da bancada");
        ProvarAvisoDaBancada();
        Marcar("cliques nos diálogos");
        ProvarCliques();
        Marcar("commits dos diálogos");
        ProvarCommits();
        Marcar("aviso de reinício da atualização");
        ProvarAvisoDeAtualizacao();
        Marcar("botões da barra de acções");
        ProvarBotoes(destino);
        Marcar("dois telemóveis no seletor");
        ProvarDoisTelemoveis();
        Marcar("falha repetida na mesma ação");
        ProvarFalhaRepetida(destino);
        Marcar("falha repetida pelo handler do App");
        ProvarFalhaPeloHandlerDeProducao(destino);
        Marcar("barra de scroll do Win95");
        ProvarBarraDeScroll(destino);
        Marcar("diálogos e capturas finais");

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
            // O cartão do QR é branco e cobre a janela toda: o fundo do tema
            // não aparece, de propósito — a captura continua a exigir tamanho,
            // cores e variância.
            Capturar(() => new QrWindow(qr), Path.Combine(destino, "qr.png"), 340, 420,
                fundoDoTema: false);
        }

        VerificarUiSemExcecoes(logUi);
        Console.WriteLine($"capturas em {Path.GetFullPath(destino)}");

        Marcar("instalação da atualização contra o loopback");
        ProvarInstalacaoDaAtualizacao(destino);
        Marcar("os defeitos por omissão da atualização");
        ProvarDefeitoDaAtualizacao(destino);
        ProvarAplicacaoDoStaging(destino);

        // O último passo do harness não é uma prova: é o `Terminar` de
        // produção a correr a sério, a fechar a app como faria na bancada. Não
        // há nada depois dele — quando volta, o `Run` já está a acabar, e o
        // `Main` confirma que foi ele (e não o vigia) a fechar.
        Marcar("Terminar de produção (fecho do harness)");
        ProvarTerminarDeProducao();
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
            var hwnd = DialogoDaApp();
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
                new IntPtr(resposta switch { "sim" => IdYes, "ok" => IdOk, _ => IdNo }),
                IntPtr.Zero);
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
            var hwnd = DialogoDaApp();
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
    /// Clica um botão a sério e, se o handler rebentar, regista a mesma linha
    /// «! UI: …» que o `App` real escreve — em vez de deixar a exceção matar o
    /// processo sem dizer qual foi. Devolve `false` quando houve exceção.
    /// </summary>
    private static bool Clicar(ButtonBase botao)
    {
        try
        {
            // `OnClick` e não `RaiseEvent(ClickEvent)`: num RadioButton é o
            // `OnClick` que alterna o `IsChecked` (e só depois dispara o Click).
            // Com RaiseEvent o rádio nunca ficava marcado e o handler de
            // idioma/tema não corria — duas verificações a vermelho por um
            // clique que não era um clique.
            OnClickDeBotao.Invoke(botao, null);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Exception("UI", ex);
            Console.WriteLine($"  ! UI: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Põe a janela fora do ecrã e fora da barra de tarefas, como as capturas:
    /// num runner sem desktop interactivo isto impede que as janelas se roubem
    /// o foco umas às outras.
    /// </summary>
    private static T ForaDoEcra<T>(T janela) where T : Window
    {
        janela.WindowStartupLocation = WindowStartupLocation.Manual;
        janela.Left = -20_000;
        janela.Top = -20_000;
        janela.ShowInTaskbar = false;
        return janela;
    }

    /// <summary>Abre um diálogo fora do ecrã, como as capturas.</summary>
    private static void Abrir(Window janela)
    {
        ForaDoEcra(janela);
        janela.Show();
        Respirar();
    }

    private static void Fechar(Window janela)
    {
        try { janela.Close(); } catch { }
    }

    /// <summary>
    /// Abre o diálogo **como modal** — o único sítio onde `DialogResult` é
    /// aceite — e interage lá dentro por um `DispatcherTimer` de disparo único,
    /// que corre no loop do `ShowDialog`. Interagir no `Loaded` não servia:
    /// nesse momento a janela ainda não conta como diálogo, e foi por isso que
    /// o APLICAR/GUARDAR ficaram dois lotes sem prova nenhuma.
    /// </summary>
    private static bool? AbrirModal(Window janela, Action<Window> interagir, string rotulo,
        int timeoutMs = 15_000)
    {
        ForaDoEcra(janela);

        var interagiu = false;
        var estourou = false;
        var acao = new DispatcherTimer(DispatcherPriority.Background, janela.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        acao.Tick += (_, _) =>
        {
            acao.Stop();
            interagiu = true;
            try { interagir(janela); }
            catch (Exception ex) { Anotar("UI", ex); }
        };
        // Sem prazo, uma validação inesperada deixa o `ShowDialog` a bloquear e
        // o run só acaba no vigia de 180 s, sem dizer qual diálogo foi.
        var prazo = new DispatcherTimer(DispatcherPriority.Background, janela.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(timeoutMs),
        };
        prazo.Tick += (_, _) =>
        {
            prazo.Stop();
            estourou = true;
            if (janela.IsLoaded) janela.Close();
        };

        acao.Start();
        prazo.Start();
        var resultado = janela.ShowDialog();
        prazo.Stop();

        Verificar($"{rotulo}: a interação correu dentro do modal", interagiu);
        Verificar($"{rotulo}: fechou em menos de {timeoutMs / 1000} s", !estourou);
        return resultado;
    }

    /// <summary>
    /// Dois telemóveis no seletor, **sem hardware**: `AttachDevice` e
    /// `DetachDevice` são exactamente o que o `DeviceDetector` chama quando um
    /// aparelho liga ou desliga — chamá-los daqui é a mesma ronda, com os mesmos
    /// dados e os mesmos handlers. Prova o que o `T-09` pedia: o seletor só
    /// aparece com mais de um, o segundo não rouba a bancada, escolher troca o
    /// activo, desligar o activo passa para o outro e um estado sem OS completo
    /// desliga as ações.
    /// </summary>
    private static void ProvarDoisTelemoveis()
    {
        Console.WriteLine("== dois telemóveis no seletor (sem hardware) ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        var primeiro = new DetectedDevice(DeviceKind.Android, "AAA111", "Pixel 8 · android", "ready");
        var segundo = new DetectedDevice(DeviceKind.Android, "BBB222", "Galaxy S23 · android", "ready");
        var porAutorizar = new DetectedDevice(DeviceKind.Ios, "CCC333", "iPhone 14 · ios", "unauthorized");

        var janela = new MainWindow();
        Abrir(janela);
        try
        {
            var seletor = janela.FindName("DeviceSelector") as ComboBox;
            var scan = janela.FindName("ScanButton") as Button;
            var teste = janela.FindName("TestButton") as Button;
            var bancada = janela.FindName("BenchButton") as Button;
            var estado = janela.FindName("StatusText") as TextBlock;
            var consola = janela.FindName("Console") as TextBox;
            if (seletor is null || scan is null || teste is null || bancada is null || estado is null)
            {
                Verificar("seletor: os controlos existem", false);
                return;
            }

            ChamarMainWindow(janela, "AttachDevice", primeiro);
            Verificar("seletor: um aparelho — escondido",
                seletor.Visibility == Visibility.Collapsed && Ativo(janela)?.Id == primeiro.Id);

            ChamarMainWindow(janela, "AttachDevice", segundo);
            Verificar("seletor: dois aparelhos — aparece",
                seletor.Visibility == Visibility.Visible && seletor.Items.Count == 2);
            Verificar("seletor: o 2º aparelho não rouba a bancada", Ativo(janela)?.Id == primeiro.Id);
            Verificar("seletor: mostra o aparelho ativo",
                (seletor.SelectedItem as DetectedDevice)?.Id == primeiro.Id);

            // Escolher no seletor é o gesto do utilizador: o caminho passa a ser
            // o handler `DeviceSelector_Changed`, não a chamada direta.
            seletor.SelectedItem = seletor.Items.OfType<DetectedDevice>().First(x => x.Id == segundo.Id);
            Respirar();
            Verificar("seletor: escolher troca o aparelho ativo", Ativo(janela)?.Id == segundo.Id);
            Verificar("seletor: a troca vai para a consola",
                consola is not null && consola.Text.Contains(L10n.F("log.devswitch", segundo.Label)));

            // Desligar o ativo não pode deixar a bancada morta.
            ChamarMainWindow(janela, "DetachDevice", segundo);
            Verificar("seletor: desligar o ativo passa para o outro",
                Ativo(janela)?.Id == primeiro.Id && seletor.Items.Count == 1);
            Verificar("seletor: volta a esconder com um só",
                seletor.Visibility == Visibility.Collapsed && scan.IsEnabled);

            // Estado sem OS completo: entra na lista mas desliga as ações.
            ChamarMainWindow(janela, "AttachDevice", porAutorizar);
            seletor.SelectedItem = seletor.Items.OfType<DetectedDevice>().First(x => x.Id == porAutorizar.Id);
            Respirar();
            Verificar("seletor: o aparelho por autorizar fica ativo", Ativo(janela)?.Id == porAutorizar.Id);
            Verificar("seletor: sem OS completo as ações desligam",
                !scan.IsEnabled && !teste.IsEnabled && !bancada.IsEnabled);
            Verificar("seletor: o estado diz o que fazer",
                estado.Text == L10n.F("status.unauthorized", porAutorizar.Label));

            ChamarMainWindow(janela, "DetachDevice", porAutorizar);
            Verificar("seletor: tirar o por autorizar devolve a bancada ao outro",
                Ativo(janela)?.Id == primeiro.Id && scan.IsEnabled && teste.IsEnabled);

            ChamarMainWindow(janela, "DetachDevice", primeiro);
            Verificar("seletor: sem aparelhos volta a «a vigiar»",
                Ativo(janela) is null && estado.Text == L10n.T("status.watching") && scan.IsEnabled);
        }
        finally { Fechar(janela); }
    }

    /// <summary>
    /// Três falhas na mesma ação: a app avisa o técnico e desliga o botão, e o
    /// log para de crescer. É a rede de quem está na bancada — a do CI não o
    /// protege. O log desta prova vai para um ficheiro à parte: as linhas
    /// «! UI:» são intencionais e a rede do harness reprova qualquer uma.
    /// </summary>
    private static void ProvarFalhaRepetida(string destino)
    {
        Console.WriteLine("== falha repetida na mesma ação ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        var logProva = Path.Combine(destino, "diag-falhas.log");
        try { File.Delete(logProva); } catch { }
        var anterior = AppLog.RedirectTo(logProva);

        var janela = new MainWindow();
        Abrir(janela);
        try
        {
            if (janela.FindName("ScanButton") is not Button botao)
            {
                Verificar("falha repetida: o botão existe", false);
                return;
            }
            var falhou = typeof(MainWindow).GetMethod("Falhou",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

            falhou.Invoke(janela, ["ScanButton", new InvalidOperationException("falha 1")]);
            falhou.Invoke(janela, ["ScanButton", new InvalidOperationException("falha 2")]);
            Verificar("falha repetida: duas falhas ainda não desligam o botão", botao.IsEnabled);
            Verificar($"falha repetida: duas falhas escrevem duas linhas ({LinhasUi(logProva)})",
                LinhasUi(logProva) == 2);

            using var vigia = new CancellationTokenSource();
            var rede = VigiaDoMessageBox(vigia.Token);
            var resposta = ResponderMessageBox("ok");
            falhou.Invoke(janela, ["ScanButton", new InvalidOperationException("falha 3")]);
            var corpo = resposta.GetAwaiter().GetResult();
            vigia.Cancel();
            rede.Wait(500);

            Verificar("falha repetida: à terceira aparece o aviso", corpo is not null);
            var texto = SemEspacos(L10n.F("err.repeated", "SCAN"));
            Verificar("falha repetida: o aviso é o do L10n",
                corpo is not null && SemEspacos(corpo).Contains(texto[(texto.Length / 3)..]));
            Verificar("falha repetida: o botão ficou desligado", !botao.IsEnabled);

            var consola = SemEspacos((janela.FindName("Console") as TextBox)?.Text ?? "");
            var avisoLog = SemEspacos(L10n.F("log.err.disabled", "SCAN"));
            Verificar("falha repetida: a consola diz que o botão foi desligado",
                consola.Contains(avisoLog[(avisoLog.Length / 3)..]));

            var log3 = LinhasUi(logProva);
            falhou.Invoke(janela, ["ScanButton", new InvalidOperationException("falha 4")]);
            Pausa(300);
            Verificar($"falha repetida: a 3.ª falha escreve a 3.ª linha ({log3})", log3 == 3);
            Verificar($"falha repetida: a 4.ª não escreve mais nada (dedupe: {LinhasUi(logProva)})",
                LinhasUi(logProva) == log3);
            Verificar("falha repetida: a 4.ª não abre outro aviso", DialogoDaApp() == IntPtr.Zero);
        }
        finally
        {
            Fechar(janela);
            AppLog.RedirectTo(anterior);
        }
    }

    /// <summary>
    /// A mesma falha repetida, mas pelo caminho de produção: a exceção sobe do
    /// dispatcher, o `App` apanha-a no `DispatcherUnhandledException`, atribui-a
    /// à ação em curso e só então o `Falhou` conta, desliga e avisa. Era este
    /// percurso que escondia o checklist morto; o harness chamava o `Falhou`
    /// diretamente e provava o efeito, não quem lá chega.
    ///
    /// A janela é a do próprio `App` (`Application.Current.MainWindow`), porque
    /// é nela que o handler do `App` cai — não nas que o harness abre.
    /// </summary>
    private static void ProvarFalhaPeloHandlerDeProducao(string destino)
    {
        Console.WriteLine("== falha repetida pelo handler do App ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        if (Application.Current.MainWindow is not MainWindow janela)
        {
            Verificar("produção: o App arrancou a janela principal", false);
            return;
        }
        Verificar("produção: o App arrancou a janela principal", true);

        var log = Path.Combine(destino, "diag-producao.log");
        try { File.Delete(log); } catch { }
        var anterior = AppLog.RedirectTo(log);
        try
        {
            if (janela.FindName("ScanButton") is not Button botao)
            {
                Verificar("produção: o botão existe", false);
                return;
            }
            var rotulo = botao.Content?.ToString() ?? "SCAN";

            // O gesto: o handler global não recebe o remetente, por isso a
            // janela guarda a ação no preview. Um rato sintético não entrega
            // `OriginalSource` num runner sem desktop — levantar o evento dava
            // três linhas de log e nenhum botão desligado —, por isso chama-se
            // a atribuição (que é a mesma), não o `Falhou`.
            LembrarGesto(janela, botao);
            Verificar("produção: o gesto ficou atribuído ao botão",
                typeof(MainWindow).GetField("_acaoEmCurso",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(janela) as string
                == "ScanButton");

            using var vigia = new CancellationTokenSource();
            var rede = VigiaDoMessageBox(vigia.Token);
            var resposta = ResponderMessageBox("ok");

            // `BeginInvoke`, não `Invoke`: o `Invoke` devolve a exceção ao
            // chamador e o dispatcher nunca a vê. Três, como um técnico a
            // carregar no botão que não responde.
            for (var i = 1; i <= 3; i++)
            {
                var n = i;
                janela.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => throw new InvalidOperationException($"falha de produção {n}")));
            }

            // À terceira, o `Falhou` abre um `MessageBox` que prende esta
            // thread: a resposta vem de trás e a bombagem deixa os envois correr.
            string? corpo = null;
            for (var i = 0; i < 150 && corpo is null; i++)
            {
                Pausa(100);
                if (resposta.IsCompleted) corpo = resposta.Result;
            }
            vigia.Cancel();
            rede.Wait(500);

            Verificar("produção: o handler do App avisou à terceira", corpo is not null);
            var texto = SemEspacos(L10n.F("err.repeated", rotulo));
            Verificar($"produção: o aviso é o do L10n ('{texto[..Math.Min(40, texto.Length)]}…')",
                corpo is not null && SemEspacos(corpo).Contains(texto[(texto.Length / 3)..]));
            Verificar("produção: o botão da janela do App ficou desligado", !botao.IsEnabled);
            Verificar($"produção: as três falhas passaram pelo log ({LinhasUi(log)})",
                LinhasUi(log) == 3);
            Verificar("produção: a via «! UI:» aparece na consola da janela",
                ((TextBox)janela.FindName("Console")!).Text.Contains("! UI:"));
        }
        finally { AppLog.RedirectTo(anterior); }
    }

    /// <summary>Linhas de exceção engolida no log de uma prova.</summary>
    private static int LinhasUi(string log) =>
        File.Exists(log) ? File.ReadAllLines(log).Count(l => l.Contains("! UI:")) : 0;

    /// <summary>Chama um método privado do `MainWindow` (o caminho que o detetor usa).</summary>
    private static void ChamarMainWindow(MainWindow janela, string metodo, object? arg) =>
        typeof(MainWindow).GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(janela, [arg]);

    /// <summary>O `_current` do `MainWindow`: quem manda na bancada agora.</summary>
    private static DetectedDevice? Ativo(MainWindow janela) =>
        typeof(MainWindow).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(janela) as DetectedDevice;

    /// <summary>
    /// O lado que escreve: APLICAR, GUARDAR, OK, GERAR e ENVIAR a sério, com
    /// dados válidos, dentro do modal. Antes disto nenhum commit de diálogo
    /// alguma vez tinha corrido — a rede só via cliques que não gravam nada.
    /// </summary>
    private static void ProvarCommits()
    {
        Console.WriteLine("== commits dos diálogos (dentro do modal) ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        // 1. Checklist: marcar e APLICAR grava no relatório.
        var relatorio = RelatorioAtual();
        var checklist = new ChecklistDialog(relatorio);
        var aplicou = AbrirModal(checklist, janela =>
        {
            var itens = janela.FindName("ItemsPanel") as DependencyObject;
            var marcas = itens is null ? Array.Empty<Button>() : Descendentes<Button>(itens).ToArray();
            if (marcas.Length < 6) return;
            Clicar(marcas[0]); // moldura: ok
            Clicar(marcas[5]); // vidro traseiro: n/t
            if (janela.FindName("ApplyBtn") is Button aplicar) Clicar(aplicar);
        }, "commit: checklist APLICAR");
        Verificar("commit: checklist APLICAR devolveu OK", aplicou == true);
        Verificar("commit: o relatório ficou com `pass` na moldura",
            relatorio.Results.TryGetValue("checklist.frame", out var pass) && pass.Status == "pass");
        Verificar("commit: o relatório ficou com `skipped` no vidro traseiro",
            relatorio.Results.TryGetValue("checklist.backglass", out var pulou) && pulou.Status == "skipped");
        Verificar("commit: só os itens marcados foram gravados",
            !relatorio.Results.ContainsKey("checklist.buttons"));

        // 2. Opções GUARDAR: o preview passa a estado gravado.
        var config = DiagConfig.Load();
        var opcoes = new SettingsDialog(config);
        var guardou = AbrirModal(opcoes, janela =>
        {
            if (janela.FindName("LangEn") is RadioButton ingles) Clicar(ingles);
            if (janela.FindName("SaveBtn") is Button salvar) Clicar(salvar);
        }, "commit: opções GUARDAR");
        Verificar("commit: opções GUARDAR devolveu OK", guardou == true);
        Verificar("commit: o config ficou com o idioma do preview", config.Language == "en");
        Verificar("commit: o config ficou com o tema actual", config.Theme == ThemeManager.Current);
        Verificar("commit: a retenção foi lida do combo (90 por omissão)",
            config.HistoryRetentionDays == 90);
        L10n.Set("pt");
        ThemeManager.Apply(ThemeManager.Terminal);

        // 3. Fechar sem guardar: o preview volta atrás e o config não muda.
        var config2 = DiagConfig.Load();
        var idiomaAntes = config2.Language;
        var opcoes2 = new SettingsDialog(config2);
        var fechou = AbrirModal(opcoes2, janela =>
        {
            if (janela.FindName("LangEn") is RadioButton ingles) Clicar(ingles);
            janela.Close(); // Esc/X: fecha sem guardar
        }, "commit: opções fechar sem guardar");
        Verificar("commit: fechar sem guardar não devolve OK", fechou != true);
        Verificar("commit: fechar sem guardar repõe o idioma de antes", L10n.Current == "pt");
        Verificar("commit: fechar sem guardar não escreve no config", config2.Language == idiomaAntes);

        // 4. Token OK: o código escrito é o que o diálogo devolve.
        var token = new TokenDialog();
        var leuToken = AbrirModal(token, janela =>
        {
            if (janela.FindName("TokenBox") is PasswordBox caixa) caixa.Password = "ABCD-1234";
            if (janela.FindName("OkBtn") is Button ok) Clicar(ok);
        }, "commit: token OK");
        Verificar("commit: token OK devolveu OK", leuToken == true);
        Verificar("commit: o token devolvido é o que foi escrito", token.Token == "ABCD-1234");
        Verificar("commit: «lembrar» ficou desligado por omissão", !token.Remember);

        // 5. Seguro GERAR: com os três campos obrigatórios, produz formulário.
        var seguro = new InsuranceDialog(DiagConfig.Load());
        var gerou = AbrirModal(seguro, janela =>
        {
            (janela.FindName("InsuredNameBox") as TextBox)!.Text = "Ana Silva";
            (janela.FindName("DamageBox") as TextBox)!.Text = "Ecrã partido no canto inferior";
            (janela.FindName("ShopNameBox") as TextBox)!.Text = "Oficina Teste";
            if (janela.FindName("GenerateBtn") is Button gerar) Clicar(gerar);
        }, "commit: seguro GERAR");
        Verificar("commit: seguro GERAR devolveu OK", gerou == true);
        Verificar("commit: o formulário saiu com os dados escritos",
            seguro.Form is { InsuredName: "Ana Silva" } form
            && form.DamageDescription.Length > 5 && form.ShopName == "Oficina Teste");

        // 6. Envio ENVIAR: valida e devolve os dados do cliente.
        var envio = new SendDialog();
        var enviou = AbrirModal(envio, janela =>
        {
            (janela.FindName("ShopCodeBox") as TextBox)!.Text = "ABC123";
            (janela.FindName("NameBox") as TextBox)!.Text = "Ana Silva";
            (janela.FindName("PhoneBox") as TextBox)!.Text = "912345678";
            if (janela.FindName("SendBtn") is Button enviar) Clicar(enviar);
        }, "commit: envio ENVIAR");
        Verificar("commit: envio ENVIAR devolveu OK", enviou == true);
        Verificar("commit: o código e o contacto leram-se",
            envio.ShopCode == "ABC123" && envio.CustomerName == "Ana Silva");
    }

    /// <summary>
    /// O aviso de reinício depois de a atualização ficar encenada. O caminho
    /// real descarrega 100 MB e acaba em `Shutdown()`, que mataria o harness a
    /// meio: os dois passos entram por injecção (`Baixar`, `Terminar`) e o
    /// resto é o handler a sério — o `MessageBox` com o texto do `L10n`,
    /// lido por UIAutomation e respondido por `WM_COMMAND`.
    /// </summary>
    private static void ProvarAvisoDeAtualizacao()
    {
        Console.WriteLine("== aviso de reinício depois da atualização ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        var janela = new MainWindow();
        Abrir(janela);
        try
        {
            if (janela.FindName("UpdateButton") is not Button botao)
            {
                Verificar("atualização: o botão existe", false);
                return;
            }

            var tag = "v9.9.9";
            var baseUrl = $"https://github.com/{UpdateChecker.TrustedOwner}/{UpdateChecker.TrustedRepo}" +
                          $"/releases/download/{tag}/";
            Definir(janela, "_update", new UpdateChecker.UpdateInfo(new Version(9, 9, 9), tag,
                baseUrl + "oficinaos-diag-win-x64.zip", baseUrl + "SHA256SUMS.txt",
                baseUrl + "SHA256SUMS.sig"));
            Definir(janela, "Baixar",
                new Func<UpdateChecker.UpdateInfo, Version, Task<(bool ok, string message)>>(
                    (_, _) => Task.FromResult((true, "encenado: os 100 MB ficaram em casa"))));
            var terminou = 0;
            Definir(janela, "Terminar", new Action(() => terminou++));

            // O botão só aparece quando um `CheckAsync` encontra release nova;
            // aqui a release é a encenada, por isso mostra-se à mão.
            botao.Visibility = Visibility.Visible;
            botao.IsEnabled = true;

            using var vigia = new CancellationTokenSource();
            var rede = VigiaDoMessageBox(vigia.Token);
            var resposta = ResponderMessageBox("ok");
            var clicou = Clicar(botao); // fica dentro do MessageBox até alguém responder
            var corpo = resposta.GetAwaiter().GetResult();
            vigia.Cancel();
            rede.Wait(500);

            Verificar("atualização: o handler correu sem exceção", clicou);
            Verificar("atualização: o aviso de reinício é o do L10n",
                TextoDoAviso(corpo, "log.update.ready"));
            Verificar($"atualização: o aviso vem antes do fecho ({terminou} fecho(s))", terminou == 1);
            Verificar("atualização: o aviso já não está aberto",
                Esperar(() => DialogoDaApp() == IntPtr.Zero, 2000));
            Verificar("atualização: a consola diz que a atualização está encenada",
                (janela.FindName("Console") as TextBox)?.Text.Contains("encenado") == true);
        }
        finally { Fechar(janela); }
    }

    /// <summary>
    /// O caminho que **instala** a atualização, de ponta a ponta: descarregar o
    /// zip, validar a assinatura do `SHA256SUMS`, conferir o checksum e deixar
    /// o script de staging escrito. Até aqui só se provavam as recusas; nunca
    /// se tinha visto um byte a ser descarregado, um hash a bater certo nem um
    /// script a apontar para a pasta da app.
    ///
    /// Sem rede externa e sem a chave do dono: o transporte vai a um servidor
    /// HTTP em loopback (TCP à mão, sem `HttpListener` e sem ACL), a chave de
    /// assinatura é gerada aqui (EC P-256) e injectada em `ChavePublicaDaRelease`,
    /// e o último passo (`powershell.exe` a substituir a app) é substituído por
    /// um leitor do script. As costuras são as de produção por omissão.
    /// </summary>
    private static void ProvarInstalacaoDaAtualizacao(string destino)
    {
        Console.WriteLine("== instalação da atualização: download, checksum e staging ==");
        ThemeManager.Apply(ThemeManager.Terminal);

        const string tag = "v9.9.9";
        var actual = new Version(0, 1, 0);

        // O zip de prova: 2 KB com o exe lá dentro, para o staging ter o que
        // extrair se alguém olhar para ele.
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var arquivo = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var entrada = arquivo.CreateEntry(PackagePaths.AppExeName, CompressionLevel.NoCompression)
                       .Open())
                entrada.Write(new byte[2048]);
            zip = ms.ToArray();
        }

        using var chave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var servidor = new ServidorDeRelease();
        using var http = new HttpClient();

        string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        byte[] Sums(string hash, string versao) =>
            Encoding.UTF8.GetBytes($"version {versao}\n{hash}  oficinaos-diag-win-x64.zip\n");
        string Assinar(byte[] sums) => Convert.ToBase64String(chave.SignData(
            sums, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        var appDir = Path.Combine(Path.GetFullPath(destino), "instalacao", "app");
        Directory.CreateDirectory(appDir);
        servidor.Servir("oficinaos-diag-win-x64.zip", zip);

        var pedidos = new List<string>();
        string Nome(string url) => url.Split('/').Last();
        var sumOriginal = UpdateChecker.ChavePublicaDaRelease;
        var lerSumsOriginal = UpdateChecker.LerSums;
        var lerSigOriginal = UpdateChecker.LerSig;
        var abrirZipOriginal = UpdateChecker.AbrirZip;
        var arrancarOriginal = UpdateChecker.ArrancarAtualizador;
        try
        {
            UpdateChecker.ChavePublicaDaRelease = Convert.ToBase64String(chave.ExportSubjectPublicKeyInfo());
            UpdateChecker.LerSums = url =>
            {
                lock (pedidos) pedidos.Add(url);
                return http.GetByteArrayAsync(servidor.Url(Nome(url)));
            };
            UpdateChecker.LerSig = url =>
            {
                lock (pedidos) pedidos.Add(url);
                return http.GetStringAsync(servidor.Url(Nome(url)));
            };
            UpdateChecker.AbrirZip = url =>
            {
                lock (pedidos) pedidos.Add(url);
                return http.GetStreamAsync(servidor.Url(Nome(url)));
            };
            string? script = null;
            UpdateChecker.ArrancarAtualizador = ps1 =>
            {
                script = File.Exists(ps1) ? File.ReadAllText(ps1) : null;
                return true;
            };

            var baseUrl = $"https://github.com/{UpdateChecker.TrustedOwner}/{UpdateChecker.TrustedRepo}" +
                          $"/releases/download/{tag}/";
            var info = new UpdateChecker.UpdateInfo(new Version(9, 9, 9), tag,
                baseUrl + "oficinaos-diag-win-x64.zip", baseUrl + "SHA256SUMS.txt",
                baseUrl + "SHA256SUMS.sig");

            // O harness corre no dispatcher. Um `GetResult()` aqui em cima
            // bloqueava a thread onde as continuações dos `await` do
            // `DownloadAndStageAsync` iam ser postas — deadlock, e o vigia de
            // 180 s é que o contava. No thread pool não há contexto a capturar.
            (bool ok, string message) Descarregar(Version de) =>
                Task.Run(() => UpdateChecker.DownloadAndStageAsync(info, appDir, de))
                    .GetAwaiter().GetResult();

            // 1. O caminho feliz: SUMS assinado com a chave de teste, checksum
            //    do zip certo. Tem de descarregar, conferir e estagiar.
            var sumsBom = Sums(Hash(zip), "9.9.9");
            servidor.Servir("SHA256SUMS.txt", sumsBom);
            servidor.Servir("SHA256SUMS.sig", Encoding.UTF8.GetBytes(Assinar(sumsBom)));
            var (ok, msg) = Descarregar(actual);
            Verificar($"instalação: o download e o staging correram («{msg}»)",
                ok && msg.Contains("A atualizar", StringComparison.Ordinal));
            Verificar($"instalação: os três pedidos vão às URLs do release ({pedidos.Count})",
                pedidos.Count == 3
                && pedidos.Contains(info.ChecksumUrl) && pedidos.Contains(info.SigUrl)
                && pedidos.Contains(info.ZipUrl));
            Verificar("instalação: o script de staging aponta para a pasta da app",
                script is not null
                && script.Contains(appDir.Replace("'", "''"), StringComparison.Ordinal)
                && script.Contains($"oficinaos-diag-{tag}.zip", StringComparison.Ordinal));
            Verificar($"instalação: a release de prova tem {zip.Length} B (o zip de 2 KB)",
                zip.Length is > 1500 and < 4000);

            // 2. Assinatura inválida: recusa antes de o zip ser pedido.
            var zipAntes = servidor.Pedidos("oficinaos-diag-win-x64.zip");
            servidor.Servir("SHA256SUMS.sig",
                Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))));
            var (okSig, msgSig) = Descarregar(actual);
            Verificar($"instalação: a assinatura inválida é recusada antes do zip («{msgSig}»)",
                !okSig && msgSig.Contains("Assinatura da release inválida", StringComparison.Ordinal)
                && servidor.Pedidos("oficinaos-diag-win-x64.zip") == zipAntes);

            // 3. Replay: SUMS assinado a sério, mas a declarar a versão que já
            //    temos. A assinatura valida; o que recusa é a versão.
            var sumsReplay = Sums(Hash(zip), "9.9.9");
            servidor.Servir("SHA256SUMS.txt", sumsReplay);
            servidor.Servir("SHA256SUMS.sig", Encoding.UTF8.GetBytes(Assinar(sumsReplay)));
            var (okReplay, msgReplay) = Descarregar(new Version(9, 9, 9));
            Verificar($"instalação: o replay de um SUMS assinado é recusado («{msgReplay}»)",
                !okReplay && msgReplay.Contains("não é mais recente", StringComparison.Ordinal)
                && servidor.Pedidos("oficinaos-diag-win-x64.zip") == zipAntes);

            // 4. Checksum: SUMS assinado com o hash de outros bytes. O download
            //    acontece e é o checksum que trava.
            var sumsMau = Sums(Hash(Encoding.UTF8.GetBytes("outro build")), "9.9.9");
            servidor.Servir("SHA256SUMS.txt", sumsMau);
            servidor.Servir("SHA256SUMS.sig", Encoding.UTF8.GetBytes(Assinar(sumsMau)));
            var (okHash, msgHash) = Descarregar(actual);
            Verificar($"instalação: o checksum do zip não confere («{msgHash}»)",
                !okHash && msgHash.Contains("Checksum do download não confere", StringComparison.Ordinal)
                && servidor.Pedidos("oficinaos-diag-win-x64.zip") == zipAntes + 1);
        }
        finally
        {
            UpdateChecker.ChavePublicaDaRelease = sumOriginal;
            UpdateChecker.LerSums = lerSumsOriginal;
            UpdateChecker.LerSig = lerSigOriginal;
            UpdateChecker.AbrirZip = abrirZipOriginal;
            UpdateChecker.ArrancarAtualizador = arrancarOriginal;
            try { File.Delete(Path.Combine(Path.GetTempPath(), $"oficinaos-diag-{tag}.zip")); }
            catch { }
        }
    }

    /// <summary>
    /// Um servidor HTTP em loopback para a prova da instalação: `TcpListener` e
    /// uma resposta `HTTP/1.1` escrita à mão. `HttpListener` exigia reserva de
    /// URL (ACL) no Windows, e um `TcpListener` em `127.0.0.1:0` não: fica numa
    /// porta livre, só acessível desta máquina, e nunca sai para a rede.
    /// </summary>
    private sealed class ServidorDeRelease : IDisposable
    {
        private readonly TcpListener _ouvido = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, byte[]> _ficheiros = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pedidos = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _fim = new();

        public ServidorDeRelease()
        {
            _ouvido.Start();
            Porta = ((IPEndPoint)_ouvido.LocalEndpoint).Port;
            _ = Task.Run(Atender);
        }

        public int Porta { get; }

        public void Servir(string caminho, byte[] corpo)
        {
            lock (_ficheiros) _ficheiros[caminho] = corpo;
        }

        public int Pedidos(string caminho)
        {
            lock (_pedidos) return _pedidos.TryGetValue(caminho, out var n) ? n : 0;
        }

        public string Url(string caminho) => $"http://127.0.0.1:{Porta}/{caminho}";

        private async Task Atender()
        {
            while (!_fim.IsCancellationRequested)
            {
                TcpClient cliente;
                try { cliente = await _ouvido.AcceptTcpClientAsync(_fim.Token); }
                catch { return; }
                _ = Task.Run(() => Responder(cliente));
            }
        }

        private async Task Responder(TcpClient cliente)
        {
            using (cliente)
            {
                try
                {
                    var fluxo = cliente.GetStream();
                    using var leitor = new StreamReader(fluxo, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var linha = await leitor.ReadLineAsync() ?? "";
                    while (!string.IsNullOrEmpty(await leitor.ReadLineAsync())) { }
                    var partes = linha.Split(' ');
                    var caminho = partes.Length >= 2 ? partes[1].TrimStart('/') : "";
                    byte[]? achado = null;
                    lock (_ficheiros) _ficheiros.TryGetValue(caminho, out achado);
                    var corpo = achado ?? Encoding.UTF8.GetBytes("não encontrado");
                    lock (_pedidos)
                        _pedidos[caminho] = (_pedidos.TryGetValue(caminho, out var n) ? n : 0) + 1;
                    var achou = achado is not null;
                    var cabecalho = $"HTTP/1.1 {(achou ? "200 OK" : "404 Not Found")}\r\n" +
                                    $"Content-Length: {corpo.Length}\r\n" +
                                    "Content-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
                    await fluxo.WriteAsync(Encoding.ASCII.GetBytes(cabecalho));
                    await fluxo.WriteAsync(corpo);
                    await fluxo.FlushAsync();
                }
                catch { } // cliente que desiste não interessa a ninguém
            }
        }

        public void Dispose()
        {
            _fim.Cancel();
            _ouvido.Stop();
            _fim.Dispose();
        }
    }

    /// <summary>
    /// O `Baixar` e o `Terminar` por omissão — o caminho que a prova do aviso
    /// de reinício substitui e que, por isso, ninguém exercitava: trocar a
    /// pasta da app ou emudecer o `Shutdown` deixava todos os `[OK]` verdes.
    ///
    /// Sem rede, sem `Shutdown` e sem matar nada: o delegado por omissão tem
    /// de *ser* o método de produção (nome, não lambda anónimo), a pasta tem de
    /// ser a da app, o argumento tem de chegar ao `DownloadAndStageAsync` (duas
    /// recusas que acontecem antes de qualquer HTTP o provam) e o corpo dos
    /// dois métodos é lido em IL — um `TerminarDeProducao` vazio ou a baixar
    /// para outra pasta falha aqui sem nada correr.
    /// </summary>
    /// <summary>
    /// A aplicação do staging a sério: o script que o updater gera é executado
    /// pelo powershell.exe sobre uma instalação de mentira, uma vez com
    /// sucesso e outra com a cópia a falhar a meio. Sem isto ninguém sabia se
    /// uma falha deixava a app utilizável, nem o que o técnico via no arranque
    /// seguinte.
    /// </summary>
    private static void ProvarAplicacaoDoStaging(string destino)
    {
        Console.WriteLine("== aplicação do staging: rollback e aviso no arranque ==");
        static string LerTexto(string caminho) => File.Exists(caminho) ? File.ReadAllText(caminho) : "(falta)";
        var raiz = Path.Combine(Path.GetFullPath(destino), "staging");
        if (Directory.Exists(raiz)) Directory.Delete(raiz, true);

        // O PID já saiu: o Wait-Process do script volta logo.
        using var saiu = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit")
            { CreateNoWindow = true, UseShellExecute = false });
        saiu!.WaitForExit();
        var pid = saiu.Id;

        // Instalação de mentira: exe, DLL, um subdiretório e um ficheiro
        // chamado `zzz`. O zip traz uma pasta `zzz`, e é aí que a cópia
        // rebenta — depois de já ter escrito o exe e a DLL, ou seja, mesmo a
        // meio.
        string Montar(string nome)
        {
            var app = Path.Combine(raiz, nome, "app");
            Directory.CreateDirectory(Path.Combine(app, "Data"));
            File.WriteAllText(Path.Combine(app, PackagePaths.AppExeName), "exe-v2");
            File.WriteAllText(Path.Combine(app, "OficinaDiag.Core.dll"), "core-v2");
            File.WriteAllText(Path.Combine(app, "zzz"), "era-um-ficheiro");
            File.WriteAllText(Path.Combine(app, "Data", "models.json"), "[]");
            return app;
        }

        byte[] Zip(Action<ZipArchive> encher)
        {
            using var ms = new MemoryStream();
            using (var arquivo = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) encher(arquivo);
            return ms.ToArray();
        }

        static void Escrever(ZipArchive a, string nome, string texto)
        {
            using var w = new StreamWriter(a.CreateEntry(nome).Open());
            w.Write(texto);
        }

        (int Codigo, string Registo) Correr(string app, byte[] zip, string tag)
        {
            var dir = Path.Combine(Path.GetDirectoryName(app)!, "execucao-" + tag);
            Directory.CreateDirectory(dir);
            var caminhoZip = Path.Combine(dir, "update.zip");
            File.WriteAllBytes(caminhoZip, zip);
            var log = Path.Combine(dir, "update.log");
            var ps1 = Path.Combine(dir, "update.ps1");
            // Só o relançar fica desligado: um Start-Process sobre um ficheiro
            // de texto chamado .exe não é a app, e a prova não quer processos.
            UpdateChecker.WriteUpdateScript(ps1, UpdateChecker.BuildUpdateScript(tag, app, caminhoZip, log, pid)
                .Replace("Start-Process $exe", "# relançar desligado na prova"));
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var saida = p!.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            var registo = File.Exists(log) ? File.ReadAllText(log) : "";
            return (p.ExitCode, saida + registo);
        }

        var appOk = Montar("sucesso");
        var ok = Correr(appOk, Zip(a =>
        {
            Escrever(a, PackagePaths.AppExeName, "exe-v3");
            Escrever(a, "OficinaDiag.Core.dll", "core-v3");
            // Subdiretório no zip sem colidir com o ficheiro `zzz` do disco.
            Escrever(a, "sub/novo.dll", "novo-v3");
        }), "v9.9.8");
        // As medidas no log: sem elas uma falha desta prova obriga a outra
        // corrida inteira só para saber qual das condições caiu.
        Console.WriteLine($"  staging sucesso: exit={ok.Codigo} exe='{LerTexto(Path.Combine(appOk, PackagePaths.AppExeName))}' "
            + $"dll='{LerTexto(Path.Combine(appOk, "OficinaDiag.Core.dll"))}' sub={Directory.Exists(Path.Combine(appOk, "sub"))} "
            + $"bak={Directory.GetDirectories(Path.GetDirectoryName(appOk)!, "app.bak-*").Length} "
            + $"log='{ok.Registo.Replace(Environment.NewLine, " | ").Trim()}'");
        Verificar("staging: sucesso troca a árvore e limpa o staging",
            ok.Codigo == 0
            && File.ReadAllText(Path.Combine(appOk, PackagePaths.AppExeName)) == "exe-v3"
            && File.ReadAllText(Path.Combine(appOk, "OficinaDiag.Core.dll")) == "core-v3"
            && File.Exists(Path.Combine(appOk, "sub", "novo.dll"))
            && !File.Exists(Path.Combine(appOk, UpdateChecker.UpdateFailureMarkerName))
            && Directory.GetDirectories(Path.GetDirectoryName(appOk)!, "app.bak-*").Length == 0);

        var appFalha = Montar("falha");
        var falha = Correr(appFalha, Zip(a =>
        {
            Escrever(a, PackagePaths.AppExeName, "exe-v9");
            Escrever(a, "OficinaDiag.Core.dll", "core-v9");
            Escrever(a, "zzz/quebra.dll", "x");
        }), "v9.9.9");
        Verificar("staging: falha a meio repõe o build anterior",
            falha.Codigo == 1
            && File.ReadAllText(Path.Combine(appFalha, PackagePaths.AppExeName)) == "exe-v2"
            && File.ReadAllText(Path.Combine(appFalha, "OficinaDiag.Core.dll")) == "core-v2"
            && File.ReadAllText(Path.Combine(appFalha, "zzz")) == "era-um-ficheiro"
            && File.Exists(Path.Combine(appFalha, "Data", "models.json"))
            && falha.Registo.Contains("a restaurar o build anterior"));

        var marcador = Path.Combine(appFalha, UpdateChecker.UpdateFailureMarkerName);
        Console.WriteLine($"  staging falha: exit={falha.Codigo} exe='{LerTexto(Path.Combine(appFalha, PackagePaths.AppExeName))}' "
            + $"marcador={File.Exists(marcador)} texto='{(File.Exists(marcador) ? LerTexto(marcador) : "-")}'");
        var aviso = UpdateChecker.ConsumeUpdateFailure(appFalha);
        Verificar("staging: a falha fica no marcador que o arranque mostra",
            aviso is not null
            && aviso.Contains("v9.9.9")
            && aviso.Contains("falhou")
            && !File.Exists(marcador));
    }

    private static void ProvarDefeitoDaAtualizacao(string destino)
    {
        Console.WriteLine("== atualização: os defeitos por omissão do MainWindow ==");
        var janela = new MainWindow();

        // 1. O campo por omissão aponta para o método de produção. Com um
        //    lambda, o nome seria gerado pelo compilador e isto falhava.
        var baixar = Campo("Baixar", janela) as Delegate;
        Verificar($"produção: o Baixar por omissão é o BaixarDeProducao (é «{baixar?.Method.Name}»)",
            baixar?.Method.Name == "BaixarDeProducao");
        var terminar = Campo("Terminar", janela) as Delegate;
        Verificar($"produção: o Terminar por omissão é o TerminarDeProducao (é «{terminar?.Method.Name}»)",
            terminar?.Method.Name == "TerminarDeProducao");

        // 2. A pasta é a da app — é ela que `BaixarDeProducao` passa adiante.
        var pasta = typeof(MainWindow)
            .GetProperty("PastaDeInstalacao", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) as string;
        Verificar($"produção: a atualização escreve na pasta da app ({pasta})",
            pasta == AppContext.BaseDirectory);

        // 3. O corpo dos dois métodos, em IL: `BaixarDeProducao` tem de usar a
        //    `PastaDeInstalacao` e passar por `BaixarPara`; `TerminarDeProducao`
        //    tem de acabar em `Application.Shutdown`.
        var chamadasDoBaixar = Chamadas("BaixarDeProducao");
        Verificar($"produção: o BaixarDeProducao usa a pasta da app e o BaixarPara (chama: {Juntar(chamadasDoBaixar)})",
            chamadasDoBaixar is not null
            && chamadasDoBaixar.Contains("MainWindow.get_PastaDeInstalacao/0")
            && chamadasDoBaixar.Contains("MainWindow.BaixarPara/3"));
        var chamadasDoTerminar = Chamadas("TerminarDeProducao");
        Verificar($"produção: o TerminarDeProducao chama o Application.Shutdown (chama: {Juntar(chamadasDoTerminar)})",
            chamadasDoTerminar is not null
            && chamadasDoTerminar.Contains("Application.get_Current/0")
            && chamadasDoTerminar.Contains("Application.Shutdown/0"));

        // 4. O argumento chega mesmo ao `DownloadAndStageAsync`: com uma pasta
        //    que não existe, a recusa é a dele (e vem antes da rede).
        const string tag = "v9.9.9";
        var baseUrl = $"https://github.com/{UpdateChecker.TrustedOwner}/{UpdateChecker.TrustedRepo}" +
                      $"/releases/download/{tag}/";
        var confiavel = new UpdateChecker.UpdateInfo(new Version(9, 9, 9), tag,
            baseUrl + "oficinaos-diag-win-x64.zip", baseUrl + "SHA256SUMS.txt",
            baseUrl + "SHA256SUMS.sig");
        var semPasta = Path.Combine(Path.GetFullPath(destino), "pasta-que-nao-existe", "app");
        Verificar($"produção: «{semPasta}» não existe (senão este passo não prova nada)",
            !Directory.Exists(semPasta));
        var (okPasta, msgPasta) = BaixarParaProva(semPasta, confiavel, new Version(0, 1, 0));
        Verificar($"produção: a pasta vai como argumento e é recusada («{msgPasta}»)",
            !okPasta && msgPasta.Contains("sem permissão de escrita", StringComparison.Ordinal));

        // 5. E o delegado por omissão chega ao `DownloadAndStageAsync`: a
        //    recusa por origem é texto de lá, antes de qualquer HTTP.
        var estranho = confiavel with
        {
            ZipUrl = "https://exemplo.invalid/releases/download/v9.9.9/oficinaos-diag-win-x64.zip",
            ChecksumUrl = "https://exemplo.invalid/releases/download/v9.9.9/SHA256SUMS.txt",
            SigUrl = "https://exemplo.invalid/releases/download/v9.9.9/SHA256SUMS.sig",
        };
        var (okOrigem, msgOrigem) = ((Task<(bool ok, string message)>)
                baixar!.DynamicInvoke(estranho, new Version(0, 1, 0))!)
            .GetAwaiter().GetResult();
        Verificar($"produção: o Baixar por omissão chega ao DownloadAndStageAsync («{msgOrigem}»)",
            !okOrigem && msgOrigem.Contains("Origem da atualização não reconhecida", StringComparison.Ordinal));
    }

    /// <summary>
    /// O `Terminar` de produção, a correr a sério e como último passo: pede o
    /// `Shutdown` e é ele que fecha a app do harness, tal como faria na
    /// bancada. O que o torna seguro é não haver nada depois — e um vigia de
    /// 10 s: se o `Terminar` não fechar nada (mutante), o vigia fecha a app,
    /// marca `_fimForcado` e o `Main` reprova, em vez de o job ficar pendurado
    /// até ao limite do GitHub.
    /// </summary>
    private static void ProvarTerminarDeProducao()
    {
        Console.WriteLine("== Terminar de produção: é ele que fecha o harness ==");
        var janela = Application.Current?.MainWindow as MainWindow;
        var terminar = (janela is null ? null : Campo("Terminar", janela)) as Delegate;
        Verificar($"produção: o Terminar por omissão é o TerminarDeProducao (é «{terminar?.Method.Name}»)",
            terminar?.Method.Name == "TerminarDeProducao");
        if (terminar is null) return;

        var vigia = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        vigia.Tick += (_, _) =>
        {
            vigia.Stop();
            _fimForcado = true;
            Application.Current?.Shutdown();
        };

        _terminouPorProducao = true;
        vigia.Start();
        terminar.DynamicInvoke();
    }

    private static object? Campo(string nome, object alvo) =>
        typeof(MainWindow).GetField(nome, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(alvo);

    private static (bool ok, string message) BaixarParaProva(
        string appDir, UpdateChecker.UpdateInfo info, Version current)
    {
        var metodo = typeof(MainWindow).GetMethod("BaixarPara",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((Task<(bool ok, string message)>)metodo.Invoke(null, [appDir, info, current])!)
            .GetAwaiter().GetResult();
    }

    private static string Juntar(List<string>? chamadas) =>
        chamadas is null ? "corpo não reconhecido" : string.Join(", ", chamadas);

    /// <summary>
    /// As chamadas de um método do `MainWindow`, com os nomes resolvidos. Serve
    /// para provar o corpo dos defeitos por omissão sem os correr: um
    /// `TerminarDeProducao` vazio, ou um `BaixarDeProducao` a apontar para outra
    /// pasta, muda as chamadas.
    ///
    /// O leitor é tolerante de propósito: não tem tabela de opcodes. Percorre
    /// os bytes à procura dos `call`/`callvirt` e, quando o token não resolve,
    /// anda **um** byte e tenta outra vez. Um opcode novo do compilador (ou um
    /// prefixo que este leitor não conheça) deixa de rebentar a prova; anda-se
    /// por cima dele e as chamadas verdadeiras continuam a ser encontradas,
    /// porque nunca se salta por cima de um byte sem olhar. Um token que calhe
    /// por acaso num `0x28` de um operando resolve para outro método — só pode
    /// dar um nome a mais na lista, nunca esconder uma chamada que exista.
    /// </summary>
    private static List<string>? Chamadas(string nomeDoMetodo)
    {
        var metodo = typeof(MainWindow).GetMethod(nomeDoMetodo,
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var il = metodo.GetMethodBody()?.GetILAsByteArray();
        if (il is null || il.Length == 0) return null;
        var modulo = metodo.Module;
        var chamadas = new List<string>();
        for (var i = 0; i < il.Length;)
        {
            if ((il[i] == 0x28 || il[i] == 0x6F) && i + 4 < il.Length)
            {
                try
                {
                    var alvo = modulo.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                    // Nome do tipo incluído: `Shutdown` de outra classe também
                    // é `Shutdown`, e a prova quer o `Application`. E um
                    // `ResolveMethod` que devolva um campo não conta.
                    if (alvo is MethodBase m)
                    {
                        // Nome do tipo, nome e número de parâmetros: um
                        // `Shutdown(int)` não é o `Shutdown()` da prova.
                        chamadas.Add($"{m.DeclaringType?.Name}.{m.Name}/{m.GetParameters().Length}");
                        i += 5;
                        continue;
                    }
                }
                catch (Exception)
                {
                    // Token inválido: não é uma chamada, é um operando que
                    // calhou em `0x28`. Anda-se um byte.
                }
            }
            i++;
        }
        return chamadas;
    }

    /// <summary>
    /// Os botões do `MainWindow` que não abrem diálogo nativo nem vão à rede —
    /// SCAN, SCAN+ (BANCADA), COMPARAR e a etiqueta. Eram o resto da barra sem
    /// rede nenhuma: sabia-se que outros clicavam sem rebentar, não que faziam
    /// o que dizem com os dados que têm. A comparação corre com um histórico
    /// emprestado (`MainWindow(ScanHistory)`), senão escrevia no de quem corre
    /// isto e a retenção podava-o.
    /// </summary>
    private static void ProvarBotoes(string destino)
    {
        Console.WriteLine("== botões do MainWindow (sem diálogo nativo nem rede) ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        var janela = new MainWindow();
        Abrir(janela);
        try
        {
            var consola = (TextBox)janela.FindName("Console")!;
            var scan = (Button)janela.FindName("ScanButton")!;
            var bancada = (Button)janela.FindName("BenchButton")!;
            var comparar = (Button)janela.FindName("CompareButton")!;

            // 1. SCAN sem aparelho: avisa, devolve o botão ao serviço e a app
            // não fica «ocupada» (o `_busy` prende o ticker do header).
            consola.Clear();
            Verificar("botões: SCAN sem aparelho correu", Clicar(scan));
            Verificar("botões: SCAN sem aparelho diz que não há aparelho",
                consola.Text.Contains(L10n.T("log.nodev")));
            Verificar("botões: SCAN devolve o botão ao serviço", scan.IsEnabled);
            Verificar("botões: SCAN não deixa a app ocupada",
                typeof(MainWindow).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(janela) is false);

            // 2. BANCADA (SCAN+) sem aparelho: nasce desligado, mas o handler
            // tem de dizer o que fazer e não prender a janela.
            bancada.IsEnabled = true;
            consola.Clear();
            Verificar("botões: BANCADA sem aparelho correu", Clicar(bancada));
            Verificar("botões: BANCADA sem aparelho manda ligar por USB",
                consola.Text.Contains(L10n.T("log.nodev.usb")));

            // 3. COMPARAR sem serial: não há como casar scans de aparelhos.
            Definir(janela, "_report", new DeviceReport { Platform = "android" });
            comparar.IsEnabled = true;
            consola.Clear();
            Verificar("botões: COMPARAR sem serial correu", Clicar(comparar));
            Verificar("botões: COMPARAR sem serial diz porquê",
                consola.Text.Contains(L10n.T("log.cmp.noserial")));
        }
        finally { Fechar(janela); }

        // 4. COMPARAR com serial mas sem scan anterior do mesmo aparelho.
        var vazio = ComHistorico(new ScanHistory(
            Path.Combine(destino, $"historico-vazio-{Environment.ProcessId}.jsonl")));
        Abrir(vazio);
        try
        {
            Definir(vazio, "_report", RelatorioAtual());
            var consola = (TextBox)vazio.FindName("Console")!;
            ((Button)vazio.FindName("CompareButton")!).IsEnabled = true;
            consola.Clear();
            // Fechada também quando não devia abrir: se uma regressão a abrir
            // com o histórico vazio, isto reprova em segundos em vez de deixar
            // o `ShowDialog` pendurado até ao vigia de 3 minutos.
            var semAnterior = Comparar(vazio);
            Verificar($"botões: COMPARAR sem scan anterior diz que não há nenhum (abriu: {semAnterior.Abriu})",
                consola.Text.Contains(L10n.T("log.cmp.none")) && !semAnterior.Abriu);
        }
        finally { Fechar(vazio); }

        // 5. COMPARAR com um scan anterior: abre a janela e o diff tem de
        // contar a história certa (78% warn → 94% pass, e o que só existe agora).
        var caminho = Path.Combine(destino, $"historico-{Environment.ProcessId}.jsonl");
        try { File.Delete(caminho); } catch { }
        var historico = new ScanHistory(caminho);
        historico.Add(RelatorioAnterior());

        var comHistorico = ComHistorico(historico);
        Abrir(comHistorico);
        try
        {
            Definir(comHistorico, "_report", RelatorioAtual());
            ((Button)comHistorico.FindName("CompareButton")!).IsEnabled = true;

            var r = Comparar(comHistorico);
            Verificar($"botões: COMPARAR abriu a janela com o scan anterior ({r.Scans} scan(s))",
                r.Clicou && r.Abriu && r.Scans == 1);
            Verificar($"botões: COMPARAR lista as seis chaves do diff ({r.Linhas})", r.Linhas == 6);
            Verificar($"botões: COMPARAR mostra 78% [warn] → 94% [pass] ('{r.Antes}' → '{r.Agora}')",
                r.Antes == "78% [warn]" && r.Agora == "94% [pass]");
            Verificar($"botões: COMPARAR marca o que não existia antes ('{r.SemAntes}')",
                r.SemAntes == "—");
        }
        finally { Fechar(comHistorico); }

        // 6. ETIQUETA: o clique abre o `PrintDialog` (nativo), mas o que a
        // etiqueta leva — modelo e serial — constrói-se sem impressora.
        var etiqueta = OficinaDiag.Reports.LabelPrinter.BuildVisual(RelatorioAtual());
        var moldura = ForaDoEcra(new Window { Content = etiqueta, Width = 320, Height = 200 });
        moldura.Show();
        try
        {
            moldura.UpdateLayout();
            Respirar();
            var textos = Descendentes<TextBlock>(etiqueta).Select(t => t.Text).ToList();
            Verificar($"botões: a etiqueta leva o serial do relatório ({textos.Count} texto(s))",
                textos.Any(t => t.Contains("3B1F0A2C9D")));
            Verificar("botões: a etiqueta leva o modelo",
                textos.Any(t => t.Contains("Pixel 8")));
        }
        finally { Fechar(moldura); }
    }

    /// <summary>
    /// Clica em COMPARAR e, se a janela abrir (é modal), lê-a e fecha-a de
    /// dentro do dispatcher. Nunca deixa o `ShowDialog` pendurado: mesmo quando
    /// a janela não devia abrir, fecha-a.
    /// </summary>
    private static Comparacao Comparar(Window janela)
    {
        var r = new Comparacao();
        var relogio = new DispatcherTimer(DispatcherPriority.Background, janela.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        relogio.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<CompareWindow>().FirstOrDefault() is not { } cmp)
                return;
            relogio.Stop();
            r.Abriu = true;
            cmp.UpdateLayout();
            r.Scans = ((ListBox)cmp.FindName("ScanList")).Items.Count;
            var lista = (((ItemsControl)cmp.FindName("DiffList")).ItemsSource
                as IEnumerable<DiffRow>)?.ToList() ?? [];
            r.Linhas = lista.Count;
            r.Antes = lista.FirstOrDefault(x => x.Key == "battery.health")?.Before;
            r.Agora = lista.FirstOrDefault(x => x.Key == "battery.health")?.Now;
            r.SemAntes = lista.FirstOrDefault(x => x.Key == "screen.deadpixels")?.Before;
            Fechar(cmp);
        };
        relogio.Start();
        try
        {
            r.Clicou = Clicar((Button)janela.FindName("CompareButton")!);
            // Caso raro: a janela pode abrir depois de o clique voltar.
            for (var i = 0; i < 4 && !r.Abriu; i++) Pausa(50);
        }
        finally { relogio.Stop(); }
        return r;
    }

    /// <summary>O que a janela de comparação mostrou (ou não).</summary>
    private sealed class Comparacao
    {
        public bool Clicou;
        public bool Abriu;
        public int Scans = -1;
        public int Linhas = -1;
        public string? Antes;
        public string? Agora;
        public string? SemAntes;
    }

    /// <summary>O gesto que atribui a falha ao botão (o preview do rato não chega).</summary>
    private static void LembrarGesto(MainWindow janela, object alvo) =>
        typeof(MainWindow).GetMethod("LembrarAcao", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(janela, [alvo]);

    /// <summary>Uma `MainWindow` com um histórico emprestado (construtor interno).</summary>
    private static MainWindow ComHistorico(ScanHistory historico) =>
        (MainWindow)Activator.CreateInstance(typeof(MainWindow),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [historico], null)!;

    /// <summary>Põe um campo (privado ou interno) — a mesma via do resto do harness.</summary>
    private static void Definir(object alvo, string campo, object? valor) =>
        alvo.GetType().GetField(campo, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(alvo, valor);

    /// <summary>
    /// A barra de scroll do tema Win95: setas visíveis e zonas de página que se
    /// distinguem do corpo. Ficou «pendente de decisão de design» desde o T-11
    /// e não precisa de hardware nenhum — precisa de um template que a desenhe
    /// e de alguém que olhe para ela.
    /// </summary>
    private static void ProvarBarraDeScroll(string destino)
    {
        Console.WriteLine("== barra de scroll do Win95 ==");
        ThemeManager.Apply(ThemeManager.Win95);
        L10n.Set("pt");

        var janela = ForaDoEcra(new SettingsDialog(DiagConfig.Load()));
        janela.Width = 420;
        janela.Height = 640;
        janela.Show();
        try
        {
            if (Primeiro<ScrollViewer>(janela) is not { } sv)
            {
                Verificar("setas: as Opções têm um ScrollViewer", false);
                return;
            }
            sv.MaxHeight = 200; // sem isto a barra não aparece: o conteúdo cabe
            janela.UpdateLayout();
            Respirar();

            var barra = Descendentes<ScrollBar>(sv)
                .FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            if (barra is null)
            {
                Verificar("setas: a barra vertical apareceu", false);
                return;
            }

            var botoes = Descendentes<RepeatButton>(barra).ToList();
            var setas = botoes.Where(b => ReferenceEquals(b.Command, ScrollBar.LineUpCommand)
                                       || ReferenceEquals(b.Command, ScrollBar.LineDownCommand)).ToList();
            Verificar($"setas: uma seta para cima e outra para baixo ({setas.Count})", setas.Count == 2);
            Verificar("setas: as duas setas têm glifo",
                setas.Count == 2 && setas.All(b => Descendentes<System.Windows.Shapes.Path>(b).Any(p =>
                    p.Data is { } d && d.Bounds.Width > 1 && d.Bounds.Height > 1)));

            var zonas = botoes.Where(b => ReferenceEquals(b.Command, ScrollBar.PageUpCommand)
                                       || ReferenceEquals(b.Command, ScrollBar.PageDownCommand)).ToList();
            var corTrack = Cor(barra.Background);
            var corZona = zonas.Count == 2
                ? Cor(Descendentes<Border>(zonas[0]).FirstOrDefault()?.Background)
                : null;
            Verificar($"setas: a zona de página distingue-se do track ({corZona} vs {corTrack})",
                zonas.Count == 2 && corZona is not null && corTrack is not null && corZona != corTrack);
            Verificar("setas: a zona de página é o cinza claro do 95",
                corZona == (Color)ColorConverter.ConvertFromString("#DFDFDF"));
            Verificar("setas: o polegar existe",
                Descendentes<Thumb>(barra).Any(t => t.IsVisible && t.ActualHeight > 1));

            // A captura é da janela inteira e não da barra isolada: o
            // `RenderTargetBitmap` de um elemento só da barra saiu cinzento
            // (os filhos não foram pintados) e uma «prova visual» vazia é pior
            // que nenhuma. E mede-se: o glifo é escuro e tem de estar no PNG.
            var raiz = (FrameworkElement)VisualTreeHelper.GetChild(janela, 0);
            janela.UpdateLayout();
            Respirar();
            var png = Path.Combine(destino, "scrollbar-win95.png");
            var (w, h) = Gravar(raiz, png);
            Console.WriteLine($"  scrollbar-win95.png {w}x{h}");
            var area = barra.TransformToAncestor(raiz).TransformBounds(
                new Rect(0, 0, barra.ActualWidth, barra.ActualHeight));
            var escuros = ContarEscuros(png, new Int32Rect(
                (int)Math.Floor(area.X), (int)Math.Floor(area.Y),
                (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height)));
            Verificar($"setas: a captura mostra os glifos ({escuros} px escuros na barra)",
                escuros >= 20);
        }
        finally
        {
            Fechar(janela);
            ThemeManager.Apply(ThemeManager.Terminal);
        }
    }

    /// <summary>
    /// Quantos pixels do PNG são escuros dentro do retângulo indicado. Serve
    /// para a captura ser prova e não decoração: um glifo preto num fundo
    /// `#C0C0C0` conta, um render vazio conta zero.
    /// </summary>
    private static int ContarEscuros(string png, Int32Rect area, int limite = 96)
    {
        using var fs = File.OpenRead(png);
        var decodificador = new PngBitmapDecoder(fs,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bgra = new FormatConvertedBitmap(decodificador.Frames[0], PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        var escuros = 0;
        var fimY = Math.Min(bgra.PixelHeight, area.Y + area.Height);
        var fimX = Math.Min(bgra.PixelWidth, area.X + area.Width);
        for (var y = Math.Max(0, area.Y); y < fimY; y++)
        for (var x = Math.Max(0, area.X); x < fimX; x++)
        {
            var i = y * stride + x * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] < limite * 3) escuros++;
        }
        return escuros;
    }

    /// <summary>As três vias do `App` que transformam uma exceção numa linha de log.</summary>
    private static readonly string[] ViasEngolidas = ["! UI:", "! background:", "! fatal:"];

    /// <summary>
    /// Escreve a linha da via, como o `App` faz — e na consola, que é o que o CI
    /// mostra sem precisar do artefacto.
    /// </summary>
    private static void Anotar(string onde, Exception ex)
    {
        AppLog.Exception(onde, ex);
        Console.WriteLine($"  ! {onde}: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// O log deste run tem alguma linha de exceção engolida, por qualquer das
    /// três vias? Uma só já reprova — foi uma destas que escondeu o checklist
    /// morto durante dois lotes. A contagem vai por via, para o relatório dizer
    /// qual delas disparou.
    /// </summary>
    private static void VerificarUiSemExcecoes(string log)
    {
        string[] linhas;
        try
        {
            linhas = File.Exists(log)
                ? File.ReadAllLines(log).Where(l => ViasEngolidas.Any(l.Contains)).ToArray()
                : [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  log da UI ilegível: {ex.GetType().Name}");
            linhas = [$"(ilegível) {ex.Message}"];
        }
        foreach (var linha in linhas.Take(10)) Console.WriteLine($"    {linha}");
        var porVia = string.Join(", ", ViasEngolidas.Select(v =>
            $"{v.Replace("! ", "").TrimEnd(':')} {linhas.Count(l => l.Contains(v))}"));
        Verificar($"log sem exceções engolidas ({linhas.Length} linha(s); {porVia})",
            linhas.Length == 0);
    }

    /// <summary>O aviso mostra o texto que o `L10n` manda para esta chave?</summary>
    private static bool TextoDoAviso(string? corpo, string chave)
    {
        var esperado = SemEspacos(L10n.T(chave));
        if (esperado.Length == 0) return false;
        return SemEspacos(corpo ?? "").Contains(esperado[..Math.Min(20, esperado.Length)]);
    }

    /// <summary>
    /// Os botões dos cinco diálogos, clicados a sério. Foi um handler que
    /// escondeu o checklist morto: a exceção ficava na linha «! UI:» do log e a
    /// app seguia, com a marca por pôr. Todos os cliques passam por `Clicar`, e
    /// uma exceção em qualquer um deles reprova o run no fim.
    ///
    /// Os botões que fecham a janela (`DialogResult`) ou abrem um diálogo
    /// nativo ficam de fora: aqui não há modal nem ninguém para os responder —
    /// clicá-los provava o harness, não a app.
    /// </summary>
    private static void ProvarCliques()
    {
        Console.WriteLine("== cliques nos botões dos cinco diálogos ==");
        ThemeManager.Apply(ThemeManager.Terminal);
        L10n.Set("pt");

        // 1. Checklist: os três botões de uma linha e a alternância de cada um.
        // Era aqui que o `SetState` rebentava com `KeyNotFoundException` e a
        // marca nunca pegava — por isso o clique tem de pegar, de facto.
        Marcar("clique: checklist");
        var relatorio = RelatorioAtual();
        var checklist = new ChecklistDialog(relatorio);
        Abrir(checklist);
        try
        {
            var itens = checklist.FindName("ItemsPanel") as DependencyObject;
            var marcas = itens is null
                ? Array.Empty<Button>()
                : Descendentes<Button>(itens).ToArray();
            if (marcas.Length < 6)
            {
                Verificar("clique: o checklist tem ok/defeito/n/t", false);
            }
            else
            {
                Verificar("clique: checklist 'ok' marca o item",
                    Clicar(marcas[0]) && marcas[0].FontWeight == FontWeights.Bold);
                Verificar("clique: checklist 'defeito' troca a marca",
                    Clicar(marcas[1]) && marcas[1].FontWeight == FontWeights.Bold
                    && marcas[0].FontWeight == FontWeights.Normal);
                Verificar("clique: checklist 'n/t' troca a marca",
                    Clicar(marcas[2]) && marcas[2].FontWeight == FontWeights.Bold
                    && marcas[1].FontWeight == FontWeights.Normal);
                Verificar("checklist: premir outra vez desmarca",
                    Clicar(marcas[2]) && marcas[2].FontWeight == FontWeights.Normal);
                Verificar("checklist: marcar outro item não desmarca o primeiro",
                    Clicar(marcas[0]) && Clicar(marcas[5])
                    && marcas[0].FontWeight == FontWeights.Bold
                    && marcas[5].FontWeight == FontWeights.Bold);
            }
        }
        finally { Fechar(checklist); }

        // 2. Opções: idioma e tema aplicam-se em preview assim que se escolhe.
        Marcar("clique: opções");
        var opcoes = new SettingsDialog(DiagConfig.Load());
        Abrir(opcoes);
        try
        {
            var cabecalho = opcoes.FindName("HeaderText") as TextBlock;
            var antes = cabecalho?.Text;
            if (opcoes.FindName("LangEn") is RadioButton ingles)
                Verificar("clique: opções 'English' muda o idioma",
                    Clicar(ingles) && L10n.Current == "en" && cabecalho?.Text != antes);
            else Verificar("clique: opções tem o botão 'English'", false);

            if (opcoes.FindName("LangPt") is RadioButton portugues)
                Verificar("clique: opções 'Português' volta ao PT",
                    Clicar(portugues) && L10n.Current == "pt");
            else Verificar("clique: opções tem o botão 'Português'", false);

            if (opcoes.FindName("ThemeFluent") is RadioButton fluente)
                Verificar("clique: opções muda o tema a quente",
                    Clicar(fluente) && ThemeManager.Current == ThemeManager.Fluent);
            else Verificar("clique: opções tem o botão do tema Fluent", false);
        }
        finally
        {
            Fechar(opcoes);
            L10n.Set("pt");
            ThemeManager.Apply(ThemeManager.Terminal);
        }

        // 3–5. Os três que validam campos: o botão abre um aviso (MessageBox
        // nativo) e o diálogo fica aberto. O aviso prova que o handler correu
        // até ao fim sem exceção; o `Clicar` é que garante que a exceção, se
        // houver, aparece como «! UI:».
        var validacoes = new (string Nome, Func<Window> Criar, string Botao, string Chave)[]
        {
            ("seguro", () => new InsuranceDialog(DiagConfig.Load()), "GenerateBtn", "ins.err.insured"),
            ("envio", () => new SendDialog(), "SendBtn", "send.err.code"),
            ("token", () => new TokenDialog(), "OkBtn", "dlg.ai.token.empty"),
        };
        foreach (var (nome, criar, botao, chave) in validacoes)
        {
            Marcar($"clique: {nome} (abrir)");
            var dialogo = criar();
            Abrir(dialogo);
            try
            {
                if (dialogo.FindName(botao) is not Button alvo)
                {
                    Verificar($"clique: {nome} tem o botão {botao}", false);
                    continue;
                }

                using var vigia = new CancellationTokenSource();
                var rede = VigiaDoMessageBox(vigia.Token);
                var resposta = ResponderMessageBox("ok");
                Marcar($"clique: {nome} ({botao})");
                var clicou = Clicar(alvo); // fica dentro do MessageBox até alguém responder
                var corpo = resposta.GetAwaiter().GetResult();
                vigia.Cancel();
                rede.Wait(500);

                Verificar($"clique: {nome} correu o handler sem exceção", clicou);
                Verificar($"clique: {nome} avisou «{chave}» em vez de seguir",
                    TextoDoAviso(corpo, chave));
                Verificar($"clique: {nome} continua aberto depois do aviso", dialogo.IsLoaded);
            }
            finally { Fechar(dialogo); }
        }

        ThemeManager.Apply(ThemeManager.Terminal);
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

        var janela = ForaDoEcra(new BenchWindow("Pixel 8 · android",
            () => Task.FromResult<LiveTelemetry?>(null)));
        janela.Width = 920;
        janela.Height = 620;
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
        var janela = ForaDoEcra(ComRelatorio(new MainWindow(), relatorio));
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
            var dialogo = ForaDoEcra(criar());
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
        var janela = ForaDoEcra(new SettingsDialog(DiagConfig.Load()));
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
        if (_provas is not null)
        {
            try { File.AppendAllText(_provas, $"[{(ok ? "OK" : "FALHA")}] {nome}{Environment.NewLine}"); }
            catch { } // o registo nunca pode derrubar a prova
        }
        if (!ok) _falhas++;
    }

    /// <summary>
    /// Grava um elemento em PNG sobre o fundo do tema. Único sítio que escreve
    /// imagens — havia dois e só um pintava o fundo opaco.
    /// </summary>
    private static (int W, int H) Gravar(
        FrameworkElement raiz, string ficheiro, double? esperadoLargura = null,
        double? esperadoAltura = null, bool fundoDoTema = false)
    {
        var w = Math.Max(1, (int)Math.Ceiling(raiz.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(raiz.ActualHeight));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Fundo(w, h));
        bmp.Render(raiz);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        // Bloco fechado, e não `using var`: a verificação relê o ficheiro, e
        // com o `using var` ele ainda estava aberto para escrita (IOException
        // em todas as capturas, run 43901656490).
        using (var fs = File.Create(ficheiro))
            enc.Save(fs);

        // Todas as capturas passam por aqui, por isso é aqui que se verifica
        // que não estão vazias: um `RenderTargetBitmap` de um elemento sem
        // filhos pintados dá um PNG de uma só cor, que ninguém abre e que
        // «prova» tudo — foi o que aconteceu à barra do Win95 em 2026-11-15.
        ConferirCaptura(ficheiro, bmp.PixelWidth, bmp.PixelHeight, esperadoLargura, esperadoAltura,
            fundoDoTema);
        return (bmp.PixelWidth, bmp.PixelHeight);
    }

    /// <summary>
    /// Cada PNG verifica-se a si mesmo, e não só a barra do Win95: o que
    /// distingue uma janela desenhada de um retângulo de fundo é o tamanho
    /// pedido, a variedade de cores e a cor do tema lá dentro. Um
    /// `RenderTargetBitmap` sem os filhos pintados dá um PNG de uma cor —
    /// passa por captura e não prova nada.
    ///
    /// Devolve as razões medidas no log (dimensão, cores, variância, peso do
    /// fundo do tema), para uma falha dizer o que viu e não só que falhou.
    /// </summary>
    private static void ConferirCaptura(
        string ficheiro, int w, int h, double? esperadoLargura, double? esperadoAltura, bool fundoDoTema)
    {
        var nome = Path.GetFileName(ficheiro);
        try
        {
            using var fs = File.OpenRead(ficheiro);
            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var bgra = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            var total = bgra.PixelWidth * bgra.PixelHeight;

            var cores = new HashSet<int>();
            var contagem = new Dictionary<int, int>();
            double soma = 0, somaQuadrados = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var rgb = b | (g << 8) | (r << 16);
                cores.Add(rgb);
                contagem[rgb] = (contagem.TryGetValue(rgb, out var n) ? n : 0) + 1;
                var luz = 0.299 * r + 0.587 * g + 0.114 * b;
                soma += luz;
                somaQuadrados += luz * luz;
            }

            var dominante = contagem.Count == 0 ? 0.0 : (double)contagem.Values.Max() / total;
            var media = total == 0 ? 0 : soma / total;
            // Variância da luminância numa escala 0–255: um PNG uniforme dá 0.
            var variancia = total == 0 ? 0 : Math.Max(0, somaQuadrados / total - media * media);

            // A cor de fundo do tema tem de estar lá: é o que o `Fundo(w, h)`
            // pinta por baixo, e um render que não pintou nada tem-na a 100%.
            var fundo = (Application.Current.TryFindResource("BgBrush") as SolidColorBrush)?.Color
                        ?? Colors.Black;
            var corFundo = fundo.B | (fundo.G << 8) | (fundo.R << 16);
            var pesoFundo = contagem.TryGetValue(corFundo, out var daCor) ? (double)daCor / total : 0.0;
            var corDoTema = !fundoDoTema || pesoFundo >= 0.02;

            // O que sai é a **área cliente**: sem moldura nem barra de título
            // (~16 px a menos), a janela encolhe-se à work area, e os diálogos
            // com `SizeToContent` ficam mais altos do que o `Height` pedido.
            // Exigir igualdade ao pedido reprovou as 59 (run 43903982912) por
            // uma expectativa inventada; o que se exige é a largura pedida (já
            // limitada pela work area, menos a moldura) e que não seja uma
            // tira: um render cortado ou de 1 px não passa, e a altura só tem
            // chão.
            var dimensao = esperadoLargura is not { } el
                           || (w >= Math.Min(el, SystemParameters.WorkArea.Width) - 40
                               && w <= Math.Min(el, SystemParameters.WorkArea.Width) + 2
                               && h >= 120);
            var esperado = esperadoLargura is { } l && esperadoAltura is { } a
                ? $" (pedido {l:0}x{a:0})"
                : "";

            Verificar($"captura {nome}: {w}x{h}{esperado}, {cores.Count} cores, " +
                      $"variância {variancia:0.0}, fundo do tema {pesoFundo:P0}, {dominante:P0} na dominante",
                dimensao && w >= 40 && h >= 40 && cores.Count >= 6
                && variancia >= 8 && corDoTema && dominante <= 0.98);
        }
        catch (Exception ex)
        {
            Verificar($"captura {nome}: legível ({ex.GetType().Name})", false);
        }
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
        Action<Window>? ajustar = null, double? workArea = null, string[]? acoes = null,
        bool fundoDoTema = true)
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
            // O tamanho pedido é o esperado; com work area simulada, o `clamp`
            // da app (`Min`) pode encolher a janela, e esse é o esperado.
            var (w, h) = Gravar(raiz, ficheiro, largura,
                workArea is { } teto ? Math.Min(altura, teto) : altura,
                fundoDoTema: fundoDoTema);
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
