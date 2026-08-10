using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

/// <summary>
/// Запросы справочников для форм модуля: подразделения (порт реализует ПРОФИЛЬ — вопрос 3 Э4-35)
/// и пользователи (реестр ядра <c>core.app_user</c> — вопрос 4).
/// </summary>

/// <summary>
/// Пределы регистрации для ТЕКУЩЕГО субъекта: до какого грифа и в каких подразделениях он вправе
/// зарегистрировать документ (ТБ-020/021, этап 6.6).
/// </summary>
/// <param name="MaxClassification">Максимальный гриф субъекта включительно.</param>
/// <param name="OwnerDivisions">
/// Подразделения, которые можно указать ВЛАДЕЛЬЦЕМ документа — пересечение справочника профиля
/// с допуском субъекта. Пустой список означает «регистрировать нельзя», а не «можно любое»
/// (default-deny, ТБ-021).
/// </param>
public sealed record RegistrationScope(short MaxClassification, IReadOnlyList<DivisionItem> OwnerDivisions);
