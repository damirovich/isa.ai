using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>Что доступно на экране переноса.</summary>
/// <param name="Allowed">Есть право «Перенос баз Access».</param>
/// <param name="UnavailableReason">Почему перенос на этом сервере невозможен (нет драйвера ACE); <see langword="null"/> — возможен.</param>
/// <param name="CanCreateReferences">Есть право вести справочники — галочка «завести недостающие записи» доступна.</param>
public sealed record AccessImportSetup(bool Allowed, string? UnavailableReason, bool CanCreateReferences);

/// <summary>Загруженная база: файл, её таблицы и предложенное сопоставление.</summary>
/// <param name="File">Файл в хранилище.</param>
/// <param name="Tables">Таблицы со столбцами.</param>
/// <param name="Suggested">Сопоставление, предложенное по названиям.</param>
public sealed record AccessDatabaseInfo(AccessDatabaseFile File, IReadOnlyList<AccessTable> Tables, AccessMapping Suggested);

/// <summary>Общие проверки сценариев переноса — одинаковые для проверки и запуска.</summary>
internal static class AccessImportGuard
{
    /// <summary>Отказ: заводить записи справочников — право «Подразделения и справочники».</summary>
    public static readonly string ReferencesDenied =
        "Завести недостающие записи справочников может только роль с правом «"
        + InvestigationPermissions.Get(InvestigationPermissions.AdminDirectories).Label
        + "». Снимите галочку или заведите записи в справочниках заранее.";

    /// <summary>Отказ: нет учётной записи (перенос без автора невозможен).</summary>
    public const string NoUser = "Перенос выполняет только вошедший сотрудник.";

    /// <summary>
    /// Право на перенос, при галочке — право вести справочники, драйвер на сервере и вошедший сотрудник. Возвращает
    /// текст отказа или <see langword="null"/> и идентификатор сотрудника.
    /// </summary>
    public static async Task<(string? Denied, int UserId)> CheckAsync(
        IUserRoleStore roles, ISubjectProvider subjects, IAccessDatabaseReader reader, bool createReferences,
        CancellationToken cancellationToken)
    {
        if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.CasesImport, cancellationToken))
        {
            return (RoleGuard.Denied(InvestigationPermissions.CasesImport), 0);
        }

        if (createReferences
            && !await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminDirectories, cancellationToken))
        {
            return (ReferencesDenied, 0);
        }

        if (reader.UnavailableReason is { } reason)
        {
            return (reason, 0);
        }

        return await subjects.GetCurrentUserIdAsync(cancellationToken) is { } userId ? (null, userId) : (NoUser, 0);
    }

    /// <summary>
    /// Гриф и подразделение — в пределах допуска оператора (ТБ-024): иначе хранилище откажет каждому делу; лучше
    /// сказать это до чтения базы.
    /// </summary>
    public static bool WithinClearance(AccessContext access, AccessImportOptions options) =>
        options.Classification <= access.MaxClassification && access.AllowedDivisions.Contains(options.DivisionId);
}

/// <summary>Состояние экрана переноса: право, драйвер, право вести справочники.</summary>
public sealed record GetAccessImportSetupQuery : IRequest<ResponseDto<AccessImportSetup>>
{
    /// <inheritdoc cref="GetAccessImportSetupQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IAccessDatabaseReader reader)
        : IRequestHandler<GetAccessImportSetupQuery, ResponseDto<AccessImportSetup>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessImportSetup>> Handle(GetAccessImportSetupQuery query, CancellationToken cancellationToken)
        {
            var allowed = await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.CasesImport, cancellationToken);
            var references = allowed
                && await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.AdminDirectories, cancellationToken);
            return ResponseDto<AccessImportSetup>.Ok(new AccessImportSetup(allowed, reader.UnavailableReason, references));
        }
    }
}

