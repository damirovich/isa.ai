namespace ISC.AI.Abstractions.Retrieval;

/// <summary>
/// Необязательные доменно-нейтральные ограничения извлечения. НЕ может ослабить фильтр доступа
/// (он применяется ядром в любом случае). По умолчанию возвращаются только актуальные источники.
/// </summary>
/// <param name="IncludeSuperseded">
/// Включать ли неактуальные источники (<c>IsCurrent = false</c>, например утратившие силу редакции
/// для явного показа с пометкой ТЭ-003). По умолчанию <c>false</c> — только актуальные.
/// </param>
public sealed record RetrievalFilter(bool IncludeSuperseded = false);
