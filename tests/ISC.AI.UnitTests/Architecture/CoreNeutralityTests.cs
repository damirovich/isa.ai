using System.Reflection;
using ISC.AI.Profile.Inspector.Domain.Entities;
using Shouldly;

namespace ISC.AI.UnitTests.Architecture;

/// <summary>
/// Архитектурные тесты НЕЙТРАЛЬНОСТИ ядра (ТС-003, ADR-0002, ADR-0013). Доменная модель НПА
/// (норма / редакция / статус редакции) не должна возвращаться в сборки ядра: она живёт в профиле
/// (схема <c>inspector</c>). Тест — защита от регресса утечки, устранённой выносом НПА в профиль.
/// </summary>
/// <remarks>
/// Сборки ядра определяются ПО СОГЛАШЕНИЮ ОБ ИМЕНАХ (все <c>ISC.AI.*</c>, кроме хоста <c>Web</c>,
/// профилей <c>Profile.*</c> и тестов) — чтобы НОВЫЕ сборки ядра (напр. дом грунтовки <c>ISC.AI.AI</c>)
/// попадали под защиту автоматически. Проверяются имена типов, свойств, полей и членов enum.
/// </remarks>
public sealed class CoreNeutralityTests
{
    // Точечный список доменных НПА-идентификаторов. Подстроку "Norm" целиком НЕ баним, чтобы не
    // задеть нейтральные Normalize/Normalizer (напр. ICitationNormalizer грунтовки — ADR-0013).
    private static readonly string[] ForbiddenNpaIdentifiers =
        ["LegalNorm", "NormRevision", "RevisionStatus", "NormId", "RevisionId"];

    [Fact(DisplayName = "Сборки ядра не содержат доменных идентификаторов НПА (нейтральность ядра)")]
    public void Core_assemblies_contain_no_npa_identifiers()
    {
        var coreAssemblies = LoadCoreAssemblies();
        coreAssemblies.ShouldNotBeEmpty("Не найдено ни одной сборки ядра для проверки (проверь каталог вывода/ссылки).");

        var offenders = new List<string>();

        foreach (var assembly in coreAssemblies)
        {
            var assemblyName = assembly.GetName().Name;

            foreach (var type in SafeGetTypes(assembly))
            {
                CheckName($"{assemblyName}: тип {type.Name}", type.Name, offenders);
                CheckName($"{assemblyName}: пространство {type.Namespace}", type.Namespace ?? string.Empty, offenders);

                const BindingFlags memberFlags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                foreach (var property in type.GetProperties(memberFlags))
                {
                    CheckName($"{assemblyName}: {type.Name}.{property.Name}", property.Name, offenders);
                }

                foreach (var field in type.GetFields(memberFlags))
                {
                    CheckName($"{assemblyName}: {type.Name}.{field.Name}", field.Name, offenders);
                }

                if (type.IsEnum)
                {
                    foreach (var memberName in type.GetEnumNames())
                    {
                        CheckName($"{assemblyName}: {type.Name}.{memberName}", memberName, offenders);
                    }
                }
            }
        }

        offenders.ShouldBeEmpty(
            "В сборках ядра найдены доменные НПА-идентификаторы (должны жить в профиле, схема inspector): "
            + string.Join("; ", offenders));
    }

    [Fact(DisplayName = "Сборки ядра не ссылаются на сборки профиля (нейтральность на уровне метаданных)")]
    public void Core_assemblies_do_not_reference_profile_assemblies()
    {
        var coreAssemblies = LoadCoreAssemblies();
        coreAssemblies.ShouldNotBeEmpty("Не найдено ни одной сборки ядра для проверки.");

        var offenders = new List<string>();
        foreach (var assembly in coreAssemblies)
        {
            var profileRefs = assembly.GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(n => n is not null && n.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal))
                .ToArray();
            if (profileRefs.Length > 0)
            {
                offenders.Add($"{assembly.GetName().Name} → {string.Join(", ", profileRefs)}");
            }
        }

        // Дополняет структурный DependencyRulesTests (граф .csproj) проверкой уже СКОМПИЛИРОВАННЫХ метаданных:
        // ловит и транзитивную/случайную ссылку ядра на профиль, а не только прямую в csproj.
        offenders.ShouldBeEmpty("Сборка ядра ссылается на профиль (нарушение нейтральности): " + string.Join("; ", offenders));
    }

    [Fact(DisplayName = "Доменная модель НПА перенесена в профиль (Inspector.Domain)")]
    public void Npa_model_lives_in_inspector_profile()
    {
        var typeNames = typeof(LegalNorm).Assembly.GetTypes().Select(t => t.Name).ToArray();

        typeNames.ShouldContain(nameof(LegalNorm));
        typeNames.ShouldContain(nameof(NormRevision));
        typeNames.ShouldContain("RevisionStatus");
    }

    // Сборки ядра — все ISC.AI.*.dll из каталога вывода теста, кроме хоста Web, профилей и тестов.
    private static Assembly[] LoadCoreAssemblies()
    {
        var baseDir = AppContext.BaseDirectory;
        return Directory.GetFiles(baseDir, "ISC.AI.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsCoreLibraryName)
            .Distinct(StringComparer.Ordinal)
            .Select(name => Assembly.LoadFrom(Path.Combine(baseDir, name + ".dll")))
            .ToArray();
    }

    private static bool IsCoreLibraryName(string? name) =>
        name is not null
        && name.StartsWith("ISC.AI.", StringComparison.Ordinal)
        && name != "ISC.AI.Web"
        && !name.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal)
        && !name.StartsWith("ISC.AI.UnitTests", StringComparison.Ordinal)
        && !name.StartsWith("ISC.AI.IntegrationTests", StringComparison.Ordinal)
        && !name.StartsWith("ISC.AI.Evals", StringComparison.Ordinal);

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static void CheckName(string location, string name, List<string> offenders)
    {
        if (ForbiddenNpaIdentifiers.Any(forbidden => name.Contains(forbidden, StringComparison.Ordinal)))
        {
            offenders.Add(location);
        }
    }
}
