using System.Net;
using System.Net.Sockets;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.AI.Models;

/// <summary>
/// Страховка air-gap (инвариант №2 CLAUDE.md, ТБ-044): адрес сервера инференса обязан быть ВНУТРИ контура.
/// Защита от ошибочного конфига, который вывел бы трафик модели в публичную сеть. Проверка выполняется на
/// этапе регистрации клиентов (<see cref="CoreAiModelsServiceCollectionExtensions"/>) — сбой конфигурации
/// ловится при старте, а не в проде.
/// </summary>
/// <remarks>
/// Судить достоверно можно только об IP-литералах: приватные/loopback/link-local диапазоны — внутри контура;
/// публичный IP — явная ошибка (хард-фейл). Имя хоста в изолированном контуре без DNS не разрешить, поэтому
/// адрес-имя пропускается (за то, что имя резолвится только внутри периметра, отвечает оператор). Хард-фейл
/// выбран вместо предупреждения намеренно: air-gap — жёсткий инвариант (№2), и «тихо утёкший» наружу трафик
/// хуже, чем не стартовавший сервис.
/// </remarks>
internal static class AirGapEndpointGuard
{
    /// <summary>
    /// Бросает <see cref="InvalidOperationException"/>, если <paramref name="endpoint"/> указывает на публичный
    /// IP-адрес (трафик ушёл бы за пределы контура). Приватный/loopback IP и имена хостов допускаются.
    /// </summary>
    public static void EnsureWithinPerimeter(Uri endpoint, ModelRole role)
    {
        if (IsPublicIpLiteral(endpoint.Host))
        {
            throw new InvalidOperationException(
                $"Air-gap (ТБ-044): адрес модели роли «{role}» — публичный IP {endpoint.Host}. " +
                "В изолированном контуре допустимы только адреса внутри периметра (приватные/loopback). " +
                "Исправьте секцию Llm:Models в конфигурации.");
        }
    }

    /// <summary>
    /// <c>true</c>, если <paramref name="host"/> — IP-литерал, маршрутизируемый ВНЕ периметра контура.
    /// Имя хоста (не IP) → <c>false</c>: офлайн его не разрешить, судить не можем (см. remarks).
    /// </summary>
    public static bool IsPublicIpLiteral(string host) =>
        IPAddress.TryParse(host, out var ip) && !IsWithinPerimeter(ip);

    // Диапазоны «внутри периметра»: loopback; IPv4 приватные (RFC 1918) и link-local; IPv6 loopback,
    // link-local, site-local (устар.) и unique-local fc00::/7.
    private static bool IsWithinPerimeter(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                 // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16 link-local
                || b[0] == 127;                               // 127.0.0.0/8
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }
}
