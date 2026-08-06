using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Clearances;

/// <summary>
/// Ведение допусков пользователей (ТБ-011/020/021) из интерфейса. Допуск — ядровое понятие
/// (<c>core.clearance</c>), а правило «кто вправе выдавать» — профильное (§2.1 ТЗ СКИД: Администратор),
/// поэтому порт живёт в ядре, а сценарии — здесь.
/// </summary>
/// <remarks>
/// До этого экрана гриф и подразделения правились ТОЛЬКО SQL'ем по живой базе: правка не попадала в
/// неизменяемый журнал (ТБ-030), а расхождение номеров подразделений со справочником профиля
/// проявлялось у пользователя как «не могу зарегистрировать документ и не понимаю почему».
/// </remarks>

/// <summary>Подразделение из допуска: наименование либо признак «в справочнике такого номера нет».</summary>
public sealed record ClearanceDivision(int Id, string? Name)
{
    /// <summary>Есть ли такое подразделение в справочнике профиля (<c>inspector.division</c>).</summary>
    public bool IsKnown => Name is not null;
}

/// <summary>Строка экрана допусков: пользователь, его допуск и разбор его подразделений.</summary>
public sealed record UserClearanceRow(
    int UserId,
    string DisplayName,
    short? MaxClassification,
    IReadOnlyList<ClearanceDivision> Divisions)
{
    /// <summary>Есть ли действующий допуск (иначе субъект не может ни читать, ни регистрировать).</summary>
    public bool HasClearance => MaxClassification is not null;

    /// <summary>
    /// Номера подразделений, которых НЕТ в справочнике. Это и есть тихая поломка, ради которой экран
    /// разбирает допуск, а не показывает голый массив: такой номер выглядит как выданный доступ, но не
    /// даёт ничего — ни одного документа с таким подразделением в системе нет и не появится.
    /// </summary>
    public IReadOnlyList<int> UnknownDivisionIds => [.. Divisions.Where(d => !d.IsKnown).Select(d => d.Id)];
}

