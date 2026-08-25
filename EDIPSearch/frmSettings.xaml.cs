using ipinpool; // Твое пространство имен для wiseIPList и IPclass
using System;
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace EDIPSearch;

public partial class frmSettings : Window
{
    // Список строк для отображения в ListView (содержит и IP, и комментарии)
    private List<FilterRowItem> _uiRows = new List<FilterRowItem>();

    // Твой оригинальный класс-фильтр, который мы наполним для передачи в главное окно
    private wiseIPList _filterListForEngine = new wiseIPList();

    public frmSettings()
    {
        InitializeComponent();

        btnOK.Click += BtnOK_Click;
        btnCancel.Click += BtnCancel_Click;
        tbFilterAdd.Click += TbFilterAdd_Click;
        tbFilterRemove.Click += TbFilterRemove_Click;

        this.Loaded += FrmSettings_Loaded;
    }

    private void FrmSettings_Loaded(object sender, RoutedEventArgs e)
    {
        // Просто считываем уже гарантированно инициализированные параметры
        tbEdPath.Text = Properties.Settings.Default.EDLogFolder;

        string defaultDataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDIPSearch");
        tbWorkingDir.Text = string.IsNullOrEmpty(Properties.Settings.Default.DataFolder) ? defaultDataDir : Properties.Settings.Default.DataFolder;

        cbListenLog.IsChecked = Properties.Settings.Default.AutoMonitoringEnabled;

        // Подвязываем клики по кнопкам-иконкам папок
        btnEDOpen.Click += BtnEDOpen_Click;
        btnWorkingDir.Click += BtnWorkingDir_Click;

        LoadFiltersFile();
    }

