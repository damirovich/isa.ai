#!/usr/bin/env bash
# Установка и обновление ISC.AI на сервере приложений Linux (изолированный контур, ТИ-003): «ИнспекторAI» или
# «Следствие AI» — по пакету (VERSION, строка profile=). Части, которых в пакете нет (модели, ffmpeg, распознаватель
# речи — только у «Следствия»), просто пропускаются.
#
#   sudo bash install.sh            — установить или обновить; работающая служба НЕ перезапускается (сначала миграции)
#   sudo bash install.sh --start    — то же и (пере)запустить службу — когда настройки и база уже готовы
#
# Скрипт повторяемый: повторный запуск того же пакета ничего не ломает. Настройки (/etc/isc-ai) и данные
# (/var/lib/isc-ai) он НЕ перезаписывает — шаблоны кладутся только при первой установке.
#
# Раскладка:
#   /opt/isc-ai/releases/<версия>/{app,tools}   — программа (каждая версия рядом, для отката)
#   /opt/isc-ai/releases/<версия>/speech-worker — распознаватель речи той же версии («Следствие»)
#   /opt/isc-ai/current -> releases/<версия>     — текущая версия (переключается одной ссылкой)
#   /opt/isc-ai/tessdata                         — языковые файлы OCR (rus, kir)
#   /opt/isc-ai/models/{vision,speech}           — модели распознавания лиц и речи («Следствие»)
#   /opt/isc-ai/ffmpeg/{bin,lib}                 — ffmpeg LGPL: видео и звук («Следствие»)
#   /etc/isc-ai/appsettings.Production.json      — настройки (root:iscai 0640)
#   /etc/isc-ai/isc-ai.env                       — секреты: пароль БД и т. п. (root:root 0600, читает только systemd)
#   /etc/isc-ai/tls/                             — сертификат и ключ HTTPS
#   /var/lib/isc-ai/{storage,docflow-files,keys} — данные службы (iscai 0700)
#   /var/lib/isc-ai/tmp                          — временные копии (видео, звук, сканы) — на томе данных, не в /tmp
#   /etc/systemd/system/isc-ai.service           — служба (свои правки — только через systemctl edit isc-ai)
set -euo pipefail

PKG="$(dirname "$(readlink -f "$0")")"
APP_USER=iscai
BASE=/opt/isc-ai
ETC=/etc/isc-ai
DATA=/var/lib/isc-ai
UNIT=/etc/systemd/system/isc-ai.service

start_after=0
[[ "${1:-}" == "--start" ]] && start_after=1

say()  { printf '\n== %s\n' "$*"; }
warn() { printf '   ВНИМАНИЕ: %s\n' "$*"; warnings=$((warnings + 1)); }
warnings=0

if [[ $EUID -ne 0 ]]; then
    echo "Запустите от root: sudo bash install.sh" >&2
    exit 2
fi

say "Проверка целостности пакета (ТБ-051)"
bash "$PKG/verify.sh"
VERSION="$(head -n 1 "$PKG/VERSION")"
PROFILE="$(sed -n 's/^profile=//p' "$PKG/VERSION")"
RELEASE="$BASE/releases/$VERSION"
echo "   профиль: ${PROFILE:-inspector}"

say "Учётная запись службы $APP_USER"
if ! id "$APP_USER" &>/dev/null; then
    # Системная учётка без входа в систему; домашний каталог = каталог данных (вне /home — ProtectHome службы).
    useradd --system --home-dir "$DATA" --no-create-home --shell /usr/sbin/nologin "$APP_USER"
    echo "   создана"
else
    echo "   уже есть"
fi

