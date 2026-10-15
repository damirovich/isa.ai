// Чат по документам дела (ADR-0051) — ES-модуль, грузится лениво из вкладки «Чат по документам». Только прокрутка ленты
// к последнему сообщению: разметку ведёт Blazor.

/** Прокрутить ленту сообщений к концу, если пользователь не листает её вверх (не дальше 120 px от низа) или force. */
export function scrollToEnd(elementId, force) {
    const element = document.getElementById(elementId);
    if (!element) {
        return false;
    }

    const nearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 120;
    if (force || nearBottom) {
        element.scrollTop = element.scrollHeight;
    }

    return true;
}
