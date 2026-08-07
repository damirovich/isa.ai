using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Валидаторы формы сценариев документов (сквозной <c>ValidationBehavior</c> хоста).
/// Правила, требующие БД (тип активен, уникальность рег. номера, Execution-обязательность),
/// проверяет хранилище — валидаторы остаются синхронными и юнит-тестируемыми.
/// </summary>
public sealed class RegisterDocumentValidator : AbstractValidator<RegisterDocumentCommand>
{
    /// <summary>Правила §3.2 + режимные поля (гриф/подразделение — fail-closed).</summary>
    public RegisterDocumentValidator(IDocFlowClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        // §3.2: дата регистрации не в будущем — «сегодня» по поясу эксплуатанта (Asia/Bishkek),
        // не по UTC сервера (обещание этапа 3.1 выполнено вместе с IDocFlowClock).
        RuleFor(c => c.RegDate).Must(d => d <= clock.Today)
            .WithMessage("Дата регистрации не может быть в будущем.");

        RuleFor(c => c.RegNumber).MaximumLength(200);
        RuleFor(c => c.ShortContent).NotEmpty().WithMessage("Укажите краткое содержание (ТЗ §3.2).")
            .MaximumLength(2000);
        RuleFor(c => c.Source).MaximumLength(500);
        RuleFor(c => c.Notes).MaximumLength(2000);
        RuleFor(c => c.TypeId).GreaterThan(0).WithMessage("Выберите тип документа.");
        RuleFor(c => c.Direction).IsInEnum();
        RuleFor(c => c.Priority).IsInEnum().When(c => c.Priority.HasValue);

        // Решётка доступа (ADR-0017 п.5): без грифа и подразделения документ не регистрируется.
        RuleFor(c => c.Classification).GreaterThanOrEqualTo((short)0)
            .WithMessage("Укажите гриф документа.");
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Укажите подразделение-владельца документа.");

        RuleFor(c => c.CommonDeadline).NotNull()
            .When(c => c.UseCommonDeadline)
            .WithMessage("Укажите единый срок исполнения (ТЗ §4.1).");

        RuleForEach(c => c.Assignments).ChildRules(a =>
            a.RuleFor(x => x.DivisionId).GreaterThan(0).WithMessage("Укажите подразделение назначения."));

        RuleFor(c => c.Assignments)
            .Must(list => list.Select(a => a.DivisionId).Distinct().Count() == list.Count)
            .WithMessage("Подразделения назначений не должны повторяться (ТЗ §4.1).");
    }
}

/// <summary>
/// Валидатор правки документа (§3.2). Правила формы те же, что при регистрации, — реквизиты одни и те же.
/// </summary>
/// <remarks>
/// Отдельный класс, а не общий базовый с <see cref="RegisterDocumentValidator"/>: у регистрации есть
/// назначения и единый срок, у правки их нет, и общий предок пришлось бы обвешивать условиями
/// «а это только при создании» — ровно та связанность, из-за которой потом ломается и то и другое.
/// Правила, требующие БД (тип активен, уникальность номера, смена группы, режимные ограничения по
/// грифу), проверяет хранилище — здесь только форма.
/// </remarks>
public sealed class UpdateDocumentValidator : AbstractValidator<UpdateDocumentCommand>
{
    /// <inheritdoc cref="UpdateDocumentValidator" />
    public UpdateDocumentValidator(IDocFlowClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.DocumentId).GreaterThan(0);

        RuleFor(c => c.RegDate).Must(d => d <= clock.Today)
            .WithMessage("Дата регистрации не может быть в будущем.");

        RuleFor(c => c.RegNumber).MaximumLength(200);
        RuleFor(c => c.ShortContent).NotEmpty().WithMessage("Укажите краткое содержание (ТЗ §3.2).")
            .MaximumLength(2000);
        RuleFor(c => c.Source).MaximumLength(500);
        RuleFor(c => c.Notes).MaximumLength(2000);
        RuleFor(c => c.TypeId).GreaterThan(0).WithMessage("Выберите тип документа.");
        RuleFor(c => c.Direction).IsInEnum();
        RuleFor(c => c.Priority).IsInEnum().When(c => c.Priority.HasValue);

        RuleFor(c => c.Classification).GreaterThanOrEqualTo((short)0)
            .WithMessage("Укажите гриф документа.");
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Укажите подразделение-владельца документа.");
    }
}

/// <summary>Общие файловые лимиты (перенос контракта СКИД §6.1: защита от падения на MaxRequestBodySize).</summary>
internal static class FileRules
{
    /// <summary>Максимум файлов на одну операцию.</summary>
    public const int MaxCount = 10;

