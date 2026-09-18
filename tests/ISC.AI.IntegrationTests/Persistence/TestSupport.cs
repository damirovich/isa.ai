using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

// Общая обвязка интеграционных тестов. Раньше каждый тестовый класс держал СВОЮ копию этих
// заглушек (набралось 33 копии пяти сущностей) — при смене строки подключения или контракта
// хранилища пришлось бы править десятки файлов, и копии уже начали расходиться в мелочах
// (текст исключения, имя временной папки). Здесь — единственный экземпляр каждой.
// Тестовые дублёры с СОБСТВЕННЫМ поведением, на котором построена проверка (например,
// RecordingStorage в DocFlowDeletionTests, запоминающий удалённые файлы), остаются локальными —
// их смысл виден только рядом с тестом.

/// <summary>
/// Единственное место, где записан docker-образ тестовой БД. Раньше имя образа было захардкожено
/// в каждом тестовом классе (38 копий): переход на новый Postgres означал бы правку всех файлов,
/// а опечатка в одном — тихую проверку на другой версии, чем у остальных.
/// Контейнер — ПО-ПРЕЖНЕМУ на каждый тестовый класс (изоляция дороже секунд старта).
/// </summary>
internal static class TestPostgres
{
    /// <summary>Образ с pgvector: ядро хранит эмбеддинги, обычного postgres недостаточно.</summary>
    public const string Image = "pgvector/pgvector:pg16";

    public static PostgreSqlContainer Create() => new PostgreSqlBuilder(Image).Build();
}

/// <summary>
/// Фабрика <see cref="CoreDbContext"/> для тестов над настоящим Postgres (Testcontainers,
/// EF InMemory запрещён стандартом). Настройки — те же, что в хосте: snake_case, история миграций
/// в схеме ядра и pgvector (ядро хранит эмбеддинги).
/// </summary>
internal sealed class CoreContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString, npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                npg.UseVector();
            })
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// Фабрика <see cref="DocFlowDbContext"/> (схема модуля документооборота). Без pgvector:
/// модуль эмбеддинги не хранит, индексация в корпус идёт через ядро.
/// </summary>
internal sealed class DocFlowContextFactory(string connectionString) : IDbContextFactory<DocFlowDbContext>
{
    public DocFlowDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<DocFlowDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// Фабрика <see cref="InspectorDbContext"/> (схема профиля «ИнспекторAI»).
/// </summary>
internal sealed class InspectorContextFactory(string connectionString) : IDbContextFactory<InspectorDbContext>
{
    public InspectorDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<InspectorDbContext>()
            .UseNpgsql(connectionString, npg =>
                npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// Хранилище файлов, которое ЛОМАЕТСЯ при любой попытке чтения или записи. Для тестов, где файлы
/// не участвуют: если проверяемый код неожиданно полез в хранилище — тест должен упасть громко,
/// а не молча пройти на пустой заглушке. Удаление — единственная безобидная операция (no-op),
/// иначе компенсирующая очистка в сценариях отката валила бы тест не по своей теме.
/// </summary>
internal sealed class NoFileStorage : IDocFlowFileStorage
{
    public Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Этот тест не работает с файлами: запись в хранилище не ожидалась.");

    public Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Этот тест не работает с файлами: чтение из хранилища не ожидалось.");

    public Task DeleteAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// Настоящее файловое хранилище во временной папке — для тестов, где файлы участвуют в сценарии
/// (загрузка версий, вложения, файлы переходов). Каждый экземпляр пишет в свой подкаталог
/// (<see cref="Guid.NewGuid()"/>), поэтому параллельные тестовые классы не пересекаются.
/// </summary>
internal sealed class TempFileStorage : IDocFlowFileStorage
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "iscai-integration-tests", Guid.NewGuid().ToString("N"));

    public async Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default)
    {
        var storedFileName = Guid.NewGuid().ToString("N") + extension;
        var directory = Path.Combine(_root, category, subPath);
        Directory.CreateDirectory(directory);
        await using var fileStream = File.Create(Path.Combine(directory, storedFileName));
        await content.CopyToAsync(fileStream, cancellationToken);
        return storedFileName;
    }

    public Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(File.OpenRead(Path.Combine(_root, category, subPath, storedFileName)));

    public Task DeleteAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, category, subPath, storedFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Детерминированный эмбеддер: каждому тексту — один и тот же единичный вектор [1, 0, …, 0]
/// нужной размерности. Тестам индексации важно, ЧТО попало в корпус и с какими грифами,
/// а не осмысленность векторов; настоящая модель (ADR-0011) в контуре тестов недоступна.
/// </summary>
internal sealed class FixedEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ReadOnlyMemory<float> _vector = BuildVector(dimensions);

    private static float[] BuildVector(int dimensions)
    {
        var values = new float[dimensions];
        values[0] = 1f;
        return values;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(_ => new Embedding<float>(_vector)).ToList()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// Фабрика <see cref="MediaDbContext"/> (схема пакета «Медиа»). С pgvector: шаблоны лиц — векторы.
/// </summary>
internal sealed class MediaContextFactory(string connectionString) : IDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<MediaDbContext>()
            .UseNpgsql(connectionString, npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.Schema);
                npg.UseVector();
            })
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// Хранилище файлов ядра (<see cref="IFileStorage"/>), запоминающее удалённые файлы — для проверки,
/// что гарантированное удаление носителя доходит до диска (GATE-6). Чтение/запись не поддерживаются.
/// </summary>
internal sealed class RecordingFileStorage : IFileStorage
{
    public List<string> Deleted { get; } = [];

    public Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Guid.NewGuid().ToString("N") + extension);

    public Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Этот тест не читает файлы.");

    public Task DeleteAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
    {
        Deleted.Add($"{category}/{subPath}/{storedFileName}");
        return Task.CompletedTask;
    }
}
