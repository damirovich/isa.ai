using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Exchange;

/// <summary>
/// Экран «Обмен между офисами» (ТФ-АДМ-05, ADR-0068): готовность экземпляра, журнал выгрузок и ход текущей. Только
/// метаданные — содержимого пакетов экран не показывает (ТБ-079).
/// </summary>
/// <remarks>
/// Не аудируется, как обзор резервных копий: экран перечитывает его каждые 3 с, пока идёт выгрузка, а содержимого в нём
/// нет. В журнал попадают запуск выгрузки и её акт (<see cref="StartExchangeExportCommand"/>).
/// </remarks>
public sealed record GetExchangeOverviewQuery : IRequest<ResponseDto<ExchangeOverview>>
{
    /// <inheritdoc cref="GetExchangeOverviewQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IExchangeExport exchange)
        : IRequestHandler<GetExchangeOverviewQuery, ResponseDto<ExchangeOverview>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ExchangeOverview>> Handle(GetExchangeOverviewQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminExchange, cancellationToken))
            {
                return ResponseDto<ExchangeOverview>.BadRequest(RoleGuard.Denied(InvestigationPermissions.AdminExchange));
            }

            return ResponseDto<ExchangeOverview>.Ok(await exchange.GetOverviewAsync(cancellationToken));
        }
    }
}

/// <summary>
/// Сформировать пакет обмена (ТФ-ОБМ-01, ТБ-083, ADR-0068): Администратор выбирает период, номера дел и виды носителей,
/// хранитель ключа вводит пароль ключа подписи; пакет формируется в фоне на носитель обмена.
/// </summary>
/// <param name="Year">Год периода.</param>
/// <param name="Month">Месяц периода.</param>
/// <param name="CaseNumbers">Номера дел через запятую или с новой строки; пусто — дела с движением за период.</param>
/// <param name="MediaKinds">Виды носителей, файлы которых едут в пакете.</param>
/// <param name="ResendFiles">Отправить и файлы, ушедшие прежними пакетами.</param>
/// <param name="SigningKeyPassword">Пароль ключа подписи — вводит хранитель; не сохраняется и не пишется в журнал.</param>
public sealed record StartExchangeExportCommand(
    int Year,
    int Month,
    string? CaseNumbers,
    IReadOnlyList<MediaKind> MediaKinds,
    bool ResendFiles,
    string SigningKeyPassword) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    /// <remarks>Только отбор (метаданные); пароль ключа подписи в журнал не попадает (ТБ-083). Акт с итогом пишет задание.</remarks>
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:exchange:export:{Year:D4}-{Month:D2};cases={ParseCaseNumbers(CaseNumbers).Count};kinds={string.Join(",", MediaKinds)}{(ResendFiles ? ";resend" : string.Empty)}");

    /// <summary>
    /// Текст запроса — БЕЗ пароля ключа подписи (ТБ-083): запись сама печатала бы все свойства, и пароль попал бы в любой
    /// лог, куда попадёт запрос.
    /// </summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{nameof(StartExchangeExportCommand)} {{ Period = {Year:D4}-{Month:D2}, ResendFiles = {ResendFiles} }}");

    /// <summary>Номера дел из поля ввода: через запятую, точку с запятой или с новой строки; без повторов.</summary>
    public static IReadOnlyList<string> ParseCaseNumbers(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    /// <inheritdoc cref="StartExchangeExportCommand" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IExchangeExport exchange)
        : IRequestHandler<StartExchangeExportCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(StartExchangeExportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminExchange, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.Denied(InvestigationPermissions.AdminExchange));
            }

            if (await subjects.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<int>.BadRequest("Пакет формирует вошедший сотрудник — войдите в систему.");
            }

            var request = new ExchangeExportRequest(
                command.Year,
                command.Month,
                ParseCaseNumbers(command.CaseNumbers),
                [.. command.MediaKinds.Distinct().Order().Select(k => k.ToString())],
                command.ResendFiles);
            var started = await exchange.StartAsync(request, userId, command.SigningKeyPassword, cancellationToken);
            return started.ExportId is { } id
                ? ResponseDto<int>.Ok(id, "Пакет формируется — ход на этой странице.")
                : ResponseDto<int>.Fail(started.Error ?? "Пакет не запущен.");
        }
    }
}

/// <summary>Проверка отбора пакета: период не в будущем, хотя бы один вид носителей, пароль ключа подписи введён.</summary>
public sealed class StartExchangeExportValidator : AbstractValidator<StartExchangeExportCommand>
{
    /// <summary>Наибольшее число номеров дел в одном отборе.</summary>
    public const int MaxCaseNumbers = 500;

    /// <summary>Правила.</summary>
    public StartExchangeExportValidator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        RuleFor(c => c.Month).InclusiveBetween(1, 12).WithMessage("Месяц — от 1 до 12.");
        RuleFor(c => c.Year).InclusiveBetween(2000, 2100).WithMessage("Год — от 2000 до 2100.");
        RuleFor(c => c)
            .Must(c => new DateOnly(c.Year, Math.Clamp(c.Month, 1, 12), 1) <= DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime))
            .When(c => c.Year is >= 2000 and <= 2100)
            .WithMessage("Период ещё не наступил.");
        RuleFor(c => c.MediaKinds).NotEmpty().WithMessage("Выберите хотя бы один вид носителей — фото, видео или звук.");
        RuleFor(c => c.CaseNumbers)
            .Must(text => StartExchangeExportCommand.ParseCaseNumbers(text).Count <= MaxCaseNumbers)
            .WithMessage($"Номеров дел больше {MaxCaseNumbers} — сформируйте пакет по периоду без номеров.");
        RuleFor(c => c.SigningKeyPassword).NotEmpty().WithMessage("Пароль ключа подписи вводит хранитель ключа.");
    }
}
