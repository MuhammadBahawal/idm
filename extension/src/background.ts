const NATIVE_HOST_NAME = "com.mydm.native";
const REQUEST_TIMEOUT_MS = 15000;
const MAX_DEBUG_LOGS = 300;
const MAX_DETECTED_MEDIA = 100;
const MAX_DETECTED_RESOURCES = 200;

const DOWNLOAD_EXTENSIONS = new Set([
    "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso",
    "exe", "msi", "dmg", "deb", "rpm", "apk",
    "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv",
    "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v",
    "mp3", "flac", "wav", "aac", "ogg", "wma", "m4a",
    "jpg", "jpeg", "png", "gif", "bmp", "svg", "webp"
]);

interface ExtMessage {
    type: string;
    url?: string;
    filename?: string;
    manifestUrl?: string;
    mediaType?: string;
    quality?: string;
    title?: string;
    headers?: Record<string, string>;
    requestId?: string;
    resources?: Array<Record<string, unknown>>;
}

interface SendResult {
    success: boolean;
    error?: string;
    response?: Record<string, unknown>;
    requestId?: string;
}

interface PendingRequest {
    resolve: (result: SendResult) => void;
    timer: ReturnType<typeof setTimeout>;
}

let nativePort: chrome.runtime.Port | null = null;
let lastDisconnectReason = "";
const pendingRequests = new Map<string, PendingRequest>();

function nowIso(): string {
    return new Date().toISOString();
}

function safeString(value: unknown): string {
    return value instanceof Error ? value.message : String(value);
}

function appendDebugLog(
    level: "info" | "warn" | "error",
    event: string,
    data: Record<string, unknown> = {}
): void {
    const entry = {
        ts: nowIso(),
        level,
        event,
        ...data
    };

    if (level === "error") {
        console.error("[MyDM]", event, data);
    } else if (level === "warn") {
        console.warn("[MyDM]", event, data);
    } else {
        console.log("[MyDM]", event, data);
    }

    chrome.storage.local.get(["debugLogs"], (result) => {
        const logs = (result["debugLogs"] as Array<Record<string, unknown>>) || [];
        logs.push(entry);
        chrome.storage.local.set({
            debugLogs: logs.slice(-MAX_DEBUG_LOGS),
            lastDebugLogAt: Date.now()
        });
    });
}

function setConnectionState(
    state: "connected" | "disconnected" | "error" | "connecting",
    reason = ""
): void {
    chrome.storage.local.set({
        connectionStatus: state,
        disconnectReason: reason,
        lastConnectionStateAt: Date.now()
    });
}

function getPayload(obj: Record<string, unknown>): Record<string, unknown> | undefined {
    const payload = obj["payload"];
    return payload && typeof payload === "object" ? payload as Record<string, unknown> : undefined;
}

function extractRequestId(message: Record<string, unknown>): string | undefined {
    const payload = getPayload(message);
    const requestId = payload?.["requestId"];
    return typeof requestId === "string" ? requestId : undefined;
}

function parseNativeError(message: Record<string, unknown>): string | undefined {
    if (message["type"] === "error") {
        const payload = getPayload(message);
        const nativeMessage = payload?.["message"];
        if (typeof nativeMessage === "string" && nativeMessage.trim().length > 0) {
            return nativeMessage;
        }
        return "Native host returned an error";
    }
    return undefined;
}

function rejectAllPending(error: string): void {
    for (const [requestId, pending] of pendingRequests.entries()) {
        clearTimeout(pending.timer);
        pending.resolve({
            success: false,
            error,
            requestId
        });
    }
    pendingRequests.clear();
}

function handleNativeMessage(message: Record<string, unknown>): void {
    const requestId = extractRequestId(message);
    const nativeError = parseNativeError(message);
    appendDebugLog("info", "native.message", { requestId, type: message["type"] ?? "unknown" });
    chrome.storage.local.set({
        lastNativeResponse: message,
        lastNativeResponseAt: Date.now()
    });

    if (!requestId) {
        return;
    }

    const pending = pendingRequests.get(requestId);
    if (!pending) {
        return;
    }

    clearTimeout(pending.timer);
    pendingRequests.delete(requestId);
    pending.resolve({
        success: !nativeError,
        error: nativeError,
        response: message,
        requestId
    });
}

