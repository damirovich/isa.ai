using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Application.Features.Assignments;
using ISC.AI.Modules.DocFlow.Application.Features.Comments;
using ISC.AI.Modules.DocFlow.Application.Features.Directory;
using ISC.AI.Modules.DocFlow.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Пределы регистрации (<see cref="GetRegistrationScopeQuery"/>): форма не должна предлагать гриф и
/// подразделение, которые сервер заведомо отвергнет (ТБ-020/021, этап 6.6).
/// </summary>
/// <remarks>
/// Это удобство, а не защита: серверная проверка в <c>DocumentStore.CreateAsync</c> остаётся и
/// покрыта отдельно (<c>InspectorAccessPolicyTests</c>). Здесь проверяется только то, что запрос
/// СУЖАЕТ, а не расширяет — и что пустой допуск даёт пустой список, а не «все».
/// </remarks>
public sealed class RegistrationScopeTests
{
    private static readonly IReadOnlyList<DivisionItem> Directory =
    [
        new(1, "Главная инспекция"),
        new(5, "Территориальная инспекция"),
        new(9, "Линейное подразделение"),
    ];

    [Fact(DisplayName = "Пределы регистрации: подразделения — пересечение справочника с допуском, гриф — из допуска")]
    public async Task Scope_intersects_directory_with_clearance()
    {
        var scope = await HandleAsync(new AccessContext("42", MaxClassification: 2, AllowedDivisions: [5, 9, 777]));

        scope.MaxClassification.ShouldBe((short)2);

        // 777 в допуске есть, но в справочнике его нет — выдумывать строку не из чего.
        scope.OwnerDivisions.Select(d => d.Id).ShouldBe([5, 9]);
    }

    [Fact(DisplayName = "Пределы регистрации: пустой допуск по подразделениям даёт пустой список, а не «все»")]
    public async Task Empty_division_scope_means_none_not_all()
    {
        var scope = await HandleAsync(new AccessContext("42", MaxClassification: 0, AllowedDivisions: []));

        scope.OwnerDivisions.ShouldBeEmpty();
        scope.MaxClassification.ShouldBe((short)0);
    }

    [Fact(DisplayName = "Пределы регистрации: без контекста допуска запрос падает, а не отдаёт пустые пределы")]
    public async Task Missing_access_context_fails_closed()
    {
        var directory = Substitute.For<IDivisionDirectory>();
        directory.ListAsync(Arg.Any<CancellationToken>()).Returns(Directory);

        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<Task<AccessContext>>(_ => throw new AccessContextRequiredException());

        var handler = new GetRegistrationScopeQuery.Handler(directory, accessProvider);

        // Fail-closed (ТБ-012): «допуск неизвестен» НЕ должно молча превращаться в «пределы пустые» —
        // тогда форма показала бы «вам ничего не разрешено» вместо честной ошибки. Обработчик обязан
        // бросить; в приложении сквозной ExceptionHandlingBehavior превратит это в неуспешный ответ,
        // и форма покажет «не удалось определить ваш допуск» с заблокированной кнопкой.
        await Should.ThrowAsync<AccessContextRequiredException>(
            async () => await handler.Handle(new GetRegistrationScopeQuery(), CancellationToken.None));
    }

    private static async Task<RegistrationScope> HandleAsync(AccessContext access)
    {
        var directory = Substitute.For<IDivisionDirectory>();
        directory.ListAsync(Arg.Any<CancellationToken>()).Returns(Directory);

        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var handler = new GetRegistrationScopeQuery.Handler(directory, accessProvider);
        var response = await handler.Handle(new GetRegistrationScopeQuery(), CancellationToken.None);

        response.Status.ShouldBeTrue();
        return response.Data.ShouldNotBeNull();
    }
}
