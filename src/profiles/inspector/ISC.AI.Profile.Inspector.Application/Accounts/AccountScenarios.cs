using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Accounts;

/// <summary>
/// Ведение учётных записей и смена пароля (Э4-35 §6.5, шаг 3). После перехода на локальную
/// идентичность это единственное место, где заводят доступ и восстанавливают забытый пароль:
/// внешней системы, куда можно было отослать пользователя, больше нет.
/// </summary>
/// <remarks>
/// ПАРОЛИ НИКОГДА НЕ ПОПАДАЮТ В ЖУРНАЛ (ТБ-043): в <c>AuditSummary</c> уходит только факт и субъект.
/// Временный пароль показывается администратору ОДИН раз в ответе команды и нигде не сохраняется
/// в открытом виде.
/// </remarks>

/// <summary>Все учётные записи (экран администрирования).</summary>
public sealed record ListUserAccountsQuery : IRequest<ResponseDto<IReadOnlyList<UserAccountRow>>>
{
    /// <inheritdoc cref="ListUserAccountsQuery" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserAccountsQuery, ResponseDto<IReadOnlyList<UserAccountRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>> Handle(
            ListUserAccountsQuery query, CancellationToken cancellationToken)
        {
            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserAccountRow>>.BadRequest(AccountGuard.Denied);
            }

            var items = await accounts.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Создать учётную запись; в ответе — ВРЕМЕННЫЙ пароль (показывается один раз).</summary>
public sealed record CreateUserAccountCommand(string UserName, string? DisplayName)
    : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Пароль в журнал НЕ пишется — только факт создания и имя входа (ТБ-043).</remarks>
    public string? AuditSummary => $"inspector:account:create:{UserName}";

    /// <inheritdoc cref="CreateUserAccountCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateUserAccountCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            CreateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AccountGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            var userId = await accounts.CreateAsync(
                command.UserName, command.DisplayName, temporary, cancellationToken);

            return userId is null
                ? ResponseDto<string>.BadRequest("Имя входа уже занято.")
                : ResponseDto<string>.Ok(temporary, "Учётная запись создана. Передайте временный пароль лично.");
        }
    }
}

/// <summary>Сбросить пароль на временный; в ответе — новый временный пароль.</summary>
public sealed record ResetUserPasswordCommand(int UserId) : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:account:{UserId}:reset-password";

    /// <inheritdoc cref="ResetUserPasswordCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ResetUserPasswordCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            ResetUserPasswordCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AccountGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            return await accounts.ResetPasswordAsync(command.UserId, temporary, cancellationToken)
                ? ResponseDto<string>.Ok(
                    temporary, "Пароль сброшен, сессии пользователя завершены. Передайте пароль лично.")
                : ResponseDto<string>.NotFound("Учётная запись не найдена.");
        }
    }
}

/// <summary>Включить или отключить учётную запись (отключение обрывает сессии немедленно).</summary>
public sealed record SetUserAccountActiveCommand(int UserId, bool IsActive)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:account:{UserId}:{(IsActive ? "enable" : "disable")}";

    /// <inheritdoc cref="SetUserAccountActiveCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserAccountActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetUserAccountActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
            {
                return ResponseDto<bool>.BadRequest(AccountGuard.Denied);
            }

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AccountGuard.Denied);
            }

            // Отключить САМОГО СЕБЯ нельзя: администратор мгновенно потерял бы доступ и, если он
            // единственный, систему стало бы некому администрировать (тот же класс отказа, что
            // «замок без ключа» с ролями, §6.4.1).
            if (command.UserId == callerId && !command.IsActive)
            {
                return ResponseDto<bool>.BadRequest(
                    "Нельзя отключить собственную учётную запись — вы потеряете доступ немедленно.");
            }

            return await accounts.SetActiveAsync(command.UserId, command.IsActive, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Учётная запись не найдена.");
        }
    }
}

