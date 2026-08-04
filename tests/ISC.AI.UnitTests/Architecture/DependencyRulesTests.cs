using System.Xml.Linq;
using Shouldly;

namespace ISC.AI.UnitTests.Architecture;

/// <summary>
/// Архитектурные тесты правила зависимостей (ТС-009; CLAUDE.md «Правило зависимостей», ДОК-02 §6).
/// </summary>
/// <remarks>
/// Тесты проверяют граф ссылок <c>ProjectReference</c> решения, а не загруженные сборки, —
/// это прямой и устойчивый способ зафиксировать инварианты:
/// (а) ядро никогда не ссылается на профиль; (б) хост подключает ровно один профиль;
/// (в) профиль зависит «внутрь» (к <c>Abstractions</c>), а единственная его связь с конкретным проектом
/// ядра — <c>&lt;Профиль&gt;.Data → Persistence</c>. Правило структурное и действует для ЛЮБОГО профиля
/// (Inspector, ERP, …), а не для конкретного имени. Нарушение любого из них — провал сборки в CI.
///
/// Пакеты модулей <c>src/modules/*</c> (ADR-0017) — третий уровень между ядром и профилем:
/// (г) ядро не ссылается и на модуль; (д) модуль НЕ зависит ни от одного профиля и ни от хоста — иначе
/// теряется его переиспользуемость другими профилями (ровно ради неё документооборот вынесен из профиля);
/// (е) модуль зависит «внутрь» по тому же правилу, что и профиль (<c>&lt;Модуль&gt;.Data → Persistence</c>).
/// Ссылка «профиль → модуль» разрешена и является единственным направлением связи между ними.
/// </remarks>
public sealed class DependencyRulesTests
{
    private const string Abstractions = "ISC.AI.Abstractions";
    private const string Persistence = "ISC.AI.Persistence";
    private const string Host = "ISC.AI.Web";

    /// <summary>
    /// Проекты-библиотеки ядра (без хоста <c>Web</c>): им запрещено ссылаться на профиль. Список НЕ
    /// захардкожен — вычисляется из фактического содержимого <c>src/core</c>, поэтому НОВЫЙ ядровой проект
    /// попадает под запрет автоматически (без правки теста).
    /// </summary>
    private static readonly string[] CoreLibraries = DiscoverCoreLibraries();

    private static readonly IReadOnlyDictionary<string, string[]> Graph = LoadProjectGraph();

    // Ядровые библиотеки — все *.csproj из src/core, кроме хоста Web (хосту профиль подключать МОЖНО).
    private static string[] DiscoverCoreLibraries()
    {
        var coreDir = Path.Combine(FindRepoRoot(), "src", "core");
        return Directory.GetFiles(coreDir, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && name != Host)
            .Distinct(StringComparer.Ordinal)
            .ToArray()!;
    }

    [Fact(DisplayName = "Ядро не ссылается на профиль")]
    public void Core_does_not_reference_profile()
    {
        // Страховка от «пустого» списка (битый путь → тест прошёл бы вхолостую): ядро точно не пустое.
        CoreLibraries.ShouldNotBeEmpty("Не найдено ни одной ядровой библиотеки в src/core — проверь путь обнаружения.");
        CoreLibraries.ShouldContain(Abstractions);

        foreach (var core in CoreLibraries)
        {
            var toProfile = Graph[core].Where(IsProfileProject).ToArray();
            toProfile.ShouldBeEmpty(
                $"Проект ядра «{core}» не должен ссылаться на профиль, но ссылается на: {string.Join(", ", toProfile)}");
        }
    }

    [Fact(DisplayName = "Ядро не ссылается на модуль")]
    public void Core_does_not_reference_module()
    {
        // Та же страховка от «пустого» списка, что и в проверке профиля.
        CoreLibraries.ShouldNotBeEmpty("Не найдено ни одной ядровой библиотеки в src/core — проверь путь обнаружения.");

        foreach (var core in CoreLibraries)
        {
            var toModule = Graph[core].Where(IsModuleProject).ToArray();
            toModule.ShouldBeEmpty(
                $"Проект ядра «{core}» не должен ссылаться на пакет модулей, но ссылается на: {string.Join(", ", toModule)}");
        }
    }

    [Fact(DisplayName = "Хост подключает ровно один профиль")]
    public void Host_references_exactly_one_profile()
    {
        var manifests = Graph[Host].Where(IsProfileManifest).ToArray();
        manifests.Length.ShouldBe(1,
            $"Хост «{Host}» должен подключать ровно один профиль-манифест, найдено: [{string.Join(", ", manifests)}]");
    }

