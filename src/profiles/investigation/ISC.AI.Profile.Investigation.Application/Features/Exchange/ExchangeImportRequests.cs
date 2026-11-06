using System.Text.RegularExpressions;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Exchange;

/// <summary>
/// Экран «Обмен между офисами → Приём» (ТФ-ОБМ-02, ТФ-АДМ-05, ADR-0068 v0.2): готовность, пакеты на носителе с проверкой
/// подписи, журнал приёмов и ход текущего. Только манифесты и счётчики — содержимого пакетов экран не показывает (ТБ-079).
/// </summary>
/// <remarks>Не аудируется, как обзор отправки: экран перечитывает его, пока идёт приём; в журнал попадают запуск и акт.</remarks>
public sealed record GetExchangeImportOverviewQuery : IRequest<ResponseDto<ExchangeImportOverview>>
{
    /// <inheritdoc cref="GetExchangeImportOverviewQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IExchangeImport exchange)
        : IRequestHandler<GetExchangeImportOverviewQuery, ResponseDto<ExchangeImportOverview>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ExchangeImportOverview>> Handle(
            GetExchangeImportOverviewQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminExchange, cancellationToken))
            {
                return ResponseDto<ExchangeImportOverview>.BadRequest(RoleGuard.Denied(InvestigationPermissions.AdminExchange));
            }

            return ResponseDto<ExchangeImportOverview>.Ok(await exchange.GetImportOverviewAsync(cancellationToken));
        }
    }
}

/// <summary>
/// Принять пакет из другого офиса (ТФ-ОБМ-02, ТБ-083, ADR-0068 v0.2): Администратор выбирает пакет на носителе и отмечает,
/// что он прошёл проверку на шлюзе, хранитель ключа вводит пароль ключа приёма; приём идёт в фоне.
/// </summary>
/// <param name="PackageName">Каталог пакета на носителе.</param>
/// <param name="GatewayConfirmed">Пакет прошёл проверку на шлюзе ([Расш. 1.3]).</param>
/// <param name="ReceivingKeyPassword">Пароль ключа приёма — вводит хранитель; не сохраняется и не пишется в журнал.</param>
public sealed record StartExchangeImportCommand(string PackageName, bool GatewayConfirmed, string ReceivingKeyPassword)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    /// <remarks>Только имя пакета и отметка шлюза; пароль ключа приёма в журнал не попадает (ТБ-083). Акт с итогом пишет задание.</remarks>
    public string? AuditSummary =>
        "investigation:exchange:import:" + PackageName + ";gateway=" + (GatewayConfirmed ? "yes" : "no");

    /// <summary>
    /// Текст запроса — БЕЗ пароля ключа приёма (ТБ-083): запись сама печатала бы все свойства, и пароль попал бы в любой
    /// лог, куда попадёт запрос.
    /// </summary>
    public override string ToString() =>
        $"{nameof(StartExchangeImportCommand)} {{ PackageName = {PackageName}, GatewayConfirmed = {GatewayConfirmed} }}";

    /// <inheritdoc cref="StartExchangeImportCommand" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IExchangeImport exchange)
        : IRequestHandler<StartExchangeImportCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(StartExchangeImportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminExchange, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.Denied(InvestigationPermissions.AdminExchange));
            }

            if (await subjects.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<int>.BadRequest("Пакет принимает вошедший сотрудник — войдите в систему.");
            }

            var started = await exchange.StartImportAsync(
                command.PackageName, command.GatewayConfirmed, userId, command.ReceivingKeyPassword, cancellationToken);
            return started.ImportId is { } id
                ? ResponseDto<int>.Ok(id, "Пакет принимается — ход на этой странице.")
                : ResponseDto<int>.Fail(started.Error ?? "Приём не запущен.");
        }
    }
}

/// <summary>Проверка запуска приёма: имя пакета обмена, отметка шлюза, пароль ключа приёма.</summary>
public sealed partial class StartExchangeImportValidator : AbstractValidator<StartExchangeImportCommand>
{
    /// <summary>Правила.</summary>
    public StartExchangeImportValidator()
    {
        RuleFor(c => c.PackageName)
            .NotEmpty().WithMessage("Выберите пакет на носителе.")
            .MaximumLength(200).WithMessage("Имя пакета длиннее 200 знаков.")
            .Must(name => name is null || PackageNamePattern().IsMatch(name))
            .WithMessage("Это не имя пакета обмена (isc-exchange-…).");
        RuleFor(c => c.GatewayConfirmed).Equal(true)
            .WithMessage("Отметьте, что пакет прошёл проверку на шлюзе: без неё приём не начинается.");
        RuleFor(c => c.ReceivingKeyPassword).NotEmpty().WithMessage("Пароль ключа приёма вводит хранитель ключа.");
    }

    [GeneratedRegex("^isc-exchange-[A-Za-z0-9._-]+$")]
    private static partial Regex PackageNamePattern();
}