    private void LoadFiltersFile()
    {
        _uiRows.Clear();
        string filePath = System.IO.Path.Combine(tbWorkingDir.Text, "filters.txt");

        if (!File.Exists(filePath)) return;

        try
        {
            string[] lines = File.ReadAllLines(filePath);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Сценарий 2: Чистый комментарий (начинается с #)
                if (trimmed.StartsWith("#"))
                {
                    _uiRows.Add(new FilterRowItem { IsComment = true, RawText = line });
                    continue;
                }

                // Сценарий 3 при загрузке: В строке есть и IP, и комментарий через #
                if (trimmed.Contains("#"))
                {
                    // Бьем строго по первому символу #
                    int hashIndex = line.IndexOf('#');
                    string ipPart = line.Substring(0, hashIndex).Trim();
                    string commentPart = line.Substring(hashIndex).Trim(); // Сохраняет сам символ # и текст за ним

                    // Валидируем IP-часть (авто-допишет /32 если надо и проверит дубли)
                    if (ValidateAndNormalizeIp(ipPart, out string normalizedIp, out IPclass? ipObj))
                    {
                        // Разворачиваем в наборе данных: сначала добавляем комментарий, затем IP
                        _uiRows.Add(new FilterRowItem { IsComment = true, RawText = commentPart });
                        _uiRows.Add(new FilterRowItem { IsComment = false, RawText = normalizedIp, IpObject = ipObj });
                    }
                    continue;
                }

                // Сценарий 1 при загрузке: Чистый IP/маска
                if (ValidateAndNormalizeIp(trimmed, out string cleanIp, out IPclass? pureIpObj))
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


    private void RefreshGrid()
    {
        lvFilters.ItemsSource = null;
        lvFilters.ItemsSource = _uiRows;
    }

    private void TbFilterAdd_Click(object sender, RoutedEventArgs e)
    {
        string input = tbAddress.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        // SCENARIO 2: Пользователь добавляет чистый #комментарий
        if (input.StartsWith("#"))
        {
            var commentRow = new FilterRowItem { IsComment = true, RawText = tbAddress.Text }; // Сохраняем с пробелами как ввёл
            _uiRows.Add(commentRow);
            RefreshGrid();
            lvFilters.ScrollIntoView(commentRow);
            tbAddress.Clear();
            return;
        }

        // SCENARIO 3: Пользователь добавляет комбинированную строку "ip[/mask] #комментарий"
        if (input.Contains("#"))
        {
            int hashIndex = tbAddress.Text.IndexOf('#');
            string ipPart = tbAddress.Text.Substring(0, hashIndex).Trim();
            string commentPart = tbAddress.Text.Substring(hashIndex).Trim(); // Забирает # и всё что после

            // Проводим валидацию секции IP
            if (ValidateAndNormalizeIp(ipPart, out string normalizedIp, out IPclass? ipObj))
            {
                // Если валидация прошла — последовательно добавляем две строки
                var commentRow = new FilterRowItem { IsComment = true, RawText = commentPart };
                var ipRow = new FilterRowItem { IsComment = false, RawText = normalizedIp, IpObject = ipObj };

                _uiRows.Add(commentRow);
                _uiRows.Add(ipRow);

                RefreshGrid();
                lvFilters.ScrollIntoView(ipRow);
                tbAddress.Clear();
            }
            else
            {
                MessageBox.Show("Неверный формат IP-адреса, либо такой адрес уже существует в таблице!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        // SCENARIO 1: Пользователь добавляет чистый ip[/mask]
        if (ValidateAndNormalizeIp(input, out string singleNormalizedIp, out IPclass? singleIpObj))
        {
            var ipRow = new FilterRowItem { IsComment = false, RawText = singleNormalizedIp, IpObject = singleIpObj };
            _uiRows.Add(ipRow);
            RefreshGrid();
            lvFilters.ScrollIntoView(ipRow);
            tbAddress.Clear();
        }
        else
        {
            MessageBox.Show("Неверный формат IP-адреса, либо такой адрес уже существует!", "Ошибка валидации", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }


    /// <summary>
    /// Кнопка МИНУС (-): Удаление строки (хоть IP, хоть комментария)
    /// </summary>
    private void TbFilterRemove_Click(object sender, RoutedEventArgs e)
    {
        if (lvFilters.SelectedItem is FilterRowItem selectedItem)
        {
            _uiRows.Remove(selectedItem);
            RefreshGrid();
        }
    }

    private void BtnOK_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string targetFolder = tbWorkingDir.Text.Trim();
            if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);

            string filePath = System.IO.Path.Combine(targetFolder, "filters.txt");

            // Пересохраняем файл, сохраняя ВСЮ структуру и комментарии на своих местах!
            using (StreamWriter writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
            {
                foreach (var row in _uiRows)
                {
                    if (row.IsComment)
                    {
                        writer.WriteLine(row.RawText); // Пишем комментарий как есть
                    }
                    else
                    {
                        writer.WriteLine(row.IpObject?.ToString() ?? row.RawText); // Пишем IP
                    }
                }
            }

            // Параллельно собираем чистый wiseIPList для отправки в ядро программы (без комментариев)
            _filterListForEngine = new wiseIPList();
            foreach (var row in _uiRows)
            {
                if (!row.IsComment && row.IpObject != null)
                {
                    _filterListForEngine.AddAddress(row.IpObject);
                }
            }

            // Сохраняем системные настройки
            Properties.Settings.Default.EDLogFolder = tbEdPath.Text.Trim();
            Properties.Settings.Default.DataFolder = targetFolder;
            Properties.Settings.Default.AutoMonitoringEnabled = cbListenLog.IsChecked ?? false;
            Properties.Settings.Default.Save();

            // Здесь передай _filterListForEngine в свое главное окно, если нужно обновить фильтр на лету

            this.DialogResult = true;
            this.Close();
        }
        catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}"); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        this.Close();
    }
    /// <summary>
    /// Валидация и унификация IP-адреса. Дописывает /32 при отсутствии маски и проверяет на дубликаты.
    /// </summary>
    /// <param name="rawIpInput">Сырая строка ввода (например, "1.2.3.4" или "10.0.0.0/8")</param>
    /// <param name="cleanNormalizedIp">Выходная унифицированная строка</param>
    /// <param name="parsedObj">Выходной готовый объект IPclass</param>
    private bool ValidateAndNormalizeIp(string rawIpInput, out string cleanNormalizedIp, out IPclass? parsedObj)
    {
        cleanNormalizedIp = rawIpInput.Trim();
        parsedObj = null;

        if (string.IsNullOrEmpty(cleanNormalizedIp)) return false;

        // Если пользователь забыл указать маску, принудительно дописываем /32 для унификации
        if (!cleanNormalizedIp.Contains("/"))
        {
            cleanNormalizedIp += "/32";
        }

        // Пробуем распарсить твоим оригинальным методом
        parsedObj = IPclass.Parse(cleanNormalizedIp);
        if (parsedObj == null) return false;

        // Особого смысла проверять всю математику пула нет, проверяем физическое совпадение IP-строки в таблице
        foreach (var row in _uiRows)
        {
            if (!row.IsComment && row.IpObject != null)
            {
                // Сравниваем чистые IP (например, "194.87.147.176/32")
                if (row.IpObject.ToString().Equals(parsedObj.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Такой адрес уже физически есть в списке
                }
            }
        }

        return true;
    }

/// <summary>
/// Продвинутый автоматический поиск папки журналов Elite Dangerous
/// </summary>
private string AutoDetectEliteDangerousPath()
{
    // 1. Стандартный путь Windows (Saved Games), где игра хранит логи у 95% пилотов
    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string defaultLogPath = System.IO.Path.Combine(userProfile, "Saved Games", "Frontier Developments", "Elite Dangerous");

    if (Directory.Exists(defaultLogPath))
    {
        return defaultLogPath;
    }

    // 2. Подстраховка: Пробуем найти через реестр Steam, если папка кастомная
    try
    {
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key != null)
            {
                string? steamPath = key.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steamPath))
                {
                    // Проверяем дефолтную библиотеку стима
                    string steamLogPath = System.IO.Path.Combine(steamPath, "steamapps", "common", "Elite Dangerous");
                    // Сами журналы лежат в профиле пользователя, но если пользователь ищет корневой каталог:
                    if (Directory.Exists(steamLogPath)) return steamLogPath;
                }
            }
        }
    }
    catch { }

    return string.Empty; // Если определить не удалось, оставляем пустым для ручного ввода
}

/// <summary>
/// Выбор папки логов Elite Dangerous
/// </summary>
private void BtnEDOpen_Click(object sender, RoutedEventArgs e)
{
    var dialog = new OpenFolderDialog
    {
        Title = "Укажите папку с журналами (Journal.*.log) Elite Dangerous",
        InitialDirectory = Directory.Exists(tbEdPath.Text) ? tbEdPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    };

    if (dialog.ShowDialog() == true)
    {
        tbEdPath.Text = dialog.FolderName;
    }
}

/// <summary>
/// Выбор папки хранилища данных приложения
/// </summary>
    private void BtnWorkingDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
    {
        Title = "Выберите папку для хранения данных EDIPSearch (filters.txt)",
        InitialDirectory = Directory.Exists(tbWorkingDir.Text) ? tbWorkingDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
    };

        if (dialog.ShowDialog() == true)
    {
        tbWorkingDir.Text = dialog.FolderName;
    }
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

        // ИСПРАВЛЕНО: Жестко задаем пробел в качестве разделителя тысяч
        var nfi = new System.Globalization.NumberFormatInfo
        {
            NumberGroupSeparator = " ", // Разделитель - пробел
            NumberDecimalDigits = 0      // Отключаем копейки/дробную часть
        };

        // Форматируем число с использованием нашей маски
        return usableAddresses.ToString("N", nfi);
    }
}

