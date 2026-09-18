@echo off
chcp 65001 >nul
rem ============================================================================
rem  Запуск поставки профиля «Следствие» («СледствиеAI»), ADR-0021.
rem  Профиль выбирается свойством сборки IscProfile; каталог obj у хоста общий
rem  для обоих профилей, поэтому перед сборкой обязателен clean — иначе
rem  останутся ссылки прежнего профиля и запустится «ИнспекторAI».
rem  База — ОТДЕЛЬНАЯ (ADR-0006): у профилей разные справочники подразделений,
rem  а таблица допусков одна на базу. Здесь это локальный контейнер pgvector.
rem  Возврат к «ИнспекторAI» — deploy\run-inspector.cmd.
rem ============================================================================
setlocal
set REPO=%~dp0..
set CONN=Host=localhost;Port=55432;Database=ISC_AI_INV;Username=postgres

echo [1/3] База данных (контейнер iscai-investigation)...
docker start iscai-investigation >nul 2>&1
if errorlevel 1 (
  echo     контейнера нет — создаю и накатываю схемы
  docker run -d --name iscai-investigation -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=ISC_AI_INV -p 55432:5432 pgvector/pgvector:pg16 >nul || goto :fail
  timeout /t 8 /nobreak >nul
  set ISCAI_CORE_CONNECTION=%CONN%
  set ISCAI_DOCFLOW_CONNECTION=%CONN%
  set ISCAI_MEDIA_CONNECTION=%CONN%
  set ISCAI_INVESTIGATION_CONNECTION=%CONN%
  pushd "%REPO%"
  dotnet ef database update --project src\core\ISC.AI.Persistence --startup-project src\core\ISC.AI.Persistence --context CoreDbContext || goto :fail
  dotnet ef database update --project src\modules\docflow\ISC.AI.Modules.DocFlow.Data --startup-project src\modules\docflow\ISC.AI.Modules.DocFlow.Data --context DocFlowDbContext || goto :fail
  dotnet ef database update --project src\modules\media\ISC.AI.Modules.Media.Data --startup-project src\modules\media\ISC.AI.Modules.Media.Data --context MediaDbContext || goto :fail
  dotnet ef database update --project src\profiles\investigation\ISC.AI.Profile.Investigation.Data --startup-project src\profiles\investigation\ISC.AI.Profile.Investigation.Data --context InvestigationDbContext || goto :fail
  popd
) else (
  echo     контейнер запущен
)

echo [2/3] Очистка каталога сборки хоста (обязательна при смене профиля)...
pushd "%REPO%"
dotnet clean src\core\ISC.AI.Web -v q >nul

echo [3/3] Сборка и запуск профиля «Следствие» на http://localhost:5136
echo     вход: admin / IscAi-Smenite-Parol-2026 (пароль временный)
echo.
dotnet run --project src\core\ISC.AI.Web\ISC.AI.Web.csproj -p:IscProfile=investigation --urls http://localhost:5136 ^
  "--ConnectionStrings:Core=%CONN%" "--ConnectionStrings:DocFlow=%CONN%" ^
  "--ConnectionStrings:Media=%CONN%" "--ConnectionStrings:Investigation=%CONN%" ^
  "--Storage:BasePath=%REPO%\storage-files" ^
  "--Vision:Detector:Path=%REPO%\deploy\offline\models\face_detection_yunet_2023mar.onnx" ^
  "--Vision:Embedder:Path=%REPO%\deploy\offline\models\face_recognition_sface_2021dec.onnx"
popd
goto :eof

:fail
echo.
echo ОШИБКА на подготовке базы. Проверьте, запущен ли Docker Desktop.
pause
