using ISC.AI.Profile.Investigation;

namespace ISC.AI.Web;

/// <summary>
/// Точка выбора профиля поставки «Следствие» (ТС-004, ADR-0002).
/// </summary>
/// <remarks>
/// Единственный код хоста, знающий КОНКРЕТНЫЙ тип профиля. Файл компилируется только при
/// <c>IscProfile=investigation</c> (<c>dotnet build -p:IscProfile=investigation</c> или переменная
/// окружения <c>IscProfile</c>) — см. <c>&lt;Compile Include="Composition\$(IscProfile)\*.cs"&gt;</c>
/// в <c>ISC.AI.Web.csproj</c>. В поставке по умолчанию (inspector) этот файл в сборку не входит,
/// поэтому отсутствие проекта <c>ISC.AI.Profile.Investigation</c> её не ломает. <c>Program.cs</c>
/// о различиях не знает и вызывает <see cref="Create"/> одинаково.
/// </remarks>
internal static class HostProfile
{
    /// <summary>Создаёт манифест профиля «Следствие».</summary>
    public static InvestigationProfile Create() => new();
}
