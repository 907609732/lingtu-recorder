using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using UnpackVision.App;

namespace UnpackVision.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class BrandLinkUiTests(WpfTestHost wpf)
{
    [Fact]
    public void ExistingWorkspaceRendersWithNewIdentityAndPreservedControls()
    {
        wpf.Invoke(() =>
        {
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "App.xaml"));
            var dictionary = new XElement(presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                document.Root!.Element(presentation + "Application.Resources")!.Elements());
            foreach (var image in dictionary.Descendants(presentation + "BitmapImage"))
                image.SetAttributeValue("UriSource", "pack://application:,,,/拆包智录;component/Assets/EcommerceUnpackRecorder-Logo.png");
            var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            System.Windows.Application.Current.Resources.MergedDictionaries.Add(resources);
            Velopack.VelopackApp.Build().Run();
            var window = new MainWindow();
            // Render the real view without starting cameras, loading personal settings or a station.
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, "OnLoaded");
            window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, "OnClosing");
            foreach (var field in typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                if (field.GetValue(window) is DispatcherTimer timer) timer.Stop();
            try
            {
                Assert.Equal("灵兔拆包智录", window.Title);
                Assert.NotNull(window.FindName("ScannerInput"));
                Assert.NotNull(window.FindName("RecordingPageButton"));
                Assert.NotNull(window.FindName("BarcodePageButton"));
                var shell = Assert.IsType<Grid>(window.Content);
                shell.Opacity = 1;
                shell.RenderTransform = Transform.Identity;
                shell.Measure(new Size(1480, 920));
                shell.Arrange(new Rect(0, 0, 1480, 920));
                shell.UpdateLayout();
                var output = Environment.GetEnvironmentVariable("UNPACKVISION_QA_OUTPUT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    foreach (var dpi in new[] { 96, 144 })
                    {
                        var bitmap = new RenderTargetBitmap(1480 * dpi / 96, 920 * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual();
                        using (var drawing = visual.RenderOpen())
                        {
                            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1480, 920));
                            drawing.DrawRectangle(new VisualBrush(shell), null, new Rect(0, 0, 1480, 920));
                        }
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, $"desktop-{dpi}.png"));
                        encoder.Save(stream);
                    }
                }
            }
            finally { window.Close(); }
        });
    }
}
