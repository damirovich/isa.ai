using System.Linq.Expressions;

namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Политика доступа по умолчанию: без дополнительных доменных ограничений сверх ядрового
/// <see cref="BaselineAccess"/>. Профиль регистрирует собственную <see cref="IAccessPolicy"/>,
/// чтобы СУЗИТЬ доступ под свою модель (например, поддерево подразделений, тип дела или состав
/// дел субъекта). Ядровой baseline применяется потребителями в любом случае (ADR-0014).
/// </summary>
/// <remarks>Перенесена в <c>Abstractions</c> вместе с <see cref="BaselineAccess"/> (ADR-0018).</remarks>
public sealed class AllowAllAccessPolicy : IAccessPolicy
{
    /// <inheritdoc />
    public Expression<Func<T, bool>> BuildFilter<T>(AccessContext subject) where T : IClassified
        => _ => true;
}
