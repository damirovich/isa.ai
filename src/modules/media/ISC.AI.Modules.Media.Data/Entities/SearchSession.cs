using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Поисковая сессия (<c>media.search_session</c>, ТО-инф-12, ТФ-ПЛ-07): один запуск поиска «лицо по фото»
/// в контексте дела и основания (ТБ-071) — параметры, версии моделей и хеш пробы. ВЕКТОР ПРОБЫ НЕ ХРАНИТСЯ
/// (ТБ-074): в базу шаблонов проба не попадает, здесь только SHA-256 и, при наличии, вырезка лица пробы
/// для показа пары «пробное ↔ кандидат». Гриф/подразделение — дела (ТБ-070): чтение — под решёткой ядра
/// (ТБ-020). Дело, основание и пользователь — слабые ссылки по значению, без FK через границу схем
/// (ТО-инф-08).
/// </summary>
public class SearchSession : BaseEntity, IClassified
{
    /// <summary>Дело, в контексте которого ведётся поиск (значение из схемы профиля).</summary>
    public int CaseId { get; set; }

    /// <summary>Реквизиты основания поиска (поручение/постановление/ОРМ) — в аудит и историю (ТБ-071/072).</summary>
    public required string AuthorizationRef { get; set; }

    /// <summary>Вид области поиска (ТФ-ПЛ-05).</summary>
    public SearchScopeKind Scope { get; set; }

    /// <summary>Дела, составившие область поиска (массив значений; для <see cref="SearchScopeKind.CurrentCase"/> — одно дело).</summary>
    public int[] CaseIds { get; set; } = [];

    /// <summary>SHA-256 пробного изображения (hex) — связь с копией в аудите (ТБ-072).</summary>
    public required string ProbeSha256 { get; set; }

    /// <summary>Лицо базы, взятое пробой («этот человек в других материалах», ТФ-ПЛ-03); <see langword="null"/> — внешняя проба.</summary>
    public int? ProbeFaceId { get; set; }

    /// <summary>Имя файла вырезки лица пробы (категория <c>media-probes</c>), если сохранена.</summary>
    public string? ProbeCropStoredFileName { get; set; }

    /// <summary>Ширина выдачи (ТН-008).</summary>
    public int TopK { get; set; }

    /// <summary>Порог косинусного расстояния; <see langword="null"/> — без отсечения.</summary>
    public double? MaxCosineDistance { get; set; }

    /// <summary>Версия детектора (ТО-прог-11).</summary>
    public required string DetectorVersion { get; set; }

    /// <summary>Версия векторизатора (ТО-прог-11).</summary>
    public required string EmbedderVersion { get; set; }

    /// <summary>Широта обхода HNSW при поиске (<c>hnsw.ef_search</c>) — параметр воспроизводимости.</summary>
    public int HnswEfSearch { get; set; }

    /// <summary>Гриф дела (ТБ-070). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение дела (ТБ-070). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Кто запустил поиск — слабая ссылка на <c>core.app_user</c>.</summary>
    public int? RequestedByUserId { get; set; }
}