say "Каталоги"
install -d -m 0755 -o root -g root "$BASE" "$BASE/releases"
install -d -m 0750 -o root -g "$APP_USER" "$ETC" "$ETC/tls"
# Данные — только у службы: хранилища файлов и ключи шифрования cookie (на Linux ключи лежат на диске без
# шифрования, их защищают права каталога — ТБ-062). tmp — временные копии материалов (TMPDIR службы): видео в
# гигабайты не должно ложиться в /tmp системного раздела, а копия материала дела — вне каталога службы.
install -d -m 0700 -o "$APP_USER" -g "$APP_USER" "$DATA" "$DATA/storage" "$DATA/docflow-files" "$DATA/keys" "$DATA/tmp"

say "Программа $VERSION → $RELEASE"
was_active=0
systemctl is-active --quiet isc-ai.service && was_active=1
stopped_same=0
if [[ -d "$RELEASE" ]]; then
    echo "   версия уже разложена — заменяю её содержимое"
    # Эта же версия сейчас работает — остановить до замены файлов, иначе перезапуск по сбою попадёт в полупустой каталог.
    if [[ $was_active -eq 1 && "$(readlink -f "$BASE/current" 2>/dev/null || true)" == "$RELEASE" ]]; then
        systemctl stop isc-ai.service
        stopped_same=1
    fi
    rm -rf "$RELEASE"
fi
install -d -m 0755 "$RELEASE"
cp -a "$PKG/app" "$PKG/tools" "$PKG/VERSION" "$RELEASE/"
# Распознаватель речи — той же версии, что программа: они разговаривают своим протоколом, и откат версии откатывает обоих.
[[ -d "$PKG/speech-worker" ]] && cp -a "$PKG/speech-worker" "$RELEASE/"
chown -R root:root "$RELEASE"
# Программа только для чтения: писать в неё службе незачем (ProtectSystem=strict это и закрепляет).
chmod -R u=rwX,go=rX "$RELEASE"
chmod 0755 "$RELEASE/app/ISC.AI.Web" "$RELEASE/tools/backup-restore/ISC.AI.Backup.Restore"
[[ -d "$RELEASE/speech-worker" ]] && chmod 0755 "$RELEASE/speech-worker/ISC.AI.Speech.Worker"
# Настройки читаются из /etc: ссылка в каталоге программы (хост берёт appsettings.<Окружение>.json из рабочего каталога).
ln -sfn "$ETC/appsettings.Production.json" "$RELEASE/app/appsettings.Production.json"

