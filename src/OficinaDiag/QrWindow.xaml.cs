using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        Background = System.Windows.Media.Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        using var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(12, new byte[] { 0x33, 0xff, 0x33, 0xff }, new byte[] { 0x00, 0x00, 0x00, 0xff });
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "LÊ COM A CÂMARA DO TELEMÓVEL",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0xff, 0x33)),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(new Image { Source = bmp, Width = 280, Height = 280 });
        Content = panel;
    }
}
