using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Directory;
using ISC.AI.Profile.Investigation.Application.Features.Divisions;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Вкладка «Материалы» карточки дела профиля «Следствие» (ТФ-ДЕЛ-02, ADR-0026): аудиозапись подписана
/// «Аудио», а не «Фото»; состояние индексации — по-русски, без сырого имени перечисления; число лиц у
/// аудио — прочерк. Страница рендерится целиком: запросы Mediator подменены, вкладка открывается щелчком
/// (она ленивая — носители грузятся при первом открытии).
/// </summary>
public sealed class CaseCardMediaTabTests : BunitContext, IAsyncLifetime
{
    private const int CaseId = 3;
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public CaseCardMediaTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));

        RespondOk<ListDivisionsQuery, IReadOnlyList<DivisionNode>>([]);
        RespondOk<ListUserDirectoryQuery, IReadOnlyList<UserAccountRow>>([]);
        RespondOk<ListPersonsQuery, IReadOnlyList<PersonRow>>([]);
        RespondOk<GetCaseQuery, CaseDetails>(new CaseDetails(
            CaseId, "12-2026", "Кража", CaseKind.CriminalCase, CaseStatus.InProgress, new DateOnly(2026, 9, 1),
            InvestigatorUserId: null, DivisionId: 5, Classification: 0, Basis: null, ClosedAt: null,
            CreatedAt: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            Media: [new CaseMediaLinkRow(50, null, null, DateTime.UtcNow), new CaseMediaLinkRow(51, null, null, DateTime.UtcNow)],
            Authorizations: []));
        RespondOk<ListCaseMediaQuery, IReadOnlyList<MediaAssetRow>>(
        [
            Asset(50, MediaKind.Audio, "voice.ogg", MediaIndexStatus.NotApplicable, faceCount: 0, TranscriptStatus.Done),
            Asset(51, MediaKind.Image, "photo.jpg", MediaIndexStatus.Indexed, faceCount: 2, TranscriptStatus.NotApplicable),
        ]);
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Строка аудиозаписи: вид «Аудио», не «Фото»; индексация — «Поиск по лицу неприменим», без сырого NotApplicable; лиц — «—»")]
    public void Audio_row_shows_audio_kind_and_russian_index_status()
    {
        var cut = OpenMediaTab();

        var row = Row(cut, "voice.ogg");
        Cell(row, "Вид").ShouldBe("Аудио");
        Cell(row, "Индексация").ShouldBe("Поиск по лицу неприменим");
        Cell(row, "Лиц").ShouldBe("—");
        Cell(row, "Расшифровка").ShouldBe("Расшифровано");
        row.TextContent.ShouldNotContain("Фото");
        cut.Markup.ShouldNotContain("NotApplicable");
    }

    [Fact(DisplayName = "Строка фотографии не изменилась: вид «Фото», число лиц, расшифровка — «—»")]
    public void Image_row_keeps_photo_kind_and_face_count()
    {
        var cut = OpenMediaTab();

        var row = Row(cut, "photo.jpg");
        Cell(row, "Вид").ShouldBe("Фото");
        Cell(row, "Индексация").ShouldBe("Проиндексирован");
        Cell(row, "Лиц").ShouldBe("2");
        Cell(row, "Расшифровка").ShouldBe("—");
    }

    private IRenderedComponent<CaseCard> OpenMediaTab()
    {
        Render<MudPopoverProvider>();
        var cut = Render<CaseCard>(p => p.Add(x => x.Id, CaseId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Дело 12-2026"));

        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("Материалы", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("voice.ogg"));
        return cut;
    }

    private static IElement Row(IRenderedComponent<CaseCard> cut, string fileName) =>
        cut.FindAll("tr").Single(r => r.TextContent.Contains(fileName, StringComparison.Ordinal));

    private static string Cell(IElement row, string label) =>
        row.QuerySelector($"td[data-label='{label}']").ShouldNotBeNull().TextContent.Trim();

    private void RespondOk<TQuery, TData>(TData data)
        where TQuery : IRequest<ResponseDto<TData>> =>
        _mediator.Send(Arg.Any<TQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TData>>(ResponseDto<TData>.Ok(data)));

    private static MediaAssetRow Asset(
        int id, MediaKind kind, string fileName, MediaIndexStatus indexStatus, int faceCount, TranscriptStatus transcript) => new(
        id, kind, fileName, "0123456789abcdef0123456789abcdef.bin", "application/octet-stream", 1024, DurationMs: null,
        Source: null, CapturedAt: null, Classification: 0, DivisionId: 5, UploadedByUserId: null,
        IndexStatus: indexStatus, IndexError: null, DetectorVersion: null, EmbedderVersion: null, IndexedAt: null,
        CreatedAt: new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc), FaceCount: faceCount, TranscriptStatus: transcript);
}
