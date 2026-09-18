using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using Pgvector;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Биометрический шаблон лица (<c>media.face_template</c>, ТС-012): L2-нормированный вектор для
/// ANN-поиска. Режимные поля и флаг актуальности ДЕНОРМАЛИЗОВАНЫ на строку с вектором (ТБ-020/070):
/// pre-filter доступа применяется В ЭТОЙ ЖЕ строке до обхода HNSW, без джойнов через лицо/носитель —
/// та же опора fail-closed, что у <c>core.embedding</c> (GATE-1 → GATE-4). Каскадно удаляется с лицом.
/// </summary>
public class FaceTemplate : BaseEntity, IClassified
{
    /// <summary>
    /// Размерность вектора выбранного векторизатора (SFace, ADR-0020); фиксируется типом столбца
    /// <c>vector(N)</c>. Смена модели с иной размерностью = миграция + переиндексация носителей.
    /// </summary>
    public const int Dimensions = 128;

    /// <summary>Лицо-источник.</summary>
    public int FaceId { get; set; }

    /// <summary>Навигация к лицу.</summary>
    public Face? Face { get; set; }

    /// <summary>Носитель (денормализован для выдачи без джойна и для массовых операций по носителю).</summary>
    public int AssetId { get; set; }

    /// <summary>Вектор (pgvector). Размерность — <see cref="Dimensions"/>.</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>Версия модели, породившей вектор (ТО-прог-11): шаблоны разных моделей несравнимы.</summary>
    public required string ModelVersion { get; set; }

    /// <summary>Пригодно ли исходное лицо (шаблоны непригодных по умолчанию в поиск не идут).</summary>
    public bool QualityAcceptable { get; set; }

    /// <summary>Гриф (денормализован, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Актуальность носителя (денормализована, ADR-0013). NOT NULL, по умолчанию <c>true</c>.</summary>
    public bool IsCurrent { get; set; } = true;
}
