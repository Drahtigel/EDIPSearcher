using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EDIPSearch.Models;

public class MikrotikConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Новый роутер";
    public string InternalIp { get; set; } = "192.168.88.1";
    public int Port { get; set; } = 443;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string EncryptedPassword { get; set; } = string.Empty; // Храним только в шифрованном виде
    public string TargetAddressList { get; set; } = "ED"; // Имя списка на роутере
    public string WanInterface { get; set; } = "ether1";

    // Свойство для работы в UI (не сериализуется в JSON напрямую)
    [System.Text.Json.Serialization.JsonIgnore]
    public string Password
    {
        get => Decrypt(EncryptedPassword);
        set => EncryptedPassword = Encrypt(value);
    }

    #region DPAPI Encryption
    private static readonly byte[] Entropy = { 0x05, 0x56, 0x0B, 0x11, 0x03, 0x13 }; // Дополнительная соль

    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    private string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText)) return string.Empty;
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch { return "ОШИБКА_ДЕШИФРОВАНИЯ"; }
    }
    #endregion
}

public static class RouterStorage
{
    private static readonly string FolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EDIPSearch");
    private static readonly string FilePath = Path.Combine(FolderPath, "routers.dat");

    public static void Save(List<MikrotikConfig> routers)
    {
        if (!Directory.Exists(FolderPath)) Directory.CreateDirectory(FolderPath);
        string json = JsonSerializer.Serialize(routers, new JsonSerializerOptions { WriteIndented = true });

        // Шифруем весь файл конфигурации целиком для максимальной безопасности (IP, порты, логины)
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encryptedBytes);
    }

    public static List<MikrotikConfig> Load()
    {
        if (!File.Exists(FilePath)) return new List<MikrotikConfig>();
        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(FilePath);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<List<MikrotikConfig>>(json) ?? new List<MikrotikConfig>();
        }
        catch
        {
            return new List<MikrotikConfig>(); // Если файл поврежден или чужой пользователь
        }
    }
}
