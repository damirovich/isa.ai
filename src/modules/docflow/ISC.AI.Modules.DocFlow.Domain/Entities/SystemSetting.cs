namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Системная настройка модуля документооборота (ТЗ СКИД §9). Схема <c>docflow</c>, ключ — строка.
/// </summary>
/// <remarks>
/// Настройки живут В БАЗЕ, а не в файле конфигурации, ровно по одной причине: их меняет эксплуатант
/// на работающей системе, а правка <c>appsettings.json</c> требует доступа к серверу и перезапуска.
/// Хранилище «ключ-значение», а не колонка на параметр: следующий параметр не должен требовать
/// миграции. Значение — строка; разбор и границы допустимого — на стороне читающего кода, чтобы
/// таблица не знала о смысле своих строк.
/// </remarks>
public class SystemSetting
{
    /// <summary>Ключ настройки (первичный ключ).</summary>
    public required string Key { get; set; }

    /// <summary>Значение в строковом виде.</summary>
    public required string Value { get; set; }

    /// <summary>Когда изменено (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Кто изменил — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int? UpdatedByUserId { get; set; }
}