say "Распознавание текста (OCR): языковые файлы и системные библиотеки Tesseract"
install -d -m 0755 "$BASE/tessdata"
install -m 0644 "$PKG"/tessdata/*.traineddata "$BASE/tessdata/"
# Пакет Tesseract для .NET ищет на Linux x64/libtesseract50.so и x64/libleptonica-1.82.0.so рядом с программой, а
# свои библиотеки везёт только для Windows. Подставляем системные (пакеты дистрибутива tesseract 5 и leptonica)
# ссылками с ожидаемыми именами. Без них OCR отключится при первом скане: текстовые PDF, DOCX, TXT грузятся и так.
# Список библиотек системы — ОДИН раз в переменную, поиск — по ней. Не «ldconfig -p | awk … exit» и не «| grep -q»:
# при set -o pipefail ранний выход читателя убивает ldconfig сигналом SIGPIPE (код 141), и set -e молча обрывает
# установку посреди шага — тем вероятнее, чем больше библиотек в системе (сотни строк при tesseract и клиенте
# PostgreSQL). Здесь-строку shell отдаёт целиком сам — читателю можно выходить когда угодно.
LDCACHE="$(ldconfig -p)"
find_lib() { awk -v pat="$1" '$1 ~ pat && /x86-64/ { print $NF; exit }' <<<"$LDCACHE"; }
has_lib() { grep -qE "$1" <<<"$LDCACHE"; }
tess="$(find_lib '^libtesseract\.so\.5')"
lept="$(find_lib '^libleptonica\.so\.6')"
[[ -z "$lept" ]] && lept="$(find_lib '^liblept\.so\.5')"
install -d -m 0755 "$RELEASE/app/x64"
if [[ -n "$tess" && -n "$lept" ]]; then
    ln -sfn "$tess" "$RELEASE/app/x64/libtesseract50.so"
    ln -sfn "$lept" "$RELEASE/app/x64/libleptonica-1.82.0.so"
    echo "   $tess, $lept"
else
    warn "нет системных libtesseract.so.5 / libleptonica — OCR сканов работать не будет (поставьте tesseract 5 и leptonica, затем повторите install.sh)"
fi
# Загрузчик библиотек того же пакета обращается к «libdl.so» — на новых glibc есть только libdl.so.2 (без пакета
# разработчика libc6-dev). Ссылка рядом с программой снимает зависимость от него.
if ! has_lib 'libdl\.so '; then
    libdl="$(find_lib '^libdl\.so\.2')"
    [[ -n "$libdl" ]] && ln -sfn "$libdl" "$RELEASE/app/libdl.so"
fi

# Модели и ffmpeg («Следствие») — общие для версий, как языковые файлы OCR: заменяются целиком новой копией (сначала
# рядом, потом переименованием), чтобы работающая служба не застала каталог наполовину пустым. Их целостность уже
# сверил verify.sh по SHA256SUMS, а пины моделей программа проверяет ещё раз при загрузке.
replace_dir() {
    local source="$1" target="$2"
    rm -rf "$target.new" "$target.old"
    cp -a "$source" "$target.new"
    chown -R root:root "$target.new"
    chmod -R u=rwX,go=rX "$target.new"
    [[ -d "$target" ]] && mv "$target" "$target.old"
    mv "$target.new" "$target"
    rm -rf "$target.old"
}
if [[ -d "$PKG/models" ]]; then
    say "Модели распознавания лиц и речи → $BASE/models"
    replace_dir "$PKG/models" "$BASE/models"
    find "$BASE/models" -type f -name '*.onnx' -printf '   %P\n'
fi
if [[ -d "$PKG/ffmpeg" ]]; then
    say "ffmpeg (LGPL) → $BASE/ffmpeg"
    replace_dir "$PKG/ffmpeg" "$BASE/ffmpeg"
    chmod 0755 "$BASE/ffmpeg/bin/ffmpeg" "$BASE/ffmpeg/bin/ffprobe"
    # Проверка запуском: библиотеки ищутся рядом ($ORIGIN/../lib), а сборка обязана быть LGPL (ТСТ-001/004).
    if configuration="$("$BASE/ffmpeg/bin/ffmpeg" -hide_banner -version 2>&1 | grep '^configuration:')"; then
        if [[ "$configuration" == *--enable-gpl* || "$configuration" == *--enable-nonfree* ]]; then
            echo "ОТКАЗ: ffmpeg в пакете собран как GPL/nonfree — поставка запрещена (ТСТ-001/004)." >&2
            exit 1
        fi
        echo "   $("$BASE/ffmpeg/bin/ffmpeg" -hide_banner -version | head -n 1)"
    else
        warn "ffmpeg не запускается ($BASE/ffmpeg/bin/ffmpeg -version) — видео и звук обрабатываться не будут; причина: ldd $BASE/ffmpeg/bin/ffmpeg"
    fi
fi

say "Настройки (только при первой установке; существующие не трогаются)"
if [[ ! -f "$ETC/appsettings.Production.json" ]]; then
    install -m 0640 -o root -g "$APP_USER" "$PKG/config/appsettings.Production.json" "$ETC/appsettings.Production.json"
    warn "положен шаблон $ETC/appsettings.Production.json — заполните все значения в угловых скобках"
else
    echo "   $ETC/appsettings.Production.json — оставлен как есть; сверьте с шаблоном config/ этого пакета"
fi
if [[ ! -f "$ETC/isc-ai.env" ]]; then
    install -m 0600 -o root -g root "$PKG/config/isc-ai.env" "$ETC/isc-ai.env"
    warn "положен шаблон $ETC/isc-ai.env — впишите пароль БД (Database__Password)"
else
    echo "   $ETC/isc-ai.env — оставлен как есть"
fi
[[ -f "$ETC/tls/server.crt" && -f "$ETC/tls/server.key" ]] \
    || warn "нет сертификата HTTPS: положите $ETC/tls/server.crt и server.key (root:$APP_USER 0640)"

say "Служба systemd"
install -m 0644 "$PKG/systemd/isc-ai.service" "$UNIT"
systemctl daemon-reload
systemctl enable isc-ai.service >/dev/null
echo "   isc-ai.service установлена и включена в автозапуск"

say "Проверка окружения"
has_lib 'libicuuc\.so' || warn "нет ICU (libicu): без неё программа не запустится — поставьте пакет libicu"
has_lib 'libssl\.so\.(3|1\.1)' || warn "нет OpenSSL (libssl 3 или 1.1): HTTPS и SSL к БД не заработают"
fonts_ok=0
for f in /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf /usr/share/fonts/dejavu/DejaVuSans.ttf \
         /usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf \
         /usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf; do
    [[ -f "$f" ]] && fonts_ok=1
done
[[ $fonts_ok -eq 1 ]] || warn "нет шрифтов DejaVu/Liberation: PDF-отчёты документооборота не сформируются (или задайте DocFlow:Reports:PdfFont)"
if [[ "$PROFILE" == "investigation" ]]; then
    # Нативный Skia (обработка изображений: лица, вырезки, лента кадров, печать фото) загружается вместе с fontconfig —
    # без него не обработается ни одно фото и видео.
    has_lib 'libfontconfig\.so\.1' \
        || warn "нет fontconfig (libfontconfig.so.1): распознавание лиц и печать фото не заработают — поставьте пакет fontconfig"
fi
command -v pg_dump >/dev/null || ls /usr/lib/postgresql/*/bin/pg_dump &>/dev/null \
    || warn "нет pg_dump (клиент PostgreSQL той же версии, что сервер) — резервные копии работать не будут"

