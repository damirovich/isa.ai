using ISC.AI.Abstractions.Modules;
using ISC.AI.Web.Common;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Web;

/// <summary>
/// Прямая ссылка на раздел, скрытый для роли (ТП-004): оболочка узнаёт раздел по маршруту модуля и вложенным
/// адресам под ним, не путая соседние маршруты с общим началом.
/// </summary>
public sealed class ModuleAccessTests
{
    private static readonly IModule[] Modules =
    [
        new ModuleDescriptor("admin-roles", "/admin/roles", "Роли пользователей", null, typeof(object), "p"),
        new ModuleDescriptor("cases", "/cases", "Дела", null, typeof(object), "p"),
        new ModuleDescriptor("media-search", "/media/search", "Поиск по лицу", null, typeof(object), "p"),
    ];

    [Theory(DisplayName = "Скрытый раздел узнаётся по маршруту и вложенному адресу, с параметрами и без учёта регистра")]
    [InlineData("admin/roles")]
    [InlineData("admin/roles/5")]
    [InlineData("Admin/Roles?x=1")]
    [InlineData("admin/roles/#top")]
    public void Hidden_route_is_recognised(string path)
    {
        ModuleAccess.HiddenModuleFor(path, Modules, new HashSet<string> { "cases", "media-search" })
            .ShouldNotBeNull().Id.ShouldBe("admin-roles");
    }

    [Theory(DisplayName = "Видимый раздел, соседний маршрут с общим началом и главная — не скрыты")]
    [InlineData("cases/3")]
    [InlineData("admin/rolesx")]
    [InlineData("media/search-history")]
    [InlineData("")]
    public void Other_routes_are_not_hidden(string path)
    {
        ModuleAccess.HiddenModuleFor(path, Modules, new HashSet<string> { "cases", "media-search" }).ShouldBeNull();
    }
}
