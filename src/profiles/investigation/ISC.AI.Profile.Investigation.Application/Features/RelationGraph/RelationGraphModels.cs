using System;
using System.Collections.Generic;

namespace ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

/// <summary>Вид узла графа связей (ADR-0045 п. 2).</summary>
public enum RelationNodeKind
{
    /// <summary>Фигурант, доступный субъекту по полной решётке; раскрывается.</summary>
    Person = 1,

    /// <summary>Дело, доступное субъекту по полной решётке; раскрывается в своих фигурантов.</summary>
    Case = 2,

    /// <summary>
    /// Чужое дело из пересечения (ТБ-084, ADR-0029 п. 4): только номер, вид, статус и ведущий; не раскрывается и на
    /// карточку не ведёт.
    /// </summary>
    ForeignCase = 3,

    /// <summary>
    /// Автотранспорт: один узел на нормализованный госномер (ТО-мат-11); раскрывается в доступных фигурантов с этим
    /// номером (ADR-0045 п. 10). Без госномера — узел на строку, не раскрывается.
    /// </summary>
    Vehicle = 4,

    /// <summary>
    /// Адрес: один узел на нормализованный адрес (ТО-мат-11); раскрывается в доступных фигурантов с этим адресом
    /// (ADR-0045 п. 10). Без нормализованного значения — узел на строку, не раскрывается.
    /// </summary>
    Address = 5,

    /// <summary>
    /// Номер телефона (ADR-0055): один узел на нормализованный номер (ТО-мат-11); раскрывается в доступных фигурантов с
    /// этим номером (ADR-0045 п. 10). Номер без цифр не сохраняется, поэтому узла на строку нет.
    /// </summary>
    Phone = 6,

    /// <summary>
    /// Событие — «дело-посредник» связи объектов (ТФ-ПЕР-11, ADR-0056 п. 4): узел на событие, видимое субъекту;
    /// раскрывается во всех видимых участников — доступные фигуранты и чужие дела (ТБ-084).
    /// </summary>
    Event = 7,
}

/// <summary>Вид ребра графа связей.</summary>
public enum RelationEdgeKind
{
    /// <summary>Фигурант — его дело (подпись — роль в деле).</summary>
    CaseMember = 1,

    /// <summary>Связь фигуранта из того же дела (ТФ-ПЕР-06): от связи к тому, чьей связью она является.</summary>
    PersonLink = 2,

    /// <summary>Реквизит фигуранта: автотранспорт, адрес или номер телефона (ТФ-ПЕР-06, ТФ-ПЕР-12).</summary>
    Requisite = 3,

    /// <summary>Пересечение с другим делом (ТФ-ПЕР-07): от совпавшего реквизита (или фигуранта) к делу.</summary>
    Intersection = 4,

    /// <summary>
    /// Совместное появление (ADR-0045 п. 11): у обоих фигурантов есть действующие появления на одних и тех же
    /// носителях. Следственная версия, а не утверждение системы (ТФ-ВЕР-03).
    /// </summary>
    CoAppearance = 5,

    /// <summary>
    /// Участие в событии (ТФ-ПЕР-11, ADR-0056 п. 4): от фигуранта (или чужого дела участника) к событию; подпись — роль
    /// и уровень связи. Заводит человек (ТБ-073).
    /// </summary>
    EventLink = 6,
}

/// <summary>Узел графа связей.</summary>
/// <param name="Key">Ключ узла, уникальный в графе (вид + идентификатор или нормализованный реквизит).</param>
/// <param name="Kind">Вид узла.</param>
/// <param name="EntityId">
/// Для раскрытия и ссылки: фигурант или дело; у автотранспорта и адреса — фигурант, у которого этот реквизит
/// (через него сервер проверяет раскрытие, ADR-0045 п. 10); у чужого дела — <see langword="null"/>.
/// </param>
/// <param name="Title">Заголовок: ФИО, «Дело № …», госномер, адрес.</param>
/// <param name="Subtitle">Вторая строка: роль и дело, вид и статус дела, марка и модель.</param>
/// <param name="Level">Кольцо от центра графа (центр — 0).</param>
/// <param name="ParentKey">Узел, при раскрытии которого этот узел появился впервые; у центра — <see langword="null"/>.</param>
/// <param name="Expanded">Соседи узла показаны.</param>
/// <param name="ResponsibleUserId">Ведущий дела — у узлов дела и чужого дела (ADR-0029 п. 4).</param>
/// <param name="Classification">
/// Гриф сведений узла (ADR-0030): фигуранта, дела; у реквизита — его фигуранта. У чужого дела — <see langword="null"/>:
/// гриф чужого дела не раскрывается (ТБ-084). По нему размечается выгрузка картинки (ADR-0045 п. 14).
/// </param>
public sealed record RelationNode(
    string Key,
    RelationNodeKind Kind,
    int? EntityId,
    string Title,
    string? Subtitle,
    int Level,
    string? ParentKey,
    bool Expanded,
    int? ResponsibleUserId = null,
    short? Classification = null)
{
    /// <summary>
    /// Узел можно раскрыть: фигурант, доступное дело, автотранспорт и адрес с нормализованным ключом; чужие дела —
    /// никогда (ADR-0045 п. 3).
    /// </summary>
    public bool Expandable { get; init; } = RelationNodeKeys.IsExpandable(Kind);
}

