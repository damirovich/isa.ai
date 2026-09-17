using System.Text.RegularExpressions;
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
/// (Inspector и любого будущего), а не для конкретного имени. Нарушение любого из них — провал сборки в CI.
///
/// Пакеты модулей <c>src/modules/*</c> (ADR-0017) — третий уровень между ядром и профилем:
/// (г) ядро не ссылается и на модуль; (д) модуль НЕ зависит ни от одного профиля и ни от хоста — иначе
/// теряется его переиспользуемость другими профилями (ровно ради неё документооборот вынесен из профиля);
/// (е) модуль зависит «внутрь» по тому же правилу, что и профиль (<c>&lt;Модуль&gt;.Data → Persistence</c>).
/// Ссылка «профиль → модуль» разрешена и является единственным направлением связи между ними.
///
/// Профиль поставки (ТС-004, ADR-0002). С появлением второго профиля («Следствие») в репозитории
/// лежат ДВА манифеста, а хост обязан подключать ровно один — тот, что выбран свойством MSBuild
/// <c>IscProfile</c> (<c>ISC.AI.Web.csproj</c>: условные <c>ItemGroup</c>). Поэтому граф строится с
/// учётом атрибутов <c>Condition</c>: без этого тест «ровно один профиль» видел бы обе ссылки сразу и
/// ложно падал, а с «наивным» отбрасыванием условных ссылок — ложно проходил. Условия вида
/// <c>'$(IscProfile)' == 'x'</c> / <c>!= 'x'</c> вычисляются подстановкой выбранного значения; любое
/// другое (неизвестное тесту) условие считается ИСТИННЫМ — это направление «падать громко»: лишняя
/// ссылка в графе может только ужесточить проверку, но не спрятать нарушение.
/// </remarks>
public sealed class DependencyRulesTests
{
    private const string Abstractions = "ISC.AI.Abstractions";
    private const string Persistence = "ISC.AI.Persistence";
    private const string Host = "ISC.AI.Web";

    /// <summary>Имя свойства MSBuild, выбирающего профиль поставки (ТС-004).</summary>
    private const string ProfileProperty = "IscProfile";

    /// <summary>Профиль поставки по умолчанию — «ИнспекторAI».</summary>
    private const string DefaultProfile = "inspector";

    /// <summary>
    /// Проекты-библиотеки ядра (без хоста <c>Web</c>): им запрещено ссылаться на профиль. Список НЕ
    /// захардкожен — вычисляется из фактического содержимого <c>src/core</c>, поэтому НОВЫЙ ядровой проект
    /// попадает под запрет автоматически (без правки теста).
    /// </summary>
    private static readonly string[] CoreLibraries = DiscoverCoreLibraries();

