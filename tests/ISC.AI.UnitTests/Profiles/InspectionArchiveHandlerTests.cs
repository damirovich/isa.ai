using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Application.Features.Archive;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Склейка архива проверок (§5.2.4): недоступная по решётке справка остаётся ГОЛЫМ номером
/// (карточка null), а резолверу уходят только непустые номера страницы — без дублей.
/// </summary>
public sealed class InspectionArchiveHandlerTests
{
    [Fact(DisplayName = "Архив: доступной справке — карточка, недоступной и «вне проверок» — нет")]
    public async Task Inaccessible_reference_stays_a_bare_number()
    {
        var groupVisible = Group("СП-1");
        var groupHidden = Group("СП-9");
        var groupOrphan = Group(null);

        var store = Substitute.For<IInspectionArchiveStore>();
        store.ListAsync(Arg.Any<ArchiveFilter>(), Arg.Any<CancellationToken>())
            .Returns(new ArchivePage([groupVisible, groupHidden, groupOrphan], 3));

        // Резолвер знает только СП-1 — СП-9 недоступна субъекту (или документа нет).
        var resolver = Substitute.For<IInspectionDocumentResolver>();
        resolver.ResolveAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, InspectionDocumentCard>(StringComparer.Ordinal)
            {
                ["СП-1"] = new(7, "СП-1", new DateOnly(2026, 5, 1), "Справка", "Инспектор И.", null),
            });

        var response = await new ListInspectionArchiveQuery.Handler(store, resolver)
            .Handle(new ListInspectionArchiveQuery(), CancellationToken.None);

        response.Status.ShouldBeTrue();
        var groups = response.Data!.Groups;
        groups[0].Document.ShouldNotBeNull();
        groups[0].Document!.TypeName.ShouldBe("Справка");
        groups[1].Document.ShouldBeNull();
        groups[2].Document.ShouldBeNull();

        // Резолверу ушли только непустые номера — и по одному разу.
        await resolver.Received(1).ResolveAsync(
            Arg.Is<IReadOnlyCollection<string>>(refs =>
                refs.Count == 2 && refs.Contains("СП-1") && refs.Contains("СП-9")),
            Arg.Any<CancellationToken>());
    }

    private static ArchiveGroup Group(string? reference) => new(
        reference, 1, "Альфа", 1, 1, new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 1),
        ViolationSeverity.Medium,
        [new ArchiveViolationRow(1, "Вид", "Сфера", ViolationSeverity.Medium,
            new DateOnly(2026, 5, 1), RemediationStatus.UnderControl)]);
}
