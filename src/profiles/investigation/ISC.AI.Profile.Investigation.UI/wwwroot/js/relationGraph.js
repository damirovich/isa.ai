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

// Выгрузка картинки графа (ADR-0045 п. 14). SVG собирает сервер из модели графа (без внешних ресурсов), сюда приходит
// готовой строкой — запись в журнал уже сделана. PNG рисуется из того же SVG через canvas: сторонних библиотек нет,
// а холст не «загрязняется», потому что в SVG нет чужих картинок и шрифтов. Масштаб 2 — чётко при печати; большой
// граф уменьшается до 8000 px по большей стороне (браузеры не создают холст больше ~16 тыс. px).
function saveBlob(fileName, blob) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function downloadSvg(fileName, svgText) {
    saveBlob(fileName, new Blob([svgText], { type: 'image/svg+xml;charset=utf-8' }));
}

export function downloadPng(fileName, svgText) {
    return new Promise((resolve, reject) => {
        const svgUrl = URL.createObjectURL(new Blob([svgText], { type: 'image/svg+xml;charset=utf-8' }));
        const image = new Image();
        image.onload = () => {
            try {
                const scale = Math.min(2, 8000 / Math.max(image.width, image.height, 1));
                const canvas = document.createElement('canvas');
                canvas.width = Math.ceil(image.width * scale);
                canvas.height = Math.ceil(image.height * scale);
                const context = canvas.getContext('2d');
                context.fillStyle = '#ffffff';
                context.fillRect(0, 0, canvas.width, canvas.height);
                context.scale(scale, scale);
                context.drawImage(image, 0, 0);
                canvas.toBlob(blob => {
                    URL.revokeObjectURL(svgUrl);
                    if (blob) {
                        saveBlob(fileName, blob);
                        resolve();
                    } else {
                        reject(new Error('Не удалось получить PNG.'));
                    }
                }, 'image/png');
            } catch (error) {
                URL.revokeObjectURL(svgUrl);
                reject(error);
            }
        };
        image.onerror = () => {
            URL.revokeObjectURL(svgUrl);
            reject(new Error('Не удалось нарисовать картинку графа.'));
        };
        image.src = svgUrl;
    });
}
