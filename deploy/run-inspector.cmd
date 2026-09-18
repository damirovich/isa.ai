@echo off
chcp 65001 >nul
rem ============================================================================
rem  Возврат к поставке «ИнспекторAI» (профиль по умолчанию, ADR-0021).
rem  Обязателен clean: каталог obj у хоста общий для обоих профилей, и без
rem  очистки в сборке остаются ссылки профиля «Следствие».
rem  База — боевая из appsettings.json (схемы core/inspector/docflow),
rem  пароль берётся из user-secrets либо переменной Database__Password.
rem ============================================================================
setlocal
set REPO=%~dp0..
pushd "%REPO%"
echo [1/2] Очистка каталога сборки хоста...
dotnet clean src\core\ISC.AI.Web -v q >nul
echo [2/2] Сборка и запуск «ИнспекторAI» на http://localhost:5134
echo.
dotnet run --project src\core\ISC.AI.Web\ISC.AI.Web.csproj --urls http://localhost:5134
popd