    /// <summary>Максимальный размер одного файла (25 МБ).</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Максимальный суммарный размер файлов операции (50 МБ).</summary>
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Перенос allowlist СКИД (<c>UploadDocumentFileCommandValidator</c>/<c>UploadedFileValidator</c>):
    /// файл документа и файлы к переходам/продлениям — только PDF/DOCX/DOC. Сопутствующие вложения
    /// (<see cref="UploadAttachmentValidator"/>) — БЕЗ ограничения, как и в СКИД; туда же естественно
    /// ложатся картинки для предпросмотра (этап 4.3) — новый тип для загрузки не заводился.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedDocumentContentTypes = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword",
    };

    /// <summary>Подключает правила списка файлов к валидатору команды.</summary>
    public static void ApplyFileListRules<T>(
        this AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<Domain.Services.UploadedFile>?>> files)
    {
        validator.RuleFor(files)
            .Must(list => list is null || list.Count <= MaxCount)
                .WithMessage($"Не больше {MaxCount} файлов за одну операцию.")
            .Must(list => list is null || list.All(f => f.Content.LongLength <= MaxFileBytes))
                .WithMessage("Файл больше 25 МБ.")
            .Must(list => list is null || list.Sum(f => f.Content.LongLength) <= MaxTotalBytes)
                .WithMessage("Суммарный размер файлов больше 50 МБ.")
            .Must(list => list is null || list.All(f => AllowedDocumentContentTypes.Contains(f.ContentType)))
                .WithMessage("Допустимые форматы: PDF, DOCX, DOC.");
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class UploadDocumentFileValidator : AbstractValidator<UploadDocumentFileCommand>
{
    /// <summary>Правила загрузки файла документа (§3.3).</summary>
    public UploadDocumentFileValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(500);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200)
            .Must(ct => FileRules.AllowedDocumentContentTypes.Contains(ct))
            .WithMessage("Допустимые форматы файла документа: PDF, DOCX, DOC (ТЗ СКИД).");
        RuleFor(c => c.Language).IsInEnum();
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= FileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 25 МБ.");
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class UploadAttachmentValidator : AbstractValidator<UploadAttachmentCommand>
{
    /// <summary>Правила прикрепления сопутствующего файла.</summary>
    public UploadAttachmentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(500);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= FileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 25 МБ.");
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class ChangeAssignmentStatusValidator : AbstractValidator<ChangeAssignmentStatusCommand>
{
    /// <summary>Правила формы §4.2 (+ файловые лимиты перехода).</summary>
    public ChangeAssignmentStatusValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.NewStatus).IsInEnum();
        RuleFor(c => c.Comment).MaximumLength(2000);
        this.ApplyFileListRules(c => c.Files);
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
/// <summary>
/// Правила добавления назначения (§4.1). Обязательны только документ и подразделение: назначение
/// «на подразделение», без исполнителя и без срока, — законное состояние (перенос решения СКИД,
/// делать форму строже оригинала незачем). Остальное — дубль подразделения, группа документа,
/// допуск исполнителя — проверяется в хранилище: этим правилам нужна БД.
/// </summary>
public sealed class AddAssignmentValidator : AbstractValidator<AddAssignmentCommand>
{
    /// <inheritdoc cref="AddAssignmentValidator" />
    public AddAssignmentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Для назначения необходимо указать подразделение.");
        RuleFor(c => c.AssigneeUserId).GreaterThan(0)
            .When(c => c.AssigneeUserId is not null)
            .WithMessage("Некорректный исполнитель.");
    }
}

/// <summary>Правила переназначения исполнителя (§4.7): основание НЕобязательно — как в СКИД.</summary>
public sealed class ReassignAssigneeValidator : AbstractValidator<ReassignAssigneeCommand>
{
    /// <summary>Максимальная длина основания — как у основания продления срока.</summary>
    public const int MaxReasonLength = 2000;

    /// <inheritdoc cref="ReassignAssigneeValidator" />
    public ReassignAssigneeValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.NewAssigneeUserId).GreaterThan(0)
            .WithMessage("Укажите нового исполнителя.");
        RuleFor(c => c.Reason).MaximumLength(MaxReasonLength);
    }
}

public sealed class ExtendAssignmentDeadlineValidator : AbstractValidator<ExtendAssignmentDeadlineCommand>
{
    /// <summary>Правила формы §4.6 (основание обязательно; срок строго в будущем; + файловые лимиты).</summary>
    public ExtendAssignmentDeadlineValidator(IDocFlowClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.Reason).NotEmpty().WithMessage("Укажите основание продления (ТЗ §4.6).")
            .MaximumLength(2000);

        // Перенос СКИД (ExtendDeadlineCommandValidator, Contracts §6.2): продление СТРОГО в будущее —
        // «сегодня» и раньше не продление, а искажение истории (следующий тик снова пометит просроченным).
        RuleFor(c => c.NewDeadline).Must(d => d > clock.Today)
            .WithMessage("Новый срок должен быть строго позже сегодняшней даты.");

        this.ApplyFileListRules(c => c.Files);
    }
}
