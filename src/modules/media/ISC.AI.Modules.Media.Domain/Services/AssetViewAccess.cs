using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>На каком основании субъект может просматривать носитель.</summary>
public enum AssetViewGrant
{
    /// <summary>Не может: носитель недоступен (неотличимо от «не найден», ТБ-020/021).</summary>
    None = 0,

    /// <summary>Носитель привязан к делу, доступному субъекту по полной решётке (ТБ-071, ТФ-ДЕЛ-03).</summary>
    Case = 1,

    /// <summary>
    /// Носитель — кандидат поиска, проведённого в доступном субъекту деле (взаимный поиск ОН+ОУ, ADR-0044): только
    /// просмотр, без изменений и без карточки чужого дела.
    /// </summary>
    SearchCandidate = 2,
}

/// <summary>
/// ЕДИНСТВЕННОЕ правило просмотра носителя пакета «Медиа» (ADR-0044): карточка, файлы, кадры, лицо, расшифровка. Все
/// пути ЧТЕНИЯ по прямому идентификатору носителя проверяют его, а не порт профиля напрямую.
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТ (ТБ-071, ТФ-ДЕЛ-03, ТБ-020/021): носитель доступен для просмотра ⇔ он привязан к делу, доступному
/// субъекту по полной решётке профиля (<see cref="ICaseScope.IsAssetAccessibleAsync"/>), ЛИБО он — кандидат поисковой
/// сессии, проведённой в таком деле, и сама строка кандидата проходит floor ядра (гриф и подразделение носителя в
/// пределах допуска). Второе основание — решение заказчика от 14.10.2026 «полный просмотр носителя» для кандидатов
/// взаимного поиска: следователь видит материал чужого дела ОН/ОУ, найденный его поиском, — и только его.
/// Перебором идентификаторов чужие носители не открываются: без кандидатства основания нет.
/// </para>
/// <para>
/// ИЗМЕНЕНИЯ (привязка лица, эталон, дата съёмки, снимок кадра, переиндексация, удаление) и проба поиска этим правилом
/// НЕ разрешаются — они по-прежнему требуют <see cref="ICaseScope.IsAssetAccessibleAsync"/>.
/// </para>
/// </remarks>
public static class AssetViewAccess
{
    /// <summary>Основание просмотра носителя субъектом.</summary>
    /// <param name="caseScope">Порт профиля: дела и доступность по полной решётке.</param>
    /// <param name="sessions">Поисковые сессии пакета.</param>
    /// <param name="assetId">Носитель.</param>
    /// <param name="access">Допуск субъекта.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<AssetViewGrant> ResolveAsync(
        ICaseScope caseScope, ISearchSessionStore sessions, int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseScope);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(access);

        if (await caseScope.IsAssetAccessibleAsync(assetId, access, cancellationToken))
        {
            return AssetViewGrant.Case;
        }

        // Кандидат поиска: дело сессии должно быть доступно субъекту по ПОЛНОЙ решётке — чужая сессия по чужому делу
        // основанием не служит, даже если сама сессия проходит floor.
        foreach (var caseId in await sessions.ListCaseIdsWithCandidateAssetAsync(assetId, access, cancellationToken))
        {
            if (await caseScope.GetCaseAsync(caseId, access, cancellationToken) is not null)
            {
                return AssetViewGrant.SearchCandidate;
            }
        }

        return AssetViewGrant.None;
    }

    /// <summary>Может ли субъект просматривать носитель (<see cref="ResolveAsync"/> не <see cref="AssetViewGrant.None"/>).</summary>
    public static async Task<bool> CanViewAsync(
        ICaseScope caseScope, ISearchSessionStore sessions, int assetId, AccessContext access, CancellationToken cancellationToken = default) =>
        await ResolveAsync(caseScope, sessions, assetId, access, cancellationToken) != AssetViewGrant.None;
}
