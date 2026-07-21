using ISC.AI.Web.Security;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Валидатор returnUrl (Э3-08, ТБ-010): защита от open-redirect после входа. Отвергает не только
/// "//host" (protocol-relative), но и "/\host" — браузер трактует обратный слеш как разделитель пути
/// и резолвит такую «локальную» ссылку во внешний хост (WHATWG URL).
/// </summary>
public sealed class SafeLocalUrlTests
{
    [Theory(DisplayName = "Внешние/подделанные адреса отвергаются — возвращается \"/\"")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\\\\evil.example")]
    public void Unsafe_urls_fall_back_to_root(string? url) =>
        AuthEndpoints.SafeLocalUrl(url).ShouldBe("/");

    [Theory(DisplayName = "Локальные пути пропускаются как есть")]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/risks?tab=1")]
    public void Local_paths_pass_through(string url) =>
        AuthEndpoints.SafeLocalUrl(url).ShouldBe(url);
}
