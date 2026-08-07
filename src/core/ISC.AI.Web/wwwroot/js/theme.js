// Запоминание выбранной темы оформления.
//
// localStorage, а НЕ cookie: тема — предпочтение оформления, а не данные о пользователе, и отправлять
// её на сервер с каждым запросом незачем. Заодно значение не попадает в журналы веб-сервера.
//
// Чтение выполняется ПОСЛЕ установления соединения (в OnAfterRenderAsync): при предрендере браузерного
// хранилища ещё нет, и обращение к нему на этом этапе бросает исключение.
window.iscaiTheme = {
    key: 'iscai.theme.dark',

    read: function () {
        try {
            const value = window.localStorage.getItem(window.iscaiTheme.key);
            return value === null ? null : value === 'true';
        } catch {
            // Хранилище может быть отключено политикой браузера — тогда просто нет запомненного
            // выбора. Ронять из-за этого оболочку нельзя.
            return null;
        }
    },

    write: function (isDark) {
        try {
            window.localStorage.setItem(window.iscaiTheme.key, isDark ? 'true' : 'false');
        } catch {
            // См. выше: невозможность запомнить выбор — не причина ломать переключение темы.
        }
    },
};
