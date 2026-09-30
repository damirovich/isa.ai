using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Поиск по фигуранту одной кнопкой (ТФ-ПЛ-01/03/05): проба — лучший действующий эталон с лицом (не заменённый и не
/// с отозванного появления); одно основание — подставляется и запускается сразу, несколько — выбирает оператор;
/// нет права, основания или эталона — кнопка неактивна с объяснением.
/// </summary>
public sealed class PersonSearchRulesTests
{
    private static readonly PersonSearchContext Ready = new(true, [new CaseAuthorizationItem(5, "Постановление № 1")]);

    [Fact(DisplayName = "Проба — лучший по качеству действующий эталон с лицом; заменённый, без лица и с отозванного появления не берутся")]
    public void Best_probe_skips_superseded_faceless_and_revoked()
    {
        ReferencePhotoRow[] photos =
        [
            Photo(1, face: 100, quality: 0.99f, supersededBy: 9),   // заменён
            Photo(2, face: null, quality: 0.95f),                   // без лица
            Photo(3, face: 300, quality: 0.93f),                    // с отозванного появления
            Photo(4, face: 400, quality: 0.80f),
            Photo(5, face: 500, quality: 0.70f),
        ];
        AppearanceRow[] appearances = [Appearance(300, revoked: true)];

        PersonSearchRules.BestProbe(photos, appearances)!.Id.ShouldBe(4);

        // Если то же лицо подтверждено и действующим появлением — эталон снова годен.
        PersonSearchRules.BestProbe(photos, [Appearance(300, revoked: true), Appearance(300, revoked: false)])!.Id.ShouldBe(3);
    }

    [Fact(DisplayName = "Одно основание: адрес с делом, лицом, всеми доступными делами, основанием и автозапуском")]
    public void Single_authorization_runs_immediately()
    {
        var plan = PersonSearchRules.Plan(7, [Photo(4, face: 400, quality: 0.8f)], [], Ready);

        plan.Blocker.ShouldBeNull();
        plan.Url.ShouldBe("/media/search?caseId=7&faceId=400&scope=all&authId=5&auto=1");
    }

    [Fact(DisplayName = "Несколько оснований: форма заполнена, основание выбирает оператор, без автозапуска")]
    public void Several_authorizations_leave_choice_to_operator()
    {
        var context = new PersonSearchContext(true, [new CaseAuthorizationItem(5, "А"), new CaseAuthorizationItem(6, "Б")]);

        PersonSearchRules.Plan(7, [Photo(4, face: 400, quality: 0.8f)], [], context).Url
            .ShouldBe("/media/search?caseId=7&faceId=400&scope=all");
    }

    [Fact(DisplayName = "Нет права, основания или эталона — кнопка неактивна, причина названа")]
    public void Blockers_are_explained()
    {
        var photo = Photo(4, face: 400, quality: 0.8f);

        PersonSearchRules.Plan(7, [photo], [], new PersonSearchContext(false, [])).Blocker.ShouldContain("закрыт для вашей роли");
        PersonSearchRules.Plan(7, [photo], [], new PersonSearchContext(true, [])).Blocker.ShouldContain("основания поиска");
        PersonSearchRules.Plan(7, [], [], Ready).Blocker.ShouldContain("эталона");
        PersonSearchRules.Plan(7, [photo], [], null).Url.ShouldBeNull();
    }

    [Fact(DisplayName = "Контекст поиска: без права «Поиск по лицу» основания даже не читаются")]
    public async Task Context_without_right_does_not_read_authorizations()
    {
        var roles = Substitute.For<IUserRoleStore>();
        var subject = Substitute.For<ISubjectProvider>();
        var access = Substitute.For<IAccessContextProvider>();
        var scope = Substitute.For<ICaseScope>();
        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);

        var denied = (await new GetPersonSearchContextQuery.Handler(roles, subject, access, scope)
            .Handle(new GetPersonSearchContextQuery(7), CancellationToken.None)).Data!;

        denied.CanSearch.ShouldBeFalse();
        await scope.DidNotReceiveWithAnyArgs().ListAuthorizationsAsync(default, default!, default);

        roles.GetRoleAsync(42, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", 2, [5]));
        scope.ListAuthorizationsAsync(7, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([new CaseAuthorizationItem(5, "А")]);

        var ready = (await new GetPersonSearchContextQuery.Handler(roles, subject, access, scope)
            .Handle(new GetPersonSearchContextQuery(7), CancellationToken.None)).Data!;
        ready.CanSearch.ShouldBeTrue();
        ready.Authorizations.ShouldHaveSingleItem();
    }

    private static ReferencePhotoRow Photo(int id, int? face, float quality, int? supersededBy = null) =>
        new(id, 1, 10, face, quality, null, null, null, null, supersededBy, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id));

    private static AppearanceRow Appearance(int face, bool revoked) =>
        new(face, 1, 7, 10, face, null, null, 1, face, 0.8, revoked ? AppearanceStatus.Revoked : AppearanceStatus.InvestigativeLead,
            DateTime.UtcNow, 1, 2);
}
