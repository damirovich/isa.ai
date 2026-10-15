using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Выгрузка списка дел: строки (не больше <see cref="ExportCasesQuery.MaxRows"/>), всего по отбору и кто сформировал.</summary>
/// <param name="Rows">Дела в порядке реестра.</param>
/// <param name="TotalCount">Сколько дел подходит под отбор; больше <see cref="Rows"/> — выгрузка урезана пределом.</param>
/// <param name="Author">Кто сформировал — имя вошедшего сотрудника для подписи файла.</param>
public sealed record CasesExport(IReadOnlyList<CaseRow> Rows, int TotalCount, string Author)
{
    /// <summary>Выгрузка урезана пределом строк.</summary>
    public bool Truncated => TotalCount > Rows.Count;
}

/// <summary>
/// Выгрузка реестра дел в файл для Excel (ТФ-ДЕЛ-01): те же отборы и порядок, что у списка
/// (<see cref="ListCasesQuery"/>), все страницы сразу — не больше <see cref="MaxRows"/> строк.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-030): строки — из того же хранилища под той же решёткой, что и список: выгрузка не видит
/// ничего, чего не видно в реестре. Но файл уносит сведения за пределы системы, поэтому сама выгрузка — запись журнала
/// <see cref="AuditAction.Export"/> (журнал классифицируется не ниже допуска субъекта — а он не ниже грифа любого
/// видимого ему дела). Без определённого сотрудника выгрузки нет: файл подписывается «кто сформировал».
/// </remarks>
public sealed record ExportCasesQuery(
    string? Text = null,
    CaseKind? Kind = null,
    CaseStatus? Status = null,
    int? DivisionId = null,
    int? InvestigatorUserId = null,
    CaseDirection? Direction = null,
    CaseSortField Sort = CaseSortField.Status,
    bool SortDescending = false)
    : IRequest<ResponseDto<CasesExport>>, IAuditableRequest
{
    /// <summary>Предел строк одной выгрузки.</summary>
    public const int MaxRows = 5000;

    /// <summary>Отказ: не удалось определить вошедшего сотрудника.</summary>
    public const string NoSubject = "Не удалось определить сотрудника: выгрузка без подписи «кто сформировал» не выполняется.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    /// <remarks>Критерии отбора без текста поиска и содержимого дел (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:cases:export:text={(string.IsNullOrWhiteSpace(Text) ? "-" : "*")};kind={Kind?.ToString() ?? "-"};"
        + $"status={Status?.ToString() ?? "-"};division={DivisionId?.ToString(CultureInfo.InvariantCulture) ?? "-"};"
        + $"investigator={InvestigatorUserId?.ToString(CultureInfo.InvariantCulture) ?? "-"};"
        + $"direction={Direction?.ToString() ?? "-"};format=csv";

    /// <inheritdoc cref="ExportCasesQuery" />
    public sealed class Handler(
        ICaseStore cases, IAccessContextProvider accessProvider, ISubjectProvider subjectProvider, IUserAccountStore accounts)
        : IRequestHandler<ExportCasesQuery, ResponseDto<CasesExport>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CasesExport>> Handle(ExportCasesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска провайдер бросает.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<CasesExport>.BadRequest(NoSubject);
            }

            var page = await cases.ListAsync(
                new CaseFilter(
                    query.Text, query.Kind, query.Status, query.DivisionId, query.InvestigatorUserId,
                    Page: 1, PageSize: MaxRows, query.Direction, query.Sort, query.SortDescending),
                access,
                cancellationToken);

            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == userId);
            var author = account is null
                ? string.Create(CultureInfo.InvariantCulture, $"пользователь № {userId}")
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;
            return ResponseDto<CasesExport>.Ok(new CasesExport(page.Rows, page.TotalCount, author), page.Rows.Count);
        }
    }
}
