using ISC.AI.Abstractions.Enums;
using ISC.AI.AI.Models;
using Shouldly;

namespace ISC.AI.UnitTests.Models;

/// <summary>
/// Страховка air-gap (инвариант №2, ТБ-044): адрес сервера инференса из конфигурации не должен указывать
/// наружу контура. Публичный IP-литерал — хард-фейл при регистрации; приватные/loopback и имена хостов —
/// допускаются (имя офлайн не разрешить, за периметр отвечает оператор).
/// </summary>
public sealed class AirGapEndpointGuardTests
{
    [Theory(DisplayName = "Air-gap: приватные/loopback IP и имена хостов не помечаются как публичные")]
    [InlineData("10.10.0.115")]   // прод-конфиг Llm:Models
    [InlineData("192.168.1.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]        // IPv6 unique-local
    [InlineData("fe80::1")]        // IPv6 link-local
    [InlineData("169.254.10.10")]  // IPv4 link-local
    [InlineData("llama-server")]   // имя хоста — судить офлайн нельзя, пропускаем
    [InlineData("inference.local")]
    public void Private_and_hostnames_are_not_flagged(string host) =>
        AirGapEndpointGuard.IsPublicIpLiteral(host).ShouldBeFalse();

    [Theory(DisplayName = "Air-gap: публичный IP-литерал распознаётся как выход за периметр")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]              // сразу за границей 172.16/12 — уже публичный
    [InlineData("2001:4860:4860::8888")]   // публичный IPv6
    public void Public_ip_is_flagged(string host) =>
        AirGapEndpointGuard.IsPublicIpLiteral(host).ShouldBeTrue();

    [Fact(DisplayName = "Air-gap: регистрация с публичным endpoint модели — хард-фейл с понятным сообщением")]
    public void EnsureWithinPerimeter_throws_on_public_endpoint()
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => AirGapEndpointGuard.EnsureWithinPerimeter(new Uri("http://8.8.8.8:9000/v1"), ModelRole.Draft));

        exception.Message.ShouldContain("Air-gap");
    }

    [Fact(DisplayName = "Air-gap: приватный endpoint (прод-конфиг 10.10.0.115) проходит")]
    public void EnsureWithinPerimeter_allows_private_endpoint() =>
        Should.NotThrow(
            () => AirGapEndpointGuard.EnsureWithinPerimeter(new Uri("http://10.10.0.115:9000/v1"), ModelRole.Analysis));
}
