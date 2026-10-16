using System.Globalization;
using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Briefs;

/// <summary>Тексты справок (ADR-0053).</summary>
public static class BriefMessages
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-021).</summary>
    public const string NotFound = "Дело или фигурант не найдены либо недоступны.";

    /// <summary>Нет числового идентификатора сотрудника.</summary>
    public const string NoSubject = "Справка доступна только сотруднику с учётной записью.";

    /// <summary>Начало периода позже конца.</summary>
    public const string BadPeriod = "Начало периода позже его конца.";

    /// <summary>Узел ИИ не ответил.</summary>
    public const string ModelUnavailable = "Узел ИИ недоступен или не ответил вовремя — попробуйте позже. Факты справки доступны и без изложения.";

    /// <summary>Модель ничего не написала.</summary>
    public const string EmptyAnswer = "Модель вернула пустой ответ — попробуйте ещё раз.";

    /// <summary>Пометка к изложению в справке.</summary>
    public const string SummaryNotice =
        "Изложение подготовлено локальной моделью по фактам справки и требует проверки: сверяйте его с таблицами ниже.";

    /// <summary>Отказ по праву «Справки по делу и фигуранту».</summary>
    public static string NoRight => RoleGuard.Denied(InvestigationPermissions.CasesBriefs);

    /// <summary>Пометка черновика: когда собраны факты.</summary>
    public static string DraftNotice(DateTime collectedAtUtc) =>
        "ЧЕРНОВИК — требует проверки человеком (ТБ-042). Факты — расчёт системы на "
        + DateTime.SpecifyKind(collectedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + ".";

    /// <summary>Объект журнала справки: <c>investigation:case:{id}:brief</c> или <c>investigation:person:{id}:brief</c>.</summary>
    public static string AuditRef(BriefKind kind, int id) =>
        "investigation:" + (kind == BriefKind.Person ? "person:" : "case:") + id.ToString(CultureInfo.InvariantCulture) + ":brief";

    /// <summary>Период словами для журнала: без имён и номеров (ТБ-032).</summary>
    internal static string Period(DateOnly? from, DateOnly? to) =>
        from is null && to is null
            ? string.Empty
            : ";period=" + (from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty) + ".."
                + (to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
}

/// <summary>
/// Справка по делу или по фигуранту — факты, собранные системой (ТФ-ОТЧ-01/02, ADR-0053). Модель не вызывается.
/// Открытие справки — запись <see cref="AuditAction.View"/> (гриф — не ниже допуска сотрудника).
/// </summary>
/// <param name="Kind">Вид справки.</param>
/// <param name="Id">Дело или фигурант.</param>
/// <param name="From">Начало периода (справка по делу); <see langword="null"/> — со дня открытия дела.</param>
/// <param name="To">Конец периода (справка по делу); <see langword="null"/> — по сегодня.</param>
public sealed record GetBriefQuery(BriefKind Kind, int Id, DateOnly? From = null, DateOnly? To = null)
    : IRequest<ResponseDto<Brief>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => BriefMessages.AuditRef(Kind, Id) + BriefMessages.Period(From, To);

    /// <inheritdoc cref="GetBriefQuery" />
    public sealed class Handler(
        BriefCollector collector,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<GetBriefQuery, ResponseDto<Brief>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<Brief>> Handle(GetBriefQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesBriefs, cancellationToken))
            {
                return ResponseDto<Brief>.BadRequest(BriefMessages.NoRight);
            }

            if (query.From > query.To)
            {
                return ResponseDto<Brief>.BadRequest(BriefMessages.BadPeriod);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await BriefFacts.CollectAsync(collector, query.Kind, query.Id, query.From, query.To, access, cancellationToken) is { } brief
                ? ResponseDto<Brief>.Ok(brief)
                : ResponseDto<Brief>.NotFound(BriefMessages.NotFound);
        }
    }
}

