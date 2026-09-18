using ISC.AI.Profile.Inspector;

namespace ISC.AI.Web;

/// <summary>
/// Точка выбора профиля поставки «ИнспекторAI» (ТС-004, ADR-0002).
/// </summary>
/// <remarks>
/// Единственный код хоста, знающий КОНКРЕТНЫЙ тип профиля. Файл компилируется только при
/// <c>IscProfile=inspector</c> (по умолчанию) — см. <c>&lt;Compile Include="Composition\$(IscProfile)\*.cs"&gt;</c>
/// в <c>ISC.AI.Web.csproj</c>. Одноимённый класс для другой поставки лежит в соседнем каталоге
/// <c>Composition/&lt;профиль&gt;/</c>; <c>Program.cs</c> о различиях не знает и вызывает
/// <see cref="Create"/> одинаково. Возвращается конкретный тип (не <c>IProfile</c>): композиция
/// пользуется и членами, которых в контракте ядра нет (например, <c>MapEndpoints</c>).
/// </remarks>
internal static class HostProfile
{
    /// <summary>Создаёт манифест профиля «ИнспекторAI».</summary>
    public static InspectorProfile Create() => new();
}
