using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Чтение базы Access драйвером Microsoft Access Database Engine (ACE OLE DB, x64) только на чтение (ADR-0043 п. 1).
/// </summary>
/// <remarks>
/// <para>
/// ТОЛЬКО ЧТЕНИЕ. Соединение открывается с <c>Mode=Read</c>, запросы — только <c>SELECT</c> по именам таблиц и
/// столбцов, которые есть в схеме файла (имя из запроса пользователя в текст SQL без сверки со схемой не попадает).
/// Читается временная копия, а не файл в хранилище: исходник цепочки хранения (ТБ-077) драйвер не трогает.
/// </para>
/// <para>
/// ВРЕМЕННАЯ КОПИЯ — полный учёт отдела со сведениями о лицах, поэтому она лежит в ОДНОМ известном каталоге
/// (<see cref="DefaultRoot"/>), удаляется закрытием базы, а то, что пережило аварийную остановку хоста, удаляет
/// <see cref="SweepStale"/> при старте (как копии конвейеров «Медиа», ТБ-064). Пул сеансов OLE DB отключён
/// (<c>OLE DB Services=-4</c>): иначе закрытое соединение держало бы файл, и копия не удалилась бы.
/// </para>
/// <para>
/// ДРАЙВЕР. Ищется «Microsoft.ACE.OLEDB.16.0», затем «12.0» — оба ставит бесплатный «Microsoft Access Database Engine
/// Redistributable» x64 (и Office x64). Разрядность драйвера должна совпадать с процессом хоста (x64). Нет драйвера
/// или сервер не Windows — <see cref="UnavailableReason"/> говорит, что поставить, экран переноса закрыт.
/// </para>
/// </remarks>
public sealed class OleDbAccessDatabaseReader : IAccessDatabaseReader
{
    /// <summary>Имя управляемого каталога временных копий внутри %TEMP%.</summary>
    public const string FolderName = "iscai-access";

    /// <summary>Отказ: сервер не Windows.</summary>
    public const string NotWindowsMessage =
        "Перенос из Access работает только на сервере Windows: драйвер Microsoft Access Database Engine есть только под Windows.";

    /// <summary>Отказ: нет драйвера ACE x64.</summary>
    public const string NoDriverMessage =
        "На сервере не установлен драйвер Microsoft Access Database Engine x64 (ACE OLE DB 16.0 или 12.0). "
        + "Установите «Microsoft Access Database Engine 2016 Redistributable» (x64) из комплекта поставки — "
        + "см. «Развёртывание профиля Следствие», перенос баз Access.";

    /// <summary>Отказ: файл не база Access или повреждён.</summary>
    public const string NotDatabaseMessage =
        "Файл не открывается как база Access (.accdb, .mdb): он повреждён или это другой формат.";

    /// <summary>Отказ: база защищена паролем.</summary>
    public const string PasswordMessage =
        "База защищена паролем. Откройте её в Access, снимите пароль (Файл → Сведения → Расшифровать базу данных) "
        + "и загрузите эту копию.";

    // Повторы удаления копии: движок ACE закрывает файл в течение долей секунды после закрытия соединения.
    private const int DeleteAttempts = 20;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Lazy<(string? Provider, string? Reason)> _provider = new(DetectProvider);
    private readonly string _root;

    /// <summary>Временные копии — в <see cref="DefaultRoot"/>.</summary>
    public OleDbAccessDatabaseReader()
        : this(DefaultRoot)
    {
    }

    /// <summary>Временные копии — в <paramref name="root"/> (тесты работают в своём каталоге).</summary>
    public OleDbAccessDatabaseReader(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <summary>Провайдеры ACE в порядке предпочтения.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0"];

    /// <summary>Каталог временных копий по умолчанию: <c>%TEMP%\iscai-access</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(Path.GetTempPath(), FolderName);

    /// <inheritdoc />
    public string? UnavailableReason => _provider.Value.Reason;

    /// <summary>
    /// Удаляет все временные копии в <paramref name="root"/> — вызывается при старте хоста, когда живых переносов
    /// в этом процессе ещё нет. Возвращает число удалённых файлов; занятые файлы пропускаются.
    /// </summary>
    public static int SweepStale(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Файл держит другой процесс — удалится следующим стартом; путь известен (каталог один).
            }
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<IAccessDatabase> OpenAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var provider = _provider.Value.Provider
            ?? throw new AccessDatabaseReadException(_provider.Value.Reason ?? NoDriverMessage);
        if (!OperatingSystem.IsWindows())
        {
            throw new AccessDatabaseReadException(NotWindowsMessage);
        }

        // Расширение — только из двух известных: оно уходит в имя файла, а формат драйвер всё равно берёт из содержимого.
        var safeExtension = string.Equals(extension, ".mdb", StringComparison.OrdinalIgnoreCase) ? ".mdb" : ".accdb";
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + safeExtension);
        try
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (file)
            {
                await content.CopyToAsync(file, cancellationToken);
            }

