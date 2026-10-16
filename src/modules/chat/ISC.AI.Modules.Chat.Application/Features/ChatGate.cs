using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>Кто спрашивает и где: право, контекст доступа, сотрудник, область — или текст отказа.</summary>
/// <param name="Access">Контекст доступа.</param>
/// <param name="SubjectId">Числовой идентификатор сотрудника; <see langword="null"/> — диалоги не к кому привязать.</param>
/// <param name="Area">Область.</param>
/// <param name="Error">Отказ; <see langword="null"/> — можно.</param>
internal sealed record ChatEntry(AccessContext? Access, int? SubjectId, ChatArea? Area, string? Error);

/// <summary>
/// Общая преамбула сценариев чата (ADR-0052): право профиля → контекст доступа → область под решёткой профиля.
/// Одно место — чтобы правило входа не расходилось между вопросом, поиском и списком диалогов.
/// </summary>
/// <remarks>
/// ПОРЯДОК (ТБ-012/021): право — ДО любого чтения; недоступная и несуществующая область — один ответ
/// <see cref="ChatMessages.NotFound"/>.
/// </remarks>
internal static class ChatGate
{
    /// <summary>Войти в область чата.</summary>
    public static async Task<ChatEntry> EnterAsync(
        IChatProfile profile, IAccessContextProvider accessProvider, string? areaKey, CancellationToken cancellationToken)
    {
        if (!await profile.CanUseAsync(cancellationToken))
        {
            return new ChatEntry(null, null, null, ChatMessages.NoRight);
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        var area = await profile.ResolveAreaAsync(NormalizeKey(areaKey), access, cancellationToken);
        return area is null
            ? new ChatEntry(access, access.NumericSubjectId, null, ChatMessages.NotFound)
            : new ChatEntry(access, access.NumericSubjectId, area, null);
    }

    /// <summary>Ключ области без пробелов по краям; пустой — область по умолчанию.</summary>
    public static string? NormalizeKey(string? areaKey) => string.IsNullOrWhiteSpace(areaKey) ? null : areaKey.Trim();

    /// <summary>Одна ли это область (ключи сравниваются после нормализации, с учётом регистра).</summary>
    public static bool SameArea(string? left, string? right) =>
        string.Equals(NormalizeKey(left), NormalizeKey(right), StringComparison.Ordinal);

    /// <summary>Заголовок диалога: пробелы схлопнуты, пустой — «Новый диалог», длинный — обрезан.</summary>
    public static string Title(string? text)
    {
        var normalized = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return "Новый диалог";
        }

        return normalized.Length <= ChatLimits.MaxTitleLength ? normalized : normalized[..(ChatLimits.MaxTitleLength - 1)] + "…";
    }

    /// <summary>Вопрос без пробелов по краям, не длиннее <see cref="ChatLimits.MaxQuestionLength"/>.</summary>
    public static string Question(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= ChatLimits.MaxQuestionLength ? value : value[..ChatLimits.MaxQuestionLength];
    }

    /// <summary>Число из настроек профиля в разумных пределах (ошибка профиля не должна обрушить узел ИИ).</summary>
    public static int Clamp(int value, int max) => Math.Clamp(value, 1, max);
}
