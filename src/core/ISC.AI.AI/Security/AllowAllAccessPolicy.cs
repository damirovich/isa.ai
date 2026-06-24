using System.Linq.Expressions;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.AI.Security;

/// <summary>
/// Политика доступа по умолчанию: без дополнительных доменных ограничений сверх ядрового
/// <see cref="BaselineAccess"/>. Профиль регистрирует собственную <see cref="IAccessPolicy"/>,
/// чтобы СУЗИТЬ доступ под свою модель (например, поддерево подразделений или тип дела). Ядровой
/// baseline применяется ретривером в любом случае (ADR-0014).
/// </summary>
public sealed class AllowAllAccessPolicy : IAccessPolicy
{
    /// <inheritdoc />
    public Expression<Func<T, bool>> BuildFilter<T>(AccessContext subject) where T : IClassified
        => _ => true;
}
