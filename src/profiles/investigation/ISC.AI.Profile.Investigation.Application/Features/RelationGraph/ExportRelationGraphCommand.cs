using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>Формат выгрузки картинки графа связей (ADR-0045 п. 14).</summary>
public enum RelationGraphExportFormat
{
    /// <summary>Векторная картинка — для печати.</summary>
    Svg = 1,

    /// <summary>Растровая картинка — для вставки в справку.</summary>
    Png = 2,
}

/// <summary>
/// Запись выгрузки картинки графа связей в журнал (ТБ-030, ADR-0045 п. 14). Картинку собирает страница из того, что
/// уже на холсте, — новых сведений сервер не отдаёт; но выгрузка выносит их за пределы системы, поэтому сама — запись
/// <see cref="AuditAction.Export"/>, и страница выгружает файл только после успешной записи.
/// </summary>
/// <param name="CenterKind">Центр графа (или начало цепочки): фигурант, дело или событие (ADR-0056).</param>
/// <param name="CenterId">Фигурант или дело.</param>
/// <param name="Format">Формат.</param>
/// <param name="NodeCount">Сколько узлов на картинке.</param>
/// <param name="Classification">Гриф картинки — наибольший из грифов её узлов (ADR-0030).</param>
/// <remarks>Ответ — подпись «кто сформировал» для картинки: имя вошедшего сотрудника из реестра учётных записей.</remarks>
public sealed record ExportRelationGraphCommand(
    RelationNodeKind CenterKind, int CenterId, RelationGraphExportFormat Format, int NodeCount, short Classification)
    : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    /// <remarks>Виды, идентификатор, формат и число узлов — без ФИО и реквизитов (ТБ-032).</remarks>
    public string? AuditSummary => string.Create(
        CultureInfo.InvariantCulture,
        $"investigation:graph:{RelationNodeKeys.AuditName(CenterKind)}:{CenterId}:export;format={Format.ToString().ToLowerInvariant()};nodes={NodeCount}");

    /// <inheritdoc />
    /// <remarks>Запись журнала классифицируется не ниже грифа выгруженной картинки (ТБ-030, ADR-0030).</remarks>
    public short? AuditClassification => Classification;

    /// <summary>Отказ: не удалось определить вошедшего сотрудника — картинку без подписи не выдаём.</summary>
    public const string NoSubject = "Не удалось определить сотрудника: выгрузка без подписи «кто сформировал» не выполняется.";

    /// <inheritdoc cref="ExportRelationGraphCommand" />
    public sealed class Handler(ISubjectProvider subjectProvider, IUserAccountStore accounts)
        : IRequestHandler<ExportRelationGraphCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(ExportRelationGraphCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Запись в журнал делает сквозное поведение аудита конвейера (IAuditableRequest); здесь — подпись картинки.
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<string>.BadRequest(NoSubject);
            }

            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == userId);
            var name = account is null
                ? string.Create(CultureInfo.InvariantCulture, $"пользователь № {userId}")
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;
            return ResponseDto<string>.Ok(name);
        }
    }
}

/// <summary>Правила выгрузки: центр — фигурант или дело, известный формат, есть узлы, гриф — по шкале ADR-0030.</summary>
public sealed class ExportRelationGraphValidator : AbstractValidator<ExportRelationGraphCommand>
{
    /// <inheritdoc cref="ExportRelationGraphValidator" />
    public ExportRelationGraphValidator()
    {
        RuleFor(c => c.CenterKind).Must(kind => kind is RelationNodeKind.Person or RelationNodeKind.Case or RelationNodeKind.Event);
        RuleFor(c => c.CenterId).GreaterThan(0);
        RuleFor(c => c.Format).IsInEnum();
        RuleFor(c => c.NodeCount).GreaterThan(0);
        RuleFor(c => c.Classification).Must(ClassificationLevels.IsValid);
    }
}
