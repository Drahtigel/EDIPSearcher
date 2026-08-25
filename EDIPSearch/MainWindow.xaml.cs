using EDIPSearch.Core;
using EDIPSearch.Models;
using EDIPSearch.Network;
using EDIPSearch.Properties;
using ipinpool;
using Microsoft.Win32;
using System.IO;
using System.Text;
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
using System.Diagnostics;
using System.Linq;
using System.Timers;
using System.Text.RegularExpressions;

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
    public MainWindow()
    {
        InitializeComponent();
        WorkingDir = System.IO.Path.Combine(WorkingDir, "EDIPSearch");
        // Восстанавливаем состояние переключателя из настроек при старте
        chkEnableMonitoring.IsChecked = Properties.Settings.Default.AutoMonitoringEnabled;

        if (Settings.Default.DataFolder != string.Empty)
        {
            WorkingDir = Settings.Default.DataFolder;
        }
        else
        {
            Settings.Default.DataFolder = WorkingDir;
        }

        if (!Directory.Exists(WorkingDir)) Directory.CreateDirectory(WorkingDir);
        // if (!Directory.Exists(WorkingDir + "\\routers\\profiles")) Directory.CreateDirectory(WorkingDir + "\\routers\\profiles");
        // if (!Directory.Exists(WorkingDir + "\\keys")) Directory.CreateDirectory(WorkingDir + "\\keys");
        // 2. АВТООПРЕДЕЛЕНИЕ ПУТИ К ЛОГАМ ИГРЫ ПРИ ПЕРВОМ СТАРТЕ
        string currentLogFolder = Properties.Settings.Default.EDLogFolder;
        if (string.IsNullOrEmpty(currentLogFolder))
        {
            // Если в конфиге пусто — запускаем перенесенный автопоиск
            currentLogFolder = AutoDetectEliteDangerousPath();

            if (!string.IsNullOrEmpty(currentLogFolder))
            {
                Properties.Settings.Default.EDLogFolder = currentLogFolder;
                Properties.Settings.Default.Save(); // Фиксируем найденный путь в системе
            }
        }
        //Инициализация Mikrotik REST API
        if (!File.Exists(WorkingDir + "\\filters.txt"))
        {
            EDList.SetDefaultFilters(WorkingDir + "\\filters.txt");
        }
        else
        {
            EDList.LoadFilters(WorkingDir + "\\filters.txt");
        }
        // Создаем экземпляр синхронизатора (например, на уровне MainWindow)
        EDList.OnParseStart += EDList_OnParseStart;
        EDList.OnParseProceed += EDList_OnParseProceed;
        EDList.OnParseComplete += EDList_OnParseComplete;
        SetupAndSyncMikrotiksAsync(); //Инициализация и синхронизация маршрутизаторов.
                                     // Task.Run(SetupAndSyncMikrotiksAsync);
        // Инициализируем таймер проверки игры (5000 миллисекунд = 5 секунд)
        _gameCheckTimer = new System.Timers.Timer(5000);
        _gameCheckTimer.Elapsed += GameCheckTimer_Elapsed;
        _gameCheckTimer.AutoReset = true;
        _gameCheckTimer.Start(); // Запускаем постоянный фоновый опрос

        //Загрузка роутеров
        //  SetupTestRouter(); // <--- Заглушка
        //Сохранение настроек - последний этап инициализации.
        Settings.Default.Save();
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
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        linesReadInThisTick++;

                        // Парсим строку прямо здесь, в фоновом потоке таймера, чтобы не грузить UI регексами!
                        Match match = Regex.Match(line, @"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b");
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

                    foreach (var ip in discoveredIps)
                    {
                        // Твой метод проверяет дубликаты и добавляет в ipTable
                        EDList.AddAddress(ip);
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
    private void EDList_OnParseProceed(object sender, WIP_Parse_ProceedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // Извлекаем только имя файла из полного пути для компактности в UI
            string shortName = System.IO.Path.GetFileName(e.Filename);

            // Выводим красивое лог-сообщение в правую панель
            string logMessage = $"[{DateTime.Now.ToString("HH:mm:ss")}] Анализ файла [{e.FileIndex}]: {shortName}";
            lstLiveActivity.Items.Insert(0, logMessage);

            // Чтобы список не раздувался до бесконечности, держим последние 100 записей
            if (lstLiveActivity.Items.Count > 100)
                lstLiveActivity.Items.RemoveAt(lstLiveActivity.Items.Count - 1);

            // Обновляем счетчик прогресса в статус-баре внизу экрана.
            // Так как e.FilesCount доступен только на старте, мы можем либо хранить переменную на уровне класса,
            // либо просто выводить текущий индекс файла и имя.
            tbStatusVal.Text = $"Файл: {shortName} (Индекс: {e.FileIndex})";
        });
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
    private string AutoDetectEliteDangerousPath()
    {
        // 1. Стандартный путь Windows (Saved Games) в профиле пользователя
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
                                string steamLogPath = System.IO.Path.Combine(steamPath, "steamapps", "common", "Elite Dangerous");
                                if (Directory.Exists(steamLogPath)) return steamLogPath;
                            }
                    }
            }
        }
        catch { }

        return string.Empty;
    }

}