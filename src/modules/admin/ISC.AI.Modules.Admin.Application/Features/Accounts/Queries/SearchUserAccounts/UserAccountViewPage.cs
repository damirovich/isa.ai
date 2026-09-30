namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Страница расширенного списка учётных записей.</summary>
/// <param name="Rows">Строки страницы.</param>
/// <param name="TotalCount">Общее число подходящих записей (для постраничной навигации).</param>
/// <param name="IsInitialSetup">Режим первичной настройки: Администратора в системе нет (баннер на экране «Пользователи»).</param>
public sealed record UserAccountViewPage(IReadOnlyList<UserAccountView> Rows, int TotalCount, bool IsInitialSetup = false);
