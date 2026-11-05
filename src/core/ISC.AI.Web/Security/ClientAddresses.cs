using System.Net;

namespace ISC.AI.Web.Security;

/// <summary>
/// IP-адрес клиента строкой — одна форма для журнала аудита и списка сеансов (ТБ-080, ADR-0066).
/// </summary>
public static class ClientAddresses
{
    /// <summary>
    /// Адрес строкой. IPv4, отображённый в IPv6 (<c>::ffff:10.0.0.5</c> — так IPv4-клиента видит сокет с двойным стеком),
    /// записывается как IPv4 (<c>10.0.0.5</c>): иначе один и тот же адрес выглядел бы в журнале и в списке сеансов
    /// по-разному, а поиск по адресу находил бы лишь половину записей. <see langword="null"/> — адреса нет.
    /// </summary>
    public static string? Format(IPAddress? address) =>
        address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
