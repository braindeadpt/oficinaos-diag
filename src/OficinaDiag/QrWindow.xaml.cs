using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace OficinaDiag;

/// <summary>Popup com o QR code da página de teste — para iPhone ler com a câmara.</summary>
public sealed class QrWindow : Window
{
    public QrWindow(QRCodeData data)
    {
        Title = "SCAN ME";
        Width = 340; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var fg = ThemeColor("FgBrush");
        var bg = ThemeColor("PanelBrush");
        Background = new SolidColorBrush(bg);

        using var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(12,
            new byte[] { fg.R, fg.G, fg.B, 0xff }, new byte[] { bg.R, bg.G, bg.B, 0xff });
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = L10n.T("qr.text"),
            Foreground = new SolidColorBrush(fg),
            FontFamily = (FontFamily)Application.Current.TryFindResource("UiFont")
                ?? new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new Image { Source = bmp, Width = 280, Height = 280 });
        Content = panel;
    }

    private static Color ThemeColor(string key) =>
        Application.Current.TryFindResource(key) is SolidColorBrush b ? b.Color : Colors.Black;
}
