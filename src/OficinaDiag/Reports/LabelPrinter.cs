using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OficinaDiag.Devices;
using QRCoder;

namespace OficinaDiag.Reports;

/// <summary>
/// Etiqueta de balcão ~70×40mm — modelo, serial, grau e QR com o payload
/// OFDIAG|serial|grade|data. Cola no saco do aparelho e liga-o ao histórico/
/// ticket da loja (o QR é texto, não um URL — funciona 100% offline).
/// </summary>
public static class LabelPrinter
{
    /// <summary>Constroi o visual da etiqueta — reutilizável para preview.</summary>
    public static FrameworkElement BuildVisual(DeviceReport r)
    {
        var grade = r.Results.TryGetValue("grade.overall", out var g) ? g.Value ?? "—" : "—";
        var model = r.Device.MarketingName ?? r.Device.Model ?? "?";
        var serial = r.Device.Serial ?? "?";
        var payload = $"OFDIAG|{serial}|{grade}|{r.CollectedAt:yyyyMMdd}";

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(8);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        var root = new Border
        {
            // As constantes vêm do Core: o desenho e o cálculo de rotação/
            // escala (LabelLayout) não podem divergir.
            Width = LabelLayout.LabelWidth,   // ~74mm a 96dpi
            Height = LabelLayout.LabelHeight, // ~40mm
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Padding = new Thickness(8),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Child = grid;

        grid.Children.Add(new Image { Source = bmp, Width = 100, Height = 100,
            VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        text.Children.Add(new TextBlock
        {
            Text = model, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"s/n {serial}", FontSize = 11, Foreground = Brushes.Black,
            FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 2, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = $"GRAU {grade}", FontSize = 26, FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black, Margin = new Thickness(0, 6, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = $"oficinaos-diag · {r.CollectedAt:dd-MM-yyyy}",
            FontSize = 9, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0),
        });

        root.Measure(new Size(LabelLayout.LabelWidth, LabelLayout.LabelHeight));
        root.Arrange(new Rect(0, 0, LabelLayout.LabelWidth, LabelLayout.LabelHeight));
        return root;
    }

    /// <summary>Abre o diálogo de impressão e imprime a etiqueta.</summary>
    public static bool Print(DeviceReport r)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return false;
        var visual = BuildVisual(r);
        // A etiqueta é desenhada em LabelLayout.LabelWidth×LabelHeight DIP (~74×40 mm) e a geometria
        // vive no Core (`LabelLayout.Fit`, testada sem WPF): roda 90° em
        // rolos térmicos verticais estreitos (Brother 62 mm, Dymo 57×32,
        // Zebra 50×30) para o lado comprido seguir o rolo, encolhe quando
        // não cabe, e mantém o A4 no tamanho físico.
        var layout = LabelLayout.Fit(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        if (layout.Rotate || layout.Scale < 0.999)
        {
            var transform = new TransformGroup();
            if (layout.Rotate) transform.Children.Add(new RotateTransform(90));
            if (layout.Scale < 0.999)
                transform.Children.Add(new ScaleTransform(layout.Scale, layout.Scale));
            visual.LayoutTransform = transform;
        }
        dlg.PrintVisual(visual, $"OficinaDiag etiqueta {r.Device.Serial}");
        return true;
    }
}
