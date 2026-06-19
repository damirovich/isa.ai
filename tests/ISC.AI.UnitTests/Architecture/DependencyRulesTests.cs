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
/// (в) профиль зависит «внутрь» (к <c>Abstractions</c>), а единственная его связь с конкретным
/// проектом ядра — <c>Inspector.Data → Persistence</c>. Нарушение любого из них — провал сборки в CI.
/// </remarks>
public sealed class DependencyRulesTests
{
    private const string Abstractions = "ISC.AI.Abstractions";
    private const string Persistence = "ISC.AI.Persistence";
    private const string Host = "ISC.AI.Web";
    private const string InspectorData = "ISC.AI.Profile.Inspector.Data";

    /// <summary>Проекты-библиотеки ядра (без хоста <c>Web</c>): им запрещено ссылаться на профиль.</summary>
    private static readonly string[] CoreLibraries =
    [
        "ISC.AI.Abstractions", "ISC.AI.AI", "ISC.AI.Ingestion", "ISC.AI.Documents", "ISC.AI.Persistence",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Graph = LoadProjectGraph();

    [Fact(DisplayName = "Ядро не ссылается на профиль")]
    public void Core_does_not_reference_profile()
    {
        foreach (var core in CoreLibraries)
        {
            var toProfile = Graph[core].Where(IsProfileProject).ToArray();
            toProfile.ShouldBeEmpty(
                $"Проект ядра «{core}» не должен ссылаться на профиль, но ссылается на: {string.Join(", ", toProfile)}");
        }
    }

    [Fact(DisplayName = "Хост подключает ровно один профиль")]
    public void Host_references_exactly_one_profile()
    {
        var manifests = Graph[Host].Where(IsProfileManifest).ToArray();
        manifests.Length.ShouldBe(1,
            $"Хост «{Host}» должен подключать ровно один профиль-манифест, найдено: [{string.Join(", ", manifests)}]");
    }

    [Fact(DisplayName = "Профиль зависит внутрь; только Inspector.Data ссылается на Persistence")]
    public void Profile_depends_inward_and_only_InspectorData_references_Persistence()
    {
        foreach (var (project, references) in Graph.Where(kv => IsProfileProject(kv.Key)))
        {
            references.ShouldNotContain(Host,
                $"Профиль «{project}» не должен ссылаться на хост «{Host}».");

            foreach (var reference in references.Where(IsCoreLibrary))
            {
                var allowed = reference == Abstractions
                    || (reference == Persistence && project == InspectorData);
                allowed.ShouldBeTrue(
                    $"Профиль «{project}» ссылается на ядро «{reference}»: разрешено только Abstractions (всем) и Persistence (только {InspectorData}).");
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

    private static bool IsCoreLibrary(string name) => CoreLibraries.Contains(name);

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
