using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RestSharp;
using Newtonsoft.Json;
using ZXing;
using ZXing.Windows.Compatibility;
using System.Windows.Media;

namespace RudrakshPrintSpooler
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _pollTimer;
        private bool _isPolling = false;

        public MainWindow()
        {
            InitializeComponent();
            LoadPrinters();
            UpdatePreview();

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(5);
            _pollTimer.Tick += async (s, e) => await PollPrintQueue();
        }

        private void LoadPrinters()
        {
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                PrinterComboBox.Items.Add(printer);
            }
            if (PrinterComboBox.Items.Count > 0)
                PrinterComboBox.SelectedIndex = 0;
        }

        private void Setting_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) UpdatePreview();
        }

        private void Setting_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded) UpdatePreview();
        }

        private void PollButton_Click(object sender, RoutedEventArgs e)
        {
            _isPolling = !_isPolling;
            if (_isPolling)
            {
                PollButton.Content = "Stop Background Polling";
                PollButton.Background = new SolidColorBrush(Colors.DarkRed);
                PollButton.Foreground = new SolidColorBrush(Colors.White);
                StatusLog.Text = "Status: Polling started...";
                _pollTimer.Start();
            }
            else
            {
                PollButton.Content = "Start Background Polling";
                PollButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#022c22"));
                PollButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d4af37"));
                StatusLog.Text = "Status: Polling stopped.";
                _pollTimer.Stop();
            }
        }

        private void UpdatePreview()
        {
            // Simple mockup item for preview
            string huid = "1A2B3C";
            string name = "Gold Chain 22K";
            string weight = "15.5g | Gold";
            string price = "₹75,000";

            PreviewCanvas.Children.Clear();

            // 1. Generate QR code using ZXing and set into an Image control
            try
            {
                var writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new ZXing.Common.EncodingOptions
                    {
                        Height = 100,
                        Width = 100,
                        Margin = 0
                    }
                };

                using (var bitmap = writer.Write(huid))
                {
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        stream.Position = 0;

                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.StreamSource = stream;
                        bitmapImage.EndInit();

                        Image qrImage = new Image
                        {
                            Source = bitmapImage,
                            Width = 100,
                            Height = 100
                        };

                        double qrX = 0, qrY = 0;
                        double.TryParse(QrXBox.Text, out qrX);
                        double.TryParse(QrYBox.Text, out qrY);

                        // Visual scaling factor: let's map roughly 1 native TSPL dot to 0.5 WPF unit just to fit mostly.
                        // Or we can just use 1:1 if the canvas is 800x400
                        Canvas.SetLeft(qrImage, qrX);
                        Canvas.SetTop(qrImage, qrY);
                        PreviewCanvas.Children.Add(qrImage);
                    }
                }
            }
            catch { }

            // 2. Add text blocks
            double textX = 360, textY = 10;
            double.TryParse(TextXBox.Text, out textX);
            double.TryParse(TextYBox.Text, out textY);

            double currentY = textY;
            double yStep = 24 * 1.5; // Roughly 24 dots shifted in UI

            if (ShowHuidCheck.IsChecked == true)
            {
                AddPreviewText(huid, textX, currentY, true);
                currentY += yStep;
            }
            if (ShowNameCheck.IsChecked == true)
            {
                AddPreviewText(name, textX, currentY);
                currentY += yStep;
            }
            if (ShowWeightCheck.IsChecked == true)
            {
                AddPreviewText(weight, textX, currentY);
                currentY += yStep;
            }
            if (ShowPriceCheck.IsChecked == true)
            {
                AddPreviewText(price, textX, currentY, true);
            }
        }

        private void AddPreviewText(string text, double x, double y, bool isBold = false)
        {
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontSize = 16, // Relative size for preview
                FontFamily = new FontFamily("Arial"),
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            PreviewCanvas.Children.Add(tb);
        }

        private async Task PollPrintQueue()
        {
            try
            {
                var client = new RestClient(ApiUrlBox.Text);
                var request = new RestRequest("queenprint/pending", Method.Get);
                var response = await client.ExecuteAsync(request);

                if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    var items = JsonConvert.DeserializeObject<List<QueenPrintItem>>(response.Content);
                    if (items != null && items.Count > 0)
                    {
                        StatusLog.Text = $"Status: Found {items.Count} items to print at {DateTime.Now:T}";
                        foreach (var item in items)
                        {
                            PrintItem(item);
                            await MarkAsPrinted(item.id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusLog.Text = $"Status: Error polling API ({DateTime.Now:T}) - {ex.Message}";
            }
        }

        private async Task MarkAsPrinted(string id)
        {
            try
            {
                var client = new RestClient(ApiUrlBox.Text);
                var request = new RestRequest($"queenprint/{id}/status", Method.Put);
                var body = new { status = "printed" };
                request.AddJsonBody(body);
                await client.ExecuteAsync(request);
            }
            catch { }
        }

        private void PrintItem(QueenPrintItem item)
        {
            string printerName = "";
            Application.Current.Dispatcher.Invoke(() => {
                printerName = PrinterComboBox.SelectedItem?.ToString() ?? "";
            });
            
            if (string.IsNullOrEmpty(printerName)) return;

            string tspl = GenerateTsplCommand(item);
            RawPrinterHelper.SendStringToPrinter(printerName, tspl);
        }

        private string GenerateTsplCommand(QueenPrintItem item)
        {
            // Note: These layout sizes (e.g. 80 mm, 40 mm) might also be configurable. Using hardcoded standard for now.
            string tspl = "SIZE 80 mm, 40 mm\n";
            tspl += "GAP 2 mm, 0 mm\n";
            tspl += "DIRECTION 1\n";
            tspl += "CLS\n";

            double qrX = 20, qrY = 10, textX = 360, textY = 10;
            
            Application.Current.Dispatcher.Invoke(() => {
                double.TryParse(QrXBox.Text, out qrX);
                double.TryParse(QrYBox.Text, out qrY);
                double.TryParse(TextXBox.Text, out textX);
                double.TryParse(TextYBox.Text, out textY);
            });

            // QR code in TSPL: "QRCODE X,Y,ECC_Level,Cell_Width,Mode,Rotation,Model,Mask,\"String\""
            string barcodeContent = item.huid ?? item.item_code ?? "123";
            tspl += $"QRCODE {qrX},{qrY},H,4,A,0,\"{barcodeContent}\"\n";

            double currentY = textY;
            double yStep = 24;

            Application.Current.Dispatcher.Invoke(() => {
                if (ShowHuidCheck.IsChecked == true)
                {
                    string huidStr = item.huid ?? "NO HUID";
                    tspl += $"TEXT {textX},{currentY},\"2\",0,1,1,\"{huidStr}\"\n";
                    currentY += yStep;
                }
                if (ShowNameCheck.IsChecked == true)
                {
                    tspl += $"TEXT {textX},{currentY},\"2\",0,1,1,\"{item.name}\"\n";
                    currentY += yStep;
                }
                if (ShowWeightCheck.IsChecked == true)
                {
                    tspl += $"TEXT {textX},{currentY},\"2\",0,1,1,\"{item.weight}g | {item.metal_type}\"\n";
                    currentY += yStep;
                }
                if (ShowPriceCheck.IsChecked == true)
                {
                    tspl += $"TEXT {textX},{currentY},\"2\",0,1,1,\"Rs {item.price}\"\n";
                }
            });

            tspl += "PRINT 1,1\n";
            tspl += "CLS\n";

            return tspl;
        }
    }

    public class QueenPrintItem
    {
        public string id { get; set; }
        public string item_id { get; set; }
        public string huid { get; set; }
        public string item_code { get; set; }
        public string name { get; set; }
        public float weight { get; set; }
        public string metal_type { get; set; }
        public string purity { get; set; }
        public float price { get; set; }
        public string status { get; set; }
    }
}