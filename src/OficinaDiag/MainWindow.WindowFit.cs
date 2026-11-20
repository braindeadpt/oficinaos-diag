using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OficinaDiag.Ui;

namespace OficinaDiag;

// A janela dentro do ecrã: DPI e multi-monitor.
public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private IntPtr _monitorAtual;
    private bool _aAjustar;
    private DpiChangedEventHandler? _assinaturaDpi;

    /// <summary>A prova do harness lê isto: a assinatura do `DpiChanged` existe?</summary>
    internal bool DpiLigado => _assinaturaDpi is not null;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNativo { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNativo rcMonitor;
        public RectNativo rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// Área útil do monitor onde a janela está, em DIP. A conversão usa o DPI
    /// da janela: a 150%, 1920 px são 1280 DIP — e é por isso que a janela
    /// deixa de caber num portátil.
    /// </summary>
    private AreaEcra? AreaDoMonitor()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return null;
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return null;
            var escala = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (!(escala > 0)) escala = 1.0;
            var dpi = 96 * escala;
            var r = info.rcWork;
            return new AreaEcra(
                WindowFit.ParaDip(r.Left, dpi),
                WindowFit.ParaDip(r.Top, dpi),
                WindowFit.ParaDip(r.Right - r.Left, dpi),
                WindowFit.ParaDip(r.Bottom - r.Top, dpi));
        }
        catch { return null; }
    }

    private AreaEcra AreaCorrente()
    {
        if (WindowFit.AreaForcada is { } forcada) return forcada;
        if (AreaDoMonitor() is { } medida) return medida;
        // Último recurso: a work area do WPF (monitor principal), como antes.
        var wa = SystemParameters.WorkArea;
        return new AreaEcra(wa.Left, wa.Top, wa.Width, wa.Height);
    }

    /// <summary>
    /// Aplica a área útil à janela. É o único sítio que o faz: o arranque, o
    /// `DpiChanged` e a mudança de monitor passam todos por aqui.
    /// <paramref name="recentrar"/> falso só limita e encosta para dentro —
    /// a janela que o técnico arrastou fica onde ele a deixou.
    /// </summary>
    internal void AjustarAoEcra(bool recentrar = true)
    {
        if (_aAjustar) return;
        _aAjustar = true;
        try
        {
            var caixa = WindowFit.Calcular(
                AreaCorrente(), Pedida(Width, MinWidth), Pedida(Height, MinHeight),
                recentrar, Left, Top);
            MaxWidth = caixa.MaxWidth;
            MaxHeight = caixa.MaxHeight;
            if (double.IsFinite(caixa.Width)) Width = caixa.Width;
            if (double.IsFinite(caixa.Height)) Height = caixa.Height;
            // Posicionar depois de encolher: centrar com o tamanho antigo
            // empurrava a janela para fora do ecrã pequeno.
            if (caixa.Left is { } left) Left = left;
            if (caixa.Top is { } top) Top = top;
        }
        finally { _aAjustar = false; }
    }

    /// <summary>
    /// Mudança de escala a meio da sessão (125% → 150%, ou arrastar entre
    /// ecrãs de escalões diferentes). Não recentra: o técnico já escolheu onde
    /// quer a janela.
    /// </summary>
    private void AoMudarEscala(object? sender, DpiChangedEventArgs e) =>
        AjustarAoEcra(recentrar: false);

    /// <summary>`Width` pode estar a NaN antes do primeiro layout.</summary>
    private static double Pedida(double valor, double minimo) =>
        double.IsFinite(valor) && valor > 0 ? valor : minimo;

    /// <summary>
    /// Mudar de monitor sem mudar de escalão não levanta `DpiChanged` — a
    /// janela ficava presa à área do ecrã anterior.
    /// </summary>
    private void TalvezMudouDeMonitor()
    {
        if (_aAjustar || WindowFit.AreaForcada is not null) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == _monitorAtual) return;
            _monitorAtual = monitor;
            // Só limitar e encostar: o utilizador arrastou-a para ali de propósito.
            AjustarAoEcra(recentrar: false);
        }
        catch { /* sem interop, fica a área anterior */ }
    }
}
