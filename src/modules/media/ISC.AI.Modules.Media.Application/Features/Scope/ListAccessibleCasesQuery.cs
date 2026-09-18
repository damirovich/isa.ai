using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Scope;

/// <summary>
/// Дела, доступные субъекту (ТБ-071, ТФ-ПЛ-05) — для выбора области поиска и подписи медиатеки дела.
/// Пакет понятия «дело» не имеет: список отдаёт порт профиля <see cref="ICaseScope"/>, уже
/// отфильтрованный по роли и решётке ядра (ТБ-020/021).
/// </summary>
public sealed record ListAccessibleCasesQuery : IRequest<ResponseDto<IReadOnlyList<CaseScopeItem>>>
{
    /// <inheritdoc cref="ListAccessibleCasesQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope)
        : IRequestHandler<ListAccessibleCasesQuery, ResponseDto<IReadOnlyList<CaseScopeItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseScopeItem>>> Handle(
            ListAccessibleCasesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed: без контекста доступа провайдер бросает — конвейер превратит это в Status=false.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var cases = await caseScope.ListAccessibleCasesAsync(access, cancellationToken);
            return ResponseDto<IReadOnlyList<CaseScopeItem>>.Ok(cases, cases.Count);
        }
    }
}
