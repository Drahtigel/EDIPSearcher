using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EDIPSearch.Models;
using EDIPSearch.Network;
using ipinpool; // Твое пространство имен для wiseIPList и IPclass

namespace EDIPSearch.Core;

public class MikrotikSyncCore
{
    // Список для фильтра-исключений (WAN IP-адреса наших роутеров)
    public List<IPclass> RouterWanExclusions { get; private set; } = new List<IPclass>();

    // Единый сквозной список адресов, построенный на основе данных со всех роутеров
    public wiseIPList GlobalRouterCache { get; private set; } = new wiseIPList();

    /// <summary>
    /// Фаза 1: Опрос роутеров, сбор WAN IP и построение единой базы существующих адресов
    /// </summary>
    /// <param name="configuredRouters">Список настроек роутеров, загруженный из RouterStorage</param>
    public async Task InitializeAndFetchAsync(List<MikrotikConfig> configuredRouters)
    {
        // Сбрасываем старые данные перед новым опросом
        RouterWanExclusions.Clear();
        GlobalRouterCache = new wiseIPList();

        foreach (var config in configuredRouters)
        {
            var client = new MikrotikRestClient(config);

            // 1. Проверяем связь, если роутер недоступен — пропускаем его во избежание зависаний
            var status = await client.TestConnectionAsync();
            if (status != ConnectionStatus.Success)
            {
                // Роутер оффлайн или неверный пароль, пропускаем его обработку
                continue;
            }

            // 2. Опрашиваем роутер на предмет внешнего IP (интерфейс ether1)
            IPclass? wanIp = await client.GetInterfaceIpAsync();
            if (wanIp != null)
            {
                // Явно выставляем маску /32 (PoolSize = 32), чтобы в исключения 
                // попал только сам роутер, а не вся подсеть провайдера вокруг него.
                wanIp.PoolSize = 32;
                RouterWanExclusions.Add(wanIp);
            }

            // 3. Запрашиваем содержимое целевого списка (например, "ED") с этого микротика
            List<string> rawAddresses = await client.GetRawAddressesFromListAsync(config.TargetAddressList);

            foreach (var rawAddr in rawAddresses)
            {
                // Используем твой оригинальный метод парсинга строк
                IPclass? ipObj = IPclass.Parse(rawAddr);
                if (ipObj != null)
                {
                    // Твой wiseIPList сам внутри себя разберется с дубликатами 
                    // и укрупнением диапазонов, строя чистую сводную таблицу
                    GlobalRouterCache.AddAddress(ipObj);
                }
            }
        }

        // На выходе из метода у нас готов GlobalRouterCache.
        // Теперь мы можем передать его в твою основную логику приложения, 
        // чтобы использовать как глобальный фильтр при чтении логов.
    }

    /// <summary>
    /// Фаза 2: Синхронизация обратно на роутеры (отправка того, чего на них не хватает)
    /// Вызывается, когда в процессе парсинга логов накопились новые адреса.
    /// </summary>
    /// <summary>
    /// Прямой и лаконичный пуш всего мастер-списка на все роутеры без лишних сверок
    /// </summary>
    public async Task PushNewAddressesToRoutersAsync(List<MikrotikConfig> configuredRouters, wiseIPList masterList)
    {
        foreach (var config in configuredRouters)
        {
            var client = new MikrotikRestClient(config);

            // Быстрая проверка: если роутер выключен — не виснем, идем к следующему
            var status = await client.TestConnectionAsync().ConfigureAwait(false);
            if (status != ConnectionStatus.Success) continue;

            // Просто бежим по всей твоей готовой таблице IPclass и шлем её в роутер!
            foreach (IPclass ipObj in masterList.ipTable)
            {
                string ipStr = ipObj.ToString();

                // Если это хост /32 (или PoolSize == 0 в твоей логике) — ставим 7 дней, иначе заливаем подсеть навсегда
                string? timeoutParam = (ipObj.PoolSize == 32 || ipObj.PoolSize == 0) ? "7d" : null;

                // Отправляем напрямую. Mikrotik сам пропустит этот IP, если он там уже был активен.
                await client.AddAddressAsync(ipStr, config.TargetAddressList, timeoutParam).ConfigureAwait(false);
            }
        }
    }

}
