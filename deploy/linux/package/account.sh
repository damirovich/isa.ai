#!/usr/bin/env bash
# Завести учётную запись или сбросить её пароль (Э4-35 §6.5) — служебная команда хоста create-account.
#
#   sudo bash account.sh <логин> ["Фамилия И. О."]
#
# Печатает ВРЕМЕННЫЙ пароль один раз (в базе только хеш); при первом входе его требуется сменить. Для существующего
# логина пароль сбрасывается, а его сеансы завершаются. Запускается под учёткой службы и с теми же настройками и
# секретами, что и служба: рабочий каталог программы (appsettings.json + /etc/isc-ai/appsettings.Production.json) и
# переменные из /etc/isc-ai/isc-ai.env. Сервер БД должен быть доступен, миграции — применены (migrate.sh).
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Запустите от root: sudo bash account.sh <логин> [\"ФИО\"]" >&2
    exit 2
fi
if [[ $# -lt 1 ]]; then
    echo "Укажите логин: sudo bash account.sh <логин> [\"ФИО\"]" >&2
    exit 2
fi

ENV_FILE=/etc/isc-ai/isc-ai.env
APP=/opt/isc-ai/current/app

# Тот же разбор, что у systemd (EnvironmentFile): строки КЛЮЧ=значение, # — комментарий, кавычки вокруг значения
# снимаются. Через source нельзя: пароль с $, ` или пробелом исказился бы или выполнился бы как команда.
while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" == \#* ]] && continue
    key="${line%%=*}"
    value="${line#*=}"
    if [[ ${#value} -ge 2 && ( "$value" == \"*\" || "$value" == \'*\' ) ]]; then
        value="${value:1:${#value}-2}"
    fi
    export "$key=$value"
done < "$ENV_FILE"

export ASPNETCORE_ENVIRONMENT=Production
cd "$APP"
# runuser без «-l» сохраняет окружение, собранное выше.
exec runuser -u iscai -- "$APP/ISC.AI.Web" create-account "$@"
