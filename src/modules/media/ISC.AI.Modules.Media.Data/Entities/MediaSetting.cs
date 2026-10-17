using System;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Настройка пакета «Медиа» (<c>media.setting</c>, ADR-0054): режим подтверждения, рабочий порог и нижняя граница
/// порога схожести. Ключ — строка, значение — строка.
/// </summary>
/// <remarks>
/// Настройки живут В БАЗЕ, а не в файле конфигурации: их меняет Администратор на работающей системе, а правка
/// <c>appsettings.json</c> требует доступа к серверу и перезапуска. «Ключ — значение», а не колонка на параметр:
/// следующая настройка не должна требовать миграции. Разбор и границы — у читающего кода
/// (<c>MediaSettingsStore</c>), таблица о смысле своих строк не знает. Образец — <c>docflow.system_setting</c>.
/// </remarks>
public class MediaSetting
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