    [Fact(DisplayName = "Профиль зависит внутрь; на Persistence ссылается только <Профиль>.Data")]
    public void Profile_depends_inward_and_only_profile_data_references_Persistence()
    {
        foreach (var (project, references) in Graph.Where(kv => IsProfileProject(kv.Key)))
        {
            references.ShouldNotContain(Host,
                $"Профиль «{project}» не должен ссылаться на хост «{Host}».");

            foreach (var reference in references.Where(IsCoreLibrary))
            {
                // Правило структурное, а не по имени профиля: слой данных ЛЮБОГО профиля (Inspector.Data,
                // ERP.Data, …) может ссылаться на Persistence; всем остальным проектам профиля — только Abstractions.
                var allowed = reference == Abstractions
                    || (reference == Persistence && IsProfileDataProject(project));
                allowed.ShouldBeTrue(
                    $"Профиль «{project}» ссылается на ядро «{reference}»: разрешено только Abstractions (всем) и Persistence (только слою данных профиля «*.Data»).");
            }
        }
    }

    [Fact(DisplayName = "Модуль не зависит от профиля и хоста (иначе теряется переиспользуемость)")]
    public void Module_does_not_reference_profile_or_host()
    {
        var modules = Graph.Keys.Where(IsModuleProject).ToArray();

        // Страховка от вхолостую прошедшего теста: пакет модулей в решении есть (src/modules/docflow).
        modules.ShouldNotBeEmpty("Не найдено ни одного пакета модулей в src/modules — проверь путь обнаружения.");

        foreach (var project in modules)
        {
            var toProfile = Graph[project].Where(IsProfileProject).ToArray();
            toProfile.ShouldBeEmpty(
                $"Модуль «{project}» не должен зависеть от профиля (ссылается на: {string.Join(", ", toProfile)}). "
                + "Связь допустима ТОЛЬКО в обратную сторону: профиль подключает модуль (ADR-0017).");

            Graph[project].ShouldNotContain(Host,
                $"Модуль «{project}» не должен ссылаться на хост «{Host}».");
        }
    }

    [Fact(DisplayName = "Модуль зависит внутрь; на Persistence ссылается только <Модуль>.Data")]
    public void Module_depends_inward_and_only_module_data_references_Persistence()
    {
        foreach (var (project, references) in Graph.Where(kv => IsModuleProject(kv.Key)))
        {
            foreach (var reference in references.Where(IsCoreLibrary))
            {
                // То же структурное правило, что и для профиля: слой данных ЛЮБОГО модуля может ссылаться
                // на Persistence ради общего EF-слоя; всем остальным проектам модуля — только Abstractions.
                var allowed = reference == Abstractions
                    || (reference == Persistence && IsModuleDataProject(project));
                allowed.ShouldBeTrue(
                    $"Модуль «{project}» ссылается на ядро «{reference}»: разрешено только Abstractions (всем) и Persistence (только слою данных модуля «*.Data»).");
            }
        }
    }

    [Fact(DisplayName = "Каждый проект зависит от Abstractions")]
    public void Every_project_depends_on_Abstractions()
    {
        foreach (var project in Graph.Keys.Where(p => p != Abstractions))
        {
            DependsOn(project, Abstractions).ShouldBeTrue(
                $"Проект «{project}» должен прямо или транзитивно зависеть от «{Abstractions}».");
        }
    }

    private static bool IsProfileProject(string name) =>
        name.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal);

    // Пакет модулей (src/modules/*, ADR-0017): переиспользуемая вертикаль, подключаемая профилем.
    private static bool IsModuleProject(string name) =>
        name.StartsWith("ISC.AI.Modules.", StringComparison.Ordinal);

    // Слой данных модуля: «ISC.AI.Modules.<Имя>.Data» — единственное место модуля, которому разрешён Persistence.
    private static bool IsModuleDataProject(string name) =>
        IsModuleProject(name) && name.EndsWith(".Data", StringComparison.Ordinal);

    private static bool IsCoreLibrary(string name) => CoreLibraries.Contains(name);

    // Слой данных профиля: «ISC.AI.Profile.<Имя>.Data» — единственное место профиля, которому разрешён Persistence.
    private static bool IsProfileDataProject(string name) =>
        IsProfileProject(name) && name.EndsWith(".Data", StringComparison.Ordinal);

    // Манифест профиля: ровно «ISC.AI.Profile.<Имя>» без доп. сегментов (.Domain/.Data/.Application/.UI).
    private static bool IsProfileManifest(string name) =>
        IsProfileProject(name) && name.Count(c => c == '.') == 3;

    private static bool DependsOn(string project, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(Graph.TryGetValue(project, out var r) ? r : []);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            if (Graph.TryGetValue(current, out var next))
            {
                foreach (var n in next)
                {
                    stack.Push(n);
                }
            }
        }

        return false;
    }

    private static Dictionary<string, string[]> LoadProjectGraph()
    {
        var root = FindRepoRoot();
        var projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var path in projectFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var references = XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            graph[name] = references;
        }

        return graph;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ISC.AI.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Не найден корень репозитория (ISC.AI.slnx) от каталога теста.");
    }
}
