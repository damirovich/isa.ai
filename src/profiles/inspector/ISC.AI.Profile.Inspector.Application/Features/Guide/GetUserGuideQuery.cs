using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Guide;

/// <summary>
/// Справка пользователя для страницы «Помощь». Текст статичный и без грифа (описание интерфейса, не материалы),
/// поэтому без журнала аудита: чтение справки — не обращение к данным.
/// </summary>
public sealed record GetUserGuideQuery : IRequest<ResponseDto<UserGuide>>
{
    /// <inheritdoc cref="GetUserGuideQuery" />
    public sealed class Handler : IRequestHandler<GetUserGuideQuery, ResponseDto<UserGuide>>
    {
        /// <inheritdoc />
        public ValueTask<ResponseDto<UserGuide>> Handle(GetUserGuideQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ResponseDto<UserGuide>.Ok(UserGuide.Current));
    }
}
