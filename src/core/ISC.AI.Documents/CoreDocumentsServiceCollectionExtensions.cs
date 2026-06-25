using ISC.AI.Abstractions.Documents;
using ISC.AI.Documents.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Documents;

/// <summary>Регистрация движка документов ядра: извлечение текста из файлов (нейтрально к типу документа).</summary>
public static class CoreDocumentsServiceCollectionExtensions
{
    /// <summary>Регистрирует извлекатели текста (.txt, .docx) и фасад <see cref="ITextExtractor"/>.</summary>
    public static IServiceCollection AddCoreDocuments(this IServiceCollection services)
    {
        services.AddSingleton<IFormatTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IFormatTextExtractor, DocxTextExtractor>();
        services.TryAddSingleton<ITextExtractor, CompositeTextExtractor>();
        return services;
    }
}