/// <summary>Готовое изложение: текст модели и его проверка.</summary>
/// <param name="Text">Текст модели как есть (разметку экран показывает текстом).</param>
/// <param name="Classification">Гриф изложения — не ниже грифа справки.</param>
/// <param name="Unverified">Ссылки (номера, статьи), которых нет во фрагментах, — проверка ядра (ТБ-040).</param>
/// <param name="NotInFacts">Числа, даты и время из изложения, которых нет в фактах справки, — проверка профиля.</param>
public sealed record BriefSummary(string Text, short Classification, IReadOnlyList<string> Unverified, IReadOnlyList<string> NotInFacts)
{
    /// <summary>Проверки ничего не нашли.</summary>
    public bool AllConfirmed => Unverified.Count == 0 && NotInFacts.Count == 0;
}

/// <summary>Обновление потока изложения: кусок черновика, итог или отказ.</summary>
/// <param name="TextDelta">Кусок черновика — НЕпроверенный текст, пока не пришёл итог.</param>
/// <param name="Final">Итог с проверками.</param>
/// <param name="Error">Отказ или сбой — текст для сотрудника.</param>
public sealed record BriefSummaryUpdate(string? TextDelta = null, BriefSummary? Final = null, string? Error = null);

/// <summary>
/// Краткое изложение справки локальной моделью (ТФ-ОТЧ-01/02, ADR-0053): факты считает система, модель пишет только
/// связующий текст. Ответ отдаётся потоком.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТЫ. Факты собираются под решёткой субъекта (<see cref="BriefCollector"/>); недоступное — «не найдено»
/// до модели. Генерация — общим грунтованным конвейером ядра: правило грунтовки первым, промпт профиля вторым и его не
/// ослабляет (ТБ-041); поиск фрагментов ВЫКЛЮЧЕН (<see cref="RetrievalFilter.DocumentIds"/> пуст) — модель не видит
/// ничего сверх фактов справки, чужие материалы в изложение не попадают. Ссылки ответа проверяет ядровой валидатор
/// (ТБ-040): «№ …», «ст. …» без фрагментов — непроверенные; числа и даты сверяет с фактами профиль
/// (<see cref="BriefTexts.NotInFacts(string, Brief)"/>). Ответ — черновик для человека (ТБ-042).</para>
/// <para>ГРИФ И ЖУРНАЛ (ТБ-030/032/033). Гриф и подразделение данных промпта передаются ядру
/// (<see cref="GroundedRequest.InputClassification"/>, <see cref="GroundedRequest.InputDivisionId"/>): запись
/// <see cref="AuditAction.Generate"/> и гриф изложения — не ниже грифа справки, запись видна аудиторам только
/// подразделения дела. До модели — запись
/// <see cref="AuditAction.View"/> на объект справки без имён и номеров; не записалась — модель не вызывается
/// (fail-closed). Поток не проходит сквозные поведения Mediator, поэтому журнал и отказы — здесь.</para>
/// </remarks>
/// <param name="Kind">Вид справки.</param>
/// <param name="Id">Дело или фигурант.</param>
/// <param name="From">Начало периода (справка по делу).</param>
/// <param name="To">Конец периода (справка по делу).</param>
public sealed record WriteBriefSummaryCommand(BriefKind Kind, int Id, DateOnly? From = null, DateOnly? To = null)
    : IStreamRequest<BriefSummaryUpdate>
{
    /// <inheritdoc cref="WriteBriefSummaryCommand" />
    public sealed class Handler(
        BriefCollector collector,
        IGroundedGenerator generator,
        IAuditWriter auditWriter,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IStreamRequestHandler<WriteBriefSummaryCommand, BriefSummaryUpdate>
    {
        /// <inheritdoc />
        public async IAsyncEnumerable<BriefSummaryUpdate> Handle(
            WriteBriefSummaryCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesBriefs, cancellationToken))
            {
                yield return new BriefSummaryUpdate(Error: BriefMessages.NoRight);
                yield break;
            }

            if (command.From > command.To)
            {
                yield return new BriefSummaryUpdate(Error: BriefMessages.BadPeriod);
                yield break;
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } me)
            {
                yield return new BriefSummaryUpdate(Error: BriefMessages.NoSubject);
                yield break;
            }

            if (await BriefFacts.CollectAsync(collector, command.Kind, command.Id, command.From, command.To, access, cancellationToken) is not { } brief)
            {
                yield return new BriefSummaryUpdate(Error: BriefMessages.NotFound);
                yield break;
            }

            // АУДИТ ДО МОДЕЛИ (ТБ-030, fail-closed): нет записи — нет обращения к модели. Без имён и номеров (ТБ-032).
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.View,
                    brief.Classification,
                    SubjectId: me,
                    ObjectRef: BriefMessages.AuditRef(command.Kind, command.Id) + ":summary",
                    DivisionId: brief.DivisionId,
                    PayloadSensitive: "Изложение справки локальной моделью: разделов "
                        + brief.Sections.Count.ToString(CultureInfo.InvariantCulture) + BriefMessages.Period(command.From, command.To)),
                cancellationToken);

            var request = new GroundedRequest(
                command.Kind == BriefKind.Person ? "краткое изложение справки по фигуранту" : "краткое изложение справки по делу",
                ModelRole.Draft,
                TopK: 1,
                TaskPrompt: BriefPrompt.Render(command.Kind, BriefTexts.ForModel(brief)),
                History: null,
                Filter: new RetrievalFilter(DocumentIds: []),
                InputClassification: brief.Classification,
                InputDivisionId: brief.DivisionId);

            await using var stream = generator.GenerateStreamingAsync(request, access, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                GroundedStreamUpdate? update = null;
                var failed = false;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (ModelUnavailableException)
                {
                    // Попытку ядро записало в журнал (Generate) с грифом справки.
                    failed = true;
                }

                if (failed || update is null)
                {
                    yield return new BriefSummaryUpdate(Error: BriefMessages.ModelUnavailable);
                    yield break;
                }

                if (update.Final is { } final)
                {
                    yield return string.IsNullOrWhiteSpace(final.Answer)
                        ? new BriefSummaryUpdate(Error: BriefMessages.EmptyAnswer)
                        : new BriefSummaryUpdate(Final: new BriefSummary(
                            final.Answer,
                            (short)Math.Max(final.ResultClassification, brief.Classification),
                            final.Grounding.Citations.Where(c => c.Status != CitationStatus.Confirmed).Select(c => c.Citation).Distinct().ToList(),
                            BriefTexts.NotInFacts(final.Answer, brief)));
                    yield break;
                }

                if (!string.IsNullOrEmpty(update.TextDelta))
                {
                    yield return new BriefSummaryUpdate(TextDelta: update.TextDelta);
                }
            }
        }
    }
}

