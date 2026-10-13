using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Profile.Investigation.Application.Features.AccessImport;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Divisions;
using ISC.AI.Profile.Investigation.Application.Features.Scope;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Экран «Перенос из Access» (ADR-0043): без права — отказ текстом, без драйвера — что поставить; загрузка базы
/// показывает её таблицы и предложенное сопоставление; проверка недоступна, пока гриф не выбран осознанно (ТБ-024).
/// </summary>
public sealed class AccessTransferUiTests : BunitContext, IAsyncLifetime
{
    private static readonly DivisionNode Division = new(5, "Отдел ОН", null, null, true, 1, CaseDirection.Surveillance);

    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public AccessTransferUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("СледствиеAI");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
        RespondOk<GetAccessScopeQuery, AccessScope>(new AccessScope(3, [Division], InvestigationRole.Administrator, 10));
        RespondOk<ListDivisionsQuery, IReadOnlyList<DivisionNode>>([Division]);
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "ADR-0043 п. 9: без права «Перенос баз Access» экран говорит, к кому идти, и загрузки нет")]
    public void Denied_without_permission()
    {
        RespondOk<GetAccessImportSetupQuery, AccessImportSetup>(new AccessImportSetup(false, null, false));

        var cut = Render<AccessTransfer>();

        cut.Markup.ShouldContain(RoleGuard.Denied(InvestigationPermissions.CasesImport));
        cut.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0043 п. 1: нет драйвера ACE — экран показывает, что поставить на сервер")]
    public void Unavailable_without_driver()
    {
        RespondOk<GetAccessImportSetupQuery, AccessImportSetup>(new AccessImportSetup(true, "нет драйвера ACE", true));

        var cut = Render<AccessTransfer>();

        cut.Markup.ShouldContain("нет драйвера ACE");
        cut.FindAll("input[type=file]").ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0043 п. 2, ТБ-024: загруженная база — таблицы и сопоставление на экране; «Проверить» ждёт выбора грифа")]
    public void Upload_shows_mapping_and_waits_for_grif()
    {
        RespondOk<GetAccessImportSetupQuery, AccessImportSetup>(new AccessImportSetup(true, null, true));
        var tables = new List<AccessTable>
        {
            new("Задания", [new("Код", "число", false), new("№ задания", "текст", false), new("Фамилия", "текст", false)]),
            new("Связи", [new("№ задания", "текст", false), new("Связь объекта", "текст", false)]),
        };
        var file = new AccessDatabaseFile("x.accdb", "otdel.accdb", 3, new string('B', 64));
        RespondOk<UploadAccessDatabaseCommand, AccessDatabaseInfo>(new AccessDatabaseInfo(file, tables, AccessAutoMapper.Suggest(tables)));
        Render<MudPopoverProvider>();

        var cut = Render<AccessTransfer>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "otdel.accdb"));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("2. Сопоставление полей"));
        cut.Markup.ShouldContain("otdel.accdb");
        // Видимый текст выбранных значений (скрытое поле MudSelect хранит сам код значения).
        var shown = cut.FindAll(".mud-input-slot").Select(d => d.TextContent.Trim()).ToList();
        shown.ShouldContain("Задания");
        shown.ShouldContain("Связи");
        shown.ShouldContain("№ задания (текст)");
        shown.ShouldContain("Фамилия (текст)");
        shown.ShouldContain("Код (число)");

        // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а проверяет вызов (CA2012 к этому неприменим).
#pragma warning disable CA2012
        _mediator.Received(1).Send(Arg.Is<UploadAccessDatabaseCommand>(c => c.FileName == "otdel.accdb"), Arg.Any<CancellationToken>());
#pragma warning restore CA2012

        // Подразделение подставилось (одно в допуске), гриф — нет: проверка закрыта до осознанного выбора.
        shown.ShouldContain("Отдел ОН");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Проверить", StringComparison.Ordinal)).HasAttribute("disabled").ShouldBeTrue();
    }

    // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ (CA2012 к этому неприменим).
#pragma warning disable CA2012
    private void RespondOk<TRequest, TData>(TData data)
        where TRequest : IRequest<ResponseDto<TData>> =>
        _mediator.Send(Arg.Any<TRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TData>>(ResponseDto<TData>.Ok(data)));
#pragma warning restore CA2012
}
