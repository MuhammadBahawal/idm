const NATIVE_HOST_NAME = "com.mydm.native";
const REQUEST_TIMEOUT_MS = 15000;
const MAX_DEBUG_LOGS = 300;
const MAX_DETECTED_MEDIA = 100;
const MAX_DETECTED_RESOURCES = 200;
const MAX_STREAM_CANDIDATES_PER_TAB = 300;

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
    pageUrl?: string;
    preferMuxed?: boolean;
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

interface StreamCandidate {
    url: string;
    tabId: number;
    timestamp: number;
    host: string;
    mimeType?: string;
    itag?: number;
    qualityLabel?: string;
    height: number;
    hasVideo: boolean;
    hasAudio: boolean;
    muxed: boolean;
}

interface ItagProfile {
    qualityLabel?: string;
    height: number;
    hasVideo: boolean;
    hasAudio: boolean;
    muxed: boolean;
}

let nativePort: chrome.runtime.Port | null = null;
let lastDisconnectReason = "";
const pendingRequests = new Map<string, PendingRequest>();
const tabStreamCandidates = new Map<number, StreamCandidate[]>();

const YOUTUBE_ITAG_PROFILES: Record<number, ItagProfile> = {
    17: { qualityLabel: "144p", height: 144, hasVideo: true, hasAudio: true, muxed: true },
    18: { qualityLabel: "360p", height: 360, hasVideo: true, hasAudio: true, muxed: true },
    22: { qualityLabel: "720p", height: 720, hasVideo: true, hasAudio: true, muxed: true },
    37: { qualityLabel: "1080p", height: 1080, hasVideo: true, hasAudio: true, muxed: true },
    38: { qualityLabel: "2160p", height: 2160, hasVideo: true, hasAudio: true, muxed: true },
    43: { qualityLabel: "360p", height: 360, hasVideo: true, hasAudio: true, muxed: true },
    44: { qualityLabel: "480p", height: 480, hasVideo: true, hasAudio: true, muxed: true },
    45: { qualityLabel: "720p", height: 720, hasVideo: true, hasAudio: true, muxed: true },
    46: { qualityLabel: "1080p", height: 1080, hasVideo: true, hasAudio: true, muxed: true },
    59: { qualityLabel: "480p", height: 480, hasVideo: true, hasAudio: true, muxed: true },
    78: { qualityLabel: "480p", height: 480, hasVideo: true, hasAudio: true, muxed: true },
    133: { qualityLabel: "240p", height: 240, hasVideo: true, hasAudio: false, muxed: false },
    134: { qualityLabel: "360p", height: 360, hasVideo: true, hasAudio: false, muxed: false },
    135: { qualityLabel: "480p", height: 480, hasVideo: true, hasAudio: false, muxed: false },
    136: { qualityLabel: "720p", height: 720, hasVideo: true, hasAudio: false, muxed: false },
    137: { qualityLabel: "1080p", height: 1080, hasVideo: true, hasAudio: false, muxed: false },
    140: { qualityLabel: "Audio", height: 0, hasVideo: false, hasAudio: true, muxed: false },
    160: { qualityLabel: "144p", height: 144, hasVideo: true, hasAudio: false, muxed: false },
    247: { qualityLabel: "720p", height: 720, hasVideo: true, hasAudio: false, muxed: false },
    248: { qualityLabel: "1080p", height: 1080, hasVideo: true, hasAudio: false, muxed: false },
    249: { qualityLabel: "Audio", height: 0, hasVideo: false, hasAudio: true, muxed: false },
    250: { qualityLabel: "Audio", height: 0, hasVideo: false, hasAudio: true, muxed: false },
    251: { qualityLabel: "Audio", height: 0, hasVideo: false, hasAudio: true, muxed: false },
    298: { qualityLabel: "720p60", height: 720, hasVideo: true, hasAudio: false, muxed: false },
    299: { qualityLabel: "1080p60", height: 1080, hasVideo: true, hasAudio: false, muxed: false },
    303: { qualityLabel: "1080p60", height: 1080, hasVideo: true, hasAudio: false, muxed: false },
    313: { qualityLabel: "2160p", height: 2160, hasVideo: true, hasAudio: false, muxed: false },
    315: { qualityLabel: "2160p60", height: 2160, hasVideo: true, hasAudio: false, muxed: false }
};

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

function parseIntParam(url: URL, key: string): number | undefined {
    const raw = url.searchParams.get(key);
    if (!raw) return undefined;
    const value = Number.parseInt(raw, 10);
    return Number.isFinite(value) ? value : undefined;
}

function parseHeightFromQuality(label?: string): number {
    if (!label) return 0;
    const match = label.match(/(\d{3,4})p/i);
    if (!match) return 0;
    const value = Number.parseInt(match[1], 10);
    return Number.isFinite(value) ? value : 0;
}

function isYoutubeHost(host: string): boolean {
    const lower = host.toLowerCase();
    return lower.includes("googlevideo.com") || lower.endsWith("youtube.com") || lower === "youtu.be";
}

