using EDIPSearch.Models;
using ipinpool;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EDIPSearch.Network;

public enum ConnectionStatus { Success, Unauthorized, Unreachable }

public class MikrotikRestClient
{
    private readonly HttpClient _httpClient;
    public MikrotikConfig Config { get; }
    private class AddressListEntry
    {
        [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
        [JsonPropertyName("disabled")] public string Disabled { get; set; } = "false";
        [JsonPropertyName("timeout")] public string Timeout { get; set; } = string.Empty; // ДОБАВЛЕНО: Сюда прилетит время вроде "5d04:12:30" или "01:20:00"
    }

    public MikrotikRestClient(MikrotikConfig config)
    {
        Config = config;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        string protocol = config.UseSsl ? "https" : "http";
        _httpClient.BaseAddress = new Uri($"{protocol}://{config.InternalIp}:{config.Port}/rest/");

        var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }
    #region Вспомогательные JSON-модели
    private class UniqueListEntry
    {
        [JsonPropertyName("list")] public string ListName { get; set; } = string.Empty;
    }

    

    private class InterfaceAddressEntry
    {
        [JsonPropertyName("address")] public string Address { get; set; } = string.Empty; // Возвращает "IP/маска"
    }

    private class InterfaceEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }
    #endregion

    /// <summary>
    /// Запрос ВСЕХ интерфейсов роутера (для выпадающего списка выбора WAN)
    /// </summary>
    public async Task<List<string>> GetInterfaceNamesAsync()
    {
        var interfaceNames = new List<string>();
        try
        {
            // Запрашиваем только свойство 'name' со всех интерфейсов роутера
            var response = await _httpClient.GetAsync("interface?.proplist=name");
            if (!response.IsSuccessStatusCode) return interfaceNames;

            var json = await response.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<InterfaceEntry>>(json);

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.Name))
                        if(entry.Name.Trim().ToLower()!="lo")
                            interfaceNames.Add(entry.Name.Trim());
                }
            }
        }
        catch { }
        return interfaceNames;
    }

    public async Task<List<string>> GetAddressListNamesAsync()
    {
        var listNames = new List<string>();
        try
        {
            // Фильтруем вывод через .proplist=list, чтобы забирать только имена списков
            var response = await _httpClient.GetAsync("ip/firewall/address-list?.proplist=list");
            if (!response.IsSuccessStatusCode) return listNames;

            var json = await response.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<UniqueListEntry>>(json);

            if (entries != null)
            {
                var uniqueSet = new HashSet<string>();
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(entry.ListName))
                        uniqueSet.Add(entry.ListName);
                }
                listNames.AddRange(uniqueSet);
            }
        }
        catch { }
        return listNames;
    }

    /// <summary>
    /// Безопасное получение точного внешнего IP-адреса роутера хостом /32.
    /// Использует явно указанный пользователем интерфейс из конфигурации.
    /// </summary>
    public async Task<IPclass?> GetInterfaceIpAsync()
    {
        string? detectedRawIp = null;
        string targetIface = Config.WanInterface; // Берем имя интерфейса из конфига роутера

        // Шаг 1: Запрашиваем IP строго с указанного интерфейса
        try
        {
            var response = await _httpClient.GetAsync($"ip/address?interface={targetIface}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var entries = JsonSerializer.Deserialize<List<InterfaceAddressEntry>>(json);

                if (entries != null && entries.Count > 0 && !string.IsNullOrEmpty(entries[0].Address))
                {
                    detectedRawIp = entries[0].Address.Trim();
                }
            }
        }
        catch { /* Ошибка запроса к роутеру */ }

        // Шаг 2: Если адрес найден, проверяем на "серость" и отсекаем маску
        if (!string.IsNullOrEmpty(detectedRawIp))
        {
            int slashIndex = detectedRawIp.IndexOf('/');
            string cleanIp = slashIndex > 0 ? detectedRawIp.Substring(0, slashIndex) : detectedRawIp;

            if (!IsPrivateIp(cleanIp))
            {
                // Адрес белый, принудительно возвращаем хост /32 для твоего пула
                return IPclass.Parse($"{cleanIp}/32");
            }
            // Если адрес приватный (за NAT провайдера), то локальный адрес интерфейса (например 10.x.x.x) 
            // нам не поможет предотвратить петлю, так как лог игры зафиксирует глобальный IP. Идем к Шагу 3.
        }

        // Шаг 3: Интерфейс пуст или за NAT — стучимся на внешний сервис через этот роутер
        try
        {
            using (var externalClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
            {
                string publicIp = await externalClient.GetStringAsync("https://icanhazip.com");
                publicIp = publicIp.Trim();

                if (!string.IsNullOrEmpty(publicIp) && System.Net.IPAddress.TryParse(publicIp, out _))
                {
                    return IPclass.Parse($"{publicIp}/32");
                }
            }
        }
        catch { }

        return null;
    }


    /// <summary>
    /// Проверка, является ли IP-адрес приватным (серым) в рамках стандартов RFC 1918 и CGNAT RFC 6598
    /// </summary>
    private bool IsPrivateIp(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out IPAddress? ip)) return true;
        byte[] bytes = ip.GetAddressBytes();

        if (bytes.Length != 4) return true; // Нас интересует только IPv4

        // 10.0.0.0/8
        if (bytes[0] == 10) return true;

        // 172.16.0.0/12
        if (bytes[0] == 172 && (bytes[1] >= 16 && bytes[1] <= 31)) return true;

        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;

        // 100.64.0.0/10 (CGNAT провайдеров — очень частая история на домашних тарифах)
        if (bytes[0] == 100 && (bytes[1] >= 64 && bytes[1] <= 127)) return true;

        return false;
    }


    /// <summary>
    /// 4. Запрос содержимого конкретного списка (возвращает ТОЛЬКО АКТИВНЫЕ строки-адреса из Mikrotik)
    /// </summary>
    public async Task<List<string>> GetRawAddressesFromListAsync(string listName)
    {
        var rawAddresses = new List<string>();
        try
        {
            // Запрашиваем свойства address и disabled со списка файрвола
            var response = await _httpClient.GetAsync($"ip/firewall/address-list?list={listName}&.proplist=address,disabled").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return rawAddresses;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        // Проверяем, отключена ли запись
                        bool isDisabled = false;
                        if (element.TryGetProperty("disabled", out JsonElement disabledProp))
                        {
                            string? disabledStr = disabledProp.GetString();
                            // Mikrotik возвращает "true" для отключенных записей
                            if (!string.IsNullOrEmpty(disabledStr) && disabledStr.Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                isDisabled = true;
                            }
                        }

                        // Если запись активна (не disabled), забираем её IP-адрес
                        if (!isDisabled && element.TryGetProperty("address", out JsonElement addrProp))
                        {
                            string? addr = addrProp.GetString();
                            if (!string.IsNullOrEmpty(addr))
                            {
                                rawAddresses.Add(addr.Trim());
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return rawAddresses;
    }


    /// <summary>
    /// 5. Добавить новый IP-адрес в список файрвола (Аналог add в CLI)
    /// </summary>
    /// <summary>
    /// Добавить новый IP-адрес в список файрвола с опциональным временем жизни (timeout)
    /// </summary>
    public async Task<bool> AddAddressAsync(string address, string listName, string? timeout = null)
    {
        try
        {
            // Создаем динамический объект для JSON payload
            object payload;
            if (!string.IsNullOrEmpty(timeout))
            {
                payload = new { address = address, list = listName, timeout = timeout };
            }
            else
            {
                payload = new { address = address, list = listName };
            }

            var content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PutAsync("ip/firewall/address-list", content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Проверка связи с роутером и аутентификация прав пользователя
    /// </summary>
    public async Task<ConnectionStatus> TestConnectionAsync()
    {
        try
        {
            // Добавляем .ConfigureAwait(false) в конец асинхронного вызова
            var response = await _httpClient.GetAsync("system/resource").ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return ConnectionStatus.Success;
            if (response.StatusCode == HttpStatusCode.Unauthorized) return ConnectionStatus.Unauthorized;

            return ConnectionStatus.Unreachable;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return ConnectionStatus.Unauthorized;
        }
        catch
        {
            return ConnectionStatus.Unreachable;
        }
    }

    /// <summary>
    /// 4. Запрос содержимого списка. Возвращает Словарь [IP-адрес -> Оставшийся таймаут]
    /// </summary>
    public async Task<Dictionary<string, string>> GetActiveAddressesWithTimeoutsAsync(string listName)
    {
        var addressMap = new Dictionary<string, string>();
        try
        {
            // Явно просим Mikrotik вернуть address, disabled и timeout
            var response = await _httpClient.GetAsync($"ip/firewall/address-list?list={listName}&.proplist=address,disabled,timeout").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return addressMap;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        bool isDisabled = false;
                        if (element.TryGetProperty("disabled", out JsonElement disabledProp))
                        {
                            string? disabledStr = disabledProp.GetString();
                            if (!string.IsNullOrEmpty(disabledStr) && disabledStr.Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                isDisabled = true;
                            }
                        }

                        if (!isDisabled && element.TryGetProperty("address", out JsonElement addrProp))
                        {
                            string? addr = addrProp.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(addr))
                            {
                                // Забираем таймаут. Если его нет (статическая запись), запишем пустую строку
                                string timeout = string.Empty;
                                if (element.TryGetProperty("timeout", out JsonElement timeoutProp))
                                {
                                    timeout = timeoutProp.GetString() ?? string.Empty;
                                }

                                // Складываем в словарь. Если Mikrotik вернул дубль (такое бывает при сбоях), перезапишем свежим
                                addressMap[addr] = timeout;
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return addressMap;
    }

}