/// <summary>Ребро графа связей: ссылается на узлы по ключам.</summary>
/// <param name="FromKey">Узел-начало.</param>
/// <param name="ToKey">Узел-конец.</param>
/// <param name="Kind">Вид ребра.</param>
/// <param name="Label">Подпись: тип связи, вид адреса, «пересечение: …», «вместе на материалах: N».</param>
public sealed record RelationEdge(string FromKey, string ToKey, RelationEdgeKind Kind, string Label)
{
    /// <summary>Ключ ребра без учёта направления — встречная связь не задваивается (как в АСИО-10).</summary>
    public string Key => string.CompareOrdinal(FromKey, ToKey) < 0
        ? $"{FromKey}|{ToKey}|{Label}"
        : $"{ToKey}|{FromKey}|{Label}";
}

/// <summary>Граф связей: узлы в порядке обхода вширь (центр первым) и рёбра.</summary>
/// <param name="Nodes">Узлы.</param>
/// <param name="Edges">Рёбра.</param>
/// <param name="Truncated">Сработал предохранитель числа узлов: часть узлов осталась свёрнутой.</param>
public sealed record RelationGraph(IReadOnlyList<RelationNode> Nodes, IReadOnlyList<RelationEdge> Edges, bool Truncated);

/// <summary>Ключи узлов графа связей.</summary>
public static class RelationNodeKeys
{
    private const string VehiclePrefix = "vehicle:";
    private const string AddressPrefix = "address:";
    private const string PhonePrefix = "phone:";

    /// <summary>Фигурант.</summary>
    public static string Person(int personId) => $"person:{personId}";

    /// <summary>Доступное дело.</summary>
    public static string Case(int caseId) => $"case:{caseId}";

    /// <summary>Чужое дело из пересечения.</summary>
    public static string ForeignCase(int caseId) => $"foreign-case:{caseId}";

    /// <summary>Автотранспорт по нормализованному госномеру.</summary>
    public static string Vehicle(string plateNormalized) => VehiclePrefix + plateNormalized;

    /// <summary>Автотранспорт без госномера — отдельный узел на строку.</summary>
    public static string VehicleRow(int vehicleId) => $"vehicle-row:{vehicleId}";

    /// <summary>Адрес по нормализованному значению.</summary>
    public static string Address(string addressNormalized) => AddressPrefix + addressNormalized;

    /// <summary>Адрес, у которого нормализованного значения нет, — отдельный узел на строку.</summary>
    public static string AddressRow(int addressId) => $"address-row:{addressId}";

    /// <summary>Номер телефона по нормализованному значению (ADR-0055).</summary>
    public static string Phone(string phoneNormalized) => PhonePrefix + phoneNormalized;

    /// <summary>Событие — «дело-посредник» (ADR-0056).</summary>
    public static string Event(int eventId) => $"event:{eventId}";

    /// <summary>Вид узла раскрывается (фигурант, доступное дело, автотранспорт, адрес, телефон, событие).</summary>
    public static bool IsExpandable(RelationNodeKind kind) =>
        kind is RelationNodeKind.Person or RelationNodeKind.Case or RelationNodeKind.Vehicle or RelationNodeKind.Address
            or RelationNodeKind.Phone or RelationNodeKind.Event;

    /// <summary>
    /// Нормализованное значение из ключа узла автотранспорта или адреса; ключ другого вида (в том числе узла на
    /// строку) — <see langword="null"/>.
    /// </summary>
    public static string? ValueOf(RelationNodeKind kind, string? key)
    {
        var prefix = kind switch
        {
            RelationNodeKind.Vehicle => VehiclePrefix,
            RelationNodeKind.Address => AddressPrefix,
            RelationNodeKind.Phone => PhonePrefix,
            _ => null,
        };
        return prefix is not null && key is { Length: > 0 } && key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length
            ? key[prefix.Length..]
            : null;
    }

    /// <summary>Вид узла для сводки журнала (ТБ-032: без значений).</summary>
    public static string AuditName(RelationNodeKind kind) => kind switch
    {
        RelationNodeKind.Person => "person",
        RelationNodeKind.Case => "case",
        RelationNodeKind.Vehicle => "vehicle",
        RelationNodeKind.Address => "address",
        RelationNodeKind.Phone => "phone",
        RelationNodeKind.Event => "event",
        RelationNodeKind.ForeignCase => "foreign-case",
        _ => "other",
    };
}
