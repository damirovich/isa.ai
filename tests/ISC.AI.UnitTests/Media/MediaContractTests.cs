using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Внешний контракт пакета модулей «Медиа» (ЭС2-03): что обязан дать подключающий профиль/хост, и
/// что пакет закрывает сам. Тот же принцип, что у <c>DocFlowContractTests</c>: список обязанностей
/// эксплуатанта сверяется с фактом регистрации, а не со словами.
/// </summary>
public sealed class MediaContractTests
{
    [Fact(DisplayName = "Порты домена пакета закрыты слоем данных и интеграцией; внешние — только нейтральные службы ядра")]
    public void All_domain_ports_are_registered_by_the_package_itself()
    {
        var services = new ServiceCollection();
        MediaModule.RegisterServices(services, Configuration());
        MediaModule.RegisterDataContexts(services, Configuration());
        var registered = services.Select(descriptor => descriptor.ServiceType).ToHashSet();

        var declared = typeof(IFaceSearch).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == "ISC.AI.Modules.Media.Domain.Services")
            .ToList();
        declared.ShouldNotBeEmpty();

        var unregistered = declared.Where(port => !registered.Contains(port)).Select(port => port.Name).ToList();
        unregistered.ShouldBeEmpty(
            "Порт домена пакета «Медиа» объявлен, но никем не реализован: " + string.Join(", ", unregistered));

        // Обязанности эксплуатанта — ТОЛЬКО нейтральные службы ядра (ADR-0018), ни одного порта домена.
        MediaModule.RequiredServices.ShouldAllBe(type => type.Namespace!.StartsWith("ISC.AI.Abstractions", StringComparison.Ordinal));
        MediaModule.RequiredServices.ShouldContain(typeof(Abstractions.Storage.IFileStorage));
        MediaModule.RequiredServices.ShouldContain(typeof(IAccessPolicy));
        MediaModule.RequiredServices.Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Ключи конфигурации пакета: строка подключения и пути моделей с пинами SHA-256 перечислены")]
    public void Configuration_keys_are_documented()
    {
        MediaModule.ConfigurationKeys.ShouldContain("ConnectionStrings:Media");
        MediaModule.ConfigurationKeys.ShouldContain("Vision:Detector:Sha256");
        MediaModule.ConfigurationKeys.ShouldContain("Vision:Embedder:Sha256");
        MediaModule.ConfigurationKeys.ShouldContain(MediaPersistenceServiceCollectionExtensions.EfSearchKey);
    }

    [Fact(DisplayName = "Раздача файлов: описание файла — режимный ресурс, floor ядра применим к нему напрямую")]
    public void File_descriptor_is_classified_and_baseline_filter_applies()
    {
        var file = new MediaFileDescriptor("a.jpg", MediaFileCategories.Originals, "1", "image/jpeg", Classification: 2, DivisionId: 7);

        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [7])).Compile()(file).ShouldBeTrue();
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 1, [7])).Compile()(file).ShouldBeFalse(); // выше допуска
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [8])).Compile()(file).ShouldBeFalse(); // чужое подразделение
        BaselineAccess.Filter<MediaFileDescriptor>(new AccessContext("u", 2, [])).Compile()(file).ShouldBeFalse();  // fail-closed
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
