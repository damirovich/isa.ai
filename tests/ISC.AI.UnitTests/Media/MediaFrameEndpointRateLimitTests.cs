using System;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Политика лимитера эндпоинта кадра (ADR-0028, защита от перегрузки ffmpeg): ключ партиции — субъект, если он уже
/// известен, иначе адрес клиента (в хосте лимитер стоит до аутентификации), иначе общая анонимная партиция; лимитер —
/// по параллельности: 2 одновременно, 4 в очереди (старые первыми), остальным — отказ сразу; отказ — 429 без тела;
/// политика регистрируется в общих <see cref="RateLimiterOptions"/> пакетом.
/// </summary>
public sealed class MediaFrameEndpointRateLimitTests
{
    [Fact(DisplayName = "Ключ партиции: субъект по NameIdentifier, иначе по имени; аноним — по адресу; без адреса — общая партиция")]
    public void Partition_key_prefers_subject_then_address()
    {
        var bySubject = new DefaultHttpContext();
        bySubject.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "10"), new Claim(ClaimTypes.Name, "ivanov")], "cookie"));
        bySubject.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        MediaFrameRateLimitPolicy.PartitionKeyFor(bySubject).ShouldBe("user:10");

        var byName = new DefaultHttpContext();
        byName.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ivanov")], "cookie"));
        MediaFrameRateLimitPolicy.PartitionKeyFor(byName).ShouldBe("user:ivanov");

        // Хост ставит UseRateLimiter до UseAuthentication: User пуст — ключ по адресу клиента.
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
        MediaFrameRateLimitPolicy.PartitionKeyFor(anonymous).ShouldBe("ip:10.0.0.5");

        // Неаутентифицированная identity (cookie не разобран) — не субъект, даже если claims есть.
        var unauthenticated = new DefaultHttpContext();
        unauthenticated.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "10")]));
        unauthenticated.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        MediaFrameRateLimitPolicy.PartitionKeyFor(unauthenticated).ShouldBe("ip:::1");

        MediaFrameRateLimitPolicy.PartitionKeyFor(new DefaultHttpContext()).ShouldBe("anonymous");
    }

    [Fact(DisplayName = "Лимитер параллельности: 2 разрешения, 4 в очереди (старые первыми), седьмой — отказ сразу; освобождение пускает первого из очереди")]
    public async Task Concurrency_limiter_permits_two_queues_four_rejects_rest()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        var partition = new MediaFrameRateLimitPolicy().GetPartition(context);
        partition.PartitionKey.ShouldBe("ip:10.0.0.7");

        using var limiter = partition.Factory(partition.PartitionKey);
        limiter.ShouldBeOfType<ConcurrencyLimiter>();

        using var first = await limiter.AcquireAsync();
        using var second = await limiter.AcquireAsync();
        first.IsAcquired.ShouldBeTrue();
        second.IsAcquired.ShouldBeTrue();

        var queued = Enumerable.Range(0, MediaFrameRateLimitPolicy.QueueLimit)
            .Select(_ => limiter.AcquireAsync().AsTask())
            .ToArray();
        queued.ShouldAllBe(task => !task.IsCompleted); // ждут в очереди

        using var seventh = await limiter.AcquireAsync();
        seventh.IsAcquired.ShouldBeFalse(); // очередь полна — отказ сразу, не ожидание

        first.Dispose();
        using var released = await queued[0].WaitAsync(TimeSpan.FromSeconds(5));
        released.IsAcquired.ShouldBeTrue(); // старые первыми
        queued.Skip(1).ShouldAllBe(task => !task.IsCompleted); // остальные ждут; их снимет Dispose лимитера
    }

    [Fact(DisplayName = "ТБ-021: отказ лимитера — 429 без тела, без сведений о носителе")]
    public async Task Rejection_is_429_without_body()
    {
        var context = new DefaultHttpContext();
        var partition = new MediaFrameRateLimitPolicy().GetPartition(context);
        using var limiter = partition.Factory(partition.PartitionKey);
        using var one = limiter.AttemptAcquire();
        using var two = limiter.AttemptAcquire();
        using var rejected = limiter.AttemptAcquire();
        rejected.IsAcquired.ShouldBeFalse();

        var policy = new MediaFrameRateLimitPolicy();
        policy.OnRejected.ShouldNotBeNull();
        await policy.OnRejected(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.ContentLength.ShouldBeNull();
        context.Response.Body.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Политика «media-frames» регистрируется пакетом в общих RateLimiterOptions и не ломает их сборку")]
    public void Policy_is_registered_in_rate_limiter_options()
    {
        var services = new ServiceCollection();
        services.AddMediaFrameRateLimiting();

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<RateLimiterOptions>));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        options.ShouldNotBeNull();

        // Повторная регистрация той же политики в тех же Options — ошибка ASP.NET Core (имя занято): пакет
        // регистрирует её ровно один раз, и имя совпадает с тем, что ставит эндпоинт.
        MediaFrameRateLimitPolicy.PolicyName.ShouldBe("media-frames");
        MediaFrameRateLimitPolicy.PermitLimit.ShouldBe(2);
        MediaFrameRateLimitPolicy.QueueLimit.ShouldBe(4);
    }
}
