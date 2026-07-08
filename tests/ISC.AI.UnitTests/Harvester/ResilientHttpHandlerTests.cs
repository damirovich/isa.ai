using System.Net;
using System.Text;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>Устойчивый HTTP-обработчик (Э4-17): повтор запроса при 429 (важно для массового сбора 170К).</summary>
public sealed class ResilientHttpHandlerTests
{
    [Fact(DisplayName = "429 → повтор → успех; запрос с телом переотправляется")]
    public async Task Retries_on_429_then_succeeds()
    {
        var stub = new FlakyHandler(failTimes: 2); // дважды 429, затем 200
        using var handler = new ResilientHttpHandler(TimeSpan.Zero, maxRetries: 3) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("http://api/x", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        stub.Calls.ShouldBe(3); // 2 неудачи + 1 успех (клон запроса отправлен трижды)
    }

    [Fact(DisplayName = "Исчерпание попыток: возвращается последний 429, без исключения")]
    public async Task Returns_last_429_after_max_retries()
    {
        var stub = new FlakyHandler(failTimes: 99);
        using var handler = new ResilientHttpHandler(TimeSpan.Zero, maxRetries: 2) { InnerHandler = stub };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("http://api/x");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        stub.Calls.ShouldBe(3); // первичная + 2 повтора
    }

    private sealed class FlakyHandler(int failTimes) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var code = Calls <= failTimes ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }
}
