using System.Globalization;
using System.Text;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Уничтожить дело со всей информацией о нём (ADR-0025, ТБ-064, ТНД-008). Необратимо.
/// </summary>
/// <param name="CaseId">Дело.</param>
/// <param name="ConfirmationNumber">Номер дела, введённый оператором вручную, — подтверждение намерения.</param>
/// <param name="Reason">Основание уничтожения (решение, истечение срока хранения и т.п.) — идёт в журнал.</param>
/// <remarks>
/// ЧТО УНИЧТОЖАЕТСЯ: носители, привязанные ТОЛЬКО к этому делу, со всеми производными (кадры, лица,
/// шаблоны, вырезки, файлы); история поисков дела (сессии, кандидат-листы, решения верификации, вырезки
/// проб); фигуранты, эталоны, появления, основания поиска, акт закрытия, само дело.
///
/// ЧТО ОСТАЁТСЯ — и почему:
/// <list type="bullet">
/// <item>зарегистрированные документы документооборота: у них автономер в журнале регистрации, и дыра
/// в нумерации — предмет вопросов при проверке; снимается только их привязка к делу (решение заказчика);</item>
/// <item>носители, привязанные ещё и к другим делам (дедупликация по хешу): это материалы соседнего
/// дела, у них снимается лишь привязка к уничтожаемому;</item>
/// <item>кандидаты в поисках ДРУГИХ дел, указывающие на носители этого дела: это история чужого дела
/// (ТБ-072), её переписывать нельзя;</item>
/// <item>неизменяемый журнал аудита — по определению (ТБ-030).</item>
/// </list>
///
/// ПРАВО — ТОЛЬКО АДМИНИСТРАТОР (строка матрицы доступа закреплена замком, ADR-0032), и без режима
/// первичной настройки (решение заказчика): следователь не может уничтожить своё же дело — это защита
/// от сокрытия следов собственной работы, а «пока Администратора нет — можно всем» здесь означало бы
/// уничтожение без единой роли.
///
/// ПОДТВЕРЖДЕНИЕ И АКТ. Оператор вводит номер дела вручную — нажатие кнопки по ошибке ничего не
/// уничтожит. Полный состав уничтожаемого с основанием пишется в неизменяемый журнал ПЕРВЫМ: это и есть
/// акт (решение заказчика — без отдельного .docx), и он переживает само дело.
///
/// ПОРЯДОК — FAIL-CLOSED: запись-акт → носители (каждый — со своей записью, ТБ-064) → история поисков
/// (со своей записью) → дело в схеме профиля. Недоступен журнал — не удалено ничего. Сбой посередине
/// оставляет дело с частью материалов; повтор безопасен — каждый шаг идемпотентен, а журнал покажет
/// обе попытки.
///
/// НЕ <see cref="IAuditableRequest"/>: состав записи сквозное поведение дать не может, и она обязана
/// появиться ДО удаления, а не после ответа обработчика.
/// </remarks>
public sealed record PurgeCaseCommand(int CaseId, string ConfirmationNumber, string Reason)
    : IRequest<ResponseDto<CasePurgeSummary>>
{
    /// <inheritdoc cref="PurgeCaseCommand" />
    public sealed class Handler(
        ICaseStore cases,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider,
        IMediaPurger purger,
        IAuditWriter auditWriter)
        : IRequestHandler<PurgeCaseCommand, ResponseDto<CasePurgeSummary>>
    {
        /// <summary>Отказ по праву «Уничтожение дела»: строка матрицы закреплена за Администратором (ADR-0032).</summary>
        internal const string Denied = "Уничтожение дела доступно только Администратору.";

        /// <summary>Отказ, когда введённый номер не совпал с номером дела.</summary>
        internal const string NumberMismatch = "Введённый номер не совпадает с номером дела — уничтожение не выполнено.";

        /// <inheritdoc />
        public async ValueTask<ResponseDto<CasePurgeSummary>> Handle(PurgeCaseCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ТБ-012: право — ДО любого чтения. У права «Уничтожение дела» режима первичной настройки нет, и это
            // здесь обязательно: уничтожение без роли недопустимо ни при каком состоянии контура.
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesPurge, cancellationToken))
            {
                return ResponseDto<CasePurgeSummary>.BadRequest(Denied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var composition = await cases.GetCompositionAsync(command.CaseId, access, cancellationToken);
            if (composition is null)
            {
                // Неразличимость (ТБ-020/021): «нет дела» и «дело вне допуска» — один ответ.
                return ResponseDto<CasePurgeSummary>.NotFound("Дело не найдено или недоступно.");
            }

            if (!string.Equals(command.ConfirmationNumber.Trim(), composition.Number, StringComparison.Ordinal))
            {
                return ResponseDto<CasePurgeSummary>.BadRequest(NumberMismatch);
            }

            var subjectId = access.NumericSubjectId;
            var reason = $"уничтожение дела №{composition.Number}: {command.Reason.Trim()}";

            // АКТ — ПЕРВЫМ (fail-closed, ТБ-064): недоступен журнал — WriteAsync бросит, и ничего не удалится.
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Purge,
                    composition.Classification,
                    SubjectId: subjectId,
                    ObjectRef: "investigation:case:" + command.CaseId.ToString(CultureInfo.InvariantCulture) + ":destroy",
                    DivisionId: composition.DivisionId,
                    PayloadSensitive: BuildAct(composition, command.Reason.Trim())),
                cancellationToken);

            var faces = 0;
            var files = 0;
            foreach (var assetId in composition.ExclusiveAssetIds)
            {
                var asset = await purger.PurgeAsync(assetId, subjectId, cancellationToken);
                faces += asset.FacesRemoved;
                files += asset.FilesRemoved;
            }

            var searches = await purger.PurgeCaseSearchesAsync(command.CaseId, reason, subjectId, cancellationToken);

            var result = await cases.PurgeAsync(command.CaseId, access, cancellationToken);
            if (result != CaseWriteResult.Ok)
            {
                // Дело исчезло из доступа между чтением состава и удалением (например, сменились допуски):
                // носители уже сняты, запись дела — нет. Сказать прямо, чтобы оператор повторил.
                return ResponseDto<CasePurgeSummary>.BadRequest(
                    "Материалы дела уничтожены, но запись дела удалить не удалось: повторите уничтожение.");
            }

            return ResponseDto<CasePurgeSummary>.Ok(new CasePurgeSummary(
                composition.Number,
                AssetsRemoved: composition.ExclusiveAssetIds.Count,
                AssetsKeptShared: composition.SharedAssetIds.Count,
                FacesRemoved: faces,
                FilesRemoved: files + searches.ProbeFilesRemoved,
                SessionsRemoved: searches.SessionsRemoved,
                CandidatesRemoved: searches.CandidatesRemoved,
                PersonsRemoved: composition.Persons,
                AppearancesRemoved: composition.Appearances,
                DocumentsKept: composition.DocumentLinks));
        }

        /// <summary>
        /// Текст акта: что уничтожается, что остаётся, по какому основанию. Идентификаторы носителей — да,
        /// имена фигурантов — нет: для проверки достаточно состава, а лишних персональных данных в журнале
        /// быть не должно.
        /// </summary>
        private static string BuildAct(CaseComposition c, string reason)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("АКТ уничтожения дела №").Append(c.Number).Append(" (id ").Append(c.CaseId.ToString(inv)).AppendLine(").");
            sb.Append("Основание: ").AppendLine(reason);
            sb.Append("Уничтожаются: носители ").Append(c.ExclusiveAssetIds.Count.ToString(inv))
              .Append(" [").Append(string.Join(",", c.ExclusiveAssetIds.Select(id => id.ToString(inv)))).Append(']')
              .Append(" со всеми производными; история поисков дела; фигуранты ").Append(c.Persons.ToString(inv))
              .Append(", эталоны ").Append(c.ReferencePhotos.ToString(inv))
              .Append(", появления ").Append(c.Appearances.ToString(inv))
              .Append(", основания поиска ").Append(c.Authorizations.ToString(inv))
              .AppendLine("; запись дела.");
            sb.Append("Сохраняются: документы документооборота ").Append(c.DocumentLinks.ToString(inv))
              .Append(" (снимается только привязка к делу); носители других дел ").Append(c.SharedAssetIds.Count.ToString(inv))
              .Append(" [").Append(string.Join(",", c.SharedAssetIds.Select(id => id.ToString(inv)))).Append(']')
              .AppendLine(" (снимается только привязка); кандидаты в поисках других дел (ТБ-072).");
            return sb.ToString();
        }
    }
}

