const NATIVE_HOST_NAME = "com.mydm.native";
const REQUEST_TIMEOUT_MS = 300_000; // 5 minutes — yt-dlp downloads can take a while
const MAX_DEBUG_LOGS = 300;
const MAX_DETECTED_MEDIA = 100;
const MAX_DETECTED_RESOURCES = 200;
const MAX_STREAM_CANDIDATES_PER_TAB = 300;

// Media content-type prefixes we care about for non-YouTube sites
const MEDIA_CONTENT_TYPES = ["video/", "audio/"];

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
    youtubeUrl?: string;
    filename?: string;
    manifestUrl?: string;
    mediaType?: string;
    quality?: string;
    title?: string;
    headers?: Record<string, string>;
    requestId?: string;
    resources?: Array<Record<string, unknown>>;
    pageUrl?: string;
    videoUrl?: string;
    audioUrl?: string;
    preferMuxed?: boolean;
    streamSource?: string;
    clickTsIso?: string;
    detectedVideoUrl?: string;
    canonicalYoutubeUrl?: string;
    formatId?: string;
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
    source: string;
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

// ──── Cookie Helper ────

async function getCookiesForUrl(url: string): Promise<string> {
    try {
        const parsed = new URL(url);
        const cookies = await chrome.cookies.getAll({ url: parsed.origin });
        if (cookies.length === 0) return "";
        return cookies.map((c) => `${c.name}=${c.value}`).join("; ");
    } catch {
        return "";
    }
}

async function buildHeadersWithCookies(
    url: string,
    extra?: Record<string, string>
): Promise<Record<string, string>> {
    const headers: Record<string, string> = { ...(extra || {}) };
    const cookieString = await getCookiesForUrl(url);
    if (cookieString) {
        headers["Cookie"] = cookieString;
    }
    return headers;
}

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

