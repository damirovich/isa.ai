// Граф связей (ADR-0045 п. 7) — ES-модуль, грузится ЛЕНИВО через IJSRuntime.InvokeAsync("import", ...) только со
// страницы графа. Скрипт и стили библиотеки Z.Blazor.Diagrams (MIT) — static web assets её пакета
// (_content/Z.Blazor.Diagrams/...), интернет не нужен. Подключаются здесь, а не в App.razor хоста: хосту не нужно
// знать про библиотеку профиля (тот же приём, что у предпросмотра документов docflow).
// Скрипт библиотеки — обычный (не ES-модуль): он объявляет глобальный window.ZBlazorDiagrams, который
// вызывает C#-часть библиотеки, поэтому страница рисует холст только ПОСЛЕ ensureLoaded().
const moduleBaseUrl = new URL('.', import.meta.url);
const libraryBaseUrl = new URL('../../Z.Blazor.Diagrams/', moduleBaseUrl);

let scriptPromise = null;

function addStyle(file) {
    const href = new URL(file, libraryBaseUrl).href;
    if (document.querySelector(`link[rel="stylesheet"][href="${href}"]`)) {
        return;
    }

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = href;
    document.head.appendChild(link);
}

function loadScript(file) {
    return new Promise((resolve, reject) => {
        if (window.ZBlazorDiagrams) {
            resolve();
            return;
        }

        const script = document.createElement('script');
        script.src = new URL(file, libraryBaseUrl).href;
        script.onload = () => resolve();
        script.onerror = () => reject(new Error(`Не удалось загрузить ${file}`));
        document.head.appendChild(script);
    });
}

// Подключить стили и скрипт библиотеки (один раз за сеанс страницы; после ошибки — повторная попытка).
export function ensureLoaded() {
    addStyle('style.min.css');
    addStyle('default.styles.min.css');
    scriptPromise ??= loadScript('script.min.js').catch(error => {
        scriptPromise = null;
        throw error;
    });
    return scriptPromise;
}