/// <summary>Все активные пользователи с их допусками (экран администрирования).</summary>
public sealed record ListUserClearancesQuery : IRequest<ResponseDto<IReadOnlyList<UserClearanceRow>>>
{
    /// <inheritdoc cref="ListUserClearancesQuery" />
    public sealed class Handler(
        IClearanceStore clearances,
        IDivisionAdminStore divisions,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserClearancesQuery, ResponseDto<IReadOnlyList<UserClearanceRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserClearanceRow>>> Handle(
            ListUserClearancesQuery query, CancellationToken cancellationToken)
        {
            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserClearanceRow>>.BadRequest(ClearanceGuard.Denied);
            }

            var directory = (await divisions.ListAsync(cancellationToken))
                .ToDictionary(d => d.Id, d => d.Name);

            var rows = (await clearances.ListAsync(cancellationToken))
                .Select(row => new UserClearanceRow(
                    row.UserId,
                    row.DisplayName,
                    row.MaxClassification,
                    [
                        .. row.DivisionScope.Select(id => new ClearanceDivision(
                            id, directory.TryGetValue(id, out var name) ? name : null)),
                    ]))
                .ToList();

            return ResponseDto<IReadOnlyList<UserClearanceRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>Справочник подразделений для выбора в допуске (номер + наименование).</summary>
public sealed record ListClearanceDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<ClearanceDivision>>>
{
    /// <inheritdoc cref="ListClearanceDivisionsQuery" />
    public sealed class Handler(
        IDivisionAdminStore divisions, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListClearanceDivisionsQuery, ResponseDto<IReadOnlyList<ClearanceDivision>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ClearanceDivision>>> Handle(
            ListClearanceDivisionsQuery query, CancellationToken cancellationToken)
        {
            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<ClearanceDivision>>.BadRequest(ClearanceGuard.Denied);
            }

            var items = (await divisions.ListAsync(cancellationToken))
                .Select(d => new ClearanceDivision(d.Id, d.Name))
                .ToList();

            return ResponseDto<IReadOnlyList<ClearanceDivision>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Выдать или заменить допуск пользователя (гриф + разрешённые подразделения).</summary>
public sealed record SetUserClearanceCommand(int UserId, short MaxClassification, IReadOnlyList<int> DivisionIds)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>
    /// В журнал идут ИМЕННО выданные границы: запись «кому какой доступ открыли» — то, ради чего
    /// журнал и ведётся (ТБ-030). Секретов тут нет — это параметры доступа, а не данные под грифом.
    /// </remarks>
    public string? AuditSummary =>
        $"inspector:clearance:{UserId}:set:grif={MaxClassification};divisions={string.Join(',', DivisionIds)}";

    /// <inheritdoc />
    /// <remarks>
    /// Гриф записи журнала — не ниже ВЫДАВАЕМОГО грифа (ТБ-032): запись о выдаче допуска «совершенно
    /// секретно» не должна быть видна тому, кому такой гриф недоступен.
    /// </remarks>
    public short? AuditClassification => MaxClassification;

    /// <inheritdoc cref="SetUserClearanceCommand" />
    public sealed class Handler(
        IClearanceStore clearances, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(ClearanceGuard.Denied);
            }

            return await clearances.SetAsync(
                command.UserId, command.MaxClassification, command.DivisionIds, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Активный пользователь с таким идентификатором не найден.");
        }
    }
}

/// <summary>Отозвать допуск пользователя — действует немедленно (ТБ-016).</summary>
public sealed record RevokeUserClearanceCommand(int UserId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:clearance:{UserId}:revoke";

    /// <inheritdoc cref="RevokeUserClearanceCommand" />
    public sealed class Handler(
        IClearanceStore clearances, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<RevokeUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            RevokeUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(ClearanceGuard.Denied);
            }

            return await clearances.RevokeAsync(command.UserId, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("У пользователя нет действующего допуска.");
        }
    }
}

/// <summary>Кто вправе распоряжаться допусками — то же правило, что у ролей (§2.1).</summary>
/// <remarks>
/// Правило намеренно повторяет <c>RoleScenariosGuard</c>: Администратор, а пока Администратора в
/// системе НЕТ — любой вошедший (режим первичной настройки, Э4-35 §6.4.1). Иначе на чистом контуре
/// выдать первый допуск было бы некому — тот же замок без ключа, что уже случался с ролями.
/// Опора — <see cref="ISubjectProvider"/>, а не контекст допуска: у распорядителя допуска может ещё
/// не быть, и это нормально.
/// </remarks>
public static class ClearanceGuard
{
    /// <summary>Единый текст отказа — чтобы он не разошёлся между четырьмя сценариями (и был проверяем тестом).</summary>
    public const string Denied = "Просмотр и выдача допусков доступны только Администратору.";

    /// <inheritdoc cref="ClearanceGuard" />
    public static async Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken)
    {
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
        {
            return false;
        }

        if (await roles.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator)
        {
            return true;
        }

        return !await roles.AnyAdministratorAsync(cancellationToken);
    }
}

/// <summary>Правила формы выдачи допуска.</summary>
public sealed class SetUserClearanceValidator : AbstractValidator<SetUserClearanceCommand>
{
    /// <summary>Верхняя граница шкалы грифов в интерфейсе (та же, что в формах документооборота).</summary>
    public const short MaxClassification = 9;

    /// <summary>Правила: пользователь обязателен, гриф в пределах шкалы, номера подразделений положительные.</summary>
    public SetUserClearanceValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.MaxClassification)
            .InclusiveBetween((short)0, MaxClassification)
            .WithMessage($"Гриф допуска — от 0 до {MaxClassification}.");

        // Пустой список РАЗРЕШЁН намеренно: «допуск есть, подразделений нет» — законное состояние
        // (default-deny, ТБ-021), им же отбирают доступ, не отзывая допуск целиком.
        RuleFor(c => c.DivisionIds)
            .NotNull()
            .Must(ids => ids is null || ids.All(id => id > 0))
                .WithMessage("Номер подразделения должен быть положительным.");
    }
}
