using EDIPSearch.Core;
using EDIPSearch.Models;
using EDIPSearch.Network;
using EDIPSearch.Properties;
using ipinpool;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;

namespace EDIPSearch;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    wiseIPList EDList = new wiseIPList();
    string WorkingDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string EDLogs = string.Empty;
    RouterList RouterList = new RouterList();
    
    private MikrotikSyncCore _syncCore = new MikrotikSyncCore();
    private System.Timers.Timer _gameCheckTimer;
    private bool _isEliteRunning = false;
    //private FileSystemWatcher? _logWatcher;
 
    private System.Timers.Timer? _logReadTimer; // Таймер для чтения строк лога
    private string? _currentLogFilePath;
    private long _lastLogPosition = 0;
    private int _linesReadCounter = 0; // Наш новый счетчик строк

    // Добавьте в начало файла, если их нет:
    // using System.Runtime.InteropServices;

    #region Win32 Flash Window с автосбросом

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 2;       // Мигать только иконкой на панели задач
    private const uint FLASHW_CAPTION = 1;    // Мигать заголовком окна (для надежности)

    /// <summary>
    /// Запускает короткое мигание иконки (около 2-3 секунд) и автоматически останавливается
    /// </summary>
    private void FlashMainWindow()
    {
        // Если приложение прямо сейчас находится в фокусе у пользователя, мигать не нужно
        if (this.IsActive) return;

        var wih = new System.Windows.Interop.WindowInteropHelper(this);
        IntPtr hWnd = wih.Handle;
        if (hWnd == IntPtr.Zero) return;

        FLASHWINFO fInfo = new FLASHWINFO();
        fInfo.cbSize = Convert.ToUInt32(Marshal.SizeOf(fInfo));
        fInfo.hwnd = hWnd;
        fInfo.dwFlags = FLASHW_TRAY | FLASHW_CAPTION;
        fInfo.uCount = 4; // Мигнуть ровно 4 раза (при стандартной частоте это займет ~2 секунды)
        fInfo.dwTimeout = 0; // Использовать стандартную частоту мерцания курсора Windows

        FlashWindowEx(ref fInfo);
    }
    #endregion


    public MainWindow()
    {
        InitializeComponent();

        // 1. Инициализируем ленту активности (чтобы видеть шаги загрузки)
        lstLiveActivity.Items.Clear();
        lstLiveActivity.Items.Add("Инициализация бортового компьютера...");

        // 2. Восстанавливаем состояние переключателя мониторинга из конфига
        chkEnableMonitoring.IsChecked = Properties.Settings.Default.AutoMonitoringEnabled;

        // 3. Выстраиваем дефолтные значения путей, если в системе пусто
        SetupDefaultPaths();

        // 4. Принудительно исправляем старые ошибки (если там застрял Saved Games)
        //FixApplicationConfiguration();

        // 5. Загружаем фильтры IP-адресов из гарантированно настроенной рабочей папки
        string filtersFilePath = System.IO.Path.Combine(WorkingDir, "filters.txt");
        if (!File.Exists(filtersFilePath))
        {
            EDList.SetDefaultFilters(filtersFilePath);
            lstLiveActivity.Items.Add("Создан базовый файл фильтров по умолчанию.");
        }
        else
        {
            EDList.LoadFilters(filtersFilePath);
            lstLiveActivity.Items.Add("Файл фильтров успешно загружен.");
        }

        // 6. Подвязываем события парсера к ядру программы
        EDList.OnParseStart += EDList_OnParseStart;
        EDList.OnParseProceed += EDList_OnParseProceed;
        EDList.OnParseComplete += EDList_OnParseComplete;

        // 7. Инициализируем и синхронизируем маршрутизаторы Mikrotik
        SetupAndSyncMikrotiksAsync();

        // 8. Запускаем фоновый 5-секундный таймер проверки статуса игры Elite Dangerous
        _gameCheckTimer = new System.Timers.Timer(5000);
        _gameCheckTimer.Elapsed += GameCheckTimer_Elapsed;
        _gameCheckTimer.AutoReset = true;
        _gameCheckTimer.Start();

        // 9. Фиксируем все изменения и дефолты в конфигурации
        Properties.Settings.Default.Save();

        // 10. Проверяем XML-файлы сети в папке игры и управляем видимостью кнопки лога
        UpdateNetLogButtonState();
    }


    /// <summary>
    /// Автоматически исправляет неверный путь в настройках приложения,
    /// если там застрял Saved Games или пустая строка.
    /// </summary>
    private void FixApplicationConfiguration()
    {
        string currentPath = Properties.Settings.Default.EDLogFolder;

        // Проверяем "грабли": если путь пустой или содержит Saved Games — переписываем
        if (string.IsNullOrEmpty(currentPath) || currentPath.Contains("Saved Games"))
        {
            lstLiveActivity.Items.Add("Обнаружена ошибка в конфиге приложения. Исправляю...");

            // Получаем правильный изолированный путь к сетевым логам из нашего нового класса
            string correctPath = EDIPSearch.Core.EliteFolders.NetLogsFolder;

            if (!string.IsNullOrEmpty(correctPath))
            {
                // Сохраняем правильный путь в системные настройки приложения
                Properties.Settings.Default.EDLogFolder = correctPath;
                Properties.Settings.Default.Save();

                lstLiveActivity.Items.Add("Настройки успешно исправлены.");
                lstLiveActivity.Items.Add($"Новый путь: ...\\{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(correctPath))}\\Logs");
            }
            else
            {
                lstLiveActivity.Items.Add("Внимание: Игра закрыта, не удалось автоматически вычислить путь.");
                lstLiveActivity.Items.Add("Запустите лаунчер/играйте или укажите путь вручную в Настройках.");
            }
        }
    }
    // Добавьте в класс MainWindow

    /// <summary>
    /// Проверяет системные настройки приложения и, если они пустые, 
    /// устанавливает корректные пути по умолчанию для всех сущностей.
    /// </summary>
    private void SetupDefaultPaths()
    {
        // 1. Дефолтный путь к СЕТЕВЫМ ЛОГАМ игры (если в конфиге пусто)
        if (string.IsNullOrEmpty(Properties.Settings.Default.EDLogFolder))
        {
            string detectedNetLogs = EDIPSearch.Core.EliteFolders.NetLogsFolder;

            if (!string.IsNullOrEmpty(detectedNetLogs))
            {
                Properties.Settings.Default.EDLogFolder = detectedNetLogs;
                lstLiveActivity.Items.Add("Установлен путь к сетевым логам по умолчанию.");
            }
            else
            {
                // Если игра закрыта и не найдена в реестре, временно оставляем пустым
                lstLiveActivity.Items.Add("Предупреждение: Путь к сетевым логам не определен автоматически.");
            }
        }

        // 2. Дефолтный путь к РАБОЧЕЙ ПАПКЕ приложения (где лежит filters.txt и routers.dat)
        // Вместо жесткого перетирания переменной WorkingDir в теле конструктора, фиксируем её в настройках
        if (string.IsNullOrEmpty(Properties.Settings.Default.DataFolder))
        {
            string defaultDataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDIPSearch");
            Properties.Settings.Default.DataFolder = defaultDataDir;
            lstLiveActivity.Items.Add("Установлена рабочая папка приложения по умолчанию.");
        }

        // Принудительно сохраняем дефолты в системе, чтобы они зафиксировались в файле App.config / user.config
        Properties.Settings.Default.Save();

        // Синхронизируем локальную переменную WorkingDir с гарантированно заполненной настройкой
        WorkingDir = Properties.Settings.Default.DataFolder;
        if (!Directory.Exists(WorkingDir)) Directory.CreateDirectory(WorkingDir);
    }


    private void BtnExportText_Click(object sender, RoutedEventArgs e)
    {
        // Генерируем динамическую временную метку (например, 20260825_201530)
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string defaultFileName = $"ed_filters_{timestamp}.txt";

        var saveFileDialog = new SaveFileDialog
        {
            Title = "Экспорт пула адресов для сторонних маршрутизаторов",
            Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
            FileName = defaultFileName, // ПОДСТАВЛЕНО: ed_дата_время.txt
            InitialDirectory = WorkingDir
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                string targetFilePath = saveFileDialog.FileName;

                // Выгружаем данные
                using (var writer = new StreamWriter(targetFilePath, false, Encoding.UTF8))
                {
                    writer.WriteLine("#=======================================================");
                    writer.WriteLine("# СВОДНЫЙ ПУЛ АДРЕСОВ ИСКЛЮЧЕНИЙ ELITE DANGEROUS");
                    writer.WriteLine($"# Сгенерировано автоматически: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
                    writer.WriteLine("# Подходит для ручного импорта в Keenetic, ASUS, TP-Link");
                    writer.WriteLine("#=======================================================");
                    writer.WriteLine();

                    // Выгружаем строки из твоего мастер-списка EDList
                    foreach (var ipObj in EDList.ipTable)
                    {
                        writer.WriteLine(ipObj.ToString());
                    }
                }

                tbStatusProp.Text = "Экспорт завершен.";
                tbStatusVal.Text = $"Сохранен файл: {System.IO.Path.GetFileName(targetFilePath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось выгрузить список: {ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }


    private void GameCheckTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        // Ищем процесс Elite Dangerous в системе
        bool currentStatus = Process.GetProcessesByName("EliteDangerous64").Any();

        // Проверяем, изменился ли статус с момента последней проверки
        if (currentStatus != _isEliteRunning)
        {
            _isEliteRunning = currentStatus;

            // Так как таймер работает в фоновом потоке, 
            // для обновления элементов UI WPF мы обязаны использовать Dispatcher
            Dispatcher.Invoke(() =>
            {
                if (_isEliteRunning)
                {
                    // 2. Иконка и статус для запущенной игры
                    txtGameIcon.Text = "🚀"; // Меняем символ-иконку на ракету
                    txtGameIcon.Foreground = System.Windows.Media.Brushes.Green;
                    txtGameStatus.Text = "ИГРА ЗАПУЩЕНА";
                    txtGameStatus.Foreground = System.Windows.Media.Brushes.Green;

                    // Запускаем мониторинг логов ТОЛЬКО если пользователь включил галочку в UI
                    if (chkEnableMonitoring.IsChecked == true)
                    {
                        StartLiveLogMonitoring();
                    }
                }
                else
                {
                    // Иконка и статус для закрытой игры
                    txtGameIcon.Text = "🛑"; // Меняем символ-иконку на стоп-сигнал
                    txtGameIcon.Foreground = System.Windows.Media.Brushes.Red;
                    txtGameStatus.Text = "ИГРА НЕ ЗАПУЩЕНА";
                    txtGameStatus.Foreground = System.Windows.Media.Brushes.Red;

                    StopLiveLogMonitoring();
                }
            });

        }
    }
    private void StartLiveLogMonitoring()
    {
        try
        {
            string logFolder = Properties.Settings.Default.EDLogFolder;
            if (!Directory.Exists(logFolder))
            {
                System.Diagnostics.Debug.WriteLine($"[Watcher] Папка логов не существует: {logFolder}");
                return;
            }

            // 1. Инициализируем или сбрасываем счетчики
            _linesReadCounter = 0;
            txtTotalLinesRead.Text = "0";

            // 2. Принудительно находим самый свежий файл прямо сейчас
            UpdateActiveLogFile(logFolder);

            // 3. Запускаем высокоточный секундный таймер для чтения дозаписи в файл
            if (_logReadTimer == null)
            {
                _logReadTimer = new System.Timers.Timer(1000); // Опрос раз в 1 секунду
                _logReadTimer.Elapsed += LogReadTimer_Elapsed;
                _logReadTimer.AutoReset = true;
            }
            _logReadTimer.Start();

            System.Diagnostics.Debug.WriteLine("[Watcher] Таймер мониторинга логов запущен.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Watcher Error] {ex.Message}");
        }
    }

    private void StopLiveLogMonitoring()
    {
        if (_logReadTimer != null)
        {
            _logReadTimer.Stop();
            _logReadTimer.Dispose();
            _logReadTimer = null;
        }

        _currentLogFilePath = null;
        _lastLogPosition = 0;

        Dispatcher.Invoke(() =>
        {
            txtCurrentLogFile.Text = "отключен";
        });
        System.Diagnostics.Debug.WriteLine("[Watcher] Таймер мониторинга логов остановлен.");
    }

    /// <summary>
    /// Сканирует папку и находит самый свежий лог-файл игры
    /// </summary>
    private void UpdateActiveLogFile(string logFolder)
    {
        try
        {
            if (!Directory.Exists(logFolder))
            {
                Dispatcher.Invoke(() => txtCurrentLogFile.Text = "Ошб: папка не найдена");
                return;
            }

            var directoryInfo = new DirectoryInfo(logFolder);

            // Берем файлы Journal.*.log или просто *.log, отсортированные по времени изменения
            var freshLogFile = directoryInfo.GetFiles("Journal.*.log")
                                             .OrderByDescending(f => f.LastWriteTime)
                                             .FirstOrDefault();

            // Если по строгой маске Элиты ничего нет, попробуем поискать любые файлы .log для страховки
            if (freshLogFile == null)
            {
                freshLogFile = directoryInfo.GetFiles("*.log")
                                            .OrderByDescending(f => f.LastWriteTime)
                                            .FirstOrDefault();
            }

            if (freshLogFile != null)
            {
                if (_currentLogFilePath != freshLogFile.FullName)
                {
                    _currentLogFilePath = freshLogFile.FullName;

                    // ИЗМЕНЕНИЕ ДЛЯ ОТЛАДКИ: Ставим 0 вместо freshLogFile.Length.
                    // Это заставит программу при старте сразу вычитать ВСЕ строки из текущего лога игры,
                    // счетчик строк мгновенно оживет (покажет 500, 1000 и т.д.), и таблица наполнится.
                    _lastLogPosition = 0;

                    Dispatcher.Invoke(() =>
                    {
                        txtCurrentLogFile.Text = freshLogFile.Name;
                        txtCurrentLogFile.ToolTip = freshLogFile.FullName;
                    });
                }
            }
            else
            {
                Dispatcher.Invoke(() => txtCurrentLogFile.Text = "Логи не найдены в папке");
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => txtCurrentLogFile.Text = $"Ошибка: {ex.Message}");
        }
    }


    private void LogReadTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentLogFilePath)) return;

        try
        {
            string logFolder = Properties.Settings.Default.EDLogFolder;
            UpdateActiveLogFile(logFolder);

            var fileInfo = new FileInfo(_currentLogFilePath);
            if (fileInfo.Length == _lastLogPosition) return;
            if (fileInfo.Length < _lastLogPosition) _lastLogPosition = 0;

            // Список для накопления IP, найденных ЗА ОДНУ СЕКУНДУ опроса таймера
            var discoveredIps = new List<IPclass>();
            int linesReadInThisTick = 0;

            using (var stream = new FileStream(_currentLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Position = _lastLogPosition;

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    
                    // БЫЛО:
                    // string? line;
                    // while ((line = reader.ReadLine()) != null) { ... Regex.Match(line, ...) ... }

                    // СТАЛО (Защита от оптимизатора Release):
                    while (true)
                    {
                        string? currentLine = reader.ReadLine();
                        if (currentLine == null) break; // Выходим, если лог кончился

                        linesReadInThisTick++;

                        // Создаем абсолютно изолированную копию строки для потока регулярки.
                        // Это на 100% запретит JIT-компилятору оптимизировать и перетирать память!
                        string internalLineCopy = string.Concat(currentLine);
                        Match match = Regex.Match(internalLineCopy, @"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b");
                        if (match.Success)
                        {
                            IPclass? parsedIp = IPclass.Parse($"{match.Value}/32");
                            if (parsedIp != null)
                            {
                                discoveredIps.Add(parsedIp);
                            }
                        }
                    }

                    _lastLogPosition = stream.Position;
                }
            }

            // Если мы что-то прочитали или нашли новые IP — отправляем это в UI ОДНИМ СИНХРОННЫМ БЛОКОМ
            if (linesReadInThisTick > 0)
            {
                // Используем Invoke вместо BeginInvoke, чтобы поток таймера подождал, пока WPF гарантированно перерисует интерфейс
                Dispatcher.Invoke(() =>
                {
                    // 1. Обновляем счетчик прочитанных строк лога
                    _linesReadCounter += linesReadInThisTick;
                    txtTotalLinesRead.Text = _linesReadCounter.ToString();

                    // 2. Замораживаем обновление DataGrid на время массового добавления
                    // (Это предотвратит ложные срабатывания CollectionChanged в WPF)
                    // Отключаем событие, чтобы UI не штормило, если у тебя там была подписка
                    int addrCount = EDList.ipTable.Count;
                    foreach (var ip in discoveredIps)
                    {
                        // Твой метод проверяет дубликаты и добавляет в ipTable
                        EDList.AddAddress(ip);
                    }
                    if (addrCount != EDList.ipTable.Count)
                    {
                        FlashMainWindow();
                    }
                    // 3. Если были добавлены реально новые IP, принудительно и безопасно обновляем таблицу
                    // ... Твой код внутри Dispatcher.Invoke в методе LogReadTimer_Elapsed ...
                    if (discoveredIps.Count > 0)
                    {
                        dgAddresses.ItemsSource = null;
                        dgAddresses.ItemsSource = EDList.ipTable;
                        txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();

                        // АВТОПРОКРУТКА: Проверяем, есть ли элементы в таблице
                        if (EDList.ipTable.Count > 0)
                        {
                            // Забираем самый последний добавленный объект IPclass из таблицы
                            var lastItem = EDList.ipTable[EDList.ipTable.Count - 1];

                            // Принудительно заставляем DataGrid плавно прокрутиться к этому элементу
                            dgAddresses.ScrollIntoView(lastItem);
                        }
                        //Подмигиваем иконкой, когда добавили новый IP.
                       
                    }

                });
            }
        }
        catch (IOException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Log Read Error] {ex.Message}");
        }
    }
  

    /// <summary>
    /// Безупречный поиск папки игры без использования реестра и поврежденных user.config лаунчера
    /// </summary>
    private string GetEliteGameFolder()
    {
        try
        {
            // 1. Способ №1 (Ультимативный): Если игра запущена, берем путь напрямую из процесса!
            var gameProcess = Process.GetProcessesByName("EliteDangerous64").FirstOrDefault();
            if (gameProcess != null)
            {
                string processPath = gameProcess.MainModule?.FileName;
                if (!string.IsNullOrEmpty(processPath))
                {
                    string? processDir = System.IO.Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(processDir) && Directory.Exists(processDir))
                    {
                        return processDir;
                    }
                }
            }

            // 2. Способ №2: Если игра закрыта, используем уже сохраненный в настройках приложения путь к логам!
            // Нам не нужен лаунчер, раз пилот уже настроил папку логов в frmSettings.
            string logFolder = Properties.Settings.Default.EDLogFolder;
            if (!string.IsNullOrEmpty(logFolder) && Directory.Exists(logFolder))
            {
                // Обычно логи лежат в: C:\Users\Имя\Saved Games\Frontier Developments\Elite Dangerous
                // А игра ставится в Steam/Epic/Frontier. Пробуем использовать стандартный путь Steam как подстраховку
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string steamDefault = System.IO.Path.Combine(programFiles, "Steam", "steamapps", "common", "Elite Dangerous", "Products", "elite-dangerous-odyssey-64");

                if (Directory.Exists(steamDefault)) return steamDefault;

                // Проверим также альтернативную папку Horizons
                string steamHorisons = System.IO.Path.Combine(programFiles, "Steam", "steamapps", "common", "Elite Dangerous", "Products", "FORC-FDEV-D-1010");
                if (Directory.Exists(steamHorisons)) return steamHorisons;
            }

            // 3. Способ №3: Поиск через системную запись инсталлятора (для standalone-версий)
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Elite Dangerous_is1"))
            {
                if (key != null)
                {
                    string installLoc = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLoc))
                    {
                        string productsPath = System.IO.Path.Combine(installLoc, "Products", "elite-dangerous-odyssey-64");
                        if (Directory.Exists(productsPath)) return productsPath;
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    // Добавьте в начало файла, если они еще не подключены:
    // using System.Windows;
    // using System.Xml.Linq;

    /// <summary>
    /// Проверяет конфигурацию игры, выводит статус в lstLiveActivity и скрывает/показывает кнопку
    /// </summary>
    // Добавьте в начало файла, если они еще не подключены:
    // using System.IO;
    // using System.Xml.Linq;
    // using System.Linq;

    /// <summary>
    /// Проверяет конфигурацию игры в AppConfig и приоритетном AppConfigLocal, выводит статус в lstLiveActivity
    /// </summary>
    // Добавьте в начало файла, если они еще не подключены:
    // using System.IO;
    // using System.Xml.Linq;
    // using System.Linq;
    // using EDIPSearch.Core;

    /// <summary>
    /// Проверяет конфигурацию сети строго через класс EliteFolders и управляет кнопкой на UI
    /// </summary>
    private void UpdateNetLogButtonState()
    {
        if (_isEliteRunning)
        {
            btnEnableNetLog.IsEnabled = false;
            btnEnableNetLog.ToolTip = "Нельзя изменить конфигурацию во время работы игры.";
            return;
        }

        // Используем наше новое свойство сущности конфигурации игры
        string configDir = EliteFolders.ConfigFolder;

        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir))
        {
            lstLiveActivity.Items.Add("Ошибка: Папка конфигурации игры не найдена. Запустите игру один раз.");
            btnEnableNetLog.Visibility = Visibility.Visible;
            btnEnableNetLog.IsEnabled = false;
            return;
        }

        string mainConfigPath = System.IO.Path.Combine(configDir, "AppConfig.xml");
        string localConfigPath = System.IO.Path.Combine(configDir, "AppConfigLocal.xml");
        bool isNetLogActive = false;

        if (File.Exists(localConfigPath))
        {
            isNetLogActive = CheckNetworkLogAttribute(localConfigPath);
        }

        if (!isNetLogActive && File.Exists(mainConfigPath))
        {
            isNetLogActive = CheckNetworkLogAttribute(mainConfigPath);
        }

        if (isNetLogActive)
        {
            lstLiveActivity.Items.Add("Статус сети: Сетевой лог игры АКТИВИРОВАН.");
            btnEnableNetLog.Visibility = Visibility.Collapsed; // Скрываем кнопку за ненадобностью
        }
        else
        {
            lstLiveActivity.Items.Add("Статус сети: Сетевой лог игры ВЫКЛЮЧЕН.");
            btnEnableNetLog.Visibility = Visibility.Visible;
            btnEnableNetLog.IsEnabled = true;
        }
    }

    /// <summary>
    /// Принудительно включает сетевой лог, создавая оверрид в AppConfigLocal.xml
    /// </summary>
    private void BtnEnableNetLog_Click(object sender, RoutedEventArgs e)
    {
        string configDir = EliteFolders.ConfigFolder;
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return;

        string localConfigPath = System.IO.Path.Combine(configDir, "AppConfigLocal.xml");

        try
        {
            XDocument doc = File.Exists(localConfigPath) ? XDocument.Load(localConfigPath) : new XDocument(new XElement("AppConfig"));
            XElement networkEl = doc.Descendants("Network").FirstOrDefault();

            if (networkEl == null)
            {
                networkEl = new XElement("Network");
                doc.Root?.Add(networkEl);
            }

            networkEl.SetAttributeValue("NetworkLog", "1");
            networkEl.SetAttributeValue("VerboseLogging", "1");
            doc.Save(localConfigPath);

            lstLiveActivity.Items.Add("AppConfigLocal.xml успешно обновлен. Сетевой лог включен.");
            MessageBox.Show("Сетевой лог успешно включен! Перезапустите игру для применения настроек.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);

            UpdateNetLogButtonState();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось обновить файл конфигурации: {ex.Message}", "Ошибка доступа", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    /// <summary>
    /// Вспомогательный метод парсинга XML-структуры Фронтиров
    /// </summary>
    private bool CheckNetworkLogAttribute(string xmlPath)
    {
        try
        {
            XDocument doc = XDocument.Load(xmlPath);
            XElement networkEl = doc.Descendants("Network").FirstOrDefault();
            if (networkEl != null)
            {
                // Игра считывает как атрибут NetworkLog, так и VerboseLogging в зависимости от версии
                var netLogAttr = networkEl.Attribute("NetworkLog");
                var verboseAttr = networkEl.Attribute("VerboseLogging");

                return (netLogAttr != null && netLogAttr.Value == "1") ||
                       (verboseAttr != null && verboseAttr.Value == "1");
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// ИСПРАВЛЕННЫЙ ИНИЦИАЛИЗАТОР (замена блока в конструкторе MainWindow):
    /// Полностью исключает автоматический запуск ломающего метода AutoDetectEliteDangerousPath в Saved Games
    /// </summary>
    private void InitializeApplicationPaths()
    {
        // Считываем путь, который пользователь настроил через frmSettings (это должна быть папка Logs в директории игры)
        string currentLogFolder = Properties.Settings.Default.EDLogFolder;

        if (string.IsNullOrEmpty(currentLogFolder) || !Directory.Exists(currentLogFolder))
        {
            lstLiveActivity.Items.Add("ВНИМАНИЕ: Укажите корректный путь к папке Logs игры в Настройках.");
            btnEnableNetLog.IsEnabled = false;
        }
        else
        {
            // Вызываем проверку XML-файлов сети только по реальному пути пользователя
            UpdateNetLogButtonState();
        }
    }

    // Добавьте в начало файла, если они еще не подключены:
    // using System.Collections.Generic;
    // using System.Linq;
    // using System.Threading.Tasks;
    // using EDIPSearch.Models;
    // using EDIPSearch.Network;

    /// <summary>
    /// Обработчик клика: Удаляет выбранные в таблице адреса из локальной базы, файла и роутеров
    /// </summary>
    // Добавьте в MainWindow.xaml.cs взамен старой версии

    /// <summary>
    /// Безопасный обработчик: удаляет выбранные адреса только из оперативной памяти и роутеров
    /// </summary>
    private async void BtnDeleteAddress_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = dgAddresses.SelectedItems.Cast<IPclass>().ToList();
        if (selectedRows.Count == 0) return;

        List<string> addressesToProcess = selectedRows.Select(x => x.ToString()).ToList();

        var confirmDialog = new frmConfirmDelete(addressesToProcess);
        confirmDialog.Owner = this;

        if (confirmDialog.ShowDialog() == true && confirmDialog.IsConfirmed)
        {
            lstLiveActivity.Items.Add($"Удаление объектов из текущей сессии ({selectedRows.Count} шт.)...");

            // 1. Чистим только оперативную память таблицы
            foreach (var ipObj in selectedRows)
            {
                EDList.ipTable.Remove(ipObj);
            }

            txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();

            // 2. Обновляем UI
            dgAddresses.ItemsSource = null;
            dgAddresses.ItemsSource = EDList.ipTable;

            // 3. Асинхронно отправляем команды удаления на Mikrotik
            List<MikrotikConfig> configuredRouters = RouterStorage.Load();
            if (configuredRouters == null || configuredRouters.Count == 0) return;

            await Task.Run(async () =>
            {
                foreach (var config in configuredRouters)
                {
                    var client = new MikrotikRestClient(config);
                    var connectionStatus = await client.TestConnectionAsync().ConfigureAwait(false);
                    if (connectionStatus != ConnectionStatus.Success) continue;

                    int successCount = 0;
                    foreach (string ipStr in addressesToProcess)
                    {
                        bool isRemoved = await client.DeleteAddressAsync(ipStr, config.TargetAddressList).ConfigureAwait(false);
                        if (isRemoved) successCount++;
                    }

                    Dispatcher.Invoke(() =>
                        lstLiveActivity.Items.Add($"Роутер [{config.Name}]: Удалено {successCount} из {addressesToProcess.Count} записей."));
                }
            });

            lstLiveActivity.Items.Add("Процедура удаления завершена.");
        }
    }


    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            this.DragMove(); // Позволяет перетаскивать окно мышкой за любое место заголовка
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }


    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Запоминаем выбор пользователя перед выходом
        Properties.Settings.Default.AutoMonitoringEnabled = chkEnableMonitoring.IsChecked ?? false;
        Properties.Settings.Default.Save(); // Записываем на диск

        // Твой старый код очистки таймеров
        if (_gameCheckTimer != null)
        {
            _gameCheckTimer.Stop();
            _gameCheckTimer.Dispose();
        }
        _logReadTimer?.Dispose();

        base.OnClosing(e);
    }


    // Хранилище для WAN-исключений (чтобы метод события их видел)
    private List<IPclass> _routerWanExclusions = new List<IPclass>();
    private List<MikrotikConfig> _activeRouters = new List<MikrotikConfig>();

    private async Task SetupAndSyncMikrotiksAsync()
    {
        tbStatusProp.Text = "Синхронизация роутеров...";
        _activeRouters = RouterStorage.Load();
        if (_activeRouters.Count == 0) return;

        _routerWanExclusions.Clear();

        // Глобальная база данных: [IP-адрес -> Самый свежий оставшийся таймаут среди всех роутеров]
        var globalTimeoutMap = new Dictionary<string, string>();

        // ЭТАП 1: Собираем данные и точные таймауты со всех роутеров
        foreach (var config in _activeRouters)
        {
            var client = new MikrotikRestClient(config);
            if (await client.TestConnectionAsync() != ConnectionStatus.Success) continue;

            // Забираем WAN IP для исключений
            var wanIp = await client.GetInterfaceIpAsync();
            if (wanIp != null)
            {
                wanIp.PoolSize = 32;
                _routerWanExclusions.Add(wanIp);
            }

            // Скачиваем словарь [IP -> Таймаут] с конкретного роутера
            var routerAddresses = await client.GetActiveAddressesWithTimeoutsAsync(config.TargetAddressList);
            foreach (var kvp in routerAddresses)
            {
                string ip = kvp.Key;
                string currentTimeout = kvp.Value;

                // Если адрес статический (таймаут пустой), он имеет приоритет — оставляем пустым.
                // Если адрес динамический, сохраняем его таймаут в общий котел.
                if (!globalTimeoutMap.ContainsKey(ip) || string.IsNullOrEmpty(currentTimeout))
                {
                    globalTimeoutMap[ip] = currentTimeout;
                }
            }
        }

        // ЭТАП 2: Наполняем твой главный EDList на форме для фильтрации логов
        foreach (var ipStr in globalTimeoutMap.Keys)
        {
            IPclass? ipObj = IPclass.Parse(ipStr);
            if (ipObj != null)
            {
                EDList.AddAddress(ipObj); // Твой умный список впитывает базовые адреса
            }
        }

        // ЭТАП 3: Рассылаем недостающие адреса на роутеры С КОРРЕКТНЫМ ВРЕМЕНЕМ ЖИЗНИ
        foreach (var config in _activeRouters)
        {
            var client = new MikrotikRestClient(config);
            if (await client.TestConnectionAsync() != ConnectionStatus.Success) continue;

            // Повторно запрашиваем точечную базу этого роутера, чтобы понять, чего ему не хватает
            var currentRouterMap = await client.GetActiveAddressesWithTimeoutsAsync(config.TargetAddressList);

            foreach (var masterKvp in globalTimeoutMap)
            {
                string masterIpStr = masterKvp.Key;
                string preciseTimeout = masterKvp.Value; // Точное оставшееся время (например "4d12:00:15")

                // Если на этом роутере этой записи вообще нет — пушим её с точным скопированным временем жизни!
                if (!currentRouterMap.ContainsKey(masterIpStr))
                {
                    // Если таймаут пустой (статическая запись), шлем null. 
                    // Если динамический — пробрасываем точную строку времени из Mikrotik!
                    string? finalTimeoutParam = string.IsNullOrEmpty(preciseTimeout) ? null : preciseTimeout;

                    await client.AddAddressAsync(masterIpStr, config.TargetAddressList, finalTimeoutParam);
                }
            }
        }

        // ЭТАП 4: Привязываем событие живого отслеживания
        EDList.OnAddressAdded += EDList_OnAddressAdded;

        // Обновляем DataGrid на экране
        Dispatcher.Invoke(() =>
        {
            dgAddresses.ItemsSource = null;
            dgAddresses.ItemsSource = EDList.ipTable;
            txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();
        });

        tbStatusProp.Text = "Роутеры синхронизированы. Мониторинг готов.";
    }


    // Убрали 'async', теперь это обычный быстрый метод, который не ругает компилятор
    private void EDList_OnAddressAdded(IPclass newAddress)
    {
        foreach (var wan in _routerWanExclusions)
        {
            if (wan.IPinPool(newAddress)) return;
        }

        string ipStr = newAddress.ToString();
        string? timeoutParam = (newAddress.PoolSize == 32 || newAddress.PoolSize == 0) ? "7d" : null;

        // ОБНОВЛЕНИЕ UI: Выполняем в потоке интерфейса
        Dispatcher.Invoke(() =>
        {
            // 1. Динамически обновляем счетчик количества адресов в твоем wiseIPList таблицы
            // Предполагаем, что у тебя коллекция в EDList называется ipTable или аналогично
            txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();

            // 2. Добавляем запись в ленту "живого лога" на правой панели с отметкой времени
            string timeStamp = DateTime.Now.ToString("HH:mm:ss");
            string logMessage = $"[{timeStamp}] Обнаружен хост: {ipStr} -> отправка на Mikrotik";

            lstLiveActivity.Items.Insert(0, logMessage); // Добавляем наверх, чтобы свежие события были видны сразу

            // Ограничим размер истории в UI (например, хранить последние 50 записей, чтобы не забивать память)
            if (lstLiveActivity.Items.Count > 50)
            {
                lstLiveActivity.Items.RemoveAt(lstLiveActivity.Items.Count - 1);
            }
        });

        // Сетевая фоновая отправка на роутеры
        Task.Run(async () =>
        {
            foreach (var config in _activeRouters)
            {
                try
                {
                    var client = new MikrotikRestClient(config);
                    await client.AddAddressAsync(ipStr, config.TargetAddressList, timeoutParam).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rest API] Ошибка отправки на {config.Name}: {ex.Message}");
                }
            }
        });
    }
    // Добавьте в класс MainWindow внутри MainWindow.xaml.cs
    // Добавьте в класс MainWindow внутри MainWindow.xaml.cs

    // Добавьте в MainWindow.xaml.cs взамен старой версии

    /// <summary>
    /// Безопасный обработчик: добавляет адрес только в оперативную память текущей сессии и на роутеры
    /// </summary>
    private async void BtnOverlaySubmit_Click(object sender, RoutedEventArgs e)
    {
        string rawInput = tbInputAddress.Text;

        // 1. Проверяем корректность введенного формата строки
        if (!IsValidIpOrSubnet(rawInput, out string normalizedIp))
        {
            lblValidationError.Visibility = Visibility.Visible;
            return;
        }

        // Парсим строку методом ядра в объект IPclass
        IPclass ipObj = IPclass.Parse(normalizedIp);
        if (ipObj == null)
        {
            lblValidationError.Visibility = Visibility.Visible;
            return;
        }

        // 2. Отправляем в ваше модифицированное ядро. 
        // Изменения на роутеры идут СТРОГО если функция вернула true (адрес добавлен в память сессии)
        bool isAddedLocally = EDList.AddAddress(ipObj);

        if (isAddedLocally)
        {
            // Закрываем оверлей ввода
            gridOverlay.Visibility = Visibility.Collapsed;
            lstLiveActivity.Items.Add($"Адрес {normalizedIp} добавлен в пул текущей сессии.");

            // Обновляем счетчик и таблицу на UI (без касания диска)
            txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();
            dgAddresses.ItemsSource = null;
            dgAddresses.ItemsSource = EDList.ipTable;

            // 3. АСИНХРОННЫЙ ПУШ НА МАРШРУТИЗАТОРЫ
            List<MikrotikConfig> configuredRouters = RouterStorage.Load();
            if (configuredRouters == null || configuredRouters.Count == 0) return;

            await Task.Run(async () =>
            {
                foreach (var config in configuredRouters)
                {
                    var client = new MikrotikRestClient(config);
                    var status = await client.TestConnectionAsync().ConfigureAwait(false);
                    if (status != ConnectionStatus.Success) continue;

                    // Для хостов /32 выставляем таймаут 7 дней (7d) согласно логике Фазы 2
                    string timeoutParam = (ipObj.PoolSize == 32 || ipObj.PoolSize == 0) ? "7d" : null;

                    // Отправляем запись в адрес-лист маршрутизатора через REST API
                    await client.AddAddressAsync(ipObj.ToString(), config.TargetAddressList, timeoutParam).ConfigureAwait(false);

                    Dispatcher.Invoke(() =>
                        lstLiveActivity.Items.Add($"Роутер [{config.Name}]: Адрес {ipObj} успешно добавлен."));
                }
            });
        }
        else
        {
            // Ядро заблокировало добавление (дубликат или перекрытие подсетью в памяти сессии)
            lstLiveActivity.Items.Add($"Отказ добавления {normalizedIp}: Адрес заблокирован фильтром подсетей ядра.");
            MessageBox.Show("Данный IP-адрес или подсеть уже обрабатываются в текущей сессии, либо полностью перекрываются существующей подсетью!",
                            "Фильтр подсетей ядра", MessageBoxButton.OK, MessageBoxImage.Warning);
            gridOverlay.Visibility = Visibility.Collapsed;
        }
    }


    /// <summary>
    /// Открывает оверлей добавления адреса
    /// </summary>
    private void BtnOpenOverlay_Click(object sender, RoutedEventArgs e)
    {
        tbInputAddress.Clear();
        lblValidationError.Visibility = Visibility.Collapsed;
        gridOverlay.Visibility = Visibility.Visible;
        tbInputAddress.Focus();
    }

    /// <summary>
    /// Закрывает оверлей без сохранения
    /// </summary>
    private void BtnOverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        gridOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Проверяет, является ли строка корректным IPv4 адресом или подсетью с маской (X.X.X.X/Y)
    /// </summary>
    private bool IsValidIpOrSubnet(string input, out string normalizedIp)
    {
        normalizedIp = input.Trim();
        if (string.IsNullOrEmpty(normalizedIp)) return false;

        // Если маски нет, для совместимости с вашей логикой добавляем /32
        if (!normalizedIp.Contains("/"))
        {
            normalizedIp += "/32";
        }

        string[] parts = normalizedIp.Split('/');
        if (parts.Length != 2) return false;

        // 1. Валидация самого IP-адреса
        if (!System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        // 2. Валидация маски подсети (от 0 до 32)
        if (!int.TryParse(parts[1], out int mask) || mask < 0 || mask > 32)
        {
            return false;
        }

        return true;
    }


    private void CreatePVK()
    {
        string filename_pvt = WorkingDir + "\\keys\\private.key";
        string filename_pub = WorkingDir + "\\keys\\public.key";
        if (!File.Exists(filename_pvt))
        {
            if (File.Exists(filename_pub)) File.Delete(filename_pub);
            var keygen = new SshKeyGenerator.SshKeyGenerator(2048); // 2048 — длина ключа в битах

            var privateKey = keygen.ToPrivateKey();
            // Console.WriteLine(privateKey);

            var publicSshKey = keygen.ToRfcPublicKey();
            // Console.WriteLine(publicSshKey);
            FileStream fs = File.Create(filename_pvt);
            byte[] buf = System.Text.Encoding.UTF8.GetBytes(privateKey);
            fs.Write(buf, 0, buf.Length);
            fs.Flush();
            fs.Close();
            //
            fs = File.Create(filename_pub);
            buf = System.Text.Encoding.UTF8.GetBytes(publicSshKey);
            fs.Write(buf, 0, buf.Length);
            fs.Flush();
            fs.Close();

        }

    }

    // 1. СОБЫТИЕ: СТАРТ ПАРСИНГА (Узнаем общее количество файлов)
    private void EDList_OnParseStart(object sender, WIP_Parse_StartEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Очищаем окно живого лога перед началом нового пакетного анализа
            lstLiveActivity.Items.Clear();

            string message = $"[СИСТЕМА] Запущен пакетный анализ. Всего файлов для обработки: {e.FilesCount}";
            lstLiveActivity.Items.Insert(0, message);

            tbStatusProp.Text = "Пакетный анализ...";
            tbStatusVal.Text = $"Обработано файлов: 0 из {e.FilesCount}";
        });
    }

    // 2. СОБЫТИЕ: ПРОГРЕСС ПАРСИНГА (Срабатывает при переходе к каждому следующему файлу)
    // Внутри твоего метода EDList_OnParseProceed (Прогресс парсинга)
    private void EDList_OnParseProceed(object sender, WIP_Parse_ProceedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            string shortName = System.IO.Path.GetFileName(e.Filename);

            // В RELEASE-МОДЕ: Если файлов ОЧЕНЬ много, частая запись в ListBox 
            // вызывает коллизии памяти. Обновляем ТОЛЬКО строку статуса внизу!
            tbStatusVal.Text = $"Анализ: {shortName} ([{e.FileIndex}])";

            // Добавление в ListBox для логов делаем только для вех, 
            // например для каждого 5-го файла, чтобы UI не падал в гонку потоков
            if (e.FileIndex % 5 == 0)
            {
                lstLiveActivity.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Обработка пакета [{e.FileIndex}]");
            }
        }));
    }


    // 3. СОБЫТИЕ: ЗАВЕРШЕНИЕ ПАРСИНГА
    private void EDList_OnParseComplete(object sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            string endMessage = $"[СИСТЕМА] Пакетный анализ полностью завершен. Сводная таблица обновлена.";
            lstLiveActivity.Items.Insert(0, endMessage);

            tbStatusProp.Text = "Анализ завершен успешно.";
            // Выводим итоговое количество элементов в твоем wiseIPList таблицы
            tbStatusVal.Text = $"Итого уникальных сетей в базе: {EDList.ipTable.Count}";
            // ======================================================================
            // НАША НОВАЯ НАСТРОЙКА: Безопасное удаление старых файлов netLog
            // ======================================================================
            if (Properties.Settings.Default.DeleteLogsAfterParse)
            {
                string netLogFolder = EDIPSearch.Core.EliteFolders.NetLogsFolder;

                if (!string.IsNullOrEmpty(netLogFolder) && Directory.Exists(netLogFolder))
                {
                    try
                    {
                        var directoryInfo = new DirectoryInfo(netLogFolder);
                        var logFiles = directoryInfo.GetFiles("netLog.*.log");

                        DateTime today = DateTime.Today;
                        int deletedCount = 0;

                        foreach (var file in logFiles)
                        {
                            // Безопасность: сносим файлы логов, созданные строго ДО сегодняшнего дня
                            if (file.LastWriteTime.Date < today)
                            {
                                try
                                {
                                    file.Delete();
                                    deletedCount++;
                                }
                                catch
                                {
                                    // Если файл занят игрой или другим процессом — просто пропускаем его
                                }
                            }
                        }

                        if (deletedCount > 0)
                        {
                            string cleanMessage = $"[СИСТЕМА] Очистка диска: Удалено устаревших файлов логов: {deletedCount} шт.";
                            lstLiveActivity.Items.Insert(0, cleanMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        string errMessage = $"[СИСТЕМА] Ошибка при очистке папки логов: {ex.Message}";
                        lstLiveActivity.Items.Insert(0, errMessage);
                    }
                }
            }
        });
    }


    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {

    }
    private void LogAnalyze(object sender, RoutedEventArgs e)
    {
        EDList.ParseEDLogsAsync(EDLogs);
    }
    private void SaveList(object sender, RoutedEventArgs e)
    {
        SaveList(false);
    }
    private void SaveListMikrotik(object sender, RoutedEventArgs e)
    {
        SaveList(true);
    }
    private void SaveList(bool forMikrotik)
    {
        SaveFileDialog SFD = new SaveFileDialog();
        SFD.InitialDirectory = WorkingDir;
        SFD.AddExtension = true;
        SFD.CreatePrompt = true;
        if (forMikrotik)
        {
            SFD.Title = "Сохранить список адресов для Mikrotik";
            SFD.Filter = "Mikrotik resource(*.RSC)|*.rsc|Текстовый файл(*.txt)|*.txt";
        }
        else
        {
            SFD.Title = "Сохранить список адресов";
            SFD.Filter = "Текстовый файл(*.txt)|*.txt";
        }
        if (SFD.ShowDialog() == true)
        {
            EDList.SaveTo(SFD.FileName, forMikrotik);
        }
    }

    

    private void MenuItem_Click_1(object sender, RoutedEventArgs e)
    {
        var a = new frmMikrotikRestConfig();
        a.ShowDialog();
    }

    private void ChkEnableMonitoring_Checked(object sender, RoutedEventArgs e)
    {
        // Защита от падения во время инициализации окна
        if (chkEnableMonitoring == null || tbStatusProp == null) return;

        if (_isEliteRunning)
        {
            StartLiveLogMonitoring();
        }
        tbStatusProp.Text = "Автоматическое отслеживание логов включено.";
    }

    private void ChkEnableMonitoring_Unchecked(object sender, RoutedEventArgs e)
    {
        if (chkEnableMonitoring == null || tbStatusProp == null) return;

        StopLiveLogMonitoring();
        tbStatusProp.Text = "Автоматическое отслеживание логов отключено.";
    }


    private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // Открываем созданную ранее отдельную форму настроек REST API
        var settingsWindow = new frmMikrotikRestConfig { Owner = this };
        settingsWindow.ShowDialog();
    }

    private async void BtnOldParse_Click(object sender, RoutedEventArgs e)
    {
        // Блокируем UI от повторных нажатий на время обработки истории
        btnOldParse.IsEnabled = false;
        chkEnableMonitoring.IsEnabled = false;

        // 1. Отвязываем событие живой отправки хостов на время чтения истории,
        // чтобы не флудить в сеть одиночными запросами в процессе парсинга
        EDList.OnAddressAdded -= EDList_OnAddressAdded;

        // 2. Запускаем твой тяжелый метод анализа папки в фоновом потоке ОС.
        // Интерфейс приложения при этом остается полностью живым и отзывчивым!
        await Task.Run(() =>
        {
            // Вызываем твой ОРИГИНАЛЬНЫЙ метод пакетного анализа папки с логами
            // Пример: EDList.ParseAllHistoryFiles();
            EDList.ParseEDLogs(Settings.Default.EDLogFolder, false);
        });

        // 3. Анализ завершен. Один раз красиво обновляем DataGrid и счетчик на экране
        dgAddresses.ItemsSource = null;
        dgAddresses.ItemsSource = EDList.ipTable;
        txtTotalAddressesCount.Text = EDList.ipTable.Count.ToString();

        // 4. ПРЯМОЙ ПУШ: Отправляем готовый список на роутеры без лишних проверок
        tbStatusProp.Text = "Синхронизация списков на роутерах...";
        var routers = RouterStorage.Load();
        var syncCore = new EDIPSearch.Core.MikrotikSyncCore();

        // Сетевую отправку тоже делаем в фоне, чтобы форма не моргала
        await Task.Run(async () =>
        {
            await syncCore.PushNewAddressesToRoutersAsync(routers, EDList).ConfigureAwait(false);
        });

        // 5. Возвращаем событие живого мониторинга логов на место для отлова IP во время игры
        EDList.OnAddressAdded += EDList_OnAddressAdded;

        // Разблокируем интерфейс
        btnOldParse.IsEnabled = true;
        chkEnableMonitoring.IsEnabled = true;
        tbStatusProp.Text = "Готово.";
        tbStatusVal.Text = $"В базе роутеров обновлено элементов: {EDList.ipTable.Count}";
    }



    private void BtnOldSettings_Click(object sender, RoutedEventArgs e)
    {
        var a = new frmSettings();
        if(a.ShowDialog()==true)
        {
            Settings.Default.Save();
        }
    }


    // ... внутри класса MainWindow ...

    /// <summary>
    /// Продвинутый автоматический поиск папки журналов Elite Dangerous
    /// </summary>
    /// <summary>
    /// Безупречное автоопределение папки сетевых журналов netLog в каталоге установки игры
    /// </summary>
    private string AutoDetectEliteDangerousPath()
    {
        // --- ШАГ 1: Поиск в системном реестре установленных программ Windows (Работает при закрытой игре) ---
        string[] registryPaths = new string[]
        {
        // 1. Путь для 64-битных систем (Steam / Epic / Frontier Launcher)
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Elite Dangerous",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Elite Dangerous",
        // 2. Альтернативный ключ Epic Games Store
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Eden", // Внутреннее имя Элиты в Epic Store
        // 3. Прямые пути регистрации команд запуска Windows (App Paths)
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\EliteDangerous64.exe"
        };

        foreach (var regPath in registryPaths)
        {
            try
            {
                // Ищем сначала в ветке LocalMachine, затем в CurrentUser
                using (var key = Registry.LocalMachine.OpenSubKey(regPath) ?? Registry.CurrentUser.OpenSubKey(regPath))
                {
                    if (key != null)
                    {
                        // Для Uninstall-ключей забираем InstallLocation (папку установки)
                        string? installDir = key.GetValue("InstallLocation") as string;

                        // Если это был ключ App Paths, забираем значение по умолчанию (путь к .exe)
                        if (string.IsNullOrEmpty(installDir))
                        {
                            string? exePath = key.GetValue("") as string; // Значение (По умолчанию)
                            if (!string.IsNullOrEmpty(exePath)) installDir = System.IO.Path.GetDirectoryName(exePath);
                        }

                        if (!string.IsNullOrEmpty(installDir))
                        {
                            // Достраиваем сквозной путь до сетевой папки Logs Одиссеи
                            string logPath = System.IO.Path.Combine(installDir, "Products", "elite-dangerous-odyssey-64", "Logs");
                            if (Directory.Exists(logPath))
                            {
                                System.Diagnostics.Debug.WriteLine($"[Registry AutoDetect] Папка логов найдена в реестре Windows: {logPath}");
                                return logPath;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // --- ШАГ 2: Резервный динамический перехват (Если реестр пуст, но игра запущена прямо сейчас) ---
        try
        {
            Process[] processes = Process.GetProcessesByName("EliteDangerous64");
            if (processes.Length > 0)
            {
                string? exeFullPath = processes[0].MainModule?.FileName;
                if (!string.IsNullOrEmpty(exeFullPath))
                {
                    string? productDirectory = System.IO.Path.GetDirectoryName(exeFullPath);
                    if (!string.IsNullOrEmpty(productDirectory))
                    {
                        string dynamicLogPath = System.IO.Path.Combine(productDirectory, "Logs");
                        if (Directory.Exists(dynamicLogPath))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Process AutoDetect] Путь перехвачен от запущенной игры: {dynamicLogPath}");
                            return dynamicLogPath;
                        }
                    }
                }
            }
        }
        catch { }

        return string.Empty; // Если ничего не помогло, пилот выберет папку сам через проводник 📁
    }


}