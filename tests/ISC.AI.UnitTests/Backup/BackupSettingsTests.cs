using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ISC.AI.Abstractions.Backup;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Persistence.Backup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Backup;

/// <summary>
/// Что входит в резервную копию (ТБ-082, ADR-0063): базы, которые зарегистрировали слои данных поставки (одна копия на
/// сервер, порт и базу; строки других профилей из общего файла настроек не попадают), хранилище ядра и каталоги файлов
/// пакетов (документооборот регистрирует свой), полные пути и отсев повторов; имя каталога, повтор имени и вложенные
/// каталоги — в «что не настроено» (копия не делается).
/// </summary>
public sealed class BackupSettingsTests
{
    private const string CoreDb = "Host=db1;Port=5432;Database=ISC_AI_INV;Username=postgres;Password=секрет";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "iscai-backup-settings");

    [Fact(DisplayName = "Базы: только зарегистрированные слоями данных, одинаковые сервер/порт/база — одна; строки других профилей из настроек — нет")]
    public void Databases_come_from_registrations()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Storage:BasePath"] = Path.Combine(Root, "storage"),
            ["ConnectionStrings:Inspector"] = "Host=10.10.0.115;Database=ISC_AI;Username=postgres",
        });

        var settings = BackupSettings.From(configuration,
            [new BackupDatabase("core", CoreDb), new BackupDatabase("media", CoreDb), new BackupDatabase("other", "Host=db2;Database=ISC_AI_INV;Username=postgres")]);

        settings.Databases.Select(d => (d.Host, d.Database)).ShouldBe([("db1", "ISC_AI_INV"), ("db2", "ISC_AI_INV")]);
        settings.Problems().ShouldNotContain(p => p.Contains("Нет баз", StringComparison.Ordinal));
        BackupSettings.From(configuration, []).Problems().ShouldContain(p => p.Contains("Нет баз", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "База для копии в журнале и отладчике — без строки подключения: в ней пароль (ТБ-013)")]
    public void Database_registration_hides_password() =>
        new BackupDatabase("core", CoreDb).ToString().ShouldNotContain("секрет");

    [Fact(DisplayName = "Каталоги файлов: первым — хранилище ядра, затем пакетов; полные пути, одинаковая регистрация — один раз")]
    public void File_roots_core_first_and_distinct()
    {
        var docflow = new BackupFileRoot("docflow", Path.Combine(Root, "docflow-files") + Path.DirectorySeparatorChar);

        var settings = Settings(docflow, docflow with { Path = Path.Combine(Root, "docflow-files") });

        settings.FileRoots.Select(r => r.Name).ShouldBe([BackupFileRoot.CoreName, "docflow"]);
        settings.FileRoots[1].Path.ShouldBe(Path.Combine(Root, "docflow-files"));
        settings.StorageRoot.ShouldBe(Path.Combine(Root, "storage"));
        RootProblems(settings).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Каталоги файлов: вложенный в хранилище ядра, тот же каталог, неверное имя, повтор имени — что не настроено")]
    [InlineData("docflow", "storage/docflow", "совпадают или вложены")]
    [InlineData("docflow", "storage", "совпадают или вложены")]
    [InlineData("DocFlow", "docflow-files", "латиница в нижнем регистре")]
    [InlineData("core", "other", "зарегистрирован дважды")]
    public void File_root_problems(string name, string relative, string problem)
    {
        var settings = Settings(new BackupFileRoot(name, Path.Combine(Root, relative)));

        RootProblems(settings).ShouldContain(p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Документооборот регистрирует свою базу и свой каталог файлов (DocFlow:Storage:BasePath) — оба попадают в копию")]
    public void DocFlow_registers_database_and_file_root()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Core"] = "Host=localhost;Database=isc;Username=postgres",
            ["DocFlow:Storage:BasePath"] = Path.Combine(Root, "docflow-files"),
        });
        using var provider = new ServiceCollection().AddDocFlowPersistence(configuration).BuildServiceProvider();

        var roots = provider.GetServices<BackupFileRoot>().ToList();
        var databases = provider.GetServices<BackupDatabase>().ToList();

        roots.ShouldBe([new BackupFileRoot(LocalDocFlowFileStorage.BackupName, Path.Combine(Root, "docflow-files"))]);
        databases.Select(d => d.Name).ShouldBe(["docflow"]);
        var settings = BackupSettings.From(configuration, databases, roots);
        settings.FileRoots.Select(r => r.Name).ShouldBe([BackupFileRoot.CoreName, "docflow"]);
        settings.Databases.Single().Database.ShouldBe("isc");
    }

    private static BackupSettings Settings(params BackupFileRoot[] packageRoots) =>
        BackupSettings.From(
            Configuration(new Dictionary<string, string?> { ["Storage:BasePath"] = Path.Combine(Root, "storage") }),
            [new BackupDatabase("core", CoreDb)], packageRoots);

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // Только замечания о каталогах файлов: ключ, носитель и базы в этих тестах не заданы намеренно.
    private static List<string> RootProblems(BackupSettings settings) =>
        settings.Problems().Where(p => p.StartsWith("Каталог", StringComparison.Ordinal)).ToList();
}
