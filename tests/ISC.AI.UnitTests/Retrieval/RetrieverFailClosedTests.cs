using ISC.AI.AI.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Retrieval;

/// <summary>
/// Тест fail-closed-инварианта ретривера (ТБ-012/021): без контекста доступа извлечение не выполняется.
/// </summary>
public sealed class RetrieverFailClosedTests
{
    [Fact(DisplayName = "Retriever fail-closed: без контекста доступа — отказ (ТБ-021)")]
    public async Task Retrieve_without_access_context_throws()
    {
        var retriever = new PgVectorRetriever(
            Substitute.For<IDbContextFactory<CoreDbContext>>(),
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
            Substitute.For<IAccessPolicy>());

        // Отказ происходит до обращения к модели/БД — мокам поведение не нужно.
        await Should.ThrowAsync<AccessContextRequiredException>(
            () => retriever.RetrieveAsync("запрос", access: null!, topK: 5));
    }
}