function buildStreamCandidate(urlValue: string, tabId: number): StreamCandidate | null {
    if (tabId < 0) return null;
    let parsed: URL;
    try {
        parsed = new URL(urlValue);
    } catch {
        return null;
    }

    const host = parsed.hostname.toLowerCase();
    const itag = parseIntParam(parsed, "itag");
    const profile = itag !== undefined ? YOUTUBE_ITAG_PROFILES[itag] : undefined;
    const mimeType = parsed.searchParams.get("mime") || undefined;
    const qualityLabel = profile?.qualityLabel
        || parsed.searchParams.get("quality_label")
        || parsed.searchParams.get("quality")
        || undefined;
    const height = profile?.height || parseHeightFromQuality(qualityLabel);

    let hasVideo = profile?.hasVideo ?? false;
    let hasAudio = profile?.hasAudio ?? false;
    if (!profile && mimeType) {
        const lower = mimeType.toLowerCase();
        if (lower.startsWith("video/")) hasVideo = true;
        if (lower.startsWith("audio/")) hasAudio = true;
    }

    const isMediaByQuery = Boolean(
        mimeType
        || parsed.searchParams.get("clen")
        || parsed.searchParams.get("dur")
        || parsed.searchParams.get("mime")
    );

    if (!hasVideo && !hasAudio && !isMediaByQuery) {
        return null;
    }

    if (!isYoutubeHost(host) && !isDirectDownloadUrl(urlValue) && !isMediaByQuery) {
        return null;
    }

    return {
        url: urlValue,
        tabId,
        timestamp: Date.now(),
        host,
        mimeType,
        itag,
        qualityLabel,
        height,
        hasVideo,
        hasAudio,
        muxed: profile?.muxed ?? (hasVideo && hasAudio)
    };
}

function rememberStreamCandidate(candidate: StreamCandidate): void {
    const list = tabStreamCandidates.get(candidate.tabId) || [];
    if (list.some((item) => item.url === candidate.url)) {
        return;
    }

    list.push(candidate);
    list.sort((a, b) => b.timestamp - a.timestamp);
    tabStreamCandidates.set(candidate.tabId, list.slice(0, MAX_STREAM_CANDIDATES_PER_TAB));
}

function scoreStreamCandidate(candidate: StreamCandidate, preferMuxed: boolean): number {
    const muxedBonus = preferMuxed && candidate.muxed ? 1_000_000 : 0;
    const audioBonus = preferMuxed && candidate.hasAudio ? 250_000 : 0;
    const videoBonus = candidate.hasVideo ? 100_000 : 0;
    const heightScore = candidate.height * 1000;
    const recencyScore = Math.floor(candidate.timestamp / 1000);
    return muxedBonus + audioBonus + videoBonus + heightScore + recencyScore;
}

function rankStreamCandidates(candidates: StreamCandidate[], preferMuxed: boolean): StreamCandidate[] {
    return [...candidates].sort((a, b) => scoreStreamCandidate(b, preferMuxed) - scoreStreamCandidate(a, preferMuxed));
}

function filterCandidatesForPage(candidates: StreamCandidate[], pageUrl?: string): StreamCandidate[] {
    if (!pageUrl) return candidates;
    try {
        const page = new URL(pageUrl);
        const host = page.hostname.toLowerCase();
        if (host.includes("youtube.com") || host === "youtu.be" || host === "m.youtube.com") {
            const yt = candidates.filter((candidate) => candidate.host.includes("googlevideo.com"));
            if (yt.length > 0) return yt;
        }
        return candidates;
    } catch {
        return candidates;
    }
}

function resolveBestStreamCandidate(
    tabId: number,
    pageUrl?: string,
    preferMuxed = true
): StreamCandidate | null {
    const all = tabStreamCandidates.get(tabId) || [];
    if (all.length === 0) return null;

    const filtered = filterCandidatesForPage(
        all.filter((candidate) => candidate.hasVideo || candidate.muxed),
        pageUrl
    );
    if (filtered.length === 0) return null;

    const ranked = rankStreamCandidates(filtered, preferMuxed);
    return ranked[0] || null;
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

chrome.tabs.onRemoved.addListener((tabId) => {
    tabStreamCandidates.delete(tabId);
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
                case "resolve_video_source": {
                    const tabId = sender.tab?.id;
                    if (typeof tabId !== "number") {
                        sendResponse({ success: false, error: "Cannot resolve source without tab context", requestId });
                        return;
                    }

                    const pageUrl = message.pageUrl || sender.tab?.url || "";
                    const preferMuxed = message.preferMuxed !== false;
                    const resolved = resolveBestStreamCandidate(tabId, pageUrl, preferMuxed);
                    if (!resolved) {
                        sendResponse({
                            success: false,
                            error: "No downloadable stream detected yet. Let the video play for a few seconds and try again.",
                            requestId
                        });
                        return;
                    }

                    appendDebugLog("info", "stream.resolved", {
                        requestId,
                        tabId,
                        pageUrl,
                        url: resolved.url,
                        itag: resolved.itag,
                        quality: resolved.qualityLabel,
                        muxed: resolved.muxed
                    });

                    sendResponse({
                        success: true,
                        requestId,
                        url: resolved.url,
                        quality: resolved.qualityLabel || "",
                        mimeType: resolved.mimeType || "",
                        muxed: resolved.muxed
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

            const candidate = buildStreamCandidate(details.url, details.tabId);
            if (candidate) {
                rememberStreamCandidate(candidate);
                if (candidate.hasVideo) {
                    rememberDetectedResource({
                        url: candidate.url,
                        kind: candidate.muxed ? "network_video_muxed" : "network_video_stream",
                        tabId: details.tabId,
                        timestamp: candidate.timestamp
                    });
                }
            }
        },
        { urls: ["<all_urls>"], types: ["xmlhttprequest", "media", "main_frame", "sub_frame", "other"] }
    );
}

export { };
