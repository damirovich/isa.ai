using System.Runtime.CompilerServices;

// Открывает internal-члены для юнит-тестов (напр. ModelCallResilience — retry/circuit breaker вокруг
// вызовов модели, ТН-003/ТНД-001). Внутренняя механика устойчивости — не публичный контракт ядра.
[assembly: InternalsVisibleTo("ISC.AI.UnitTests")]
