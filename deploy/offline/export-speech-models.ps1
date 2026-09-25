# Офлайн-поставка моделей распознавания речи профиля «Следствие» (ADR-0026, ТИ-004; перенос — как у фида,
# ТБ-050/051). Запуск — на машине С ИНТЕРНЕТОМ; в изолированный контур переносится папка deploy/offline
# целиком. Файлы в git НЕ хранятся (deploy/offline/models/ в .gitignore).
#
# ЧТО СКАЧИВАЕТСЯ (всё — MIT/Apache, и код, и веса):
#   * GigaAM Multilingual, CTC, int8 — модель распознавания русского и киргизского (и kk/uz/en).
#     Веса: ai-sage/GigaAM-Multilingual, лицензия MIT (© SberDevices / salute-developers).
#     По умолчанию — вариант 600M (основной, ADR-0026 п. 13: на замере точнее и на склеенных кусках не путает
#     киргизский с казахским); 220M (-Variant 220m) — вдвое быстрее, запасной вариант для слабых машин.
#   * tokens.txt — словарь модели (71 символ: пробел, апостроф, a–z, кириллица с ё и казахско-киргизскими
#     буквами, <blk>).
#   * silero_vad.onnx — детектор речи Silero VAD, лицензия MIT (snakers4/silero-vad); файл из релиза
#     sherpa-onnx «asr-models» (k2-fsa/sherpa-onnx, Apache-2.0) — в формате, который ждёт sherpa-onnx.
#   * Тексты лицензий GigaAM и Silero VAD — MIT требует передавать уведомление вместе с весами.
#
# !!! ЗАПРЕЩЕНО: старые пакеты GigaAM v1 2024 года (например csukuangfj/sherpa-onnx-nemo-ctc-giga-am-russian-2024-10-24
# и ...-transducer-giga-am-russian-2024-10-24) — их веса под НЕКОММЕРЧЕСКОЙ лицензией. Брать ТОЛЬКО
# GigaAM-Multilingual (MIT). Модель без киргизского (GigaAM v2/v3 — только русский) задаче не подходит.
#
# ИСТОЧНИК ONNX. Официальный репозиторий ai-sage/GigaAM-Multilingual публикует только веса PyTorch
# (pytorch_model.bin, ревизии ctc / large_ctc) — готового ONNX для sherpa-onnx в нём нет (проверено
# 25.09.2026 по API Hugging Face). Готовый пакет sherpa-onnx — СТОРОННЯЯ конвертация
# fussraider/GigaAM-Multilingual-sherpa-onnx-ctc (MIT; рецепт тот же, что у официальных пакетов sherpa-onnx
# для GigaAM v2/v3: метаданные model_type=EncDecCTCModel, is_giga_am=1, 64 мел-полосы; скрипт экспорта
# export-onnx-ctc-multilingual.py лежит в том же репозитории). Берётся с ЗАКРЕПЛЁННОЙ ревизии (коммит), а
# не с main: плавающая ветка меняет содержимое под тем же адресом, пин теряет смысл.
#
# ДЛЯ БОЕВОЙ ПОСТАВКИ ЛУЧШЕ ЭКСПОРТ ИЗ ОФИЦИАЛЬНЫХ ВЕСОВ, а не чужая конвертация: на машине с интернетом
#   pip install gigaam onnx onnxruntime
#   python export-onnx-ctc-multilingual.py --out-dir <папка> --model multilingual_large_ctc   (600M; для 220M — без ключа)
# и затем этот скрипт с -OwnExportDir <папка> (для экспорта 220M — ещё -Variant 220m: по варианту называется
# файл модели в поставке): модель берётся из своей папки (пин SHA-256 = хеш своего
# экспорта, он и пойдёт в конфигурацию), а словарь сверяется с эталонным — тот же словарь значит ту же модель.
#
# ПИНЫ получены из метаданных, БЕЗ скачивания файлов (25.09.2026):
#   * модели и silero_vad.onnx — SHA-256 из API (Hugging Face: /api/models/<repo>/tree/<rev> → lfs.oid;
#     GitHub: поле digest ассета релиза);
#   * tokens.txt и тексты лицензий — мелкие файлы не в LFS: API даёт только git-хеш объекта (SHA-1 от
#     "blob <размер>\0<содержимое>"). Проверяется он (вместе с закреплённой ревизией), а SHA-256 для
#     конфигурации вычисляется после проверки и попадает в манифест.
#
# Результат: deploy/offline/models/speech + манифест speech-models.sha256 ("<SHA256>  <имя>", как у ffmpeg
# и моделей лиц). Хост требует те же SHA-256 в Speech:*:Sha256 (несовпадение — явная ошибка, ТИ-004).
# Кодировка файла — UTF-8 с BOM: иначе Windows PowerShell 5.1 прочитает кириллицу в кодовой странице ANSI.
param(
    # Вариант модели: 600m (по умолчанию, основной; 592 МБ, точнее) или 220m (225 МБ, ~2× быстрее на CPU —
    # запасной вариант для слабых машин; в конфигурации сменить путь и пин). ADR-0026 п. 13.
    [ValidateSet('220m', '600m')]
    [string]$Variant = '600m',

    [string]$ModelsDir = "$PSScriptRoot\models\speech",

    # Папка СОБСТВЕННОГО экспорта из официальных весов (model.int8.onnx + tokens.txt) — рекомендуемый путь
    # для боевой поставки; без параметра модель скачивается из закреплённой ревизии конвертации.
    [string]$OwnExportDir = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 по умолчанию может не предлагать TLS 1.2, без которого Hugging Face и GitHub не отвечают.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# ---- Пины (обновление ОСОЗНАННОЕ: сменить ревизию и хеши одновременно, прогнать
# ---- dotnet test --filter "Category=Speech", на пилоте заново измерить качество — ADR-0026) ----
$hfRepo = 'fussraider/GigaAM-Multilingual-sherpa-onnx-ctc'
$hfRevision = '9f5a77e8975211abe8511693accd3a63ee1e9f43'   # коммит от 31.07.2026

$models = @{
    '220m' = @{ Source = 'model.int8.onnx'
                Target = 'gigaam-multilingual-ctc-220m.int8.onnx'
                Size   = 224762524
                Sha256 = '2D94F93FFD4EF58E7899C9DE885C25BBBC8C9F1073618868D118A674450BA5F7' }
    '600m' = @{ Source = 'large/model.int8.onnx'
                Target = 'gigaam-multilingual-ctc-600m.int8.onnx'
                Size   = 591645642
                Sha256 = '6B6F195026B0F90721CD4593C664BECF009A71131550B664EEC71446EC351C81' }
}

# Словарь у 220M и 600M один и тот же (одинаковый git-хеш tokens.txt и large/tokens.txt).
$tokens = @{ Source = 'tokens.txt'; Target = 'tokens.txt'; Size = 391; GitBlobSha1 = '6dd5837a31f492c8ae97840717bc92af774fc9ab' }

$gigaAmLicense = @{ Url         = "https://huggingface.co/$hfRepo/resolve/$hfRevision/LICENSE"
                    Target      = 'LICENSE-GigaAM-Multilingual.txt'
                    GitBlobSha1 = '8d9a2e761e0feada9c9347a3f2265aee4653dba7' }

# Релиз «asr-models» — сборник, а не версия: ассет могут перезалить под тем же именем. Защищает пин SHA-256
# (поле digest API релиза, ассет обновлён 11.07.2025).
$vad = @{ Url    = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx'
          Target = 'silero_vad.onnx'
          Size   = 643854
          Sha256 = '9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6' }

$sileroLicense = @{ Url         = 'https://raw.githubusercontent.com/snakers4/silero-vad/5cd7945676eb32225748052e2e6a0580e4686a08/LICENSE'
                    Target      = 'LICENSE-silero-vad.txt'
                    GitBlobSha1 = '0bf5e90cac691b999d4a35044f97167d7bbbf0b9' }

# ---- Проверки ----
function Get-GitBlobSha1([string]$Path) {
    # Git-хеш объекта: SHA-1 от "blob <длина>\0" + содержимое — ровно то, что API отдаёт как oid/sha.
    $content = [IO.File]::ReadAllBytes($Path)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($content.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash([byte[]]($header + $content))
    }
    finally {
        $sha1.Dispose()
    }
    return -join ($hash | ForEach-Object { $_.ToString('x2') })
}

function Save-Pinned([string]$Url, [string]$Target, [string]$Sha256 = '', [string]$GitBlobSha1 = '', [long]$SizeBytes = 0) {
    $label = if ($SizeBytes -gt 0) { " ($(($SizeBytes / 1MB).ToString('0.0')) МБ)" } else { '' }
    Write-Host "Загрузка $(Split-Path $Target -Leaf)$label..."
    Invoke-WebRequest -Uri $Url -OutFile $Target -MaximumRedirection 5 -UseBasicParsing
    if ($SizeBytes -gt 0 -and (Get-Item $Target).Length -ne $SizeBytes) {
        $actualSize = (Get-Item $Target).Length
        Remove-Item $Target -Force
        throw "Размер файла $(Split-Path $Target -Leaf) = $actualSize байт, ожидалось $SizeBytes — загрузка оборвана или подменена, файл отброшен (ТИ-004)."
    }
    if ($Sha256) {
        $actual = (Get-FileHash $Target -Algorithm SHA256).Hash
        if ($actual -ne $Sha256) {
            Remove-Item $Target -Force
            throw "SHA256 файла $(Split-Path $Target -Leaf) не совпал с пином (получен $actual, ожидался $Sha256) — файл отброшен, перенос запрещён (ТИ-004)."
        }
    }
    if ($GitBlobSha1) {
        $actual = Get-GitBlobSha1 $Target
        if ($actual -ne $GitBlobSha1) {
            Remove-Item $Target -Force
            throw "Git-хеш файла $(Split-Path $Target -Leaf) не совпал с пином (получен $actual, ожидался $GitBlobSha1) — файл отброшен, перенос запрещён (ТИ-004)."
        }
    }
}

New-Item -ItemType Directory -Force $ModelsDir | Out-Null
$model = $models[$Variant]
$modelTarget = Join-Path $ModelsDir $model.Target
$tokensTarget = Join-Path $ModelsDir $tokens.Target

if ($OwnExportDir) {
    # Собственный экспорт из официальных весов: копируется как есть; эталоном служит словарь.
    $ownModel = Join-Path $OwnExportDir 'model.int8.onnx'
    $ownTokens = Join-Path $OwnExportDir 'tokens.txt'
    foreach ($file in $ownModel, $ownTokens) {
        if (-not (Test-Path $file)) { throw "В папке собственного экспорта нет $(Split-Path $file -Leaf): $OwnExportDir (ожидаются model.int8.onnx и tokens.txt из export-onnx-ctc-multilingual.py)." }
    }
    $ownTokensHash = Get-GitBlobSha1 $ownTokens
    if ($ownTokensHash -ne $tokens.GitBlobSha1) {
        throw "Словарь собственного экспорта отличается от эталонного GigaAM-Multilingual (git-хеш $ownTokensHash, ожидался $($tokens.GitBlobSha1)): это другая модель или сбой экспорта — поставка остановлена."
    }
    Copy-Item $ownModel $modelTarget -Force
    Copy-Item $ownTokens $tokensTarget -Force
    Write-Host "Модель — собственный экспорт из $OwnExportDir (пин SHA-256 — хеш этого файла)."
}
else {
    Save-Pinned "https://huggingface.co/$hfRepo/resolve/$hfRevision/$($model.Source)" $modelTarget -Sha256 $model.Sha256 -SizeBytes $model.Size
    Save-Pinned "https://huggingface.co/$hfRepo/resolve/$hfRevision/$($tokens.Source)" $tokensTarget -GitBlobSha1 $tokens.GitBlobSha1 -SizeBytes $tokens.Size
}

Save-Pinned $vad.Url (Join-Path $ModelsDir $vad.Target) -Sha256 $vad.Sha256 -SizeBytes $vad.Size
Save-Pinned $gigaAmLicense.Url (Join-Path $ModelsDir $gigaAmLicense.Target) -GitBlobSha1 $gigaAmLicense.GitBlobSha1
Save-Pinned $sileroLicense.Url (Join-Path $ModelsDir $sileroLicense.Target) -GitBlobSha1 $sileroLicense.GitBlobSha1

# ---- Манифест целостности (ТБ-051): по нему проверяется перенос в контур, как у ffmpeg и моделей лиц ----
$manifest = Join-Path $ModelsDir 'speech-models.sha256'
Get-ChildItem $ModelsDir -File | Where-Object { $_.Name -ne 'speech-models.sha256' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Out-File $manifest -Encoding utf8

function Get-Pin([string]$Name) { (Get-FileHash (Join-Path $ModelsDir $Name) -Algorithm SHA256).Hash }

$size = ((Get-ChildItem $ModelsDir -File | Measure-Object -Property Length -Sum).Sum / 1MB).ToString('0.0')
Write-Host "Готово: $ModelsDir ($size МБ), манифест speech-models.sha256, лицензии LICENSE-*.txt (MIT)."
Write-Host ''
Write-Host 'Конфигурация хоста (секция Speech; в контуре пути — АБСОЛЮТНЫЕ, на защищённом томе, ТБ-062):'
Write-Host "  Speech:Model:Path    = <путь к $($model.Target)>"
Write-Host "  Speech:Model:Sha256  = $(Get-Pin $model.Target)"
Write-Host "  Speech:Tokens:Path   = <путь к $($tokens.Target)>"
Write-Host "  Speech:Tokens:Sha256 = $(Get-Pin $tokens.Target)"
Write-Host "  Speech:Vad:Path      = <путь к $($vad.Target)>"
Write-Host "  Speech:Vad:Sha256    = $(Get-Pin $vad.Target)"
Write-Host '  Speech:Worker:Path   = <путь к опубликованному ISC.AI.Speech.Worker.exe>'
Write-Host '    (поставка утилиты — deploy/offline/publish-speech-worker.ps1, отдельная папка, НЕ каталог хоста)'
Write-Host '  Speech:Ffmpeg:Folder = <каталог ffmpeg>  (пусто — берётся Vision:Ffmpeg:Folder; поставка — export-ffmpeg.ps1)'
