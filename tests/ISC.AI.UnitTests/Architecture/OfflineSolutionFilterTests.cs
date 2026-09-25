using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Architecture;

/// <summary>
/// Контракт фильтра решения изолированного контура <c>ISC.AI.Offline.slnf</c> (ADR-0026, п. 7; ТБ-050,
/// ТСТ-001/004). По этому фильтру <c>deploy/offline/export-packages.ps1</c> собирает общий офлайн-фид, и по нему
/// же решение собирается в контуре. Процесс-распознаватель речи <c>ISC.AI.Speech.Worker</c> зависит от
/// <c>org.k2fsa.sherpa.onnx</c>, а тот тянет пакеты <c>org.k2fsa.sherpa.onnx.runtime.*</c>, нативные библиотеки
/// которых статически содержат eSpeak NG (GPL-3.0). Поэтому ни утилита, ни её тесты в фильтр не входят, и ни один
/// проект фильтра не должен достигать утилиты по ссылкам: <c>dotnet restore</c> по фильтру восстанавливает и
/// проекты, на которые ссылаются проекты фильтра, и пакеты утилиты попали бы в фид.
/// </summary>
/// <remarks>
/// Граф строится по ВСЕМ <c>ProjectReference</c> без вычисления условий MSBuild: лишнее ребро может только
/// ужесточить проверку, но не спрятать нарушение. Новый проект в <c>ISC.AI.slnx</c> без правки фильтра роняет
/// первый тест — фильтр не устаревает молча, и проект не выпадает из офлайн-сборки незаметно.
/// </remarks>
public sealed class OfflineSolutionFilterTests
{
    private const string SolutionFileName = "ISC.AI.slnx";
    private const string FilterFileName = "ISC.AI.Offline.slnf";

    /// <summary>Процесс-распознаватель речи — единственный потребитель пакетов sherpa-onnx (ADR-0026).</summary>
    private const string SpeechWorker = "ISC.AI.Speech.Worker";

    /// <summary>Юнит-тесты утилиты: ссылаются на неё и поэтому тоже вне фильтра.</summary>
    private const string SpeechWorkerTests = "ISC.AI.Speech.Worker.Tests";

