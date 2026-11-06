using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISC.AI.Profile.Investigation.Domain.Exchange;

/// <summary>
/// Провенанс появления, перенесённого из другого офиса (ТФ-ОБМ-02, ADR-0068 v0.3): откуда оно, чьими подписями и какими
/// решениями подтверждено у источника. Хранится JSON-ом в <c>investigation.appearance.source_provenance</c>.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-073): это решения ИСТОЧНИКА, не получателя — у получателя кандидата и сеанса поиска не было. Экран и
/// справка показывают их как «подтверждено в &lt;офис&gt;», с именами сотрудников источника; учётки получателя с тем же
/// именем входа — лишь удобство ссылки, подписью получателя они не становятся.
/// </remarks>
/// <param name="SourceInstance">Экземпляр-источник.</param>
/// <param name="PackageName">Пакет, которым появление пришло.</param>
/// <param name="SourceAppearanceId">Номер появления в источнике.</param>
/// <param name="Expert">Первая подпись у источника.</param>
/// <param name="Verifier">Вторая подпись у источника (в режиме двух лиц).</param>
/// <param name="RevokedBy">Кто отозвал у источника.</param>
/// <param name="Decisions">Решения всех этапов верификации у источника.</param>
/// <param name="ImportedAtUtc">Когда перенесено у получателя.</param>
public sealed record AppearanceProvenance(
    string SourceInstance,
    string PackageName,
    int SourceAppearanceId,
    ProvenanceSignature? Expert,
    ProvenanceSignature? Verifier,
    ProvenanceSignature? RevokedBy,
    IReadOnlyList<ProvenanceDecision> Decisions,
    DateTime ImportedAtUtc)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>JSON для столбца появления.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Провенанс из столбца; пусто или нечитаемо — <see langword="null"/> (экран покажет только пометку).</summary>
    public static AppearanceProvenance? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppearanceProvenance>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Подпись сотрудника источника: имя входа и ФИО (номера учётных записей у офисов разные).</summary>
/// <param name="UserName">Имя входа у источника.</param>
/// <param name="DisplayName">ФИО у источника.</param>
public sealed record ProvenanceSignature(string UserName, string? DisplayName)
{
    /// <summary>Подпись для экрана: ФИО, а без него — имя входа.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName.Trim();
}

/// <summary>Решение верификации у источника.</summary>
/// <param name="Stage">Этап: <c>Expert</c>, <c>Verifier</c>, <c>Supervisor</c>.</param>
/// <param name="Verdict">Вердикт: <c>Confirmed</c>, <c>Rejected</c>, <c>Undetermined</c>.</param>
/// <param name="Rationale">Обоснование.</param>
/// <param name="DecidedAtUtc">Когда.</param>
/// <param name="By">Кто.</param>
/// <param name="ConfirmationMode">Режим подтверждения у источника: <c>SingleOperator</c> или <c>TwoPerson</c>.</param>
public sealed record ProvenanceDecision(
    string Stage, string Verdict, string Rationale, DateTime DecidedAtUtc, ProvenanceSignature? By, string ConfirmationMode)
{
    /// <summary>Этап словами.</summary>
    public string StageLabel => Stage switch
    {
        "Expert" => "эксперт",
        "Verifier" => "верификатор",
        "Supervisor" => "руководитель",
        _ => Stage,
    };

    /// <summary>Вердикт словами — теми же, что у своих решений в справке (ADR-0053).</summary>
    public string VerdictLabel => Verdict switch
    {
        "Confirmed" => "подтверждено",
        "Rejected" => "отклонено",
        "Undetermined" => "не определено",
        _ => Verdict,
    };

    /// <summary>Режим подтверждения словами.</summary>
    public string ModeLabel => ConfirmationMode switch
    {
        "SingleOperator" => "один оператор",
        "TwoPerson" => "правило двух лиц",
        _ => ConfirmationMode,
    };
}
