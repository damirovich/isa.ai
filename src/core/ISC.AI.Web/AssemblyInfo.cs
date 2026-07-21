using System.Runtime.CompilerServices;

// Открывает internal-члены хоста для юнит-тестов (напр. AuthEndpoints.SafeLocalUrl — регрессионный тест
// на обход open-redirect). Хост не тестируется как библиотека — это точечное исключение для чистых функций.
[assembly: InternalsVisibleTo("ISC.AI.UnitTests")]
