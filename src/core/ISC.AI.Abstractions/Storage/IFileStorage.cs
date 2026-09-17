namespace ISC.AI.Abstractions.Storage;

/// <summary>
/// Нейтральный порт файлового хранилища ядра (ADR-0018): защищённое локальное хранение байтов
/// в изолированном контуре — исходники документов, носители медиа, вырезки. Хранилище ничего
/// не знает о смысле файла: имя в хранилище — случайное, исходное имя и режимные атрибуты
/// (гриф, подразделение) живут только в БД у владельца записи.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ: (1) выдача файла пользователю — ТОЛЬКО через эндпоинт владельца с проверкой решётки
/// доступа на выдаче (ТБ-020/021); порт сам доступ не проверяет и в веб напрямую не выходит;
/// (2) любой собранный путь остаётся под корнем хранилища — попытка выйти за корень (<c>..</c>,
/// абсолютный путь) отклоняется явно; (3) удаление идемпотентно: компенсация после сбоя БД
/// (байты записаны, строка не создалась) не должна падать на «файла уже нет» (ТБ-064).
/// Сигнатура повторяет модульный порт документооборота, чтобы модуль позже перешёл на общий порт
/// без изменения вызовов.
/// </remarks>
public interface IFileStorage
{
    /// <summary>Сохраняет содержимое; возвращает сгенерированное имя файла в хранилище (случайное + расширение).</summary>
    /// <param name="content">Байты файла.</param>
    /// <param name="extension">Расширение с точкой (например, <c>.mp4</c>); сохраняется у случайного имени.</param>
    /// <param name="category">Категория (каталог первого уровня) — задаёт владелец.</param>
    /// <param name="subPath">Подкаталог внутри категории (например, идентификатор владельца).</param>
    /// <param name="cancellationToken">Отмена копирования.</param>
    Task<string> SaveAsync(
        Stream content, string extension, string category, string subPath,
        CancellationToken cancellationToken = default);

    /// <summary>Открывает файл на чтение. <see cref="FileNotFoundException"/> — файла нет.</summary>
    Task<Stream> OpenReadAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет файл; отсутствие файла — не ошибка (идемпотентная компенсация).</summary>
    Task DeleteAsync(
        string storedFileName, string category, string subPath,
        CancellationToken cancellationToken = default);
}
