namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Текущее состояние учётки во внешней системе идентификации — для периодической ревалидации живых
/// сессий (ТБ-014/016): блокировка или смена штампа безопасности завершает сессию.
/// </summary>
/// <param name="IsBlocked">Учётка заблокирована во внешней системе.</param>
/// <param name="SecurityStamp">Текущий штамп безопасности (см. <see cref="ExternalIdentity.SecurityStamp"/>).</param>
public sealed record ExternalIdentityState(bool IsBlocked, string SecurityStamp);