/// <summary>Файл справки.</summary>
/// <param name="Content">Содержимое .docx.</param>
/// <param name="FileName">Имя файла.</param>
/// <param name="ContentType">MIME-тип.</param>
public sealed record BriefFile(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Выгрузка справки в .docx (ADR-0053): факты и гриф заново собираются НА СЕРВЕРЕ; со страницы приходит только текст
/// изложения, который человек проверил и, если нужно, поправил (он за него и отвечает, ТБ-042), и гриф, который сервер
/// выдал этому изложению. Опустить гриф или подменить факты со страницы нельзя: гриф файла — максимум грифа справки,
/// собранной сейчас, и грифа изложения (ТБ-033) — изложение могло быть написано по фактам, гриф которых с тех пор
/// снизился. Выгрузка — запись <see cref="AuditAction.Export"/> (гриф — не ниже допуска сотрудника).
/// </summary>
/// <param name="Kind">Вид справки.</param>
/// <param name="Id">Дело или фигурант.</param>
/// <param name="From">Начало периода (справка по делу).</param>
/// <param name="To">Конец периода (справка по делу).</param>
/// <param name="Summary">Изложение после проверки человеком; <see langword="null"/> — справка без изложения.</param>
/// <param name="SummaryClassification">
/// Гриф изложения из <see cref="BriefSummary.Classification"/>; может только ПОДНЯТЬ гриф файла. Без изложения не учитывается.
/// </param>
public sealed record ExportBriefCommand(
    BriefKind Kind, int Id, DateOnly? From, DateOnly? To, string? Summary, short SummaryClassification = 0)
    : IRequest<ResponseDto<BriefFile>>, IAuditableRequest
{
    /// <summary>MIME-тип .docx.</summary>
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary =>
        BriefMessages.AuditRef(Kind, Id) + ":export;format=docx" + BriefMessages.Period(From, To)
        + (string.IsNullOrWhiteSpace(Summary) ? string.Empty : ";summary");

    /// <inheritdoc cref="ExportBriefCommand" />
    public sealed class Handler(
        BriefCollector collector,
        IBriefRenderer renderer,
        IUserAccountStore accounts,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<ExportBriefCommand, ResponseDto<BriefFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<BriefFile>> Handle(ExportBriefCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesBriefs, cancellationToken))
            {
                return ResponseDto<BriefFile>.BadRequest(BriefMessages.NoRight);
            }

            if (command.From > command.To)
            {
                return ResponseDto<BriefFile>.BadRequest(BriefMessages.BadPeriod);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } me)
            {
                return ResponseDto<BriefFile>.BadRequest(BriefMessages.NoSubject);
            }

            if (await BriefFacts.CollectAsync(collector, command.Kind, command.Id, command.From, command.To, access, cancellationToken) is not { } brief)
            {
                return ResponseDto<BriefFile>.NotFound(BriefMessages.NotFound);
            }

            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == me);
            var executor = account?.DisplayName is { Length: > 0 } display ? display : account?.UserName ?? "сотрудник " + me.ToString(CultureInfo.InvariantCulture);
            var summary = BriefTexts.Plain(command.Summary);

            // ТБ-033: гриф файла не ниже грифа изложения — значение со страницы только поднимает (в пределах шкалы).
            var marking = summary.Length == 0
                ? brief.Classification
                : Math.Max(brief.Classification, Math.Clamp(command.SummaryClassification, ClassificationLevels.Unclassified, ClassificationLevels.Max));
            var content = renderer.RenderDocx(new BriefExport(
                brief with { Classification = marking },
                summary.Length == 0 ? null : summary,
                ClassificationLevels.Marking(marking),
                executor,
                BriefMessages.DraftNotice(brief.CollectedAtUtc),
                BriefMessages.SummaryNotice));
            return ResponseDto<BriefFile>.Ok(new BriefFile(content, FileName(brief), DocxContentType));
        }

        // «Справка по делу № 12-2026_045 от 16.10.2026.docx»: символы, запрещённые в именах файлов, — «_».
        private static string FileName(Brief brief)
        {
            var name = brief.Title + " от " + DateTime.SpecifyKind(brief.CollectedAtUtc, DateTimeKind.Utc).ToLocalTime()
                .ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray()) + ".docx";
        }
    }
}

/// <summary>Сбор фактов справки нужного вида.</summary>
internal static class BriefFacts
{
    /// <summary>Факты справки; <see langword="null"/> — дело или фигурант недоступны.</summary>
    public static Task<Brief?> CollectAsync(
        BriefCollector collector, BriefKind kind, int id, DateOnly? from, DateOnly? to, AccessContext access, CancellationToken cancellationToken) =>
        kind == BriefKind.Person
            ? collector.PersonAsync(id, access, cancellationToken)
            : collector.CaseAsync(id, from, to, access, cancellationToken);
}
