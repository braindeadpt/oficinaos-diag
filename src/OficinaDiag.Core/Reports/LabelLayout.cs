namespace OficinaDiag.Reports;

/// <summary>
/// Geometria da etiqueta 70×40 mm (280×150 DIP) na área útil da impressora.
/// Pura e no Core para os rolos verticais e o A4 serem cobertos por testes
/// sem WPF: a escolha de rotação e a escala não dependem do PrintDialog.
/// </summary>
public readonly record struct LabelLayout(bool Rotate, double Scale)
{
    /// <summary>Largura desenhada da etiqueta, em DIP (~74 mm).</summary>
    public const double LabelWidth = 280.0;

    /// <summary>Altura desenhada da etiqueta, em DIP (~40 mm).</summary>
    public const double LabelHeight = 150.0;

    /// <summary>
    /// Roda a etiqueta em rolos verticais estreitos (área útil mais alta que
    /// larga e mais estreita que a etiqueta) para o lado comprido seguir o
    /// rolo, e escala-a para caber inteira. Em A4, cuja largura excede a
    /// etiqueta, não roda e fica no tamanho físico.
    /// Área inválida (0, NaN, infinita) → sem transformação.
    /// </summary>
    public static LabelLayout Fit(double printableWidth, double printableHeight)
    {
        if (!double.IsFinite(printableWidth) || !double.IsFinite(printableHeight) ||
            printableWidth <= 0 || printableHeight <= 0)
            return new LabelLayout(false, 1.0);

        var rotate = printableWidth < LabelWidth && printableHeight > printableWidth;

        // Pegada da etiqueta depois de rodada: 150×280 em vez de 280×150.
        var footprintWidth = rotate ? LabelHeight : LabelWidth;
        var footprintHeight = rotate ? LabelWidth : LabelHeight;
        var scale = Math.Min(1.0, Math.Min(printableWidth / footprintWidth,
                                        printableHeight / footprintHeight));
        return new LabelLayout(rotate, scale);
    }
}