/// <summary>Сменить СВОЙ пароль. Доступно любому вошедшему — это не администрирование.</summary>
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>НИ ОДИН из паролей в журнал не попадает (ТБ-043) — только факт смены.</remarks>
    public string? AuditSummary => "inspector:account:change-own-password";

    /// <inheritdoc cref="ChangeOwnPasswordCommand" />
    public sealed class Handler(IUserAccountStore accounts, ISubjectProvider subjectProvider)
        : IRequestHandler<ChangeOwnPasswordCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ChangeOwnPasswordCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<bool>.BadRequest("Смена пароля требует аутентифицированного пользователя.");
            }

            var status = await accounts.ChangeOwnPasswordAsync(
                userId, command.CurrentPassword, command.NewPassword, cancellationToken);

            return status switch
            {
                PasswordChangeStatus.Ok => ResponseDto<bool>.Ok(
                    true, "Пароль изменён. Войдите заново с новым паролем."),
                PasswordChangeStatus.WrongCurrentPassword =>
                    ResponseDto<bool>.BadRequest("Текущий пароль указан неверно."),
                PasswordChangeStatus.SameAsCurrent =>
                    ResponseDto<bool>.BadRequest("Новый пароль совпадает с текущим."),
                PasswordChangeStatus.NoLocalPassword =>
                    ResponseDto<bool>.BadRequest(
                        "У вашей учётной записи нет локального пароля — вход выполняется через внешнюю систему."),
                _ => ResponseDto<bool>.NotFound("Учётная запись не найдена."),
            };
        }
    }
}

/// <summary>Кто вправе вести учётные записи — то же правило, что у ролей и допусков (§2.1).</summary>
public static class AccountGuard
{
    /// <summary>Единый текст отказа.</summary>
    public const string Denied = "Ведение учётных записей доступно только Администратору.";

    /// <inheritdoc cref="AccountGuard" />
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

/// <summary>Порождение временного пароля для выдачи администратором.</summary>
/// <remarks>
/// Криптостойкий генератор (не <c>Random</c>): предсказуемый временный пароль — это выданный доступ.
/// Алфавит без похожих символов (0/O, 1/l/I): пароль диктуют голосом и переписывают от руки, и
/// ошибка прочтения здесь дороже пары битов энтропии — при 12 символах её с запасом хватает.
/// </remarks>
internal static class TemporaryPassword
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Длина временного пароля.</summary>
    public const int Length = 12;

    /// <inheritdoc cref="TemporaryPassword" />
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}

/// <summary>Правила создания учётной записи.</summary>
public sealed class CreateUserAccountValidator : AbstractValidator<CreateUserAccountCommand>
{
    /// <inheritdoc cref="CreateUserAccountValidator" />
    public CreateUserAccountValidator()
    {
        RuleFor(c => c.UserName).NotEmpty().WithMessage("Укажите имя входа.")
            .MaximumLength(100)
            .Matches("^[a-zA-Z0-9._-]+$")
                .WithMessage("Имя входа: латиница, цифры, точка, дефис, подчёркивание.");
        RuleFor(c => c.DisplayName).MaximumLength(200);
    }
}

/// <summary>Правила смены собственного пароля.</summary>
public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    /// <summary>
    /// Минимальная длина нового пароля. Требований к составу (цифра/регистр/спецсимвол) НЕТ
    /// намеренно: они гонят людей к «Пароль1!» и записям на бумаге, тогда как длина даёт стойкость
    /// честно. Значение согласуется с временным паролем (12 символов из криптогенератора).
    /// </summary>
    public const int MinPasswordLength = 10;

    /// <inheritdoc cref="ChangeOwnPasswordValidator" />
    public ChangeOwnPasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty().WithMessage("Укажите текущий пароль.");
        RuleFor(c => c.NewPassword).NotEmpty()
            .MinimumLength(MinPasswordLength)
                .WithMessage($"Новый пароль — не короче {MinPasswordLength} символов.")
            .MaximumLength(200);
    }
}
