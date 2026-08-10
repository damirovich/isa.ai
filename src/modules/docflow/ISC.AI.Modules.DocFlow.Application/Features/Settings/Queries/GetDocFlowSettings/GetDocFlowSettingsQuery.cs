using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Settings;

/// <summary>Текущие системные настройки модуля (§9).</summary>
/// <remarks>
/// Читать настройку вправе любой вошедший: горизонт уведомлений — не режимные данные, а параметр
/// поведения системы, и по нему ничего нельзя узнать о документах. Ограничение стоит на ЗАПИСИ.
/// </remarks>
public sealed record GetDocFlowSettingsQuery : IRequest<ResponseDto<DocFlowSettings>>
{
    /// <inheritdoc cref="GetDocFlowSettingsQuery" />
    public sealed class Handler(ISystemSettingsStore settings)
        : IRequestHandler<GetDocFlowSettingsQuery, ResponseDto<DocFlowSettings>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DocFlowSettings>> Handle(
            GetDocFlowSettingsQuery query, CancellationToken cancellationToken) =>
            ResponseDto<DocFlowSettings>.Ok(await settings.GetAsync(cancellationToken));
    }
}
