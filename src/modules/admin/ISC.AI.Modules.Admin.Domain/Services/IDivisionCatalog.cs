namespace ISC.AI.Modules.Admin.Domain.Services;

/// <summary>Подразделение справочника профиля: номер, наименование и признак действующего.</summary>
/// <param name="Id">Номер — тот же, что попадает в допуск субъекта (<c>core.clearance.division_scope</c>).</param>
/// <param name="Name">Наименование для интерфейса.</param>
/// <param name="IsActive">Действующее ли подразделение.</param>
public sealed record DivisionCatalogItem(int Id, string Name, bool IsActive);

/// <summary>
/// ПОРТ ПРОФИЛЯ: наименования подразделений для экрана допусков. Ядро знает подразделение только как
/// ЧИСЛО (в допуске лежит список номеров), а справочник ведёт профиль — поэтому пакет спрашивает его.
/// </summary>
/// <remarks>
/// Отдаются ВСЕ подразделения, включая недействующие: в допусках могут остаться номера закрытых
/// подразделений, и экран обязан показать их наименованием, а не «неизвестный номер». Этим порт
/// отличается от справочника документооборота, который отдаёт только действующие (там идёт выбор
/// исполнителя, а здесь — объяснение того, что уже выдано).
/// </remarks>
public interface IDivisionCatalog
{
    /// <summary>Все подразделения профиля, включая недействующие.</summary>
    Task<IReadOnlyList<DivisionCatalogItem>> ListAsync(CancellationToken cancellationToken = default);
}
