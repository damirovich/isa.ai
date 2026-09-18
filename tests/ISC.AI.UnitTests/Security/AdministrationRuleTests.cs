using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Кто в профиле «ИнспекторAI» вправе администрировать платформу — вести учётные записи и допуски
/// (ТБ-011/012, §2.1 ТЗ СКИД) и читать неизменяемый журнал аудита (ТБ-030/032).
/// </summary>
/// <remarks>
/// Экраны учётных записей, допусков и журнала уехали в пакет «Администрирование платформы»
/// (ADR-0023) и о ролях профиля больше не знают: они спрашивают порт <c>IPlatformAdministration</c>.
/// Реализация профиля (<c>InspectorPlatformAdministration</c>, слой данных) НИЧЕГО не решает сама —
/// оба её метода делегируют сюда, в <see cref="AdministrationRule"/>. Поэтому проверяется правило:
/// это единственное место, где оно записано, и то же самое правило спрашивают справочники профиля
/// и настройки документооборота.
///
/// Главное, что здесь зафиксировано: правило опирается на «кто вошёл» (<see cref="ISubjectProvider"/>),
/// а НЕ на контекст допуска. Иначе на ЧИСТОМ контуре, где записи допуска нет ни у кого, экран выдачи
/// допусков был бы недоступен всем — выдать первый допуск некому (замок без ключа, тот же класс
/// отказа, что уже случался с ролями, Э4-35 §6.4.1).
/// </remarks>
public sealed class AdministrationRuleTests
{
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public AdministrationRuleTests() =>
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);

    [Fact(DisplayName = "Администратор администрирует платформу")]
    public async Task Administrator_may_manage()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await CanManageAsync()).ShouldBeTrue();
    }

    [Fact(DisplayName = "Не-Администратор при наличии Администратора получает отказ")]
    public async Task Non_administrator_is_denied_when_administrator_exists()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Inspector);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await CanManageAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Пока Администратора нет — открыто любому вошедшему (иначе допуск выдать некому)")]
    public async Task Bootstrap_window_is_open_while_no_administrator_exists()
    {
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns((UserRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await CanManageAsync()).ShouldBeTrue();
    }

    [Fact(DisplayName = "Без аутентификации — отказ, даже в режиме первичной настройки")]
    public async Task Anonymous_is_denied_even_during_bootstrap()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await CanManageAsync()).ShouldBeFalse();

        // Роль неизвестного субъекта даже не спрашивается: fail-closed до всякого обращения к данным.
        await _roles.DidNotReceive().GetRoleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Правило НЕ требует допуска у распорядителя: контекст допуска не спрашивается")]
    public async Task Rule_never_touches_the_access_context()
    {
        // Провайдер контекста допуска правилу даже не передаётся — ни один путь не может уронить
        // экран на AccessContextRequiredException, как это делала страница ролей на чистом контуре.
        _roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);

        (await CanManageAsync()).ShouldBeTrue();
        await _subject.Received().GetCurrentUserIdAsync(Arg.Any<CancellationToken>());
    }

    private Task<bool> CanManageAsync() =>
        AdministrationRule.CallerCanManageAsync(_roles, _subject, CancellationToken.None);
}