say "Переключение на версию $VERSION"
previous=""
[[ -L "$BASE/current" ]] && previous="$(readlink -f "$BASE/current")"
# Ссылка переключается без остановки: работающий процесс продолжает прежнюю версию из её каталога. Новая начнёт
# работать только после перезапуска — а перезапускать можно лишь после миграций базы (migrate.sh), иначе новая
# программа встретит старую схему. Поэтому сама установка службу не перезапускает (кроме --start).
ln -sfn "$RELEASE" "$BASE/current"
echo "   $BASE/current -> $RELEASE"
[[ -n "$previous" && "$previous" != "$RELEASE" ]] && echo "   прежняя версия для отката: $previous"

if [[ $start_after -eq 1 || $stopped_same -eq 1 ]]; then
    systemctl restart isc-ai.service
    sleep 5
    if systemctl is-active --quiet isc-ai.service; then
        echo "   служба работает на $VERSION; журнал: journalctl -u isc-ai -f"
    else
        echo "   СЛУЖБА НЕ ЗАПУСТИЛАСЬ — причина в журнале: journalctl -u isc-ai -n 100 --no-pager" >&2
        exit 1
    fi
elif [[ $was_active -eq 1 ]]; then
    echo "   служба продолжает работать на прежней версии. Переход: systemctl stop isc-ai → bash migrate.sh … →"
    echo "   systemctl start isc-ai (docs/INSTALL.ru.md, раздел 6)"
else
    echo "   служба не запускалась. Дальше: заполнить настройки → bash migrate.sh … → systemctl start isc-ai →"
    echo "   sudo bash account.sh <логин> (docs/INSTALL.ru.md, раздел 4.5)"
fi

say "Готово ($warnings предупрежд.)"
