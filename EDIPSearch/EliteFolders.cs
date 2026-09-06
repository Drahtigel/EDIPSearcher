using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace EDIPSearch.Core;

public static class EliteFolders
{
    /// <summary>
    /// Сущность 1: Путь к папке журналов игровых событий (Saved Games).
    /// Всегда фиксирован в профиле пользователя Windows.
    /// </summary>
    public static string SaveGameFolder
    {
        get
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "Saved Games", "Frontier Developments", "Elite Dangerous");
        }
    }

    /// <summary>
    /// Внутреннее ультимативное ядро для поиска корневой директории установленной игры.
    /// </summary>
    private static string GetProductRoot()
    {
        try
        {
            // Шаг 1: Если игра запущена, берем путь напрямую из памяти ОС
            var gameProcess = Process.GetProcessesByName("EliteDangerous64").FirstOrDefault();
            if (gameProcess != null)
            {
                string processPath = gameProcess.MainModule?.FileName;
                if (!string.IsNullOrEmpty(processPath))
                {
                    string processDir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrEmpty(processDir) && Directory.Exists(processDir))
                        return processDir;
                }
            }

            // Шаг 2: Подстраховка через стандартный реестр Windows (для автономных установщиков)
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Elite Dangerous_is1"))
            {
                if (key != null)
                {
                    string installLoc = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLoc))
                    {
                        string odysseyPath = Path.Combine(installLoc, "Products", "elite-dangerous-odyssey-64");
                        if (Directory.Exists(odysseyPath)) return odysseyPath;

                        string horizonsPath = Path.Combine(installLoc, "Products", "FORC-FDEV-D-1010");
                        if (Directory.Exists(horizonsPath)) return horizonsPath;
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }
    /// <summary>
    /// Сущность 2: Путь к папке конфигурационных XML-файлов (где лежат AppConfig.xml и AppConfigLocal.xml).
    /// Совпадает с корневой директорией продукта.
    /// </summary>
    public static string ConfigFolder
    {
        get
        {
            return GetProductRoot();
        }
    }

    /// <summary>
    /// Сущность 3: Путь к папке сетевых логов игры (Logs).
    /// Находится внутри корневой директории установленного продукта.
    /// </summary>
    public static string NetLogsFolder
    {
        get
        {
            string root = GetProductRoot();
            if (string.IsNullOrEmpty(root)) return string.Empty;

            string logsPath = Path.Combine(root, "Logs");

            // Гарантируем физическое существование папки Logs внутри игры
            if (!Directory.Exists(logsPath))
            {
                try { Directory.CreateDirectory(logsPath); } catch { }
            }

            return logsPath;
        }
    }
}
