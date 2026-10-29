#!/usr/bin/env bash
# Установка и обновление «ИнспекторAI» на сервере приложений Linux (изолированный контур, ТИ-003).
#
#   sudo bash install.sh            — установить или обновить; работающая служба НЕ перезапускается (сначала миграции)
#   sudo bash install.sh --start    — то же и (пере)запустить службу — когда настройки и база уже готовы
#
# Скрипт повторяемый: повторный запуск того же пакета ничего не ломает. Настройки (/etc/isc-ai) и данные
# (/var/lib/isc-ai) он НЕ перезаписывает — шаблоны кладутся только при первой установке.
#
# Раскладка:
#   /opt/isc-ai/releases/<версия>/{app,tools}   — программа (каждая версия рядом, для отката)
#   /opt/isc-ai/current -> releases/<версия>     — текущая версия (переключается одной ссылкой)
#   /opt/isc-ai/tessdata                         — языковые файлы OCR (rus, kir)
#   /etc/isc-ai/appsettings.Production.json      — настройки (root:iscai 0640)
#   /etc/isc-ai/isc-ai.env                       — секреты: пароль БД и т. п. (root:root 0600, читает только systemd)
#   /etc/isc-ai/tls/                             — сертификат и ключ HTTPS
#   /var/lib/isc-ai/{storage,docflow-files,keys} — данные службы (iscai 0700)
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
RELEASE="$BASE/releases/$VERSION"

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
# шифрования, их защищают права каталога — ТБ-062).
install -d -m 0700 -o "$APP_USER" -g "$APP_USER" "$DATA" "$DATA/storage" "$DATA/docflow-files" "$DATA/keys"

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
chown -R root:root "$RELEASE"
# Программа только для чтения: писать в неё службе незачем (ProtectSystem=strict это и закрепляет).
chmod -R u=rwX,go=rX "$RELEASE"
chmod 0755 "$RELEASE/app/ISC.AI.Web" "$RELEASE/tools/backup-restore/ISC.AI.Backup.Restore"
# Настройки читаются из /etc: ссылка в каталоге программы (хост берёт appsettings.<Окружение>.json из рабочего каталога).
ln -sfn "$ETC/appsettings.Production.json" "$RELEASE/app/appsettings.Production.json"

say "Распознавание текста (OCR): языковые файлы и системные библиотеки Tesseract"
install -d -m 0755 "$BASE/tessdata"
install -m 0644 "$PKG"/tessdata/*.traineddata "$BASE/tessdata/"
# Пакет Tesseract для .NET ищет на Linux x64/libtesseract50.so и x64/libleptonica-1.82.0.so рядом с программой, а
# свои библиотеки везёт только для Windows. Подставляем системные (пакеты дистрибутива tesseract 5 и leptonica)
# ссылками с ожидаемыми именами. Без них OCR отключится при первом скане: текстовые PDF, DOCX, TXT грузятся и так.
find_lib() { ldconfig -p | awk -v pat="$1" '$1 ~ pat && /x86-64/ { print $NF; exit }'; }
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
if ! ldconfig -p | grep -q 'libdl\.so '; then
    libdl="$(find_lib '^libdl\.so\.2')"
    [[ -n "$libdl" ]] && ln -sfn "$libdl" "$RELEASE/app/libdl.so"
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
ldconfig -p | grep -q 'libicuuc\.so' || warn "нет ICU (libicu): без неё программа не запустится — поставьте пакет libicu"
ldconfig -p | grep -qE 'libssl\.so\.(3|1\.1)' || warn "нет OpenSSL (libssl 3 или 1.1): HTTPS и SSL к БД не заработают"
fonts_ok=0
for f in /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf /usr/share/fonts/dejavu/DejaVuSans.ttf \
         /usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf \
         /usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf; do
    [[ -f "$f" ]] && fonts_ok=1
done
[[ $fonts_ok -eq 1 ]] || warn "нет шрифтов DejaVu/Liberation: PDF-отчёты документооборота не сформируются (или задайте DocFlow:Reports:PdfFont)"
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
    echo "   systemctl start isc-ai (ДОК-15 §7)"
else
    echo "   служба не запускалась. Дальше: заполнить настройки → bash migrate.sh … → systemctl start isc-ai →"
    echo "   sudo bash account.sh <логин> (ДОК-15 §5)"
fi

say "Готово ($warnings предупрежд.)"
