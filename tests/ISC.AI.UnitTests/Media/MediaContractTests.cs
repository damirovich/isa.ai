using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Внешний контракт пакета модулей «Медиа» (ЭС2-03/ЭС3): что обязан дать подключающий профиль (порты
/// профиля), что — хост (нейтральные службы ядра), и что пакет закрывает сам. Тот же принцип, что у
/// <c>DocFlowContractTests</c>: список обязанностей сверяется с фактом регистрации, а не со словами.
/// </summary>
public sealed class MediaContractTests
{
    [Fact(DisplayName = "Незарегистрированные порты домена — ровно порты профиля из манифеста (ICaseScope, IMediaAdministration, IVerificationPolicy)")]
    public void Required_services_match_the_unregistered_ports()
    {
        var services = new ServiceCollection();
        MediaModule.RegisterServices(services, Configuration());
        MediaModule.RegisterDataContexts(services, Configuration());
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var declared = typeof(IFaceSearch).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == "ISC.AI.Modules.Media.Domain.Services")
            .ToList();
        declared.ShouldNotBeEmpty();

        var unregistered = declared.Where(port => !registered.Contains(port))
            .Select(port => port.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var documented = MediaModule.RequiredServices
            .Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();

        unregistered.ShouldBe(
            documented,
            "Список обязанностей профиля разошёлся с фактом: либо порт забыли зарегистрировать в пакете, "
            + "либо он внешний — и тогда его место в MediaModule.RequiredServices.");

        documented.Count.ShouldBe(3);
        MediaModule.RequiredServices.ShouldContain(typeof(ICaseScope));
        MediaModule.RequiredServices.ShouldContain(typeof(IMediaAdministration));
        MediaModule.RequiredServices.ShouldContain(typeof(IVerificationPolicy));
    }

    [Fact(DisplayName = "Обязанности хоста — только нейтральные службы ядра из Abstractions")]
    public void Required_core_services_are_neutral_core_ports()
    {
        MediaModule.RequiredCoreServices.ShouldAllBe(type => type.Namespace!.StartsWith("ISC.AI.Abstractions", StringComparison.Ordinal));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Storage.IFileStorage));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.Audit.IAuditWriter));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(IAccessPolicy));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(IAccessContextProvider));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(ISubjectProvider));
        MediaModule.RequiredCoreServices.ShouldContain(typeof(Abstractions.BackgroundTasks.IBackgroundTaskQueue));
        MediaModule.RequiredCoreServices.Count.ShouldBe(6);
    }

    [Fact(DisplayName = "Порты пакета не тянут типы профиля — нейтральность не на словах")]
    public void Ports_do_not_leak_profile_types()
    {
        var ports = typeof(IFaceSearch).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == "ISC.AI.Modules.Media.Domain.Services");

        var leaks = new List<string>();
        foreach (var port in ports)
        {
            foreach (var method in port.GetMethods())
            {
                var types = method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType);
                leaks.AddRange(types
                    .Select(type => type.Assembly.GetName().Name ?? string.Empty)
                    .Where(assembly => assembly.StartsWith("ISC.AI.Profile.", StringComparison.Ordinal))
                    .Select(assembly => $"{port.Name}.{method.Name} → {assembly}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Ключи конфигурации: подключение, модели с пинами SHA-256, параметры поиска и раскадровки перечислены")]
    public void Configuration_keys_are_documented()
    {
        MediaModule.ConfigurationKeys.ShouldContain("ConnectionStrings:Media");
        MediaModule.ConfigurationKeys.ShouldContain("Vision:Detector:Sha256");
        MediaModule.ConfigurationKeys.ShouldContain("Vision:Embedder:Sha256");
        MediaModule.ConfigurationKeys.ShouldContain(MediaPersistenceServiceCollectionExtensions.EfSearchKey);
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:CandidateListSize");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:MinCandidateListSize");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:MaxCandidateListSize");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:MaxCosineDistance");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:MaxAllowedCosineDistance");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Video:SampleFps");
        MediaModule.ConfigurationKeys.ShouldContain("Media:Search:ProbeCopyMaxBytes");
    }

    [Fact(DisplayName = "Настройки поиска: умолчания ТН-008 (20, 5..50), настроенный порог не дальше предела, зажим границ")]
    public void Search_options_defaults_and_clamping()
    {
        var defaults = MediaSearchOptions.Read(Configuration());
        defaults.CandidateListSize.ShouldBe(20);
        defaults.MinCandidateListSize.ShouldBe(5);
        defaults.MaxCandidateListSize.ShouldBe(50);
        defaults.MaxCosineDistance.ShouldBeNull();
        defaults.MaxAllowedCosineDistance.ShouldBe(0.8);
        defaults.EffectiveMaxCosineDistance(null).ShouldBeNull();

        var tuned = MediaSearchOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:Search:CandidateListSize"] = "100",
            ["Media:Search:MinCandidateListSize"] = "10",
            ["Media:Search:MaxCandidateListSize"] = "30",
            ["Media:Search:MaxCosineDistance"] = "0.9",
            ["Media:Search:MaxAllowedCosineDistance"] = "0.7",
        }).Build());
        tuned.CandidateListSize.ShouldBe(30);
        tuned.MaxCosineDistance.ShouldBe(0.7);
        tuned.ClampTopK(1).ShouldBe(10);
        tuned.ClampTopK(null).ShouldBe(30);
        tuned.EffectiveMaxCosineDistance(0.95).ShouldBe(0.7);
        tuned.EffectiveMaxCosineDistance(0.3).ShouldBe(0.3);
    }

    [Fact(DisplayName = "Раздача файлов: описание файла — режимный ресурс, floor ядра применим к нему напрямую")]
    public void File_descriptor_is_classified_and_baseline_filter_applies()
    {
        var file = new MediaFileDescriptor("a.jpg", MediaFileCategories.Originals, "1", "image/jpeg", Classification: 2, DivisionId: 7);

        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [7])).Compile()(file).ShouldBeTrue();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 1, [7])).Compile()(file).ShouldBeFalse();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [8])).Compile()(file).ShouldBeFalse();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [])).Compile()(file).ShouldBeFalse();
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Core"] = "Host=localhost;Database=test;Username=u",
            ["Vision:Detector:Path"] = "d.onnx",
            ["Vision:Detector:Sha256"] = "00",
            ["Vision:Embedder:Path"] = "e.onnx",
            ["Vision:Embedder:Sha256"] = "00",
        }).Build();
}