/// <summary>Итог уничтожения дела для экрана (ADR-0025).</summary>
/// <param name="Number">Номер уничтоженного дела.</param>
/// <param name="AssetsRemoved">Носителей уничтожено.</param>
/// <param name="AssetsKeptShared">Носителей оставлено — они принадлежат и другим делам.</param>
/// <param name="FacesRemoved">Лиц (с шаблонами) удалено.</param>
/// <param name="FilesRemoved">Файлов удалено из хранилища (исходники, вырезки, пробы).</param>
/// <param name="SessionsRemoved">Поисковых сессий удалено.</param>
/// <param name="CandidatesRemoved">Строк кандидат-листов удалено.</param>
/// <param name="PersonsRemoved">Фигурантов удалено.</param>
/// <param name="AppearancesRemoved">Появлений удалено.</param>
/// <param name="DocumentsKept">Документов документооборота сохранено (снята только привязка).</param>
public sealed record CasePurgeSummary(
    string Number,
    int AssetsRemoved,
    int AssetsKeptShared,
    int FacesRemoved,
    int FilesRemoved,
    int SessionsRemoved,
    int CandidatesRemoved,
    int PersonsRemoved,
    int AppearancesRemoved,
    int DocumentsKept);

/// <summary>Правила команды уничтожения: номер и основание обязательны — без них акт неполон.</summary>
public sealed class PurgeCaseValidator : AbstractValidator<PurgeCaseCommand>
{
    /// <summary>Минимальная длина основания: «по решению» без реквизитов актом не является.</summary>
    public const int MinReasonLength = 10;

    /// <summary>Номер дела и основание обязательны; основание — осмысленной длины.</summary>
    public PurgeCaseValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.ConfirmationNumber)
            .NotEmpty().WithMessage("Введите номер дела для подтверждения.");
        RuleFor(c => c.Reason)
            .NotEmpty().WithMessage("Укажите основание уничтожения.")
            .Must(r => r is not null && r.Trim().Length >= MinReasonLength)
                .WithMessage($"Основание уничтожения — не короче {MinReasonLength} символов (реквизиты решения).")
            .MaximumLength(1000);
    }
}