function handleNativeDisconnect(): void {
    const reason = chrome.runtime.lastError?.message || "Native host disconnected";
    appendDebugLog("warn", "native.disconnected", { reason });
    lastDisconnectReason = reason;
    nativePort = null;
    setConnectionState("disconnected", reason);
    rejectAllPending(reason);
}

function connectNativeHost(): chrome.runtime.Port | null {
    if (nativePort) {
        return nativePort;
    }

    setConnectionState("connecting");
    try {
        nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
        nativePort.onMessage.addListener(handleNativeMessage);
        nativePort.onDisconnect.addListener(handleNativeDisconnect);
        lastDisconnectReason = "";
        setConnectionState("connected");
        appendDebugLog("info", "native.connected");
        return nativePort;
    } catch (error) {
        const reason = safeString(error);
        appendDebugLog("error", "native.connect_failed", { reason });
        lastDisconnectReason = reason;
        setConnectionState("error", reason);
        nativePort = null;
        return null;
    }
}

function postToNative(type: string, payload: Record<string, unknown>): Promise<SendResult> {
    const port = connectNativeHost();
    if (!port) {
        return Promise.resolve({
            success: false,
            error: lastDisconnectReason || "Native host not available. Is MyDM desktop app running?"
        });
    }

    const requestId = (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function")
        ? crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(16).slice(2)}`;

    const envelope: Record<string, unknown> = {
        type,
        payload: {
            ...payload,
            requestId,
            source: "extension-background",
            sentAt: nowIso()
        }
    };

    return new Promise<SendResult>((resolve) => {
        const timer = setTimeout(() => {
            pendingRequests.delete(requestId);
            resolve({
                success: false,
                error: "Timed out waiting for native host response",
                requestId
            });
        }, REQUEST_TIMEOUT_MS);

        pendingRequests.set(requestId, { resolve, timer });

        try {
            port.postMessage(envelope);
            appendDebugLog("info", "native.request_sent", { requestId, type });
        } catch (error) {
            clearTimeout(timer);
            pendingRequests.delete(requestId);
            resolve({
                success: false,
                error: `Failed to send request: ${safeString(error)}`,
                requestId
            });
        }
    });
}

async function probeNativeHost(): Promise<SendResult> {
    return postToNative("healthcheck", {});
}

function parseExtensionFromUrl(url: string): string | null {
    try {
        const parsed = new URL(url);
        const name = parsed.pathname.split("/").pop() || "";
        const dot = name.lastIndexOf(".");
        if (dot <= 0) return null;
        return name.slice(dot + 1).toLowerCase();
    } catch {
        return null;
    }
}

function isDirectDownloadUrl(url: string): boolean {
    const ext = parseExtensionFromUrl(url);
    return ext !== null && DOWNLOAD_EXTENSIONS.has(ext);
}

function rememberDetectedMedia(item: Record<string, unknown>): void {
    chrome.storage.local.get(["detectedMedia"], (result) => {
        const media = (result["detectedMedia"] as Array<Record<string, unknown>>) || [];
        const exists = media.some((m) =>
            m["url"] === item["url"] &&
            m["type"] === item["type"] &&
            m["tabId"] === item["tabId"]);
        if (exists) return;

        media.push(item);
        chrome.storage.local.set({ detectedMedia: media.slice(-MAX_DETECTED_MEDIA) });
    });
}

function rememberDetectedResource(item: Record<string, unknown>): void {
    chrome.storage.local.get(["detectedResources"], (result) => {
        const resources = (result["detectedResources"] as Array<Record<string, unknown>>) || [];
        const exists = resources.some((r) =>
            r["url"] === item["url"] &&
            r["kind"] === item["kind"] &&
            r["tabId"] === item["tabId"]);
        if (exists) return;

        resources.push(item);
        chrome.storage.local.set({ detectedResources: resources.slice(-MAX_DETECTED_RESOURCES) });
    });
}

function createContextMenus(): void {
    chrome.contextMenus.removeAll(() => {
        chrome.contextMenus.create({
            id: "mydm-download",
            title: "Download with MyDM",
            contexts: ["link", "image", "video", "audio"]
        });

        chrome.contextMenus.create({
            id: "mydm-download-page",
            title: "Download page URL with MyDM",
            contexts: ["page"]
        });
    });
}

chrome.runtime.onInstalled.addListener(() => {
    createContextMenus();
    void probeNativeHost();
});

chrome.runtime.onStartup.addListener(() => {
    void probeNativeHost();
});

chrome.contextMenus.onClicked.addListener((info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab) => {
    const url = info.linkUrl || info.srcUrl || info.pageUrl;
    if (!url) return;

    void postToNative("add_download", {
        url,
        referrer: tab?.url || "",
        filename: info.selectionText || undefined
    });
});

chrome.runtime.onMessage.addListener((message: ExtMessage, sender, sendResponse) => {
    const requestId = message.requestId || ((typeof crypto !== "undefined" && crypto.randomUUID) ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`);
    appendDebugLog("info", "runtime.message", { requestId, type: message.type });

    const run = async (): Promise<void> => {
        try {
            switch (message.type) {
                case "download_with_mydm": {
                    if (!message.url) {
                        sendResponse({ success: false, error: "Missing URL", requestId });
                        return;
                    }

                    const tab = sender.tab;
                    const result = await postToNative("add_download", {
                        url: message.url,
                        filename: message.filename,
                        referrer: tab?.url || "",
                        headers: message.headers || {}
                    });

                    sendResponse({
                        success: result.success,
                        error: result.error,
                        requestId: result.requestId || requestId
                    });
                    return;
                }
                case "download_media": {
                    if (!message.manifestUrl) {
                        sendResponse({ success: false, error: "Missing manifestUrl", requestId });
                        return;
                    }

                    const tab = sender.tab;
                    const result = await postToNative("add_media_download", {
                        manifestUrl: message.manifestUrl,
                        mediaType: message.mediaType || "hls",
                        quality: message.quality,
                        title: message.title || tab?.title || "",
                        referrer: tab?.url || "",
                        headers: message.headers || {}
                    });

                    sendResponse({
                        success: result.success,
                        error: result.error,
                        requestId: result.requestId || requestId
                    });
                    return;
                }
                case "get_connection_status": {
                    const health = await probeNativeHost();
                    sendResponse({
                        connected: health.success,
                        disconnectReason: health.success ? "" : (health.error || lastDisconnectReason),
                        requestId: health.requestId || requestId
                    });
                    return;
                }
                case "detected_media": {
                    if (message.url) {
                        rememberDetectedMedia({
                            url: message.url,
                            type: message.mediaType || "unknown",
                            tabId: sender.tab?.id,
                            title: sender.tab?.title || "",
                            timestamp: Date.now()
                        });
                    }
                    sendResponse({ success: true, requestId });
                    return;
                }
                case "detected_resources": {
                    const resources = message.resources || [];
                    for (const resource of resources) {
                        const url = resource["url"];
                        const kind = resource["kind"];
                        if (typeof url !== "string" || typeof kind !== "string") continue;
                        rememberDetectedResource({
                            ...resource,
                            tabId: sender.tab?.id,
                            title: sender.tab?.title || "",
                            timestamp: Date.now()
                        });
                    }
                    sendResponse({ success: true, requestId, count: resources.length });
                    return;
                }
                default: {
                    sendResponse({ success: false, error: `Unsupported message type: ${message.type}`, requestId });
                    return;
                }
            }
        } catch (error) {
            const err = safeString(error);
            appendDebugLog("error", "runtime.message_failed", { requestId, type: message.type, error: err });
            sendResponse({ success: false, error: err, requestId });
        }
    };

    void run();
    return true;
});

if (chrome.webRequest) {
    chrome.webRequest.onCompleted.addListener(
        (details) => {
            const url = details.url.toLowerCase();

            if (url.includes(".m3u8") || url.includes(".mpd")) {
                const mediaType = url.includes(".m3u8") ? "hls" : "dash";
                rememberDetectedMedia({
                    url: details.url,
                    type: mediaType,
                    tabId: details.tabId,
                    timestamp: Date.now()
                });
            } else if (isDirectDownloadUrl(details.url)) {
                rememberDetectedResource({
                    url: details.url,
                    kind: "network_file",
                    tabId: details.tabId,
                    timestamp: Date.now()
                });
            }
        },
        { urls: ["<all_urls>"], types: ["xmlhttprequest", "media", "main_frame", "sub_frame", "other"] }
    );
}

export { };