    /// <summary>Префикс запрещённых для фида пакетов (sherpa-onnx и его нативные runtime-пакеты).</summary>
    private const string ForbiddenPackagePrefix = "org.k2fsa";

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact(DisplayName = "Офлайн-фильтр решения содержит все проекты ISC.AI.slnx, кроме процесса-распознавателя речи и его тестов")]
    public void Filter_contains_all_solution_projects_except_speech_worker()
    {
        var (solutionPath, filterProjects) = ReadFilter();
        solutionPath.ShouldBe(SolutionFileName, $"{FilterFileName} должен ссылаться на {SolutionFileName} в корне репозитория");

        filterProjects.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ShouldBe(filterProjects.Count, $"в {FilterFileName} есть повторяющиеся проекты");

        var expected = ReadSolutionProjects()
            .Where(path => ProjectName(path) is not (SpeechWorker or SpeechWorkerTests))
            .ToList();
        filterProjects.ShouldBe(expected, ignoreOrder: true,
            customMessage: $"{FilterFileName} — это ВСЕ проекты {SolutionFileName}, кроме {SpeechWorker} и {SpeechWorkerTests}. " +
                           "Добавили проект в решение — добавьте его и в фильтр (ADR-0026, п. 7).");

        foreach (var project in filterProjects)
        {
            File.Exists(ToFullPath(project)).ShouldBeTrue($"проект фильтра не найден на диске: {project}");
        }
    }

    [Fact(DisplayName = "Ни один проект офлайн-фильтра не ссылается на процесс-распознаватель речи — ни прямо, ни транзитивно")]
    public void No_filter_project_reaches_speech_worker()
    {
        var offenders = new List<string>();
        foreach (var project in ReadFilter().Projects)
        {
            var chain = FindReferenceChain(ToFullPath(project), path => ProjectName(path) == SpeechWorker);
            if (chain is not null)
            {
                offenders.Add(string.Join(" → ", chain.Select(ProjectName)));
            }
        }

        offenders.ShouldBeEmpty(
            $"dotnet restore по {FilterFileName} восстановил бы {SpeechWorker} и его пакеты sherpa-onnx с eSpeak NG (GPL-3.0) " +
            "в общий офлайн-фид. Тесты утилиты — в отдельном проекте вне фильтра (ADR-0026, п. 7). Цепочки: " +
            string.Join("; ", offenders));
    }

    [Fact(DisplayName = "Ни один проект офлайн-фильтра и его ссылок не подключает пакеты org.k2fsa (sherpa-onnx с eSpeak NG, GPL-3.0)")]
    public void No_filter_project_references_sherpa_packages()
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in ReadFilter().Projects)
        {
            foreach (var file in ReferenceClosure(ToFullPath(project)).SelectMany(WithDirectoryBuildFiles))
            {
                foreach (var package in ReferencedPackages(file).Where(IsForbiddenPackage))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}: {package}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"пакеты {ForbiddenPackagePrefix}.* не должны восстанавливаться по {FilterFileName}: их нативные библиотеки " +
            "содержат eSpeak NG (GPL-3.0) и попали бы в общий офлайн-фид (ADR-0026, п. 7). Найдено: " +
            string.Join("; ", offenders));
    }

    // Фильтр: путь к решению и список проектов (разделители путей приводятся к «/»).
    private static (string SolutionPath, List<string> Projects) ReadFilter()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, FilterFileName)));
        var solution = document.RootElement.GetProperty("solution");
        var projects = solution.GetProperty("projects").EnumerateArray()
            .Select(item => NormalizeSeparators(item.GetString() ?? string.Empty))
            .ToList();
        return (NormalizeSeparators(solution.GetProperty("path").GetString() ?? string.Empty), projects);
    }

    // Проекты решения: атрибут Path всех элементов <Project> в ISC.AI.slnx.
    private static List<string> ReadSolutionProjects() =>
        XDocument.Load(Path.Combine(RepoRoot, SolutionFileName))
            .Descendants("Project")
            .Select(element => NormalizeSeparators((string?)element.Attribute("Path") ?? string.Empty))
            .Where(path => path.Length > 0)
            .ToList();

    // Все проекты, достижимые от project по ProjectReference, включая сам project (полные пути).
    private static HashSet<string> ReferenceClosure(string project)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var next in ProjectReferences(current))
            {
                stack.Push(next);
            }
        }

        return seen;
    }

    // Кратчайшая цепочка ссылок от project до проекта, удовлетворяющего target (поиск в ширину), или null.
    private static List<string>? FindReferenceChain(string project, Func<string, bool> target)
    {
        var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [project] = null };
        var queue = new Queue<string>([project]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (target(current))
            {
                var chain = new List<string>();
                for (string? step = current; step is not null; step = previous[step])
                {
                    chain.Add(step);
                }

                chain.Reverse();
                return chain;
            }

            foreach (var next in ProjectReferences(current).Where(next => !previous.ContainsKey(next)))
            {
                previous[next] = current;
                queue.Enqueue(next);
            }
        }

        return null;
    }

    // Прямые ProjectReference проекта — полные пути; ссылка на несуществующий файл — явная ошибка теста.
    private static IEnumerable<string> ProjectReferences(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        foreach (var include in XDocument.Load(project).Descendants("ProjectReference")
                     .Select(element => (string?)element.Attribute("Include"))
                     .Where(include => !string.IsNullOrWhiteSpace(include)))
        {
            var path = Path.GetFullPath(Path.Combine(directory, include!.Replace('\\', Path.DirectorySeparatorChar)));
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{project}: ProjectReference «{include}» не найден — граф ссылок не построить.");
            }

            yield return path;
        }
    }

    // Проект и файлы Directory.Build.props/.targets и Directory.Packages.props на пути от его каталога до корня
    // репозитория: пакет можно подключить и там (PackageReference / GlobalPackageReference).
    private static IEnumerable<string> WithDirectoryBuildFiles(string project)
    {
        yield return project;
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(project)!);
             directory is not null && directory.FullName.Length >= RepoRoot.Length;
             directory = directory.Parent)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            {
                var file = Path.Combine(directory.FullName, name);
                if (File.Exists(file))
                {
                    yield return file;
                }
            }
        }
    }

    // Пакеты, которые файл MSBuild подключает: Include/Update у PackageReference и GlobalPackageReference
    // (PackageVersion лишь задаёт версию и пакет не подключает).
    private static IEnumerable<string> ReferencedPackages(string file) =>
        XDocument.Load(file).Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "GlobalPackageReference")
            .SelectMany(element => new[] { (string?)element.Attribute("Include"), (string?)element.Attribute("Update") })
            .Where(package => !string.IsNullOrWhiteSpace(package))
            .Select(package => package!);

    private static bool IsForbiddenPackage(string package) =>
        package.StartsWith(ForbiddenPackagePrefix, StringComparison.OrdinalIgnoreCase);

    private static string ProjectName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static string ToFullPath(string relative) =>
        Path.GetFullPath(Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Не найден корень репозитория ({SolutionFileName}) от каталога теста.");
    }
}
