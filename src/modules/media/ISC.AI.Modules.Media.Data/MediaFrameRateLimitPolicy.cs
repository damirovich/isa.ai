using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Политика лимитера для эндпоинта кадра (<c>GET /media/frames/{id}?t=</c>, ADR-0028): ограничение ЧИСЛА
/// ОДНОВРЕМЕННЫХ запросов на субъекта — каждый запрос запускает процесс ffmpeg (декодирование от ключевого кадра,
/// до секунд CPU и сотен МБ на 4K), и без предела один аутентифицированный субъект сотней параллельных GET
/// исчерпал бы CPU и память узла, уронив цепи Blazor других пользователей и фоновые конвейеры (тот же ffmpeg).
/// Предел — параллельность, а не частота: покадровое листание легитимно шлёт запросы часто, но по одному.
/// </summary>
/// <remarks>
/// <para>
/// КЛЮЧ ПАРТИЦИИ. В хосте <c>UseRateLimiter()</c> стоит ДО <c>UseAuthentication()</c> (Program.cs: троттлинг входа
/// должен считать и анонимов), поэтому на этапе лимитера cookie ещё не разобран и <c>HttpContext.User</c> пуст —
/// идентификатор субъекта недоступен. Запасной ключ — адрес клиента (<c>UseForwardedHeaders</c> стоит перед лимитером,
/// так что за прокси это адрес клиента, не прокси). Если порядок middleware изменят и субъект будет известен —
/// ключ автоматически станет «по субъекту» (<c>NameIdentifier</c>, иначе имя). Без адреса — общая анонимная партиция.
/// </para>
/// <para>
/// ОТКАЗ — 429 без тела: лимитер срабатывает раньше проверок доступа эндпоинта, и ответ не должен раскрывать,
/// существует ли носитель и доступен ли он субъекту (ТБ-021); по коду 429 клиент лишь узнаёт, что сам шлёт
/// слишком много. В режиме разработки (<c>devAuth</c>) хост лимитер в конвейер не ставит — метаданные политики тогда
/// игнорируются, это ожидаемо.
/// </para>
/// </remarks>
public sealed class MediaFrameRateLimitPolicy : IRateLimiterPolicy<string>
{
    /// <summary>Имя политики для <c>RequireRateLimiting</c> и регистрации в <c>RateLimiterOptions</c>.</summary>
    public const string PolicyName = "media-frames";

    /// <summary>Сколько кадров одного субъекта вырезаются одновременно: два — шаг «вперёд» и упреждающий запрос интерфейса.</summary>
    public const int PermitLimit = 2;

    /// <summary>Сколько запросов сверх предела ждут в очереди (старые — первыми); остальные получают 429 сразу.</summary>
    public const int QueueLimit = 4;

    /// <summary>Префикс ключа партиции по субъекту.</summary>
    public const string SubjectKeyPrefix = "user:";

    /// <summary>Префикс ключа партиции по адресу клиента (запасной ключ, см. remarks).</summary>
    public const string AddressKeyPrefix = "ip:";

    /// <summary>Ключ партиции, когда ни субъект, ни адрес не известны.</summary>
    public const string AnonymousKey = "anonymous";

    /// <inheritdoc />
    /// <remarks>429 без тела и без деталей (ТБ-021); заголовок <c>Retry-After</c> не ставится — ожидание секундное.</remarks>
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };

    /// <inheritdoc />
    public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
        RateLimitPartition.GetConcurrencyLimiter(
            PartitionKeyFor(httpContext),
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = PermitLimit,
                QueueLimit = QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });

    /// <summary>
    /// Ключ партиции: субъект (<see cref="ClaimTypes.NameIdentifier"/>, иначе имя), если аутентифицирован к моменту
    /// лимитера; иначе адрес клиента; иначе <see cref="AnonymousKey"/>.
    /// </summary>
    public static string PartitionKeyFor(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var subjectId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(subjectId))
            {
                return SubjectKeyPrefix + subjectId;
            }

            if (!string.IsNullOrEmpty(user.Identity.Name))
            {
                return SubjectKeyPrefix + user.Identity.Name;
            }
        }

        var address = httpContext.Connection.RemoteIpAddress;
        return address is null ? AnonymousKey : AddressKeyPrefix + address;
    }
}

/// <summary>Регистрация политики лимитера кадров в общих <see cref="RateLimiterOptions"/> хоста.</summary>
public static class MediaFrameRateLimiting
{
    /// <summary>
    /// Добавляет политику <see cref="MediaFrameRateLimitPolicy.PolicyName"/> в <see cref="RateLimiterOptions"/>
    /// (через <c>Configure</c> — дополняет политики хоста, например троттлинг входа, не заменяя их). Сам middleware
    /// (<c>UseRateLimiter</c>) ставит хост.
    /// </summary>
    public static IServiceCollection AddMediaFrameRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(MediaFrameRateLimitPolicy.PolicyName, new MediaFrameRateLimitPolicy()));
        return services;
    }
}
