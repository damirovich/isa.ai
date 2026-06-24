namespace ISC.AI.Abstractions.Audit;

/// <summary>
/// Тип аудируемого действия (ТБ-030). Журнал фиксирует все обращения и генерации; перечень действий
/// доменно-нейтрален и общий для любого профиля.
/// </summary>
public enum AuditAction
{
    /// <summary>Вход в систему.</summary>
    Login = 0,

    /// <summary>Просмотр материала/фрагмента.</summary>
    View = 1,

    /// <summary>Поисковый/RAG-запрос.</summary>
    Search = 2,

    /// <summary>Генерация ответа/документа моделью.</summary>
    Generate = 3,

    /// <summary>Экспорт результата (например, в .docx).</summary>
    Export = 4,

    /// <summary>Печать.</summary>
    Print = 5,

    /// <summary>Загрузка/индексация в корпус.</summary>
    Ingest = 6,
}
