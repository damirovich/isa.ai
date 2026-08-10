using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Clearances;

/// <summary>
/// Ведение допусков пользователей (ТБ-011/020/021) из интерфейса. Допуск — ядровое понятие
/// (<c>core.clearance</c>), а правило «кто вправе выдавать» — профильное (§2.1 ТЗ СКИД: Администратор),
/// поэтому порт живёт в ядре, а сценарии — здесь.
/// </summary>
/// <remarks>
/// До этого экрана гриф и подразделения правились ТОЛЬКО SQL'ем по живой базе: правка не попадала в
/// неизменяемый журнал (ТБ-030), а расхождение номеров подразделений со справочником профиля
/// проявлялось у пользователя как «не могу зарегистрировать документ и не понимаю почему».
/// </remarks>

/// <summary>Подразделение из допуска: наименование либо признак «в справочнике такого номера нет».</summary>
public sealed record ClearanceDivision(int Id, string? Name)
{
    /// <summary>Есть ли такое подразделение в справочнике профиля (<c>inspector.division</c>).</summary>
    public bool IsKnown => Name is not null;
}
