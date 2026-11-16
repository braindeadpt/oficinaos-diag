namespace OficinaDiag.Ui;

/// <summary>
/// Área útil de um monitor, em DIP (96 = 100%). Um monitor de 1920×1080 a
/// 150% tem 1280×720 DIP — é essa conta que faz a janela deixar de caber.
/// </summary>
public readonly record struct AreaEcra(double Left, double Top, double Width, double Height)
{
    /// <summary>Área que serve para posicionar: nada de zeros nem de NaN.</summary>
    public bool Valida =>
        Width > 0 && Height > 0
        && double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height);
}

/// <summary>O que a janela passa a ter. <c>Left</c>/<c>Top</c> nulos = não mexer na posição.</summary>
public readonly record struct CaixaJanela(
    double MaxWidth, double MaxHeight, double Width, double Height, double? Left, double? Top);

/// <summary>
/// A conta da janela dentro da área útil, sem WPF e sem P/Invoke: o
/// <c>MainWindow</c> só aplica. Assim a regra é testável em qualquer sistema e
/// o harness injecta a área para provar 100/125/150% sem mudar a escala do
/// Windows.
/// </summary>
public static class WindowFit
{
    /// <summary>Nunca maior que a área, e centrada dentro dela.</summary>
    public static CaixaJanela Calcular(AreaEcra? area, double larguraPedida, double alturaPedida)
    {
        // Sem área fiável não se corta nada: um P/Invoke que falhou não pode
        // encolher a janela a zero.
        if (area is not { } a || !a.Valida)
            return new(double.PositiveInfinity, double.PositiveInfinity,
                larguraPedida, alturaPedida, null, null);

        var largura = Math.Min(Math.Max(larguraPedida, 0), a.Width);
        var altura = Math.Min(Math.Max(alturaPedida, 0), a.Height);
        return new(
            a.Width, a.Height,
            largura, altura,
            a.Left + Math.Max(0, (a.Width - largura) / 2),
            a.Top + Math.Max(0, (a.Height - altura) / 2));
    }

    /// <summary>
    /// Pixels físicos → DIP. DPI inválido (0 ou negativo) cai para 96 em vez de
    /// dividir por zero.
    /// </summary>
    public static double ParaDip(double pixeis, double dpi) =>
        pixeis * 96.0 / (dpi > 0 ? dpi : 96.0);

    /// <summary>
    /// Costura de teste: quando não é nula, a janela usa esta área em vez de
    /// perguntar ao monitor. Existe para o harness provar as três escalas numa
    /// máquina a 100%; em produção é sempre nula.
    /// </summary>
    public static AreaEcra? AreaForcada { get; set; }
}