/// <summary>
/// Загрузить базу Access (ADR-0043 п. 1): файл — в хранилище с SHA-256 (запись журнала с суммой — цепочка хранения,
/// ТБ-077), затем чтение схемы и предложение сопоставления. Файл, который не открылся как база, удаляется.
/// </summary>
/// <param name="Content">Содержимое (читается один раз).</param>
/// <param name="FileName">Исходное имя файла.</param>
/// <param name="Length">Заявленный размер, байт.</param>
public sealed record UploadAccessDatabaseCommand(Stream Content, string FileName, long Length)
    : IRequest<ResponseDto<AccessDatabaseInfo>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(CultureInfo.InvariantCulture, $"investigation:access-import:upload:size={Length}");

    /// <inheritdoc />
    /// <remarks>Гриф содержимого до выбора оператора неизвестен — запись журнала под высшим грифом (fail-closed).</remarks>
    public short? AuditClassification => ClassificationLevels.Max;

    /// <inheritdoc cref="UploadAccessDatabaseCommand" />
    public sealed class Handler(
        IUserRoleStore roles,
        ISubjectProvider subjects,
        IAccessDatabaseReader reader,
        AccessImportFiles files,
        IFileStorage storage,
        IAuditWriter audit)
        : IRequestHandler<UploadAccessDatabaseCommand, ResponseDto<AccessDatabaseInfo>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessDatabaseInfo>> Handle(UploadAccessDatabaseCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var (denied, userId) = await AccessImportGuard.CheckAsync(roles, subjects, reader, false, cancellationToken);
            if (denied is not null)
            {
                return ResponseDto<AccessDatabaseInfo>.BadRequest(denied);
            }

            AccessDatabaseFile file;
            try
            {
                file = await files.SaveAsync(command.Content, command.FileName, userId, cancellationToken);
            }
            catch (AccessDatabaseReadException exception)
            {
                return ResponseDto<AccessDatabaseInfo>.BadRequest(exception.Message);
            }

            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    ClassificationLevels.Max,
                    userId,
                    "investigation:access-import:file=" + file.StoredFileName,
                    PayloadSensitive: $"перенос из Access: загружена база «{file.FileName}», {file.Length.ToString(CultureInfo.InvariantCulture)} байт, sha256={file.Sha256}"),
                cancellationToken);

            try
            {
                await using var database = await files.OpenAsync(reader, file, userId, cancellationToken);
                return ResponseDto<AccessDatabaseInfo>.Ok(
                    new AccessDatabaseInfo(file, database.Tables, AccessAutoMapper.Suggest(database.Tables)));
            }
            catch (AccessDatabaseReadException exception)
            {
                // Не база — в хранилище ей делать нечего: удаляется сразу (запись о загрузке в журнале остаётся).
                await storage.DeleteAsync(file.StoredFileName, AccessImportFiles.Category, userId.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
                return ResponseDto<AccessDatabaseInfo>.BadRequest(exception.Message);
            }
        }
    }
}

/// <summary>Правила загрузки: имя файла базы, размер в пределах формата.</summary>
public sealed class UploadAccessDatabaseValidator : AbstractValidator<UploadAccessDatabaseCommand>
{
    /// <inheritdoc cref="UploadAccessDatabaseValidator" />
    public UploadAccessDatabaseValidator()
    {
        RuleFor(c => c.Content).NotNull();
        RuleFor(c => c.FileName).Must(AccessImportFiles.HasDatabaseExtension).WithMessage(AccessImportFiles.WrongExtensionMessage);
        RuleFor(c => c.Length).InclusiveBetween(1, AccessImportFiles.MaxBytes)
            .WithMessage("Файл пуст или больше 2 ГБ — это не база Access.");
    }
}

/// <summary>Проверить перенос — без записи (ADR-0043 п. 7): счёт, ошибки по записям, образец заданий.</summary>
/// <param name="File">Загруженный файл.</param>
/// <param name="Mapping">Сопоставление.</param>
/// <param name="Options">Выбор оператора.</param>
public sealed record CheckAccessImportQuery(AccessDatabaseFile File, AccessMapping Mapping, AccessImportOptions Options)
    : IRequest<ResponseDto<AccessImportPlan>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:access-import:check:division={Options.DivisionId};grif={Options.Classification}");

    /// <inheritdoc />
    /// <remarks>Образец заданий — сведения базы под выбранным грифом.</remarks>
    public short? AuditClassification => Options.Classification;

    /// <inheritdoc cref="CheckAccessImportQuery" />
    public sealed class Handler(
        IUserRoleStore roles,
        ISubjectProvider subjects,
        IAccessContextProvider accessProvider,
        IAccessDatabaseReader reader,
        AccessImportFiles files,
        AccessImporter importer)
        : IRequestHandler<CheckAccessImportQuery, ResponseDto<AccessImportPlan>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessImportPlan>> Handle(CheckAccessImportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var (denied, userId) = await AccessImportGuard.CheckAsync(
                roles, subjects, reader, query.Options.CreateMissingReferences, cancellationToken);
            if (denied is not null)
            {
                return ResponseDto<AccessImportPlan>.BadRequest(denied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!AccessImportGuard.WithinClearance(access, query.Options))
            {
                return ResponseDto<AccessImportPlan>.BadRequest(CaseGuard.OutsideClearance);
            }

            try
            {
                await using var database = await files.OpenAsync(reader, query.File, userId, cancellationToken);
                return ResponseDto<AccessImportPlan>.Ok(
                    await importer.PlanAsync(database, query.File, query.Mapping, query.Options, cancellationToken));
            }
            catch (AccessDatabaseReadException exception)
            {
                return ResponseDto<AccessImportPlan>.BadRequest(exception.Message);
            }
        }
    }
}

