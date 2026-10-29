#!/usr/bin/env bash
# Применение миграций базы «ИнспекторAI» (ТО-прог-08, ДОК-07 §6): идемпотентные SQL-скрипты пакета (sql/) в порядке
# core → docflow → inspector. Повторный запуск безопасен: каждая миграция применяется, только если её ещё нет в журнале
# миграций своей схемы (__ef_migrations_history), и каждая — в своей транзакции.
#
#   bash migrate.sh --host <сервер БД> --db <база> --user <владелец базы> [--port 5432] [--sslmode require]
#                   [--admin-user postgres] [--yes]
#
# Пароль: переменная PGPASSWORD, иначе psql спросит сам. --admin-user — суперпользователь PostgreSQL, нужен только
# если в базе ещё нет расширений vector (pgvector) и pg_trgm: создавать расширения вправе только он.
# --yes — не спрашивать про резервную копию (для первой установки на пустую базу).
set -euo pipefail

PKG="$(dirname "$(readlink -f "$0")")"
host="" ; port=5432 ; db="" ; user="" ; sslmode=require ; admin="" ; yes=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --host) host="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        --db) db="$2"; shift 2 ;;
        --user) user="$2"; shift 2 ;;
        --sslmode) sslmode="$2"; shift 2 ;;
        --admin-user) admin="$2"; shift 2 ;;
        --yes) yes=1; shift ;;
        *) echo "Неизвестный ключ: $1" >&2; exit 2 ;;
    esac
done
if [[ -z "$host" || -z "$db" || -z "$user" ]]; then
    echo "Нужны --host, --db и --user (см. начало скрипта)." >&2
    exit 2
fi
command -v psql >/dev/null || { echo "Нет psql — поставьте клиент PostgreSQL той же версии, что сервер." >&2; exit 2; }

# Канал к БД — с шифрованием (ТБ-013/044): sslmode=require по умолчанию, как в строках подключения хоста.
export PGHOST="$host" PGPORT="$port" PGDATABASE="$db" PGSSLMODE="$sslmode" PGCLIENTENCODING=UTF8
export PGOPTIONS='--client-min-messages=warning'
q() { psql -X -q -t -A -v ON_ERROR_STOP=1 -U "$1" -c "$2"; }

echo "== Подключение: $user@$host:$port/$db (sslmode=$sslmode)"
server="$(q "$user" 'SHOW server_version')"
echo "   PostgreSQL $server"
if [[ "$(q "$user" "SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'")" != "1" ]]; then
    echo "ОТКАЗ: на сервере нет расширения pgvector (vector) — поставьте пакет pgvector под эту версию PostgreSQL." >&2
    exit 1
fi

# Сколько миграций схемы уже в базе. Наличие журнала — отдельным запросом: имя таблицы в подзапросе PostgreSQL
# проверяет ещё при разборе, и на пустой базе даже ветка CASE, которая не выполнится, дала бы ошибку.
applied() {
    if [[ "$(q "$user" "SELECT to_regclass('$1.__ef_migrations_history') IS NOT NULL")" == "t" ]]; then
        q "$user" "SELECT count(*) FROM $1.__ef_migrations_history"
    else
        echo 0
    fi
}
already=$(( $(applied core) + $(applied docflow) + $(applied inspector) ))
if [[ $already -gt 0 && $yes -eq 0 ]]; then
    echo "   в базе уже применено миграций: $already — это обновление."
    read -r -p "   Резервная копия базы и хранилищ сделана (ДОК-07 §6.1, п. 1)? [y/N] " answer
    [[ "$answer" == [yYдД]* ]] || { echo "Остановлено: сначала резервная копия (страница «Резервные копии» или pg_dump)."; exit 1; }
fi

echo "== Расширения vector и pg_trgm"
missing="$(q "$user" "SELECT string_agg(n, ' ') FROM unnest(ARRAY['vector','pg_trgm']) n WHERE n NOT IN (SELECT extname FROM pg_extension)")"
if [[ -n "$missing" ]]; then
    if [[ -z "$admin" ]]; then
        echo "ОТКАЗ: в базе нет расширений: $missing. Создать их вправе суперпользователь — повторите с --admin-user postgres" >&2
        echo "       или выполните под ним: CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS pg_trgm;" >&2
        exit 1
    fi
    for ext in $missing; do
        psql -X -q -v ON_ERROR_STOP=1 -U "$admin" -c "CREATE EXTENSION IF NOT EXISTS $ext"
        echo "   создано: $ext"
    done
else
    echo "   уже есть"
fi

# Порядок обязателен: ядро первым (ТО-прог-08). Внешних ключей между схемами нет (ТО-инф-06), но профиль и пакет
# опираются на справочники ядра.
for spec in "10-core.sql core" "20-docflow.sql docflow" "30-inspector.sql inspector"; do
    set -- $spec
    file="$PKG/sql/$1"; schema="$2"
    expected="$(grep -c "INSERT INTO $schema.__ef_migrations_history" "$file")"
    echo "== Схема $schema: $1"
    psql -X -q -v ON_ERROR_STOP=1 -U "$user" -f "$file" >/dev/null
    have="$(applied "$schema")"
    if [[ "$have" -lt "$expected" ]]; then
        echo "ОТКАЗ: в $schema применено $have из $expected миграций пакета." >&2
        exit 1
    fi
    echo "   применено миграций: $have (в пакете $expected)"
    if [[ "$have" -gt "$expected" ]]; then
        echo "   ВНИМАНИЕ: база новее пакета (лишние миграции — от более поздней версии). Программа этого пакета может"
        echo "   с ней не работать: при откате версии откатывают и базу — из резервной копии (ДОК-07 §6.2)."
    fi
done

echo "== Готово: схема базы соответствует пакету $(head -n 1 "$PKG/VERSION")."