            var connection = Open(provider, path);
            try
            {
                return new OleDbAccessDatabase(connection, path, ReadTables(connection));
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch
        {
            await DeleteCopyAsync(path);
            throw;
        }
    }

    /// <summary>
    /// Удаляет копию и файл блокировки (<c>.laccdb</c>/<c>.ldb</c>), который драйвер создаёт рядом даже при чтении.
    /// Движок ACE отпускает файл не сразу после закрытия соединения, поэтому удаление повторяется с короткими паузами;
    /// не вышло — файл остаётся до уборки при старте (<see cref="SweepStale"/>): каталог один и известен.
    /// </summary>
    internal static async Task DeleteCopyAsync(string path)
    {
        string[] files = [path, Path.ChangeExtension(path, ".laccdb"), Path.ChangeExtension(path, ".ldb")];
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            var left = 0;
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }

            if (left == 0)
            {
                return;
            }

            await Task.Delay(DeleteRetryDelay);
        }
    }

    private static (string? Provider, string? Reason) DetectProvider()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, NotWindowsMessage);
        }

        try
        {
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var sources = OleDbEnumerator.GetRootEnumerator();
            while (sources.Read())
            {
                if (sources["SOURCES_NAME"] is string name)
                {
                    registered.Add(name);
                }
            }

            var provider = Providers.FirstOrDefault(registered.Contains);
            return provider is null ? (null, NoDriverMessage) : (provider, null);
        }
        catch (Exception exception) when (exception is OleDbException or InvalidOperationException or PlatformNotSupportedException)
        {
            return (null, NoDriverMessage);
        }
    }

    [SupportedOSPlatform("windows")]
    private static OleDbConnection Open(string provider, string path)
    {
        var builder = new OleDbConnectionStringBuilder { Provider = provider, DataSource = path };
        builder["Mode"] = "Read";
        builder["Persist Security Info"] = false;
        builder["OLE DB Services"] = -4;

        var connection = new OleDbConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (OleDbException exception)
        {
            connection.Dispose();
            throw new AccessDatabaseReadException(Explain(exception), exception);
        }
        catch (InvalidOperationException exception)
        {
            // Провайдер пропал после проверки (снят драйвер) — та же инструкция, что без драйвера.
            connection.Dispose();
            throw new AccessDatabaseReadException(NoDriverMessage, exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<AccessTable> ReadTables(OleDbConnection connection)
    {
        var tables = new List<AccessTable>();
        using var tableRows = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, [null, null, null, "TABLE"])
            ?? throw new AccessDatabaseReadException(NotDatabaseMessage);
        foreach (DataRow tableRow in tableRows.Rows)
        {
            if (tableRow["TABLE_NAME"] is not string name
                || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith('~'))
            {
                continue;
            }

            using var columnRows = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, [null, null, name, null]);
            var columns = columnRows is null
                ? []
                : columnRows.Rows.Cast<DataRow>()
                    .OrderBy(r => Convert.ToInt64(r["ORDINAL_POSITION"], CultureInfo.InvariantCulture))
                    .Select(ToColumn)
                    .ToList();
            tables.Add(new AccessTable(name, columns));
        }

        return tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [SupportedOSPlatform("windows")]
    private static AccessColumn ToColumn(DataRow row)
    {
        // DBCOLUMNFLAGS_ISLONG: MEMO и OLE-поля Access.
        const long IsLongFlag = 0x80;

        var name = (string)row["COLUMN_NAME"];
        var type = (OleDbType)Convert.ToInt32(row["DATA_TYPE"], CultureInfo.InvariantCulture);
        var flags = row["COLUMN_FLAGS"] is DBNull ? 0L : Convert.ToInt64(row["COLUMN_FLAGS"], CultureInfo.InvariantCulture);
        var isLong = (flags & IsLongFlag) != 0;
        var (label, binary) = type switch
        {
            OleDbType.WChar or OleDbType.VarWChar or OleDbType.Char or OleDbType.VarChar or OleDbType.BSTR
                => (isLong ? "длинный текст" : "текст", false),
            OleDbType.LongVarWChar or OleDbType.LongVarChar => ("длинный текст", false),
            OleDbType.Integer or OleDbType.SmallInt or OleDbType.TinyInt or OleDbType.UnsignedTinyInt or OleDbType.BigInt
                or OleDbType.Numeric or OleDbType.Decimal or OleDbType.Double or OleDbType.Single or OleDbType.Currency
                => ("число", false),
            OleDbType.Date or OleDbType.DBDate or OleDbType.DBTimeStamp or OleDbType.DBTime => ("дата", false),
            OleDbType.Boolean => ("да/нет", false),
            OleDbType.Binary or OleDbType.VarBinary or OleDbType.LongVarBinary
                => (isLong || type == OleDbType.LongVarBinary ? "объект OLE" : "двоичное", true),
            OleDbType.Guid => ("код репликации", false),
            _ => (type.ToString(), false),
        };
        return new AccessColumn(name, label, binary);
    }

    [SupportedOSPlatform("windows")]
    private static string Explain(OleDbException exception)
    {
        // Неверный пароль у ACE — DB_SEC_E_AUTH_FAILED (0x80040E4D) и ошибка Jet 3031; остальное при открытии
        // (3343 «нераспознаваемый формат» и др.) — не база или повреждена. Текст драйвера локализован и зависит от
        // версии Office, поэтому опора — коды, а не слова.
        const int AuthorizationFailed = unchecked((int)0x80040E4D);
        return exception.ErrorCode == AuthorizationFailed
            || exception.Errors.Cast<OleDbError>().Any(e => e.SQLState == "3031")
            ? PasswordMessage
            : NotDatabaseMessage;
    }

    /// <summary>Открытая база: соединение только на чтение поверх временной копии.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class OleDbAccessDatabase(OleDbConnection connection, string path, IReadOnlyList<AccessTable> tables)
        : IAccessDatabase
    {
        private bool _disposed;

        /// <inheritdoc />
        public IReadOnlyList<AccessTable> Tables => tables;

        /// <inheritdoc />
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
            string table,
            IReadOnlyCollection<string> columns,
            string? orderBy,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(table);
            ArgumentNullException.ThrowIfNull(columns);
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Имена — только из схемы файла: так в текст запроса не попадает ничего, кроме реальных имён таблицы и
            // столбцов, а квадратные скобки в них Access и так запрещает.
            var schema = tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.Ordinal))
                ?? throw new AccessDatabaseReadException($"В базе нет таблицы «{table}».");
            var known = schema.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var selected = columns.Distinct(StringComparer.Ordinal).ToList();
            if (selected.Count == 0)
            {
                yield break;
            }

            foreach (var name in selected.Append(orderBy).OfType<string>())
            {
                if (!known.Contains(name) || name.Contains('[', StringComparison.Ordinal) || name.Contains(']', StringComparison.Ordinal))
                {
                    throw new AccessDatabaseReadException($"В таблице «{table}» нет столбца «{name}».");
                }
            }

            using var command = connection.CreateCommand();
            var order = orderBy is null ? string.Empty : " ORDER BY " + Quote(orderBy);
            command.CommandText = $"SELECT {string.Join(", ", selected.Select(Quote))} FROM {Quote(schema.Name)}{order}";

            DbDataReader reader;
            try
            {
                reader = await command.ExecuteReaderAsync(cancellationToken);
            }
            catch (OleDbException exception)
            {
                throw new AccessDatabaseReadException($"Таблицу «{table}» не удалось прочитать.", exception);
            }

            await using (reader)
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(selected.Count, StringComparer.Ordinal);
                    for (var i = 0; i < selected.Count; i++)
                    {
                        row[selected[i]] = Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
                    }

                    yield return row;
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                connection.Dispose();
                await DeleteCopyAsync(path);
            }
        }

        private static object? Normalize(object? value) => value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            byte[] { Length: 0 } => null,
            _ => value,
        };

        private static string Quote(string name) => "[" + name + "]";
    }
}
