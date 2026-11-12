using System.Windows;
using System.Windows.Media;
using OficinaDiag.Devices;

namespace OficinaDiag;

/// <summary>
/// Gráfico de osciloscópio desenhado à mão — duas séries fósforo:
/// corrente (mA, verde vivo) e tensão (mV, verde pálido).
/// </summary>
public sealed class ScopeView : FrameworkElement
{
    // Pens congeladas e reutilizadas: criar ~18 Pen por frame (60 fps numa
    // sessão SCAN+) era lixo constante na gen0. A cache é invalidada quando
    // o tema muda, porque aí as brushes resolvidas são outras.
    private static string? _penTheme;
    private static Pen? _gridPen;
    private static Pen? _ampPen;
    private static Pen? _voltPen;
    private static Pen? _tempPen;

    private static Pen Make(string key, double width)
    {
        var pen = new Pen(Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray, width);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static void EnsurePens()
    {
        var theme = ThemeManager.Current;
        if (_penTheme == theme && _gridPen is not null) return;
        _penTheme = theme;
        _gridPen = Make("LineBrush", 1);
        _ampPen = Make("FgBrush", 1.6);
        _voltPen = Make("DimBrush", 1);
        _tempPen = Make("WarnBrush", 1.2);
    }

    private readonly List<PowerSample> _samples = new();
    private double _windowSec = 90;

    public void AddSample(PowerSample s)
    {
        _samples.Add(s);
        InvalidateVisual();
    }

    public void Reset(double windowSec)
    {
        _samples.Clear();
        _windowSec = windowSec;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 10 || h < 10) return;

        EnsurePens();
        var grid = _gridPen!;
        for (var i = 1; i < 6; i++)
            dc.DrawLine(grid, new Point(0, h * i / 6), new Point(w, h * i / 6));
        for (var i = 1; i < 10; i++)
            dc.DrawLine(grid, new Point(w * i / 10, 0), new Point(w * i / 10, h));
        dc.DrawRectangle(null, grid, new Rect(0, 0, w, h));

        if (_samples.Count < 2) return;
        var t0 = _samples[0].T;
        var t1 = Math.Max(_samples[^1].T, t0 + _windowSec);
        double X(double t) => (t - t0) / (t1 - t0) * w;

        // bandas fixas — instrumento, não auto-escala que dança
        DrawSeries(dc, h, X, s => s.Milliamps, _ampPen!, 0, 3000);
        DrawSeries(dc, h, X, s => s.Volts * 1000, _voltPen!, 3200, 4600);
        DrawSeries(dc, h, X, s => s.TempC, _tempPen!, 20, 50);
    }

    private void DrawSeries(DrawingContext dc, double h, Func<double, double> x,
        Func<PowerSample, double?> pick, Pen pen, double lo, double hi)
    {
        var pts = _samples.Select(s => (x(s.T), pick(s))).Where(p => p.Item2 is { }).ToList();
        if (pts.Count < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(pts[0].Item1, Y(pts[0].Item2!.Value)), false, false);
            foreach (var (px, py) in pts.Skip(1))
                ctx.LineTo(new Point(px, Y(py!.Value)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);

        double Y(double v) => h - 6 - Math.Clamp((v - lo) / (hi - lo), 0, 1) * (h - 12);
    }
}
