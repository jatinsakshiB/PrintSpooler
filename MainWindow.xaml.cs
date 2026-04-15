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
using System.Windows.Forms; // Required for NotifyIcon
using Microsoft.Win32;      // Required for Registry
using System.Diagnostics;   // Required for Process.Start
using System.Text;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace PrintSpooler
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _pollTimer;
        private DispatcherTimer _uiTimer;
        private NotifyIcon _notifyIcon;
        private bool _isPolling = false;
        private int _jobsToday = 0;
        private DateTime _startTime;
        private bool _isExitForced = false;

        private readonly string _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spooler_settings.json");
        private readonly string _logDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public MainWindow()
        {
            InitializeComponent();
            _startTime = DateTime.Now;
            
            EnsureLogDirectory();
            LoadPrinters();
            LoadSettings();
            InitializeTrayIcon();
            CheckStartupStatus();

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(5);
            _pollTimer.Tick += async (s, e) => {
                await PollPrintQueue();
            };

            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromSeconds(1);
            _uiTimer.Tick += (s, e) => UpdateUIStatus();
            _uiTimer.Start();

            LogMessage("Print Spooler v2.0 Initialized.");
            
            if (_isPolling) {
                _pollTimer.Start();
                LogMessage("Auto-resumed polling session.");
            }
        }

        private void EnsureLogDirectory()
        {
            if (!Directory.Exists(_logDirPath))
                Directory.CreateDirectory(_logDirPath);
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
            _notifyIcon.Text = "Rudraksh Print Spooler - Running";
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Show Spooler", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit Spooler", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            _isExitForced = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitForced && MinimizeToTrayCheckBox.IsChecked == true)
            {
                e.Cancel = true;
                this.Hide();
                _notifyIcon.ShowBalloonTip(2000, "Still Running", "Print Spooler is still active in the system tray.", ToolTipIcon.Info);
            }
            base.OnClosing(e);
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
                        _isPolling = settings.IsPolling;

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
                        UpdatePollButtonState();
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

        private void CheckStartupStatus()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    AutoStartCheckBox.IsChecked = key.GetValue("RudrakshPrintSpooler") != null;
                }
            }
            catch { }
        }

        private void UpdateUIStatus()
        {
            TimeSpan uptime = DateTime.Now - _startTime;
            SessionTimeLabel.Text = $"Uptime: {uptime:hh\\:mm\\:ss}";
            JobsCountLabel.Text = $"Jobs today: {_jobsToday}";
            ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        private void UpdatePollButtonState()
        {
            if (_isPolling)
            {
                PollButton.Content = "Stop Background Polling";
                PollButton.Background = new SolidColorBrush(Colors.DarkRed);
                PollButton.Foreground = new SolidColorBrush(Colors.White);
                StatusLog.Text = "Status: Polling active...";
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
            string entry = $"[{time}] {msg}";
            
            // UI Update
            LogBox.Dispatcher.BeginInvoke(() => {
                LogBox.AppendText(entry + "\n");
                LogBox.ScrollToEnd();
            });

            // File Logging
            try
            {
                string fileName = DateTime.Now.ToString("yyyy-MM-dd") + ".log";
                File.AppendAllText(Path.Combine(_logDirPath, fileName), entry + Environment.NewLine);
            }
            catch { }
        }

        private void PollButton_Click(object sender, RoutedEventArgs e)
        {
            _isPolling = !_isPolling;
            UpdatePollButtonState();
            
            if (_isPolling)
            {
                LogMessage("Manual start: Polling API every 5 seconds.");
                _pollTimer.Start();
            }
            else
            {
                LogMessage("Manual stop: Polling API halted.");
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
                        StatusLog.Text = $"Status: Processing {items.Count} jobs...";
                        LogMessage($"Received {items.Count} print jobs from queue.");
                        foreach (var item in items)
                        {
                            PrintItem(item);
                            await MarkAsPrinted(item.id);
                            _jobsToday++;
                        }
                    }
                }
                else if (!response.IsSuccessful)
                {
                    StatusLog.Text = $"Status: API Error ({DateTime.Now:T}) - {response.StatusCode}";
                }
                else
                {
                    StatusLog.Text = $"Status: Polling (Idle at {DateTime.Now:T})";
                }
            }
            catch (Exception ex)
            {
                StatusLog.Text = $"Status: Connection Error ({DateTime.Now:T})";
                LogMessage($"POLL ERROR: {ex.Message}");
            }
        }

        private async Task MarkAsPrinted(string id)
        {
            try
            {
                string baseUrl = ApiUrlBox.Text.Replace("/pending", "");
                var client = new RestClient(baseUrl);
                var request = new RestRequest($"/{id}/status", Method.Put);
                request.AddJsonBody(new { status = "printed" });
                var resp = await client.ExecuteAsync(request);
                if (resp.IsSuccessful)
                    LogMessage($"Marked job {id} as printed on server.");
            }
            catch (Exception ex) { LogMessage($"MARK ERROR: {ex.Message}"); }
        }

        private void PrintItem(QueenPrintItem item)
        {
            if (string.IsNullOrWhiteSpace(item.tspl_data))
            {
                LogMessage($"Job {item.id} skipped: Empty data.");
                return;
            }

            string printerName = "";
            Application.Current.Dispatcher.Invoke(() => {
                printerName = PrinterComboBox.SelectedItem?.ToString() ?? "";
                PreviewBox.Text = item.tspl_data; // Update diagnostics preview
            });
            
            if (string.IsNullOrEmpty(printerName))
            {
                LogMessage("CRITICAL ERROR: No printer selected. Job failed.");
                return;
            }

            LogMessage($"Printing Job {item.id} [{item.tspl_data.Length} bytes] to {printerName}.");
            bool success = RawPrinterHelper.SendStringToPrinter(printerName, item.tspl_data);
            
            if (success)
                LogMessage($"Job {item.id} successfully sent to printer spooler.");
            else
                LogMessage($"FAILED to print Job {item.id}. Check printer status.");
        }

        private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (AutoStartCheckBox.IsChecked == true)
                        key.SetValue("RudrakshPrintSpooler", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else
                        key.DeleteValue("RudrakshPrintSpooler", false);
                }
            }
            catch (Exception ex) { MessageBox.Show("Failed to update startup registry: " + ex.Message); }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", _logDirPath); }
            catch { }
        }

        private void CopyScript_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(PreviewBox.Text))
            {
                System.Windows.Clipboard.SetText(PreviewBox.Text);
                MessageBox.Show("Print script copied to clipboard.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SimulatePrint_Click(object sender, RoutedEventArgs e) { /* For future testing */ }
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