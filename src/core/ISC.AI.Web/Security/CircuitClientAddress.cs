namespace ISC.AI.Web.Security;

/// <summary>
/// IP-адрес клиента соединения Blazor (ТБ-080, ADR-0066): хост захватывает его при установлении соединения и обновляет
/// при переподключении (<see cref="SessionCircuitHandler"/>), а журнал аудита берёт его для действий в соединении.
/// </summary>
/// <remarks>
/// Действия в соединении приходят не HTTP-запросами, у них нет «адреса запроса»: без этого держателя журнал знал бы
/// только адрес входа в сеанс, и работа похищенным cookie с другой машины записывалась бы с адресом владельца учётной
/// записи. Один на соединение (scoped); в обычном HTTP-запросе пуст — там адрес берётся из самого запроса.
/// </remarks>
public sealed class CircuitClientAddress
{
    private volatile string? _value;

    /// <summary>Адрес соединения; <see langword="null"/> — не захвачен (не соединение Blazor или транспорт адреса не дал).</summary>
    public string? Value => _value;

    /// <summary>Запомнить адрес соединения. Пустой не затирает известный: переподключение без адреса — тот же клиент.</summary>
    public void Capture(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            _value = address;
        }
    }
}
