using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Диалог чата по материалам дел (<c>investigation.case_chat</c>, ТФ-ДДЛ-02, ADR-0051): вопросы сотрудника к материалам
/// ОДНОГО дела или всех доступных ему дел и ответы локальной модели. Личный: читает и ведёт его только владелец; диалог
/// по делу — только пока владелец видит само дело.
/// </summary>
/// <remarks>
/// РЕЖИМ. Переписка — материал дел (ДСП): поэтому она хранится в схеме профиля, а не в общих диалогах ядра. Диалог по
/// делу уходит с делом каскадом (ADR-0025, ТБ-064); диалог по всем делам уничтожается вместе с любым делом, на материалы
/// которого опирался хоть один его ответ (источники хранят дело). Гриф диалога — не ниже грифа дела (для диалога по делу)
/// и поднимается до грифа самого «тяжёлого» ответа (наследование, ТБ-032/033).
/// </remarks>
public class CaseChat : BaseEntity
{
    /// <summary>Дело (FK; уничтожение дела уносит диалог) или <see langword="null"/> — диалог по всем доступным делам.</summary>
    public int? CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Владелец — сотрудник (идентификатор учётной записи ядра; FK через границу схем нет).</summary>
    public int OwnerUserId { get; set; }

    /// <summary>Заголовок (начало первого вопроса или название, данное владельцем).</summary>
    public required string Title { get; set; }

    /// <summary>Гриф диалога: максимум грифа дела и грифов ответов. NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Сообщения (каскадно удаляются с диалогом).</summary>
    public ICollection<CaseChatMessage> Messages { get; set; } = [];
}

/// <summary>Сообщение диалога чата по делу (<c>investigation.case_chat_message</c>): вопрос сотрудника или ответ модели.</summary>
/// <remarks>
/// У ответа хранятся источники (какой фрагмент из какого документа дела) и итог проверки ссылок — чтобы после
/// перезагрузки страницы ответ показывался с теми же источниками и предупреждениями, что и в момент получения.
/// </remarks>
public class CaseChatMessage : BaseEntity
{
    /// <summary>Диалог (FK внутри схемы).</summary>
    public int ChatId { get; set; }

    /// <summary>Навигация к диалогу.</summary>
    public CaseChat? Chat { get; set; }

    /// <summary>Автор: сотрудник или ассистент.</summary>
    public ConversationMessageRole Role { get; set; }

    /// <summary>Текст.</summary>
    public required string Text { get; set; }

    /// <summary>Гриф: у ответа — максимум грифов использованных фрагментов, у вопроса — гриф дела.</summary>
    public short Classification { get; set; }

    /// <summary>Источники ответа (jsonb, <c>CaseChatSource</c>); у вопроса — <see langword="null"/>.</summary>
    public string? SourcesJson { get; set; }

    /// <summary>Итог проверки ссылок ответа (jsonb, <c>CaseChatCheck</c>); у вопроса — <see langword="null"/>.</summary>
    public string? CheckJson { get; set; }
}