/// <summary>
/// Перенести базу (ADR-0043): только после проверки с тем же отпечатком «файл + сопоставление + выбор» и с
/// подтверждением грифа и подразделения (ТБ-024). Синхронно, с ходом по заданиям; прерванный перенос повтор
/// продолжает без дублей.
/// </summary>
/// <param name="File">Загруженный файл.</param>
/// <param name="Mapping">Сопоставление.</param>
/// <param name="Options">Выбор оператора.</param>
/// <param name="Fingerprint">Отпечаток из проверки.</param>
/// <param name="ConfirmedClassification">Оператор подтвердил гриф и подразделение.</param>
/// <param name="Progress">Ход: сколько заданий обработано.</param>
public sealed record RunAccessImportCommand(
    AccessDatabaseFile File,
    AccessMapping Mapping,
    AccessImportOptions Options,
    string Fingerprint,
    bool ConfirmedClassification,
    IProgress<int>? Progress = null)
    : IRequest<ResponseDto<AccessImportReport>>, IAuditableRequest
{
    /// <summary>Отказ: после проверки что-то поменяли.</summary>
    public const string StaleCheck = "Сопоставление или выбор изменились после проверки — нажмите «Проверить» ещё раз.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:access-import:run:division={Options.DivisionId};grif={Options.Classification};status={Options.Status};sha={File.Sha256[..Math.Min(16, File.Sha256.Length)]}");

    /// <inheritdoc />
    public short? AuditClassification => Options.Classification;

    /// <inheritdoc cref="RunAccessImportCommand" />
    public sealed class Handler(
        IUserRoleStore roles,
        ISubjectProvider subjects,
        IAccessContextProvider accessProvider,
        IAccessDatabaseReader reader,
        AccessImportFiles files,
        AccessImporter importer)
        : IRequestHandler<RunAccessImportCommand, ResponseDto<AccessImportReport>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessImportReport>> Handle(RunAccessImportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var (denied, userId) = await AccessImportGuard.CheckAsync(
                roles, subjects, reader, command.Options.CreateMissingReferences, cancellationToken);
            if (denied is not null)
            {
                return ResponseDto<AccessImportReport>.BadRequest(denied);
            }

            if (!string.Equals(command.Fingerprint, AccessSnapshot.Fingerprint(command.File.Sha256, command.Mapping, command.Options), StringComparison.Ordinal))
            {
                return ResponseDto<AccessImportReport>.Conflict(StaleCheck);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!AccessImportGuard.WithinClearance(access, command.Options))
            {
                return ResponseDto<AccessImportReport>.BadRequest(CaseGuard.OutsideClearance);
            }

            try
            {
                await using var database = await files.OpenAsync(reader, command.File, userId, cancellationToken);
                return ResponseDto<AccessImportReport>.Ok(await importer.RunAsync(
                    database, command.File, command.Mapping, command.Options, access, userId, command.Progress, cancellationToken));
            }
            catch (AccessDatabaseReadException exception)
            {
                return ResponseDto<AccessImportReport>.BadRequest(exception.Message);
            }
        }
    }
}

/// <summary>Правила выбора оператора: подразделение, гриф в шкале, статус «в производстве» или «приостановлено».</summary>
public sealed class AccessImportOptionsValidator : AbstractValidator<AccessImportOptions>
{
    /// <inheritdoc cref="AccessImportOptionsValidator" />
    public AccessImportOptionsValidator()
    {
        RuleFor(o => o.DivisionId).GreaterThan(0).WithMessage("Выберите подразделение (ТБ-024).");
        RuleFor(o => o.Classification).InclusiveBetween((short)0, ClassificationLevels.Max).WithMessage("Выберите гриф из шкалы.");
        RuleFor(o => o.Status).Must(s => s is CaseStatus.InProgress or CaseStatus.Suspended)
            .WithMessage("Статус перенесённых заданий — «в производстве» или «приостановлено»; закрывают задание в карточке.");
    }
}

/// <summary>Правила проверки переноса.</summary>
public sealed class CheckAccessImportValidator : AbstractValidator<CheckAccessImportQuery>
{
    /// <inheritdoc cref="CheckAccessImportValidator" />
    public CheckAccessImportValidator()
    {
        RuleFor(q => q.File).NotNull();
        RuleFor(q => q.Mapping).NotNull();
        RuleFor(q => q.Options).NotNull().SetValidator(new AccessImportOptionsValidator());
    }
}

/// <summary>Правила запуска переноса: подтверждение грифа и подразделения обязательно (ТБ-024).</summary>
public sealed class RunAccessImportValidator : AbstractValidator<RunAccessImportCommand>
{
    /// <inheritdoc cref="RunAccessImportValidator" />
    public RunAccessImportValidator()
    {
        RuleFor(c => c.File).NotNull();
        RuleFor(c => c.Mapping).NotNull();
        RuleFor(c => c.Options).NotNull().SetValidator(new AccessImportOptionsValidator());
        RuleFor(c => c.Fingerprint).NotEmpty().WithMessage("Сначала проверьте перенос.");
        RuleFor(c => c.ConfirmedClassification).Equal(true)
            .WithMessage("Подтвердите гриф и подразделение всех заданий базы (ТБ-024).");
    }
}
