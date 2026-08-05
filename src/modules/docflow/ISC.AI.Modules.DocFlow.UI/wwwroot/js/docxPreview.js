// Клиентский предпросмотр файлов карточки документа (DOCX/PDF/изображения) — ES-модуль, грузится
// ЛЕНИВО через IJSRuntime.InvokeAsync("import", ...) только со страницы карточки (Blazor JS isolation) —
// хосту/профилю НЕ нужно знать про существование этого модуля (никаких правок App.razor).
// docx-preview/jszip — UMD-сборки (не ESM), поэтому грузятся как обычные <script> при первом вызове;
// jszip.min.js ОБЯЗАН загрузиться раньше docx-preview.min.js (последний берёт JSZip из глобала).
const moduleBaseUrl = new URL('.', import.meta.url);

let vendorScriptsPromise = null;

function loadScript(relativePath) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = new URL(relativePath, moduleBaseUrl).href;
        script.onload = () => resolve();
        script.onerror = () => reject(new Error(`Не удалось загрузить ${relativePath}`));
        document.head.appendChild(script);
    });
}

function ensureVendorScriptsLoaded() {
    vendorScriptsPromise ??= loadScript('../lib/jszip/jszip.min.js')
        .then(() => loadScript('../lib/docx-preview/docx-preview.min.js'));
    return vendorScriptsPromise;
}

// Контейнер живёт в MudDialog, а тот рендерится ДРУГИМ компонентом (провайдером диалогов) — момент
// появления узла не синхронизирован с OnAfterRenderAsync карточки. Поэтому ждём элемент явно, а не
// надеемся на порядок рендера (из-за него предпросмотр открывался ровно один раз, дальше — ошибка).
// Идентификатор уникален на каждое открытие, поэтому подхватить узел прошлого диалога невозможно.
function waitForElement(containerId, timeoutMs = 5000) {
    return new Promise(resolve => {
        const deadline = Date.now() + timeoutMs;

        const attempt = () => {
            const element = document.getElementById(containerId);
            if (element) {
                resolve(element);
            } else if (Date.now() >= deadline) {
                resolve(null);
            } else {
                setTimeout(attempt, 16);
            }
        };

        attempt();
    });
}

// DOCX (OOXML) — это ZIP, сигнатура «PK». Старый .doc — двоичный OLE2 (D0 CF 11 E0), его docx-preview
// не читает в принципе. Проверяем СОДЕРЖИМОЕ, а не расширение: .doc, который на деле OOXML (файл просто
// переименовали — обычное дело), отрисуется, а настоящий OLE2 даст точное сообщение вместо «ошибки».
function isZipArchive(buffer) {
    const header = new Uint8Array(buffer, 0, Math.min(2, buffer.byteLength));
    return header.length === 2 && header[0] === 0x50 && header[1] === 0x4B;
}

// blob-URL прошлых предпросмотров: отзываются при каждом новом открытии и при уходе со страницы,
// чтобы содержимое файлов не копилось в памяти вкладки.
let objectUrls = [];

function revokeObjectUrls() {
    for (const url of objectUrls) {
        URL.revokeObjectURL(url);
    }

    objectUrls = [];
}

/**
 * Рендерит DOCX в элемент с идентификатором containerId (уникальным на каждое открытие).
 * Возвращает { status, message }: 'ok' | 'legacy-doc' | 'unavailable' (404 — нет файла ЛИБО нет
 * допуска, сервер их намеренно не различает) | 'error'. Текст ошибки отдаётся наружу намеренно —
 * в изолированном контуре у оператора нет ни консоли разработчика, ни телеметрии.
 */
export async function renderDocxPreview(containerId, fileUrl) {
    try {
        const container = await waitForElement(containerId);
        if (!container) {
            return { status: 'error', message: 'контейнер предпросмотра не появился' };
        }

        container.textContent = '';

        await ensureVendorScriptsLoaded();

        const response = await fetch(fileUrl, { credentials: 'same-origin' });
        if (response.status === 404) {
            return { status: 'unavailable' };
        }

        if (!response.ok) {
            return { status: 'error', message: `сервер вернул ${response.status}` };
        }

        const buffer = await response.arrayBuffer();
        if (!isZipArchive(buffer)) {
            return { status: 'legacy-doc' };
        }

        await docx.renderAsync(buffer, container, container, { className: 'docflow-docx', inWrapper: true });
        return { status: 'ok' };
    } catch (error) {
        return { status: 'error', message: error?.message ?? String(error) };
    }
}

/**
 * Выкачивает файл и отдаёт blob-URL для iframe/img. Нужен, потому что напрямую скормленный iframe
 * URL при 404 отрисовал бы СТРАНИЦУ «Not Found» всей системы внутри диалога — здесь же настоящий
 * код ответа виден и превращается в аккуратное сообщение. Возвращает { status, objectUrl, message }:
 * 'ok' | 'unavailable' | 'error'.
 */
export async function fetchFileToObjectUrl(fileUrl) {
    try {
        revokeObjectUrls();

        const response = await fetch(fileUrl, { credentials: 'same-origin' });
        if (response.status === 404) {
            return { status: 'unavailable' };
        }

        if (!response.ok) {
            return { status: 'error', message: `сервер вернул ${response.status}` };
        }

        const blob = await response.blob();
        const objectUrl = URL.createObjectURL(blob);
        objectUrls.push(objectUrl);
        return { status: 'ok', objectUrl };
    } catch (error) {
        return { status: 'error', message: error?.message ?? String(error) };
    }
}

/** Освобождение blob-URL при уходе с карточки (вызывается из DisposeAsync компонента). */
export function cleanup() {
    revokeObjectUrls();
}
