using ISC.AI.Identity.Skid;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pgvector.EntityFrameworkCore;

// Перенос учёток из БД СКИД в core.app_user (Э4-35 §6.5, шаг 2).
//
// ЗАПУСК:  dotnet run --project src/tools/ISC.AI.Identity.Import
//
// СЕКРЕТЫ. Команда НЕ принимает пароли аргументами и не печатает их: строки подключения собираются
// тем же ConnectionStringResolver, что и в приложении, из конфигурации и переменных окружения.
// Пароль чужой БД обязателен ЯВНО (Database:Passwords:Skid) — общий пароль нашей БД туда не
// подставляется молча (ТБ-013), иначе забытый секрет отправил бы наш пароль на сервер СКИД.
//
// БЕЗОПАСНОСТЬ ПОВТОРНОГО ЗАПУСКА: уже перенесённый пароль не перезаписывается (см. SkidIdentityImporter).
// Поэтому команду можно запускать повторно — она не откатит пароли тем, кто уже сменил их у нас.

// Топология берётся из конфига ХОСТА — единственного места, где она описана (ConnectionStrings:Core
// и ConnectionStrings:Skid). Дублировать её в инструменте нельзя: разъедется при первом же переезде
// стенда. Порядок поиска: ISCAI_CONFIG_DIR → конфиг хоста, найденный подъёмом вверх по дереву.
//
// Подъём начинается и от папки СБОРКИ, и от текущей: `dotnet run --project X` ставит рабочую папку
// в папку ПРОЕКТА, а не туда, откуда запускали, — на этом первая версия и споткнулась.
var configDirectory = Environment.GetEnvironmentVariable("ISCAI_CONFIG_DIR")
    ?? FindHostConfigDirectory(AppContext.BaseDirectory)
    ?? FindHostConfigDirectory(Directory.GetCurrentDirectory());

if (configDirectory is null)
{
    Console.Error.WriteLine(
        "Не найден конфиг хоста (src/core/ISC.AI.Web/appsettings.json). "
        + "Укажите папку явно: $env:ISCAI_CONFIG_DIR = \"<путь к src/core/ISC.AI.Web>\".");
    return 1;
}

Console.WriteLine($"Конфигурация: {configDirectory}");

// Ищет папку конфига хоста, поднимаясь от заданной вверх до корня диска.
static string? FindHostConfigDirectory(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "src", "core", "ISC.AI.Web");
        if (File.Exists(Path.Combine(candidate, "appsettings.json")))
        {
            return candidate;
        }
    }

    return null;
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(configDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Development.json", optional: true)
    // Секреты — ТОЛЬКО отсюда: Database__Password (наша БД) и Database__Passwords__Skid (чужая).
    // Переменные окружения перекрывают файл, поэтому пароли в репозиторий не попадают (Э4-10).
    .AddEnvironmentVariables()
    .Build();

try
{
    var skidConnection = ConnectionStringResolver.ResolveExternal(configuration, "Skid");
    var coreConnection = ConnectionStringResolver.Resolve(configuration, "Core");

    var importer = new SkidIdentityImporter(
        new SkidContextFactory(skidConnection),
        new CoreContextFactory(coreConnection));

    Console.WriteLine("Перенос учёток из БД СКИД…");
    var result = await importer.ImportAsync();

    Console.WriteLine($"  прочитано в СКИД:        {result.Read}");
    Console.WriteLine($"  создано учёток:          {result.Created}");
    Console.WriteLine($"  перенесено паролей:      {result.PasswordsImported}");
    Console.WriteLine($"  пропущено (пароль уже локальный): {result.SkippedAlreadyLocal}");
    Console.WriteLine($"  пропущено (нет хеша в СКИД):      {result.SkippedNoHash}");
    Console.WriteLine();
    Console.WriteLine("Готово. Проверьте вход, и только потом переключайте Auth:Provider=Local.");
    return 0;
}
catch (Exception ex)
{
    // Текст исключения печатается, СТРОКА ПОДКЛЮЧЕНИЯ — нет: в ней пароль.
    Console.Error.WriteLine($"Перенос не выполнен: {ex.Message}");
    return 1;
}

/// <summary>Фабрика read-only контекста СКИД для команды переноса.</summary>
internal sealed class SkidContextFactory(string connectionString) : IDbContextFactory<SkidDbContext>
{
    public SkidDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<SkidDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>Фабрика контекста ядра (приёмник переноса).</summary>
internal sealed class CoreContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString, npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);

                // ОБЯЗАТЕЛЬНО, хотя перенос учёток векторов не касается: в модели ядра есть колонка
                // эмбеддингов (ТО-инф-02), и без маппинга pgvector контекст не строится ЦЕЛИКОМ —
                // падает ещё до первого запроса. Та же строка стоит в дизайн-фабрике ядра.
                npg.UseVector();
            })
            .UseSnakeCaseNamingConvention()
            .Options);
}
