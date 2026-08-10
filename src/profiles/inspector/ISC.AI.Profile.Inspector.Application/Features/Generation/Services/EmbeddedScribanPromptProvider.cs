using System.Reflection;
using ISC.AI.Abstractions.AI;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>
/// Провайдер задачных промптов (ТО-прог-04, ТО-лнг-03): загружает файлы-шаблоны <c>.scriban</c>,
/// встроенные ресурсами сборки, ПО КЛЮЧУ (= имя файла без расширения, напр. «reference»). Промпты —
/// файлы, не хардкод; добавление нового типа документа = новый <c>.scriban</c> + ключ, без нового
/// bespoke-интерфейса рендерера.
/// </summary>
/// <remarks>
/// Системная часть промпта (правило грунтовки, ТБ-041) через этот контракт НЕ отдаётся — она в ядре.
/// Отсутствие шаблона по ключу — ошибка (исключение), а не молчаливая подмена пустым/произвольным текстом.
/// </remarks>
public sealed class EmbeddedScribanPromptProvider : IPromptProvider
{
    private const string Extension = ".scriban";
    private readonly IReadOnlyDictionary<string, string> _templatesByKey;

    /// <summary>Сканирует встроенные <c>.scriban</c>-ресурсы сборки в карту «ключ → текст-шаблон».</summary>
    public EmbeddedScribanPromptProvider(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.EndsWith(Extension, StringComparison.Ordinal)))
        {
            map[KeyOf(resourceName)] = ReadResource(assembly, resourceName);
        }

        _templatesByKey = map;
    }

    /// <inheritdoc />
    public PromptTemplate GetTaskPrompt(string promptKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptKey);

        return _templatesByKey.TryGetValue(promptKey, out var text)
            ? new PromptTemplate(promptKey, text)
            : throw new KeyNotFoundException(
                $"Шаблон задачного промпта «{promptKey}» не найден среди встроенных .scriban-ресурсов.");
    }

    // Ключ = имя файла без расширения: «...Generation.Prompts.reference.scriban» → «reference».
    private static string KeyOf(string resourceName)
    {
        var withoutExtension = resourceName[..^Extension.Length];
        var lastDot = withoutExtension.LastIndexOf('.');
        return lastDot >= 0 ? withoutExtension[(lastDot + 1)..] : withoutExtension;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
