using ipinpool;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EDIPSearch.Core;

namespace EDIPSearch;

public partial class frmSettings : Window
{
    private List<FilterRowItem> _uiRows = new List<FilterRowItem>();
    private wiseIPList _filterListForEngine = new wiseIPList();

    public frmSettings()
    {
        InitializeComponent();

        // Привязка обработчиков событий к кнопкам разметки
        btnOK.Click += BtnOK_Click;
        btnCancel.Click += BtnCancel_Click;
        btnFilterAdd.Click += BtnFilterAdd_Click;
        btnFilterRemove.Click += BtnFilterRemove_Click;

        this.Loaded += FrmSettings_Loaded;
    }

    private void FrmSettings_Loaded(object sender, RoutedEventArgs e)
    {
        // Считывание сохраненных параметров
        cbListenLog.IsChecked = Properties.Settings.Default.AutoMonitoringEnabled;
        cbDeleteProcessedLogs.IsChecked = Properties.Settings.Default.DeleteLogsAfterParse;

        LoadFiltersFile();
    }

    private void LoadFiltersFile()
    {
        _uiRows.Clear();

        string workingDir = Properties.Settings.Default.DataFolder;
        if (string.IsNullOrEmpty(workingDir))
        {
            workingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDIPSearch");
        }

        string filePath = Path.Combine(workingDir, "filters.txt");
        if (!File.Exists(filePath)) return;

        try
        {
            string[] lines = File.ReadAllLines(filePath);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("#"))
                {
                    _uiRows.Add(new FilterRowItem { IsComment = true, RawText = line });
                    continue;
                }

                if (trimmed.Contains("#"))
                {
                    int hashIndex = line.IndexOf('#');
                    string ipPart = line.Substring(0, hashIndex).Trim();
                    string commentPart = line.Substring(hashIndex).Trim();

                    if (ValidateAndNormalizeIP(ipPart, out string normalizedIp, out IPclass ipObj))
                    {
                        _uiRows.Add(new FilterRowItem { IsComment = true, RawText = commentPart });
                        _uiRows.Add(new FilterRowItem { IsComment = false, RawText = normalizedIp, IpObject = ipObj });
                    }
                    continue;
                }

                if (ValidateAndNormalizeIP(trimmed, out string cleanIp, out IPclass pureIpObj))
                {
                    _uiRows.Add(new FilterRowItem { IsComment = false, RawText = cleanIp, IpObject = pureIpObj });
                }
            }

            RefreshGrid();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка чтения: {ex.Message}");
        }
    }
    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string targetFolder = Properties.Settings.Default.DataFolder;
            if (string.IsNullOrEmpty(targetFolder))
            {
                targetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDIPSearch");
            }

            if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);
            string filePath = Path.Combine(targetFolder, "filters.txt");

            using (StreamWriter writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                foreach (var row in _uiRows)
                {
                    if (row.IsComment) writer.WriteLine(row.RawText);
                    else writer.WriteLine(row.IpObject?.ToString() ?? row.RawText);
                }
            }

            _filterListForEngine = new wiseIPList();
            foreach (var row in _uiRows)
            {
                if (!row.IsComment && row.IpObject != null) _filterListForEngine.AddAddress(row.IpObject);
            }

            // Сохранение системных параметров конфигурации
            Properties.Settings.Default.AutoMonitoringEnabled = cbListenLog.IsChecked ?? false;
            Properties.Settings.Default.DeleteLogsAfterParse = cbDeleteProcessedLogs.IsChecked ?? false;
            Properties.Settings.Default.Save();

            this.DialogResult = true;
            this.Close();
        }
        catch (Exception ex) { MessageBox.Show($"Ошибка сохранения параметров: {ex.Message}"); }
    }

    private void RefreshGrid()
    {
        try
        {
            lvFilters.ItemsSource = null;
            lvFilters.ItemsSource = _uiRows;
        }
        catch (Exception ex) { MessageBox.Show($"Ошибка обновления таблицы: {ex.Message}"); }
    }

    private void BtnFilterAdd_Click(object sender, RoutedEventArgs e)
    {
        string input = Microsoft.VisualBasic.Interaction.InputBox(
            "Введите IP-адрес или подсеть для добавления в белый список исключений:",
            "Добавление фильтра", "");

        if (string.IsNullOrEmpty(input)) return;

        if (ValidateAndNormalizeIP(input, out string normalizedIp, out IPclass ipObj))
        {
            if (!_uiRows.Any(x => !x.IsComment && x.RawText.Equals(normalizedIp, StringComparison.OrdinalIgnoreCase)))
            {
                _uiRows.Add(new FilterRowItem { IsComment = false, RawText = normalizedIp, IpObject = ipObj });
                RefreshGrid();
            }
        }
        else
        {
            MessageBox.Show("Неверный формат IP-адреса или подсети!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnFilterRemove_Click(object sender, RoutedEventArgs e)
    {
        var selectedItem = lvFilters.SelectedItem as FilterRowItem;
        if (selectedItem == null) return;

        _uiRows.Remove(selectedItem);
        RefreshGrid();
    }

    private bool ValidateAndNormalizeIP(string input, out string normalizedIp, out IPclass ipObj)
    {
        normalizedIp = input.Trim();
        ipObj = null;
        if (string.IsNullOrEmpty(normalizedIp)) return false;

        if (!normalizedIp.Contains("/"))
        {
            normalizedIp += "/32";
        }

        try
        {
            string[] parts = normalizedIp.Split('/');
            if (parts.Length != 2) return false;

            if (!System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            if (!int.TryParse(parts[1], out int mask) || mask < 0 || mask > 32)
            {
                return false;
            }

            ipObj = IPclass.Parse(normalizedIp);
            return ipObj != null;
        }
        catch { return false; }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) this.DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }
}

public class FilterRowItem
{
    public bool IsComment { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string IP => IsComment ? RawText : IpObject?.IP ?? string.Empty;
    public string Mask => IsComment ? string.Empty : (IpObject?.PoolSize.ToString() ?? string.Empty);
    public string Size => IsComment ? string.Empty : CalculateSize(IpObject?.PoolSize ?? 32);
    public IPclass? IpObject { get; set; }

    private string CalculateSize(int poolSize)
    {
        long usableAddresses = 0;
        if (poolSize == 32) usableAddresses = 1;
        else if (poolSize == 31) usableAddresses = 2;
        else
        {
            long total = (long)Math.Pow(2, 32 - poolSize);
            usableAddresses = total - 2;
        }

        var nfi = new System.Globalization.NumberFormatInfo
        {
            NumberGroupSeparator = " ",
            NumberDecimalDigits = 0
        };
        return usableAddresses.ToString("N", nfi);
    }
}
