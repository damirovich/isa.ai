using System.Globalization;
using ISC.AI.Abstractions.Audit;

namespace ISC.AI.Modules.Admin.Application.Features.Audit;

/// <summary>
/// Человекочитаемое описание записи журнала (ТБ-030): сводку сценария вида «investigation:case:4:purge» экран
/// показывает как «Следствие · дело № 4 · уничтожение», а исходный код — рядом, мелко. Описание — только подсказка
/// для чтения: запись в журнале не меняется (ТБ-031), неизвестные слова остаются как есть.
/// </summary>
public static class AuditText
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // Области.
        ["core"] = "система", ["investigation"] = "Следствие", ["inspector"] = "Инспектор", ["admin"] = "администрирование",
        ["media"] = "медиа", ["docflow"] = "документооборот", ["auth"] = "вход в систему",

        // Объекты.
        ["audit"] = "журнал аудита", ["access-matrix"] = "матрица доступа", ["account"] = "учётная запись",
        ["accounts"] = "учётные записи", ["clearance"] = "допуск", ["clearances"] = "допуски", ["role"] = "роль",
        ["user-role"] = "роль", ["roles"] = "роли", ["user"] = "пользователь", ["user-history"] = "история сотрудника",
        ["case"] = "дело", ["cases"] = "дела", ["person"] = "фигурант", ["persons"] = "фигуранты",
        ["report"] = "сводка", ["reports"] = "сводки", ["report-permit"] = "запрос на правку сводки",
        ["report-permits"] = "запросы на правку сводок", ["division"] = "подразделение", ["divisions"] = "подразделения",
        ["reference"] = "справочник", ["references"] = "справочники", ["candidate"] = "кандидат",
        ["candidates"] = "кандидаты", ["asset"] = "носитель", ["assets"] = "носители", ["face"] = "лицо",
        ["faces"] = "лица", ["session"] = "сессия", ["appearance"] = "появление", ["intersection"] = "пересечение",
        ["intersections"] = "пересечения", ["reference-photo"] = "эталон", ["document"] = "документ",
        ["documents"] = "документы", ["assignment"] = "поручение", ["norm"] = "норма", ["violation"] = "нарушение",
        ["method"] = "методика", ["decision"] = "решение", ["authorization"] = "основание поиска",
        ["password"] = "пароль", ["transcript"] = "расшифровка", ["snapshot"] = "снимок кадра",

        // Действия.
        ["view"] = "просмотр", ["list"] = "список", ["create"] = "создание", ["update"] = "правка",
        ["save"] = "сохранение", ["delete"] = "удаление", ["purge"] = "уничтожение", ["approve"] = "разрешено",
        ["reject"] = "отказано", ["set"] = "изменение", ["reset"] = "сброс", ["export"] = "выгрузка",
        ["upload"] = "загрузка", ["search"] = "поиск", ["login"] = "вход", ["logout"] = "выход", ["link"] = "привязка",
        ["unlink"] = "отвязка", ["activate"] = "включение", ["deactivate"] = "отключение", ["revoke"] = "отзыв",
        ["from"] = "от редакции", ["снята"] = "снята",
    };

    /// <summary>Подпись действия журнала.</summary>
    public static string Label(AuditAction action) => action switch
    {
        AuditAction.Login => "Вход",
        AuditAction.View => "Просмотр",
        AuditAction.Search => "Поиск",
        AuditAction.Generate => "Генерация",
        AuditAction.Export => "Выгрузка",
        AuditAction.Print => "Печать",
        AuditAction.Ingest => "Загрузка в корпус",
        AuditAction.Purge => "Удаление из корпуса",
        AuditAction.Modify => "Изменение",
        _ => action.ToString(),
    };

    /// <summary>
    /// Что произошло — по сводке сценария (<paramref name="summary"/>), а без неё — по ссылке на объект и действию.
    /// </summary>
    public static string Describe(AuditAction action, string? summary, string? objectRef)
    {
        var source = string.IsNullOrWhiteSpace(summary) ? objectRef : summary;
        if (string.IsNullOrWhiteSpace(source))
        {
            return Label(action);
        }

        // Части через «:» переводятся по словарю; детали внутри части (например, ячейки матрицы) остаются как есть.
        var parts = source.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = parts.Select(Translate).Where(w => w.Length > 0).ToList();
        if (words.Count == 0)
        {
            return Label(action);
        }

        var text = string.Join(" · ", words);
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static string Translate(string part)
    {
        if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return "№ " + part;
        }

        return Words.TryGetValue(part, out var word) ? word : part;
    }
}
