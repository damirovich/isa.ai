using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Abstractions.Exchange;

/// <summary>
/// Пакет обмена между экземплярами системы (ТФ-ОБМ-01, ТБ-083, ТО-инф-14, ADR-0068) — порт ядра: ключи по закреплённым
/// отпечаткам, сборка запечатанного пакета на носителе обмена, уборка недописанных пакетов. Профиль даёт содержимое и
/// описание отбора, ядро — формат, шифрование и подпись.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-083): шифрование для ключа получателя, подпись манифеста ключом источника, отпечатки обоих ключей и список
/// файлов с размерами и SHA-256 записывает в манифест ЯДРО — по ключам, которыми оно шифровало и подписывало, и по файлам,
/// которые оно записало. Профиль не может ни подменить отпечаток, ни пропустить файл мимо манифеста, ни выбрать формат
/// резервной копии вместо формата обмена.
/// </remarks>
public interface IExchangePackages
{
    /// <summary>Отпечаток ключа — SHA-256 открытой части, HEX прописными (его печатает keygen).</summary>
    string Fingerprint(RSA key);

    /// <summary>
    /// Открытый ключ получателя из PEM-файла — только если его отпечаток совпал с закреплённым в настройке: подложенный на
    /// диск чужой ключ не примется.
    /// </summary>
    /// <exception cref="InvalidOperationException">Файла нет, в нём не ключ RSA, ключ короткий или отпечаток не совпал.</exception>
    RSA LoadRecipientKey(string pemPath, string pinnedSha256);

    /// <summary>
    /// Закрытый ключ подписи этого экземпляра из PEM под паролем хранителя — только с верным паролем и совпавшим отпечатком.
    /// Пароль не сохраняется.
    /// </summary>
    /// <exception cref="InvalidOperationException">Файла нет, пароль неверен (или файл повреждён), отпечаток не совпал.</exception>
    RSA LoadSigningKey(string pemPath, string pinnedSha256, ReadOnlySpan<char> password);

    /// <summary>
    /// Начинает пакет: каталог <c>&lt;имя&gt;.partial</c> на носителе. Пакет появляется под своим именем только целиком —
    /// при <see cref="IExchangePackageBuilder.CompleteAsync"/>; не завершённый пакет удаляется при освобождении.
    /// </summary>
    /// <param name="targetFolder">Носитель обмена (точка монтирования).</param>
    /// <param name="folderName">Имя каталога пакета: латиница, цифры, точка, дефис, подчёркивание.</param>
    /// <param name="recipientKey">Открытый ключ получателя — им шифруется каждый файл.</param>
    /// <exception cref="ArgumentException">Недопустимое имя каталога.</exception>
    /// <exception cref="IOException">Пакет с таким именем на носителе уже есть.</exception>
    IExchangePackageBuilder Create(string targetFolder, string folderName, RSA recipientKey);

    /// <summary>
    /// Удаляет с носителя недописанные пакеты (<c>&lt;префикс&gt;*.partial</c>) — после перезапуска хоста посреди сборки,
    /// чтобы обрывок никто не принял за пакет. Возвращает число удалённых.
    /// </summary>
    int RemoveUnfinished(string targetFolder, string folderPrefix);
}

/// <summary>Пакет в сборке: файлы по одному, затем манифест с подписью.</summary>
public interface IExchangePackageBuilder : IAsyncDisposable
{
    /// <summary>Имя каталога пакета на носителе.</summary>
    string FolderName { get; }

    /// <summary>Сколько байт записано в пакет (зашифрованных).</summary>
    long BytesWritten { get; }

    /// <summary>
    /// Шифрует поток ключом получателя в файл пакета <paramref name="name"/> (путь через «/», латиница, цифры, точка, дефис,
    /// подчёркивание; служебные имена манифеста заняты).
    /// </summary>
    /// <param name="name">Имя файла в пакете.</param>
    /// <param name="plaintext">Открытое содержимое.</param>
    /// <param name="progress">Ход: всего записано в пакет, байт.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="ArgumentException">Имя недопустимо или уже занято.</exception>
    Task AddAsync(string name, Stream plaintext, Action<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Пишет манифест (описание от профиля + отпечатки ключей и список файлов от ядра), подписывает его ключом источника и
    /// выставляет пакет под его именем. После этого пакет не меняется.
    /// </summary>
    Task<ExchangePackageSealed> CompleteAsync(
        ExchangeManifestContent content, RSA signingKey, CancellationToken cancellationToken = default);
}

/// <summary>Описание пакета для манифеста — то, что знает профиль.</summary>
/// <param name="Kind">Вид содержимого, например <c>isc-exchange</c>.</param>
/// <param name="SystemVersion">Версия системы-источника.</param>
/// <param name="SourceInstance">Номер экземпляра-источника.</param>
/// <param name="Period">Период пакета, «гггг-ММ».</param>
/// <param name="CreatedAt">Когда сформирован.</param>
/// <param name="CreatedByUserId">Кто сформировал.</param>
/// <param name="Classification">Наибольший гриф содержимого — маркировка пакета.</param>
/// <param name="Marking">Маркировка словами.</param>
/// <param name="Selection">Отбор, как его задал сотрудник.</param>
/// <param name="Counts">Счётчики содержимого.</param>
public sealed record ExchangeManifestContent(
    string Kind,
    string SystemVersion,
    string SourceInstance,
    string Period,
    DateTimeOffset CreatedAt,
    int? CreatedByUserId,
    short Classification,
    string Marking,
    IReadOnlyDictionary<string, string> Selection,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>Готовый пакет — для журнала выгрузок и акта в журнале аудита.</summary>
/// <param name="FolderName">Имя каталога пакета на носителе.</param>
/// <param name="Bytes">Объём файлов пакета (без манифеста), байт.</param>
/// <param name="FileCount">Число файлов в манифесте.</param>
/// <param name="RecipientKeyFingerprint">Отпечаток ключа, которым зашифрованы файлы.</param>
/// <param name="SigningKeyFingerprint">Отпечаток ключа, которым подписан манифест.</param>
/// <param name="ManifestSha256">SHA-256 байтов манифеста, как они лежат на носителе.</param>
public sealed record ExchangePackageSealed(
    string FolderName,
    long Bytes,
    int FileCount,
    string RecipientKeyFingerprint,
    string SigningKeyFingerprint,
    string ManifestSha256);