    // Условие MSBuild вида  'левая' == 'правая'  /  'левая' != 'правая'  (единственная форма, которую тест понимает).
    // ВАЖНО: объявлено ДО Graph — статические поля инициализируются в порядке объявления, а LoadProjectGraph
    // уже пользуется этими регулярными выражениями.
    private static readonly Regex EqualityCondition = new(
        @"^\s*'(?<left>[^']*)'\s*(?<op>==|!=)\s*'(?<right>[^']*)'\s*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    // Ссылка на свойство MSBuild:  $(Имя)
    private static readonly Regex PropertyReference = new(
        @"\$\((?<name>[A-Za-z_][A-Za-z0-9_]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>Граф ссылок в конфигурации по умолчанию (<c>IscProfile=inspector</c>).</summary>
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

    /// <summary>
    /// ТС-004 при двух профилях в репозитории: для КАЖДОГО значения <c>IscProfile</c> хост подключает ровно
    /// один манифест — и именно выбранный. Проверяются оба значения, а не только текущее, чтобы поставка
    /// «Следствие» не могла молча остаться с двумя профилями (или без единого) до момента её сборки.
    /// </summary>
    [Theory(DisplayName = "Хост подключает ровно один профиль — выбранный свойством IscProfile")]
    [InlineData("inspector")]
    [InlineData("investigation")]
    public void Host_references_exactly_one_profile(string iscProfile)
    {
        var graph = LoadProjectGraph(iscProfile);

        var manifests = graph[Host].Where(IsProfileManifest).ToArray();
        manifests.Length.ShouldBe(1,
            $"Хост «{Host}» при {ProfileProperty}={iscProfile} должен подключать ровно один профиль-манифест, найдено: [{string.Join(", ", manifests)}]");

        // Переключатель обязан переключать: подключён манифест ВЫБРАННОГО профиля («ISC.AI.Profile.<Имя>»),
        // а не какой-то один из имеющихся.
        string.Equals(manifests[0], $"ISC.AI.Profile.{iscProfile}", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            $"При {ProfileProperty}={iscProfile} хост подключил не тот профиль: «{manifests[0]}».");
    }

    /// <summary>
    /// Поставка по умолчанию — «ИнспекторAI»: без явного свойства (обычный <c>dotnet build</c>, Visual Studio
    /// без переменной окружения) хост обязан подключать <c>inspector</c>. Проверяется сам csproj хоста:
    /// свойство объявлено с условием «если не задано» и значением по умолчанию.
    /// </summary>
    [Fact(DisplayName = "По умолчанию (без свойства IscProfile) хост подключает inspector")]
    public void Host_profile_defaults_to_inspector()
    {
        var hostProject = Directory
            .GetFiles(Path.Combine(FindRepoRoot(), "src", "core"), $"{Host}.csproj", SearchOption.AllDirectories)
            .ShouldHaveSingleItem($"Не найден проект хоста «{Host}.csproj» в src/core.");

        var declarations = XDocument.Load(hostProject)
            .Descendants(ProfileProperty)
            .Where(e => e.Parent?.Name.LocalName == "PropertyGroup")
            .ToArray();

        var declaration = declarations.ShouldHaveSingleItem(
            $"В «{Host}.csproj» должно быть ровно одно объявление свойства {ProfileProperty}, найдено: {declarations.Length}.");

        // Значение по умолчанию применяется ТОЛЬКО когда свойство не задано снаружи (-p / переменная окружения):
        // условие «'$(IscProfile)' == ''» истинно при пустом значении и ложно при любом заданном.
        var condition = (string?)declaration.Attribute("Condition");
        condition.ShouldNotBeNull(
            $"Свойство {ProfileProperty} должно объявляться с условием «если не задано», иначе его нельзя переопределить снаружи.");
        EvaluateCondition(condition, iscProfile: string.Empty).ShouldBeTrue(
            $"Условие «{condition}» должно срабатывать при НЕ заданном {ProfileProperty}.");
        EvaluateCondition(condition, iscProfile: "investigation").ShouldBeFalse(
            $"Условие «{condition}» не должно перекрывать значение, заданное снаружи.");

        declaration.Value.Trim().ShouldBe(DefaultProfile,
            $"Профиль поставки по умолчанию должен быть «{DefaultProfile}» (ТС-004).");

        // И граф по умолчанию (без явного значения) действительно содержит только Inspector.
        Graph[Host].Where(IsProfileManifest).ShouldBe(["ISC.AI.Profile.Inspector"]);
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
                // Правило структурное, а не по имени профиля: слой данных ЛЮБОГО профиля («<Профиль>.Data»)
                // может ссылаться на Persistence; всем остальным проектам профиля — только Abstractions.
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

    /// <summary>
    /// Строит граф «проект → ссылки» по всем <c>*.csproj</c> из <c>src</c> так, как его увидел бы MSBuild
    /// при заданном профиле поставки: ссылка учитывается, только если истинны условия самого элемента
    /// <c>ProjectReference</c> И всех его предков (<c>ItemGroup</c>, <c>When</c>/<c>Otherwise</c> и т.п.).
    /// </summary>
    /// <param name="iscProfile">Значение свойства <c>IscProfile</c>; по умолчанию — поставка «ИнспекторAI».</param>
    private static Dictionary<string, string[]> LoadProjectGraph(string iscProfile = DefaultProfile)
    {
        var root = FindRepoRoot();
        var projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var path in projectFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var references = XDocument.Load(path)
                .Descendants("ProjectReference")
                .Where(e => IsEffectivelyIncluded(e, iscProfile))
                .Select(e => (string?)e.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            graph[name] = references;
        }

        return graph;
    }

    // Элемент включён, если истинно его собственное условие и условия всех предков до корня <Project>.
    private static bool IsEffectivelyIncluded(XElement element, string iscProfile)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            var condition = (string?)current.Attribute("Condition");
            if (!string.IsNullOrWhiteSpace(condition) && !EvaluateCondition(condition, iscProfile))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Упрощённый вычислитель условий MSBuild: понимает <c>'a' == 'b'</c> и <c>'a' != 'b'</c> с подстановкой
    /// <c>$(IscProfile)</c>; сравнение без учёта регистра (как в MSBuild). Любая другая форма условия или
    /// ссылка на неизвестное свойство → условие считается ИСТИННЫМ, чтобы тест не мог «потерять» ссылку
    /// (см. remarks класса).
    /// </summary>
    private static bool EvaluateCondition(string condition, string iscProfile)
    {
        var match = EqualityCondition.Match(condition);
        if (!match.Success)
        {
            return true;
        }

        var unknownProperty = false;
        string Expand(string text) => PropertyReference.Replace(text, m =>
        {
            if (string.Equals(m.Groups["name"].Value, ProfileProperty, StringComparison.OrdinalIgnoreCase))
            {
                return iscProfile;
            }

            unknownProperty = true;
            return m.Value;
        });

        var left = Expand(match.Groups["left"].Value);
        var right = Expand(match.Groups["right"].Value);
        if (unknownProperty)
        {
            return true;
        }

        var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        return match.Groups["op"].Value == "==" ? equal : !equal;
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
