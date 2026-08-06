using ISC.AI.AI.Audit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Audit;

/// <summary>
/// Сквозной аудит (Э4-11, ТБ-030, инвариант №4): каждый аудируемый сценарий пишет запись журнала; гриф =
/// max(допуск субъекта, объявленный гриф объекта); обращение фиксируется и при ошибке хендлера; сбой журнала
/// FAIL-CLOSED (результат не выдаётся).
/// </summary>
[Trait("Category", "Gate")]
public sealed class AuditBehaviorTests
{
    private sealed record FakeAuditable(short? Declared = null) : IMessage, IAuditableRequest
    {
        public AuditAction AuditAction => AuditAction.Search;
        public string? AuditSummary => "тестовое обращение";
        public short? AuditClassification => Declared;
    }

    private static (AuditBehavior<FakeAuditable, ResponseDto<string>> Behavior, IAuditWriter Writer) Build(short maxClassification = 2)
    {
        var writer = Substitute.For<IAuditWriter>();
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("1", maxClassification, [1]));
        return (Build(writer, accessProvider), writer);
    }

    private static AuditBehavior<FakeAuditable, ResponseDto<string>> Build(
        IAuditWriter writer, IAccessContextProvider accessProvider, ISubjectProvider? subjectProvider = null) =>
        new(writer,
            accessProvider,
            subjectProvider ?? Substitute.For<ISubjectProvider>(),
            NullLogger<AuditBehavior<FakeAuditable, ResponseDto<string>>>.Instance);

    private static MessageHandlerDelegate<FakeAuditable, ResponseDto<string>> Ok() =>
        (_, _) => ValueTask.FromResult(ResponseDto<string>.Ok("готово"));

    [Fact(DisplayName = "Аудит: успешный сценарий пишет запись; гриф = допуск субъекта (объявленного нет)")]
    public async Task Writes_audit_on_success()
    {
        var (behavior, writer) = Build(maxClassification: 2);

        var result = await behavior.Handle(new FakeAuditable(), Ok(), CancellationToken.None);

        result.Status.ShouldBeTrue();
        await writer.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Search && e.Classification == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит: гриф записи = max(допуск, объявленный гриф объекта) — не недо-классифицируется")]
    public async Task Classification_is_max_of_ceiling_and_declared()
    {
        var (behavior, writer) = Build(maxClassification: 1);

        // Объявленный гриф объекта (3) ВЫШЕ допуска субъекта (1) — запись не должна недо-классифицироваться.
        await behavior.Handle(new FakeAuditable(Declared: 3), Ok(), CancellationToken.None);

        await writer.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Classification == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит: ошибка хендлера — обращение зафиксировано, исключение проброшено")]
    public async Task Writes_audit_on_handler_failure()
    {
        var (behavior, writer) = Build();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await behavior.Handle(
                new FakeAuditable(),
                (_, _) => throw new InvalidOperationException("сбой хендлера"),
                CancellationToken.None));

        await writer.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит FAIL-CLOSED: сбой журнала пробрасывается (результат не выдаётся)")]
    public async Task Audit_write_failure_is_fail_closed()
    {
        var (behavior, writer) = Build();
        writer.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("журнал недоступен")));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await behavior.Handle(new FakeAuditable(), Ok(), CancellationToken.None));
    }

    [Fact(DisplayName = "Аудит без допуска: «кто» берётся из сессии, гриф — максимальный")]
    public async Task Subject_is_taken_from_session_when_clearance_is_missing()
    {
        // Ровно случай ПЕРВОЙ выдачи допуска на чистом контуре: у распорядителя допуска ещё нет,
        // GetCurrentAsync бросает. Раньше такая запись уходила в журнал без субъекта — самое
        // чувствительное действие системы оставалось без ответа на вопрос «кто» (ТБ-030).
        var writer = Substitute.For<IAuditWriter>();
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<Task<AccessContext>>(_ => throw new AccessContextRequiredException());

        var subjectProvider = Substitute.For<ISubjectProvider>();
        subjectProvider.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)7);

        await Build(writer, accessProvider, subjectProvider)
            .Handle(new FakeAuditable(), Ok(), CancellationToken.None);

        await writer.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.SubjectId == 7 && e.Classification == short.MaxValue),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит без допуска: сбой определения субъекта не срывает запись журнала")]
    public async Task Subject_resolution_failure_does_not_block_the_audit_record()
    {
        var writer = Substitute.For<IAuditWriter>();
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns<Task<AccessContext>>(_ => throw new AccessContextRequiredException());

        var subjectProvider = Substitute.For<ISubjectProvider>();
        subjectProvider.GetCurrentUserIdAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int?>>(_ => throw new InvalidOperationException("сессия недоступна"));

        // Запись без «кто» хуже записи с «кто», но НАМНОГО лучше отсутствия записи: при fail-closed
        // отказ журнала отменил бы и саму операцию.
        await Build(writer, accessProvider, subjectProvider)
            .Handle(new FakeAuditable(), Ok(), CancellationToken.None);

        await writer.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.SubjectId == null), Arg.Any<CancellationToken>());
    }
}
