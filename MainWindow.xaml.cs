using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RestSharp;
using Newtonsoft.Json;
using System.IO;

namespace PrintSpooler
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _pollTimer;
        private bool _isPolling = false;

        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spooler_settings.json");

        public MainWindow()
        {
            InitializeComponent();
            LoadPrinters();
            LoadSettings();

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(5);
            _pollTimer.Tick += async (s, e) => {
                await PollPrintQueue();
                SaveSettings(); // Save state during polling to ensure it's remembered
            };
            
            LogMessage("Print Spooler Initialized.");
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonConvert.DeserializeObject<SpoolerSettings>(json);
                    if (settings != null)
                    {
                        ApiUrlBox.Text = settings.ApiUrl;
                        if (!string.IsNullOrEmpty(settings.Printer))
                        {
                            foreach (var item in PrinterComboBox.Items)
                            {
                                if (item.ToString() == settings.Printer)
                                {
                                    PrinterComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        if (settings.IsPolling)
                        {
                            _isPolling = true;
                            UpdatePollButtonState();
                            _pollTimer = new DispatcherTimer();
                            _pollTimer.Interval = TimeSpan.FromSeconds(5);
                            _pollTimer.Tick += async (s, e) => {
                                await PollPrintQueue();
                                SaveSettings();
                            };
                            _pollTimer.Start();
                            LogMessage("Resumed background polling from last session.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to load settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new SpoolerSettings
                {
                    ApiUrl = ApiUrlBox.Text,
                    Printer = PrinterComboBox.SelectedItem?.ToString() ?? "",
                    IsPolling = _isPolling
                };
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        private void UpdatePollButtonState()
        {
            if (_isPolling)
            {
                PollButton.Content = "Stop Background Polling";
                PollButton.Background = new SolidColorBrush(Colors.DarkRed);
                PollButton.Foreground = new SolidColorBrush(Colors.White);
                StatusLog.Text = "Status: Polling started...";
            }
            else
            {
                PollButton.Content = "Start Background Polling";
                PollButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#022c22"));
                PollButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d4af37"));
                StatusLog.Text = "Status: Polling stopped.";
            }
        }

        private void LogMessage(string msg)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            LogBox.AppendText($"[{time}] {msg}\n");
            LogBox.ScrollToEnd();
        }

        private void LoadPrinters()
        {
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                PrinterComboBox.Items.Add(printer);
            }
            if (PrinterComboBox.Items.Count > 0)
                PrinterComboBox.SelectedIndex = 0;
            else
                LogMessage("WARNING: No printers found installed on this system.");
        }

        private void PollButton_Click(object sender, RoutedEventArgs e)
        {
            _isPolling = !_isPolling;
            UpdatePollButtonState();
            
            if (_isPolling)
            {
                LogMessage("Started polling API every 5 seconds.");
                _pollTimer.Start();
            }
            else
            {
                LogMessage("Stopped polling API.");
                _pollTimer.Stop();
            }
            SaveSettings();
        }

        private async Task PollPrintQueue()
        {
            try
            {
                string apiUrl = ApiUrlBox.Text;
                if (string.IsNullOrWhiteSpace(apiUrl)) return;

                var client = new RestClient(apiUrl);
                var request = new RestRequest(string.Empty, Method.Get);
                var response = await client.ExecuteAsync(request);

                if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    var items = JsonConvert.DeserializeObject<List<QueenPrintItem>>(response.Content);
                    if (items != null && items.Count > 0)
                    {
                        StatusLog.Text = $"Status: Printed {items.Count} jobs at {DateTime.Now:T}";
                        LogMessage($"Received {items.Count} print jobs from queue.");
                        foreach (var item in items)
                        {
                            PrintItem(item);
                            await MarkAsPrinted(item.id);
                        }
                    }
                }
                else if (!response.IsSuccessful)
                {
                    StatusLog.Text = $"Status: Error polling API ({DateTime.Now:T}) - Code: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                StatusLog.Text = $"Status: Connection Error ({DateTime.Now:T})";
                LogMessage($"ERROR: {ex.Message}");
            }
        }

        private async Task MarkAsPrinted(string id)
        {
            try
            {
                string baseUrl = ApiUrlBox.Text.Replace("/pending", "");
                var client = new RestClient(baseUrl);
                var request = new RestRequest($"/{id}/status", Method.Put);
                var body = new { status = "printed" };
                request.AddJsonBody(body);
                var resp = await client.ExecuteAsync(request);
                if (resp.IsSuccessful)
                    LogMessage($"Marked job {id} as printed.");
            }
            catch { }
        }

        private void PrintItem(QueenPrintItem item)
        {
            if (string.IsNullOrWhiteSpace(item.tspl_data))
            {
                LogMessage($"Job {item.id} had empty TSPL/ZPL data. Skipping.");
                return;
            }

            string printerName = "";
            Application.Current.Dispatcher.Invoke(() => {
                printerName = PrinterComboBox.SelectedItem?.ToString() ?? "";
            });
            
            if (string.IsNullOrEmpty(printerName))
            {
                LogMessage("ERROR: No printer selected.");
                return;
            }

            LogMessage($"Sending TSPL/ZPL data for job {item.id} to {printerName}.");
            RawPrinterHelper.SendStringToPrinter(printerName, item.tspl_data);
        }
    }

    public class SpoolerSettings
    {
        public string ApiUrl { get; set; } = string.Empty;
        public string Printer { get; set; } = string.Empty;
        public bool IsPolling { get; set; } = false;
    }

    public class QueenPrintItem
    {
        public string id { get; set; } = string.Empty;
        public string tspl_data { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
    }
}