// Fallback download using Chrome's built-in downloads API
// Used when NativeHost is not available or fails
function chromeDownloadFallback(url: string, filename?: string): Promise<number> {
    return new Promise((resolve, reject) => {
        const options: chrome.downloads.DownloadOptions = { url };
        if (filename) {
            // Chrome doesn't allow path separators in filename
            options.filename = filename.replace(/[/\\]/g, "_");
        }
        chrome.downloads.download(options, (downloadId) => {
            if (chrome.runtime.lastError) {
                reject(new Error(chrome.runtime.lastError.message));
                return;
            }
            if (typeof downloadId !== "number") {
                reject(new Error("Download did not start"));
                return;
            }
            appendDebugLog("info", "download.chrome_fallback_started", {
                downloadId,
                url: url.substring(0, 100),
                filename
            });
            resolve(downloadId);
        });
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
    return lower.includes("googlevideo.com")
        || lower.endsWith("youtube.com")
        || lower === "youtu.be"
        || lower.endsWith("youtube-nocookie.com");
}

function isYoutubePageUrl(urlValue: string): boolean {
    try {
        const parsed = new URL(urlValue);
        const host = parsed.hostname.toLowerCase();
        if (host.includes("googlevideo.com")) return false;
        return host.endsWith("youtube.com")
            || host === "youtu.be"
            || host === "m.youtube.com"
            || host.endsWith("youtube-nocookie.com");
    } catch {
        return false;
    }
}

function isGoogleVideoUrl(urlValue: string): boolean {
    try {
        const parsed = new URL(urlValue);
        return parsed.hostname.toLowerCase().includes("googlevideo.com");
    } catch {
        return false;
    }
}

function getSenderYoutubePageUrl(sender: chrome.runtime.MessageSender): string | null {
    const tabUrl = sender.tab?.url;
    if (typeof tabUrl === "string" && isYoutubePageUrl(tabUrl)) {
        return tabUrl;
    }
    return null;
}

function getMessageYoutubePageUrl(message: ExtMessage, sender: chrome.runtime.MessageSender): string | null {
    const candidates: string[] = [];
    const pushCandidate = (value?: string): void => {
        if (!value) return;
        const trimmed = value.trim();
        if (trimmed.length === 0) return;
        candidates.push(trimmed);
    };

    pushCandidate(message.canonicalYoutubeUrl);
    pushCandidate(message.youtubeUrl);
    pushCandidate(message.pageUrl);
    pushCandidate(sender.tab?.url);
    if (message.url && isYoutubePageUrl(message.url)) {
        pushCandidate(message.url);
    }

    for (const candidate of candidates) {
        const normalized = normalizeYouTubeUrl(candidate);
        if (normalized) {
            return normalized;
        }
        if (isYoutubePageUrl(candidate)) {
            return candidate;
        }
    }

    return null;
}

function normalizeYouTubeUrl(inputUrl: string): string | null {
    let parsed: URL;
    try {
        parsed = new URL(inputUrl);
    } catch {
        return null;
    }

    const host = parsed.hostname.toLowerCase();
    if (!(host.endsWith("youtube.com")
        || host === "youtu.be"
        || host === "m.youtube.com"
        || host.endsWith("youtube-nocookie.com"))) {
        return null;
    }

    const toWatch = (id: string): string => `https://www.youtube.com/watch?v=${encodeURIComponent(id)}`;
    const firstPathPart = (): string => parsed.pathname.split("/").filter(Boolean)[0] || "";
    const secondPathPart = (): string => parsed.pathname.split("/").filter(Boolean)[1] || "";

    if (host === "youtu.be") {
        const id = firstPathPart();
        return id ? toWatch(id) : null;
    }

    if (parsed.pathname === "/redirect") {
        const q = parsed.searchParams.get("q");
        return q ? normalizeYouTubeUrl(q) : null;
    }

    if (parsed.pathname === "/watch") {
        const id = parsed.searchParams.get("v");
        return id ? toWatch(id) : null;
    }

    if (parsed.pathname.startsWith("/shorts/") || parsed.pathname.startsWith("/embed/") || parsed.pathname.startsWith("/live/")) {
        const id = secondPathPart();
        return id ? toWatch(id) : null;
    }

    // Keep unknown YouTube URL shapes so native host can still try yt-dlp extraction.
    return parsed.toString();
}

function nativeDownloadResponse(result: SendResult, fallbackRequestId: string): Record<string, unknown> {
    const payload = (result.response && typeof result.response === "object")
        ? getPayload(result.response)
        : undefined;

    return {
        success: result.success,
        error: result.error,
        requestId: result.requestId || fallbackRequestId,
        nativeType: result.response?.["type"] ?? "",
        outputPath: typeof payload?.["outputPath"] === "string" ? payload["outputPath"] : "",
        fileName: typeof payload?.["fileName"] === "string" ? payload["fileName"] : "",
        runner: typeof payload?.["runner"] === "string" ? payload["runner"] : "",
        mode: typeof payload?.["mode"] === "string" ? payload["mode"] : ""
    };
}

async function sendYouTubeDownload(
    youtubeUrl: string,
    message: ExtMessage,
    sender: chrome.runtime.MessageSender
): Promise<SendResult> {
    const tab = sender.tab;
    const normalizedYoutubeUrl = normalizeYouTubeUrl(youtubeUrl) || youtubeUrl;
    const detectedVideoUrl = message.detectedVideoUrl || message.url || message.videoUrl || "";
    const clickTsIso = message.clickTsIso || nowIso();
    const pageUrl = message.pageUrl || tab?.url || normalizedYoutubeUrl;
    const ytHeaders = await buildHeadersWithCookies(
        normalizedYoutubeUrl,
        message.headers || {}
    );

    appendDebugLog("info", "gui.youtube.download.request", {
        clickTsIso,
        tabId: tab?.id,
        tabUrl: tab?.url || "",
        pageUrl,
        detectedVideoUrl,
        canonicalYoutubeUrl: normalizedYoutubeUrl,
        hasCookieHeader: !!ytHeaders["Cookie"],
        cookieLength: ytHeaders["Cookie"]?.length || 0,
        userAgent: ytHeaders["User-Agent"] || "",
        referer: pageUrl
    });

    return postToNative("add_youtube_download", {
        videoUrl: normalizedYoutubeUrl,
        filename: message.filename,
        title: message.title || tab?.title || "",
        quality: message.quality || "",
        referrer: pageUrl,
        headers: ytHeaders,
        pageUrl,
        clickTsIso,
        detectedVideoUrl,
        canonicalYoutubeUrl: normalizedYoutubeUrl,
        requestSource: message.streamSource || "extension"
    });
}

function normalizeStreamSource(source?: string): string {
    return (source || "unknown").toLowerCase();
}

function getStreamSourceScore(source?: string): number {
    const normalized = normalizeStreamSource(source);
    if (normalized === "webrequest") return 950_000;
    if (normalized === "fetch" || normalized === "xhr") return 900_000;
    if (normalized.endsWith("_api")) return 500_000;
    if (normalized === "json_parse" || normalized === "json_parse_nested") return 450_000;
    if (normalized === "global_var" || normalized === "ytplayer_config" || normalized === "player_api") return 350_000;
    return 150_000;
}

function isPlausibleYoutubeExpiry(parsed: URL): boolean {
    const expire = parseIntParam(parsed, "expire");
    if (expire === undefined) return true;

    // Valid googlevideo stream URLs are usually short-lived (hours, not months/years).
    const nowEpoch = Math.floor(Date.now() / 1000);
    const maxFuture = nowEpoch + (7 * 24 * 60 * 60);
    const minPast = nowEpoch - (24 * 60 * 60);
    return expire >= minPast && expire <= maxFuture;
}

// Track response Content-Type by requestId for non-YouTube media detection
const responseContentTypes = new Map<string, string>();

function buildStreamCandidate(
    urlValue: string,
    tabId: number,
    contentType?: string,
    source?: string
): StreamCandidate | null {
    if (tabId < 0) return null;
    let parsed: URL;
    try {
        parsed = new URL(urlValue);
    } catch {
        return null;
    }

    const host = parsed.hostname.toLowerCase();
    const ytHost = isYoutubeHost(host);
    const normalizedSource = normalizeStreamSource(source);
    const itag = parseIntParam(parsed, "itag");
    const profile = itag !== undefined ? YOUTUBE_ITAG_PROFILES[itag] : undefined;

    // YouTube uses mime=video%2Fmp4 which searchParams.get auto-decodes
    const rawMime = parsed.searchParams.get("mime");
    const mimeType = rawMime || contentType || undefined;
    const qualityLabel = profile?.qualityLabel
        || parsed.searchParams.get("quality_label")
        || parsed.searchParams.get("quality")
        || undefined;
    const height = profile?.height || parseHeightFromQuality(qualityLabel);

    let hasVideo = profile?.hasVideo ?? false;
    let hasAudio = profile?.hasAudio ?? false;

    // Infer from MIME type if no profile found
    if (!profile && mimeType) {
        const lower = mimeType.toLowerCase();
        if (lower.startsWith("video/")) hasVideo = true;
        if (lower.startsWith("audio/")) hasAudio = true;
    }

    // For YouTube hosts with /videoplayback, accept even if no known profile
    // (YouTube may use itag values not in our static table)
    const isVideoPlayback = ytHost && parsed.pathname.includes("/videoplayback");
    if (isVideoPlayback && !hasVideo && !hasAudio) {
        // Default to video if we can't determine type
        if (rawMime) {
            const decodedMime = decodeURIComponent(rawMime).toLowerCase();
            if (decodedMime.startsWith("video/")) hasVideo = true;
            else if (decodedMime.startsWith("audio/")) hasAudio = true;
            else hasVideo = true; // default assumption
        } else {
            hasVideo = true; // assume video for videoplayback URLs
        }
    }

    const isMediaByQuery = Boolean(
        mimeType
        || parsed.searchParams.get("clen")
        || parsed.searchParams.get("dur")
        || parsed.searchParams.get("mime")
    );

    if (ytHost && parsed.pathname.includes("/videoplayback") && !isPlausibleYoutubeExpiry(parsed)) {
        return null;
    }

    // For non-YouTube hosts, also accept if contentType indicates media
    const isMediaByContentType = contentType
        ? MEDIA_CONTENT_TYPES.some((prefix) => contentType.toLowerCase().startsWith(prefix))
        : false;

    if (!hasVideo && !hasAudio && !isMediaByQuery && !isMediaByContentType && !isVideoPlayback) {
        return null;
    }

    // Accept candidates from any host if we have media evidence
    if (!ytHost && !isDirectDownloadUrl(urlValue) && !isMediaByQuery && !isMediaByContentType) {
        return null;
    }

    return {
        url: urlValue,
        tabId,
        timestamp: Date.now(),
        host,
        source: normalizedSource,
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
    const existingIndex = list.findIndex((item) => item.url === candidate.url);
    if (existingIndex >= 0) {
        const existing = list[existingIndex];
        const existingScore = getStreamSourceScore(existing.source);
        const incomingScore = getStreamSourceScore(candidate.source);
        if (incomingScore <= existingScore) {
            return;
        }
        list[existingIndex] = candidate;
        list.sort((a, b) => b.timestamp - a.timestamp);
        tabStreamCandidates.set(candidate.tabId, list.slice(0, MAX_STREAM_CANDIDATES_PER_TAB));
        persistStreamCandidates();
        return;
    }

    list.push(candidate);
    list.sort((a, b) => b.timestamp - a.timestamp);
    tabStreamCandidates.set(candidate.tabId, list.slice(0, MAX_STREAM_CANDIDATES_PER_TAB));

    // Persist to session storage so stream URLs survive service worker restarts
    persistStreamCandidates();
}

function persistStreamCandidates(): void {
    const data: Record<string, StreamCandidate[]> = {};
    tabStreamCandidates.forEach((v, k) => {
        data[String(k)] = v;
    });
    chrome.storage.session?.set({ _streamCandidates: data }).catch(() => { /* ignore */ });
}

async function restoreStreamCandidates(): Promise<void> {
    try {
        const result = await chrome.storage.session?.get("_streamCandidates");
        const data = result?._streamCandidates as Record<string, StreamCandidate[]> | undefined;
        if (!data) return;
        for (const [key, candidates] of Object.entries(data)) {
            const tabId = Number(key);
            if (!isNaN(tabId) && Array.isArray(candidates)) {
                tabStreamCandidates.set(tabId, candidates);
            }
        }
        appendDebugLog("info", "stream_candidates.restored", {
            tabs: Object.keys(data).length,
            total: [...tabStreamCandidates.values()].reduce((s, l) => s + l.length, 0)
        });
    } catch { /* session storage might not be available */ }
}

// Restore persisted stream candidates on service worker startup
restoreStreamCandidates();

function scoreStreamCandidate(candidate: StreamCandidate, preferMuxed: boolean): number {
    const sourceScore = getStreamSourceScore(candidate.source);
    const muxedBonus = preferMuxed && candidate.muxed ? 1_000_000 : 0;
    const audioBonus = preferMuxed && candidate.hasAudio ? 250_000 : 0;
    const videoBonus = candidate.hasVideo ? 100_000 : 0;
    const heightScore = candidate.height * 1000;
    const recencyScore = Math.floor(candidate.timestamp / 1000);
    return sourceScore + muxedBonus + audioBonus + videoBonus + heightScore + recencyScore;
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

function resolveBestAudioCandidate(
    tabId: number,
    pageUrl?: string
): StreamCandidate | null {
    const all = tabStreamCandidates.get(tabId) || [];
    if (all.length === 0) return null;

    const audioOnly = filterCandidatesForPage(
        all.filter((candidate) => candidate.hasAudio && !candidate.hasVideo),
        pageUrl
    );
    if (audioOnly.length === 0) return null;

    // Prefer highest-quality audio (itag 251 > 250 > 249 > 140)
    const preferredItags = [251, 250, 249, 140];
    for (const itag of preferredItags) {
        const match = audioOnly.find((c) => c.itag === itag);
        if (match) return match;
    }

    // Fallback: sort by timestamp (most recent first)
    return audioOnly.sort((a, b) => b.timestamp - a.timestamp)[0] || null;
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
    void installGoogleVideoHeaderRules();
});

chrome.runtime.onStartup.addListener(() => {
    void probeNativeHost();
});

// ─── CRITICAL: Inject Referer/Origin headers for googlevideo.com downloads ───
// Without these headers, googlevideo.com returns HTTP 403 Forbidden.
// MV3 requires declarativeNetRequest instead of blocking webRequest.
async function installGoogleVideoHeaderRules(): Promise<void> {
    const RULE_ID_REFERER = 1;
    const RULE_ID_ORIGIN = 2;

    try {
        // Remove old rules first
        await chrome.declarativeNetRequest.updateDynamicRules({
            removeRuleIds: [RULE_ID_REFERER, RULE_ID_ORIGIN]
        });

        // Add header injection rules
        await chrome.declarativeNetRequest.updateDynamicRules({
            addRules: [
                {
                    id: RULE_ID_REFERER,
                    priority: 1,
                    action: {
                        type: chrome.declarativeNetRequest.RuleActionType.MODIFY_HEADERS,
                        requestHeaders: [
                            {
                                header: "Referer",
                                operation: chrome.declarativeNetRequest.HeaderOperation.SET,
                                value: "https://www.youtube.com/"
                            }
                        ]
                    },
                    condition: {
                        urlFilter: "*://*.googlevideo.com/*",
                        resourceTypes: [
                            chrome.declarativeNetRequest.ResourceType.XMLHTTPREQUEST,
                            chrome.declarativeNetRequest.ResourceType.MEDIA,
                            chrome.declarativeNetRequest.ResourceType.OTHER
                        ]
                    }
                },
                {
                    id: RULE_ID_ORIGIN,
                    priority: 1,
                    action: {
                        type: chrome.declarativeNetRequest.RuleActionType.MODIFY_HEADERS,
                        requestHeaders: [
                            {
                                header: "Origin",
                                operation: chrome.declarativeNetRequest.HeaderOperation.SET,
                                value: "https://www.youtube.com"
                            }
                        ]
                    },
                    condition: {
                        urlFilter: "*://*.googlevideo.com/*",
                        resourceTypes: [
                            chrome.declarativeNetRequest.ResourceType.XMLHTTPREQUEST,
                            chrome.declarativeNetRequest.ResourceType.MEDIA,
                            chrome.declarativeNetRequest.ResourceType.OTHER
                        ]
                    }
                }
            ]
        });

        appendDebugLog("info", "header_rules.installed", { rules: 2 });
    } catch (error) {
        appendDebugLog("error", "header_rules.install_failed", { error: safeString(error) });
    }
}

chrome.tabs.onRemoved.addListener((tabId) => {
    tabStreamCandidates.delete(tabId);
    persistStreamCandidates();
});

chrome.contextMenus.onClicked.addListener((info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab) => {
    const url = info.linkUrl || info.srcUrl || info.pageUrl;
    if (!url) return;
    if (isYoutubePageUrl(url)) {
        appendDebugLog("warn", "context_menu.youtube_page_blocked", { url });
        return;
    }

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
                    const senderYoutubeUrl = getMessageYoutubePageUrl(message, sender);
                    const urlLooksYoutubeVideo = isYoutubePageUrl(message.url) || isGoogleVideoUrl(message.url);
                    const explicitYoutubeRequest = !!message.youtubeUrl
                        || !!message.canonicalYoutubeUrl
                        || message.streamSource === "gui-overlay"
                        || message.streamSource === "popup-resource";
                    const requiresYoutubeFlow = urlLooksYoutubeVideo || explicitYoutubeRequest;

                    if (senderYoutubeUrl) {
                        const ytResult = await sendYouTubeDownload(senderYoutubeUrl, message, sender);
                        sendResponse(nativeDownloadResponse(ytResult, requestId));
                        return;
                    }

                    if (requiresYoutubeFlow) {
                        sendResponse({
                            success: false,
                            error: "Unable to resolve YouTube page URL for this stream. Open the video tab and try again.",
                            requestId
                        });
                        return;
                    }

                    const tab = sender.tab;
                    const headersWithCookies = await buildHeadersWithCookies(
                        message.url,
                        message.headers || {}
                    );
                    const result = await postToNative("add_download", {
                        url: message.url,
                        filename: message.filename,
                        referrer: tab?.url || "",
                        headers: headersWithCookies
                    });

                    if (result.success) {
                        sendResponse({
                            success: true,
                            error: result.error,
                            requestId: result.requestId || requestId
                        });
                        return;
                    }

                    // Never use chrome.downloads fallback for YouTube stream-like URLs.
                    if (isGoogleVideoUrl(message.url) || requiresYoutubeFlow) {
                        sendResponse({
                            success: false,
                            error: result.error || "MyDM native host is required for YouTube stream downloads",
                            requestId: result.requestId || requestId
                        });
                        return;
                    }

                    // FALLBACK: Use chrome.downloads API if NativeHost fails
                    appendDebugLog("info", "download.fallback_chrome", {
                        requestId,
                        url: (message.url as string).substring(0, 100),
                        nativeError: result.error
                    });
                    try {
                        const downloadId = await chromeDownloadFallback(
                            message.url as string,
                            message.filename as string | undefined
                        );
                        sendResponse({ success: true, requestId, downloadId });
                    } catch (dlErr) {
                        sendResponse({
                            success: false,
                            error: `Download failed: ${safeString(dlErr)}`,
                            requestId
                        });
                    }
                    return;
                }
                case "download_youtube": {
                    const youtubeUrl = message.youtubeUrl || message.url || "";
                    if (!youtubeUrl) {
                        sendResponse({ success: false, error: "Missing YouTube URL", requestId });
                        return;
                    }
                    if (!isYoutubePageUrl(youtubeUrl)) {
                        sendResponse({ success: false, error: "Invalid YouTube page URL", requestId });
                        return;
                    }

                    const ytResult = await sendYouTubeDownload(youtubeUrl, message, sender);
                    sendResponse(nativeDownloadResponse(ytResult, requestId));
                    return;
                }
                case "download_media": {
                    if (!message.manifestUrl) {
                        sendResponse({ success: false, error: "Missing manifestUrl", requestId });
                        return;
                    }

                    const tab = sender.tab;
                    const mediaHeaders = await buildHeadersWithCookies(
                        message.manifestUrl,
                        message.headers || {}
                    );
                    const result = await postToNative("add_media_download", {
                        manifestUrl: message.manifestUrl,
                        mediaType: message.mediaType || "hls",
                        quality: message.quality,
                        title: message.title || tab?.title || "",
                        referrer: tab?.url || "",
                        headers: mediaHeaders
                    });

                    sendResponse({
                        success: result.success,
                        error: result.error,
                        requestId: result.requestId || requestId
                    });
                    return;
                }
                case "download_video": {
                    if (!message.videoUrl) {
                        sendResponse({ success: false, error: "Missing videoUrl", requestId });
                        return;
                    }

                    const senderYoutubeUrl = getMessageYoutubePageUrl(message, sender);
                    if (senderYoutubeUrl) {
                        const ytResult = await sendYouTubeDownload(
                            senderYoutubeUrl,
                            message,
                            sender
                        );
                        sendResponse(nativeDownloadResponse(ytResult, requestId));
                        return;
                    }

                    if (isGoogleVideoUrl(message.videoUrl)) {
                        sendResponse({
                            success: false,
                            error: "Unable to resolve YouTube page URL for this stream. Open the video tab and try again.",
                            requestId
                        });
                        return;
                    }

                    const tab = sender.tab;
                    const videoHeaders = await buildHeadersWithCookies(
                        message.videoUrl,
                        message.headers || {}
                    );
                    const result = await postToNative("add_video_download", {
                        videoUrl: message.videoUrl,
                        audioUrl: message.audioUrl || "",
                        filename: message.filename,
                        quality: message.quality,
                        title: message.title || tab?.title || "",
                        referrer: tab?.url || "",
                        headers: videoHeaders
                    });

                    if (result.success) {
                        sendResponse({
                            success: true,
                            error: result.error,
                            requestId: result.requestId || requestId
                        });
                        return;
                    }

                    if (isGoogleVideoUrl(message.videoUrl)) {
                        sendResponse({
                            success: false,
                            error: result.error || "MyDM native host is required for YouTube stream downloads",
                            requestId: result.requestId || requestId
                        });
                        return;
                    }

                    // FALLBACK: Use chrome.downloads API if NativeHost fails
                    appendDebugLog("info", "download_video.fallback_chrome", {
                        requestId,
                        videoUrl: (message.videoUrl as string).substring(0, 100),
                        nativeError: result.error
                    });
                    try {
                        const downloadId = await chromeDownloadFallback(
                            message.videoUrl as string,
                            message.filename as string | undefined
                        );
                        sendResponse({ success: true, requestId, downloadId });
                    } catch (dlErr) {
                        sendResponse({
                            success: false,
                            error: `Download failed: ${safeString(dlErr)}`,
                            requestId
                        });
                    }
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

                    // If the resolved stream is not muxed, also find the best audio stream
                    let audioUrl = "";
                    if (!resolved.muxed && resolved.hasVideo) {
                        const audio = resolveBestAudioCandidate(tabId, pageUrl);
                        if (audio) {
                            audioUrl = audio.url;
                        }
                    }

                    appendDebugLog("info", "stream.resolved", {
                        requestId,
                        tabId,
                        pageUrl,
                        url: resolved.url,
                        audioUrl: audioUrl || undefined,
                        itag: resolved.itag,
                        quality: resolved.qualityLabel,
                        muxed: resolved.muxed
                    });

                    sendResponse({
                        success: true,
                        requestId,
                        url: resolved.url,
                        audioUrl,
                        quality: resolved.qualityLabel || "",
                        mimeType: resolved.mimeType || "",
                        muxed: resolved.muxed
                    });
                    return;
                }
                case "list_stream_qualities": {
                    const tabId = sender.tab?.id;
                    if (typeof tabId !== "number") {
                        sendResponse({ success: false, error: "No tab context", requestId });
                        return;
                    }

                    const pageUrl = message.pageUrl || sender.tab?.url || "";
                    const allCandidates = tabStreamCandidates.get(tabId) || [];
                    const candidates = filterCandidatesForPage(allCandidates, pageUrl);

                    // Build list of unique video qualities + audio
                    const seenQualities = new Set<string>();
                    const qualities: Array<{
                        url: string;
                        audioUrl: string;
                        itag: number | undefined;
                        quality: string;
                        mimeType: string;
                        muxed: boolean;
                        height: number;
                        hasVideo: boolean;
                        hasAudio: boolean;
                    }> = [];

                    // Sort: muxed first, then by height descending
                    const sorted = [...candidates].sort((a, b) => {
                        if (a.muxed !== b.muxed) return a.muxed ? -1 : 1;
                        return b.height - a.height;
                    });

                    for (const c of sorted) {
                        if (!c.hasVideo) continue; // skip audio-only for the main list
                        const key = `${c.qualityLabel || c.height}-${c.muxed ? "m" : "s"}`;
                        if (seenQualities.has(key)) continue;
                        seenQualities.add(key);

                        let audioUrl = "";
                        if (!c.muxed) {
                            const audio = resolveBestAudioCandidate(tabId, pageUrl);
                            if (audio) audioUrl = audio.url;
                        }

                        qualities.push({
                            url: c.url,
                            audioUrl,
                            itag: c.itag,
                            quality: c.qualityLabel || `${c.height}p`,
                            mimeType: c.mimeType || "",
                            muxed: c.muxed,
                            height: c.height,
                            hasVideo: c.hasVideo,
                            hasAudio: c.hasAudio
                        });
                    }

                    appendDebugLog("info", "stream.list_qualities", {
                        tabId,
                        count: qualities.length,
                        qualities: qualities.map((q) => q.quality)
                    });

                    sendResponse({ success: true, requestId, qualities });
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
                case "report_stream_url": {
                    // Receive stream URLs captured by the MAIN world inject script
                    const reportedUrl = message.url;
                    const reportTabId = sender.tab?.id;
                    if (typeof reportedUrl === "string" && typeof reportTabId === "number") {
                        const candidate = buildStreamCandidate(
                            reportedUrl,
                            reportTabId,
                            undefined,
                            message.streamSource
                        );
                        if (candidate) {
                            rememberStreamCandidate(candidate);
                            appendDebugLog("info", "stream.accepted", {
                                tabId: reportTabId,
                                url: reportedUrl.substring(0, 100),
                                source: candidate.source,
                                itag: candidate.itag,
                                quality: candidate.qualityLabel,
                                muxed: candidate.muxed,
                                hasVideo: candidate.hasVideo,
                                hasAudio: candidate.hasAudio
                            });
                        } else {
                            appendDebugLog("warn", "stream.rejected_by_filter", {
                                tabId: reportTabId,
                                url: reportedUrl.substring(0, 120),
                                source: message.streamSource || "unknown"
                            });
                        }
                    }
                    sendResponse({ success: true, requestId });
                    return;
                }
                case "list_youtube_formats": {
                    const youtubeUrl = message.youtubeUrl || message.url || "";
                    if (!youtubeUrl) {
                        sendResponse({ success: false, error: "Missing YouTube URL", requestId });
                        return;
                    }

                    appendDebugLog("info", "list_youtube_formats.start", { requestId, youtubeUrl });
                    const fmtResult = await postToNative("list_youtube_formats", {
                        videoUrl: youtubeUrl,
                        referrer: sender.tab?.url || "https://www.youtube.com/"
                    });

                    if (fmtResult.success && fmtResult.response) {
                        const payload = (fmtResult.response as Record<string, unknown>)["payload"] as Record<string, unknown> | undefined;
                        sendResponse({
                            success: true,
                            requestId: fmtResult.requestId || requestId,
                            formats: payload?.["formats"] || [],
                            title: payload?.["title"] || "",
                            duration: payload?.["duration"] || 0
                        });
                    } else {
                        sendResponse({
                            success: false,
                            error: fmtResult.error || "Failed to query formats",
                            requestId: fmtResult.requestId || requestId
                        });
                    }
                    return;
                }
                case "download_youtube_format": {
                    const youtubeUrl = message.youtubeUrl || message.url || "";
                    const formatId = message.formatId || "";
                    if (!youtubeUrl) {
                        sendResponse({ success: false, error: "Missing YouTube URL", requestId });
                        return;
                    }
                    if (!formatId) {
                        sendResponse({ success: false, error: "Missing format ID", requestId });
                        return;
                    }

                    const tab = sender.tab;
                    const ytHeaders = await buildHeadersWithCookies(
                        youtubeUrl,
                        message.headers || {}
                    );
                    appendDebugLog("info", "download_youtube_format.start", { requestId, youtubeUrl, formatId });
                    const dlResult = await postToNative("download_youtube_format", {
                        videoUrl: youtubeUrl,
                        formatId,
                        filename: message.filename,
                        title: message.title || tab?.title || "",
                        referrer: tab?.url || "https://www.youtube.com/",
                        headers: ytHeaders
                    });

                    if (dlResult.success) {
                        const payload = (dlResult.response as Record<string, unknown> | undefined)?.["payload"] as Record<string, unknown> | undefined;
                        sendResponse({
                            success: true,
                            requestId: dlResult.requestId || requestId,
                            outputPath: payload?.["outputPath"] || "",
                            fileName: payload?.["fileName"] || "",
                            runner: payload?.["runner"] || "",
                            mode: "download_youtube_format"
                        });
                    } else {
                        sendResponse({
                            success: false,
                            error: dlResult.error || "Download failed",
                            requestId: dlResult.requestId || requestId
                        });
                    }
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
    // Capture Content-Type headers from responses to detect media streams
    chrome.webRequest.onHeadersReceived.addListener(
        (details) => {
            if (!details.responseHeaders) return undefined;
            for (const header of details.responseHeaders) {
                if (header.name.toLowerCase() === "content-type" && header.value) {
                    const ct = header.value.toLowerCase();
                    if (MEDIA_CONTENT_TYPES.some((prefix) => ct.startsWith(prefix))) {
                        responseContentTypes.set(details.requestId, header.value);
                    }
                    break;
                }
            }
            return undefined;
        },
        { urls: ["<all_urls>"], types: ["xmlhttprequest", "media", "other"] },
        ["responseHeaders"]
    );

    chrome.webRequest.onCompleted.addListener(
        (details) => {
            const url = details.url.toLowerCase();
            const contentType = responseContentTypes.get(details.requestId);
            responseContentTypes.delete(details.requestId);

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

            const candidate = buildStreamCandidate(details.url, details.tabId, contentType, "webrequest");
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
