using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Русские названия статусов назначения (§4.2) — ЕДИНСТВЕННЫЙ источник этих строк в модуле.
/// </summary>
/// <remarks>
/// Заведено при переносе отчётов (этап 5): к тому моменту подписи статусов существовали уже в двух
/// копиях — в текстах уведомлений и в разметке интерфейса. Третья копия в отчётах означала бы, что
/// один и тот же статус называется в письме, на экране и в выгрузке по-разному, а расхождение
/// заметят не разработчики, а читатель отчёта. Подписи домена — часть домена, а не оформления.
/// </remarks>
public static class AssignmentStatusNames
{
    /// <summary>Название статуса.</summary>
    public static string Of(AssignmentStatus status) => status switch
    {
        AssignmentStatus.Registered => "Зарегистрировано",
        AssignmentStatus.InControl => "Контроль",
        AssignmentStatus.InProgress => "В работе",
        AssignmentStatus.PartiallyDone => "Частично исполнено",
        AssignmentStatus.Done => "Исполнено",
        AssignmentStatus.Overdue => "Просрочено",
        AssignmentStatus.Closed => "Снято с контроля",
        _ => status.ToString(),
    };
}
