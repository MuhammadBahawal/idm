interface MediaQuality {
    resolution: string;
    bandwidth: number;
    url: string;
    codecs?: string;
}

interface RuntimeResponse {
    success?: boolean;
    error?: string;
    rawError?: string;
    connected?: boolean;
    disconnectReason?: string;
    requestId?: string;
    url?: string;
    audioUrl?: string;
    quality?: string;
    mimeType?: string;
    muxed?: boolean;
    qualities?: StreamQualityOption[];
    nativeType?: string;
    outputPath?: string;
    fileName?: string;
    runner?: string;
    mode?: string;
}

interface SendDownloadOptions {
    suppressErrorToast?: boolean;
}

interface StreamQualityOption {
    url: string;
    audioUrl: string;
    quality: string;
    mimeType: string;
    muxed: boolean;
    height: number;
    hasVideo: boolean;
    hasAudio: boolean;
}

interface YouTubeFormat {
    formatId: string;
    qualityLabel: string;
    height: number;
    ext: string;
    filesize: number;
    vcodec: string;
    acodec: string;
    hasVideo: boolean;
    hasAudio: boolean;
    muxed: boolean;
    fps: number;
}

interface OverlayController {
    video: HTMLVideoElement;
    host: HTMLDivElement;
    button: HTMLButtonElement;
    hideTimer: ReturnType<typeof setTimeout> | null;
    rafHandle: number | null;
    pointerOnVideo: boolean;
    pointerOnButton: boolean;
    visible: boolean;
    resizeObserver: ResizeObserver | null;
    cleanup: () => void;
}

interface ImageOverlayController {
    image: HTMLImageElement;
    host: HTMLDivElement;
    button: HTMLButtonElement;
    hideTimer: ReturnType<typeof setTimeout> | null;
    rafHandle: number | null;
    pointerOnImage: boolean;
    pointerOnButton: boolean;
    visible: boolean;
    resizeObserver: ResizeObserver | null;
    cleanup: () => void;
}

const OVERLAY_Z_INDEX = 2147483647;
const HIDE_DELAY_MS = 550;
const MESSAGE_TIMEOUT_MS = 10000;
const MAX_RESOURCE_REPORT = 120;
const MIN_IMAGE_OVERLAY_WIDTH = 90;
const MIN_IMAGE_OVERLAY_HEIGHT = 70;
const DOWNLOADABLE_EXT_REGEX = /\.(zip|rar|7z|tar|gz|bz2|xz|iso|exe|msi|dmg|deb|rpm|apk|pdf|docx?|xlsx?|pptx?|txt|csv|mp4|mkv|avi|mov|wmv|flv|webm|m4v|mp3|flac|wav|aac|ogg|wma|m4a|jpe?g|png|gif|bmp|svg|webp)(?:$|[?#])/i;

const overlayByVideo = new WeakMap<HTMLVideoElement, OverlayController>();
const activeOverlays = new Set<OverlayController>();
const imageOverlayByElement = new WeakMap<HTMLImageElement, ImageOverlayController>();
const activeImageOverlays = new Set<ImageOverlayController>();
const reportedResources = new Set<string>();

let scanTimer: ReturnType<typeof setTimeout> | null = null;
let lastUrl = location.href;

function genRequestId(prefix: string): string {
    const suffix = (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function")
        ? crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    return `${prefix}-${suffix}`;
}

function log(level: "info" | "warn" | "error", event: string, data: Record<string, unknown> = {}): void {
    const payload = { event, ...data };
    if (level === "error") {
        console.error("[MyDM]", payload);
    } else if (level === "warn") {
        console.warn("[MyDM]", payload);
    } else {
        console.log("[MyDM]", payload);
    }
}

function buildRequestHeaders(): Record<string, string> {
    const headers: Record<string, string> = {
        "Referer": location.href,
        "User-Agent": navigator.userAgent
    };
    if (document.cookie && document.cookie.trim().length > 0) {
        headers["Cookie"] = document.cookie;
    }
    return headers;
}

function sendRuntimeMessage<T extends RuntimeResponse>(message: Record<string, unknown>): Promise<T> {
    return sendRuntimeMessageWithTimeout<T>(message, MESSAGE_TIMEOUT_MS);
}

function sendRuntimeMessageLong<T extends RuntimeResponse>(message: Record<string, unknown>): Promise<T> {
    return sendRuntimeMessageWithTimeout<T>(message, 300_000); // 5 minutes for yt-dlp ops
}

function sendRuntimeMessageWithTimeout<T extends RuntimeResponse>(message: Record<string, unknown>, timeoutMs: number): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        let done = false;
        const timer = setTimeout(() => {
            if (done) return;
            done = true;
            reject(new Error("Timed out waiting for extension response"));
        }, timeoutMs);

        chrome.runtime.sendMessage(message, (response: T) => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            if (chrome.runtime.lastError) {
                reject(new Error(chrome.runtime.lastError.message));
                return;
            }
            resolve(response);
        });
    });
}

function sanitizeFileName(name: string): string {
    return name.replace(/[<>:"/\\|?*]+/g, "").trim() || "download";
}

function getMediaUrl(video: HTMLVideoElement): string | null {
    if (video.currentSrc && video.currentSrc.trim().length > 0) return video.currentSrc;
    if (video.src && video.src.trim().length > 0) return video.src;

    const source = video.querySelector("source[src]") as HTMLSourceElement | null;
    if (source?.src) return source.src;

    return null;
}

function isHttpUrl(url: string | null | undefined): url is string {
    if (!url) return false;
    try {
        const parsed = new URL(url);
        return parsed.protocol === "http:" || parsed.protocol === "https:";
    } catch {
        return false;
    }
}

function inferExtensionFromUrl(url: string): string | null {
    try {
        const parsed = new URL(url);
        const file = parsed.pathname.split("/").pop() || "";
        const dot = file.lastIndexOf(".");
        if (dot <= 0) return null;
        const ext = file.slice(dot + 1).toLowerCase();
        if (!ext || ext.length > 8) return null;
        return ext;
    } catch {
        return null;
    }
}

function inferExtensionFromMime(mimeType?: string): string {
    if (!mimeType) return "mp4";
    const lower = mimeType.toLowerCase();
    if (lower.includes("webm")) return "webm";
    if (lower.includes("audio/mp4")) return "m4a";
    if (lower.includes("mp4")) return "mp4";
    return "mp4";
}

function inferFileNameFromUrl(url: string, fallbackBase: string): string {
    try {
        const parsed = new URL(url);
        const raw = parsed.pathname.split("/").pop() || "";
        const clean = decodeURIComponent(raw).trim();
        if (clean.length > 0) {
            return sanitizeFileName(clean);
        }
    } catch {
        // ignore
    }
    return sanitizeFileName(fallbackBase);
}

function isYoutubePage(): boolean {
    const host = location.hostname.toLowerCase();
    return host.includes("youtube.com")
        || host === "youtu.be"
        || host === "m.youtube.com"
        || host.endsWith("youtube-nocookie.com");
}

function buildCanonicalWatchUrl(videoId: string): string {
    return `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`;
}

function extractVideoIdFromPath(pathname: string, segmentIndex: number): string | null {
    const parts = pathname.split("/").filter(Boolean);
    if (parts.length <= segmentIndex) return null;
    const value = parts[segmentIndex].trim();
    return value.length > 0 ? value : null;
}

function normalizeYoutubeLikeUrl(input: string): string | null {
    let parsed: URL;
    try {
        parsed = new URL(input);
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

    if (host === "youtu.be") {
        const id = extractVideoIdFromPath(parsed.pathname, 0);
        return id ? buildCanonicalWatchUrl(id) : null;
    }

    if (parsed.pathname === "/redirect") {
        const q = parsed.searchParams.get("q");
        if (!q) return null;
        return normalizeYoutubeLikeUrl(q);
    }

    if (parsed.pathname === "/watch") {
        const id = parsed.searchParams.get("v");
        return id ? buildCanonicalWatchUrl(id) : null;
    }

    if (parsed.pathname.startsWith("/shorts/")) {
        const id = extractVideoIdFromPath(parsed.pathname, 1);
        return id ? buildCanonicalWatchUrl(id) : null;
    }

    if (parsed.pathname.startsWith("/embed/")) {
        const id = extractVideoIdFromPath(parsed.pathname, 1);
        return id ? buildCanonicalWatchUrl(id) : null;
    }

    if (parsed.pathname.startsWith("/live/")) {
        const id = extractVideoIdFromPath(parsed.pathname, 1);
        return id ? buildCanonicalWatchUrl(id) : null;
    }

    return parsed.toString();
}

function getCanonicalYoutubeVideoUrl(): string | null {
    return normalizeYoutubeLikeUrl(location.href) || location.href;
}

async function resolveFallbackVideoSource(): Promise<RuntimeResponse | null> {
    try {
        const resolved = await sendRuntimeMessage<RuntimeResponse>({
            type: "resolve_video_source",
            requestId: genRequestId("resolve"),
            pageUrl: location.href,
            preferMuxed: true
        });
        if (resolved?.success && isHttpUrl(resolved.url)) {
            return resolved;
        }
        if (resolved?.error) {
            log("warn", "resolve_video_source_failed", { error: resolved.error, pageUrl: location.href });
        }
        return null;
    } catch (error) {
        log("warn", "resolve_video_source_error", { error: String(error), pageUrl: location.href });
        return null;
    }
}

async function listStreamQualities(): Promise<StreamQualityOption[]> {
    try {
        const response = await sendRuntimeMessage<RuntimeResponse>({
            type: "list_stream_qualities",
            requestId: genRequestId("qualities"),
            pageUrl: location.href
        });
        const raw = response.qualities;
        if (!Array.isArray(raw)) {
            return [];
        }
        return raw.filter((q): q is StreamQualityOption => {
            return Boolean(
                q &&
                typeof q.url === "string" &&
                typeof q.audioUrl === "string" &&
                typeof q.quality === "string" &&
                typeof q.mimeType === "string" &&
                typeof q.muxed === "boolean"
            );
        });
    } catch (error) {
        log("warn", "list_stream_qualities_failed", {
            error: String(error),
            pageUrl: location.href
        });
        return [];
    }
}

function pickStreamQuality(qualities: StreamQualityOption[]): Promise<StreamQualityOption | null> {
    return new Promise((resolve) => {
        document.querySelector(".mydm-quality-modal")?.remove();
        document.querySelector(".mydm-modal-backdrop")?.remove();

        const modal = document.createElement("div");
        modal.className = "mydm-quality-modal";

        const header = document.createElement("div");
        header.className = "mydm-modal-header";
        header.innerHTML = "<span>Select Video Quality</span><button class=\"mydm-modal-close\">X</button>";
        modal.appendChild(header);

        const list = document.createElement("div");
        list.className = "mydm-quality-list";

        for (const q of qualities) {
            const qualityLabel = q.quality || (q.height > 0 ? `${q.height}p` : "Auto");
            const item = document.createElement("button");
            item.className = "mydm-quality-item";
            item.innerHTML = `
                <span class="mydm-q-resolution">${qualityLabel}</span>
                <span class="mydm-q-bitrate">${q.muxed ? "video+audio" : "video stream"}</span>
                <span class="mydm-q-codecs">${q.mimeType || "media"}</span>
            `;
            item.addEventListener("click", () => {
                cleanup();
                resolve(q);
            });
            list.appendChild(item);
        }

        const cancel = document.createElement("button");
        cancel.className = "mydm-quality-item";
        cancel.textContent = "Cancel";
        cancel.addEventListener("click", () => {
            cleanup();
            resolve(null);
        });
        list.appendChild(cancel);

        modal.appendChild(list);

        const backdrop = document.createElement("div");
        backdrop.className = "mydm-modal-backdrop";

        const cleanup = () => {
            modal.remove();
            backdrop.remove();
        };

        header.querySelector(".mydm-modal-close")?.addEventListener("click", () => {
            cleanup();
            resolve(null);
        });
        backdrop.addEventListener("click", () => {
            cleanup();
            resolve(null);
        });

        document.body.appendChild(backdrop);
        document.body.appendChild(modal);
    });
}

function formatFileSize(bytes: number): string {
    if (bytes <= 0) return "";
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

async function listYouTubeFormats(youtubeUrl: string): Promise<YouTubeFormat[]> {
    try {
        const requestId = genRequestId("ytfmt");
        const response = await sendRuntimeMessageLong<RuntimeResponse & { formats?: YouTubeFormat[] }>({
            type: "list_youtube_formats",
            url: youtubeUrl,
            youtubeUrl,
            requestId
        });

        if (!response?.success || !Array.isArray(response.formats)) {
            log("warn", "list_youtube_formats_failed", {
                error: response?.error || "No formats returned"
            });
            return [];
        }

        return response.formats;
    } catch (error) {
        log("error", "list_youtube_formats_error", { error: String(error) });
        return [];
    }
}

function pickYouTubeFormat(formats: YouTubeFormat[], videoTitle: string): Promise<YouTubeFormat | null> {
    return new Promise((resolve) => {
        document.querySelector(".mydm-quality-modal")?.remove();
        document.querySelector(".mydm-modal-backdrop")?.remove();

        const modal = document.createElement("div");
        modal.className = "mydm-quality-modal";
        modal.style.cssText = `
            position: fixed;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: ${OVERLAY_Z_INDEX};
            background: linear-gradient(145deg, #1a1a2e, #16213e);
            border: 1px solid rgba(99, 102, 241, 0.3);
            border-radius: 16px;
            box-shadow: 0 24px 64px rgba(0, 0, 0, 0.6), 0 0 0 1px rgba(255,255,255,0.05);
            color: #e2e8f0;
            font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
            min-width: 380px;
            max-width: 480px;
            max-height: 80vh;
            overflow: hidden;
            display: flex;
            flex-direction: column;
            animation: mydm-modal-in 0.25s ease-out;
        `;

        // Inject keyframes
        const styleEl = document.createElement("style");
        styleEl.textContent = `
            @keyframes mydm-modal-in {
                from { opacity: 0; transform: translate(-50%, -50%) scale(0.92); }
                to { opacity: 1; transform: translate(-50%, -50%) scale(1); }
            }
        `;
        document.head.appendChild(styleEl);

        // Header
        const header = document.createElement("div");
        header.style.cssText = `
            padding: 16px 20px;
            border-bottom: 1px solid rgba(99, 102, 241, 0.15);
            display: flex;
            align-items: center;
            justify-content: space-between;
        `;

        const titleEl = document.createElement("div");
        titleEl.style.cssText = "display: flex; flex-direction: column; gap: 4px; flex: 1; min-width: 0;";
        const titleText = document.createElement("span");
        titleText.textContent = "Download Video";
        titleText.style.cssText = "font-size: 15px; font-weight: 700; color: #fff;";
        const subtitleText = document.createElement("span");
        subtitleText.textContent = videoTitle.length > 55 ? videoTitle.substring(0, 55) + "..." : videoTitle;
        subtitleText.style.cssText = "font-size: 11px; color: #94a3b8; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;";
        titleEl.appendChild(titleText);
        titleEl.appendChild(subtitleText);

        const closeBtn = document.createElement("button");
        closeBtn.textContent = "✕";
        closeBtn.style.cssText = `
            background: none; border: none; color: #94a3b8; font-size: 18px;
            cursor: pointer; padding: 4px 8px; border-radius: 6px;
            transition: all 0.15s ease;
        `;
        closeBtn.addEventListener("mouseenter", () => { closeBtn.style.background = "rgba(255,255,255,0.1)"; closeBtn.style.color = "#fff"; });
        closeBtn.addEventListener("mouseleave", () => { closeBtn.style.background = "none"; closeBtn.style.color = "#94a3b8"; });

        header.appendChild(titleEl);
        header.appendChild(closeBtn);
        modal.appendChild(header);

        // Format list
        const list = document.createElement("div");
        list.style.cssText = `
            overflow-y: auto;
            max-height: 400px;
            padding: 8px 12px 12px;
        `;

        for (const fmt of formats) {
            const label = fmt.qualityLabel || (fmt.height > 0 ? `${fmt.height}p` : "Audio");
            const size = formatFileSize(fmt.filesize);
            const isAudioOnly = !fmt.hasVideo && fmt.hasAudio;
            const codecInfo = isAudioOnly
                ? (fmt.acodec !== "none" ? fmt.acodec : fmt.ext)
                : (fmt.vcodec !== "none" ? fmt.vcodec.split(".")[0] : fmt.ext);
            const typeLabel = isAudioOnly ? "🎵 Audio" : (fmt.muxed ? "🎬 Video+Audio" : "🎥 Video");
            const fpsText = fmt.fps && fmt.fps > 30 ? ` ${fmt.fps}fps` : "";

            const item = document.createElement("button");
            item.style.cssText = `
                width: 100%;
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 10px 14px;
                margin-bottom: 4px;
                background: rgba(255, 255, 255, 0.03);
                border: 1px solid rgba(255, 255, 255, 0.06);
                border-radius: 10px;
                color: #e2e8f0;
                cursor: pointer;
                font-family: inherit;
                font-size: 13px;
                transition: all 0.15s ease;
            `;
            item.addEventListener("mouseenter", () => {
                item.style.background = "rgba(99, 102, 241, 0.15)";
                item.style.borderColor = "rgba(99, 102, 241, 0.4)";
                item.style.transform = "translateX(2px)";
            });
            item.addEventListener("mouseleave", () => {
                item.style.background = "rgba(255, 255, 255, 0.03)";
                item.style.borderColor = "rgba(255, 255, 255, 0.06)";
                item.style.transform = "translateX(0)";
            });

            const leftCol = document.createElement("div");
            leftCol.style.cssText = "display: flex; align-items: center; gap: 12px;";

            const resolution = document.createElement("span");
            resolution.textContent = label + fpsText;
            resolution.style.cssText = "font-weight: 600; font-size: 14px; min-width: 70px;";

            const type = document.createElement("span");
            type.textContent = typeLabel;
            type.style.cssText = "font-size: 11px; color: #94a3b8;";

            leftCol.appendChild(resolution);
            leftCol.appendChild(type);

            const rightCol = document.createElement("div");
            rightCol.style.cssText = "display: flex; align-items: center; gap: 10px; color: #64748b; font-size: 11px;";

            if (codecInfo) {
                const codec = document.createElement("span");
                codec.textContent = codecInfo;
                codec.style.cssText = "padding: 2px 6px; background: rgba(255,255,255,0.06); border-radius: 4px; font-size: 10px;";
                rightCol.appendChild(codec);
            }

            const ext = document.createElement("span");
            ext.textContent = fmt.ext.toUpperCase();
            ext.style.cssText = "padding: 2px 6px; background: rgba(99, 102, 241, 0.15); color: #818cf8; border-radius: 4px; font-size: 10px; font-weight: 600;";
            rightCol.appendChild(ext);

            if (size) {
                const sizeEl = document.createElement("span");
                sizeEl.textContent = size;
                sizeEl.style.cssText = "min-width: 55px; text-align: right; color: #94a3b8;";
                rightCol.appendChild(sizeEl);
            }

            item.appendChild(leftCol);
            item.appendChild(rightCol);

            item.addEventListener("click", () => {
                cleanup();
                resolve(fmt);
            });
            list.appendChild(item);
        }

        modal.appendChild(list);

        // Backdrop
        const backdrop = document.createElement("div");
        backdrop.className = "mydm-modal-backdrop";
        backdrop.style.cssText = `
            position: fixed;
            top: 0; left: 0; right: 0; bottom: 0;
            background: rgba(0, 0, 0, 0.6);
            backdrop-filter: blur(4px);
            z-index: ${OVERLAY_Z_INDEX - 1};
        `;

        const cleanup = () => {
            modal.remove();
            backdrop.remove();
            styleEl.remove();
        };

        closeBtn.addEventListener("click", () => { cleanup(); resolve(null); });
        backdrop.addEventListener("click", () => { cleanup(); resolve(null); });

        document.body.appendChild(backdrop);
        document.body.appendChild(modal);
    });
}

function resolveUrl(url: string, baseUrl: string): string {
    try {
        return new URL(url, baseUrl).href;
    } catch {
        return url;
    }
}

function extractAttr(line: string, attr: string): string | null {
    const quotedMatch = line.match(new RegExp(`${attr}="([^"]*)"`));
    if (quotedMatch) return quotedMatch[1];
    const match = line.match(new RegExp(`${attr}=([^,\\s]+)`));
    return match ? match[1] : null;
}

function parseHlsMaster(content: string, baseUrl: string): MediaQuality[] {
    const qualities: MediaQuality[] = [];
    const lines = content.split("\n");

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (!line.startsWith("#EXT-X-STREAM-INF:")) continue;

        const bandwidth = extractAttr(line, "BANDWIDTH");
        const resolution = extractAttr(line, "RESOLUTION");
        const codecs = extractAttr(line, "CODECS");

        for (let j = i + 1; j < lines.length; j++) {
            const nextLine = lines[j].trim();
            if (!nextLine || nextLine.startsWith("#")) continue;

            qualities.push({
                resolution: resolution || "Unknown",
                bandwidth: parseInt(bandwidth || "0", 10),
                codecs: codecs || undefined,
                url: resolveUrl(nextLine, baseUrl)
            });
            break;
        }
    }

    return qualities.sort((a, b) => b.bandwidth - a.bandwidth);
}

function parseDashManifest(content: string, baseUrl: string): MediaQuality[] {
    const qualities: MediaQuality[] = [];
    try {
        const parser = new DOMParser();
        const doc = parser.parseFromString(content, "application/xml");
        const reps = doc.querySelectorAll("Representation[bandwidth]");
        reps.forEach((rep) => {
            const bandwidth = parseInt(rep.getAttribute("bandwidth") || "0", 10);
            const width = rep.getAttribute("width");
            const height = rep.getAttribute("height");
            const codecs = rep.getAttribute("codecs") || undefined;
            const repBase = rep.querySelector("BaseURL")?.textContent?.trim();
            const resolution = width && height ? `${width}x${height}` : "Unknown";

            qualities.push({
                resolution,
                bandwidth,
                codecs,
                url: repBase ? resolveUrl(repBase, baseUrl) : baseUrl
            });
        });
    } catch (error) {
        log("warn", "dash_parse_failed", { error: String(error) });
    }

    return qualities.sort((a, b) => b.bandwidth - a.bandwidth);
}

function showNotification(text: string, isError = false): void {
    const toast = document.createElement("div");
    toast.className = "mydm-toast";
    if (isError) {
        toast.style.background = "linear-gradient(135deg, #EF4444, #DC2626)";
        toast.style.boxShadow = "0 8px 24px rgba(239, 68, 68, 0.3)";
    }
    toast.textContent = text;
    document.body.appendChild(toast);

    setTimeout(() => {
        toast.classList.add("mydm-toast-fadeout");
        setTimeout(() => toast.remove(), 500);
    }, 3000);
}

function isUnsupportedMessageTypeError(error: string | undefined, messageType: string): boolean {
    if (!error || !messageType) return false;
    return error.includes(`Unsupported message type: ${messageType}`);
}

function isConfirmedYoutubeDownloadResult(response: RuntimeResponse | null | undefined): boolean {
    if (!response?.success) return false;
    const hasOutputPath = typeof response.outputPath === "string" && response.outputPath.trim().length > 0;
    const hasRunner = typeof response.runner === "string" && response.runner.trim().length > 0;
    const hasMode = typeof response.mode === "string" && response.mode.trim().length > 0;
    return hasOutputPath || hasRunner || hasMode;
}

async function sendDownloadToBackground(
    payload: Record<string, unknown>,
    options: SendDownloadOptions = {}
): Promise<RuntimeResponse> {
    const requestId = genRequestId("dl");
    try {
        const response = await sendRuntimeMessage<RuntimeResponse>({
            ...payload,
            requestId,
            headers: buildRequestHeaders()
        });
        if (response?.success) {
            return response;
        }
        const rawError = response?.error || "Unable to send download to MyDM";
        const isStaleBackground = rawError.includes("Unsupported message type: download_youtube");
        const finalError = isStaleBackground
            ? "Extension is outdated in this tab. Reload MyDM extension, refresh YouTube tab, and restart Chrome."
            : rawError;
        log("error", "download_request_rejected", {
            requestId,
            payloadType: typeof payload.type === "string" ? payload.type : "unknown",
            rawError,
            finalError
        });
        if (!options.suppressErrorToast) {
            showNotification(`ERROR: ${finalError}`, true);
        }
        return {
            ...(response || {}),
            success: false,
            error: finalError,
            rawError
        };
    } catch (error) {
        const rawError = String(error);
        if (!options.suppressErrorToast) {
            showNotification(`ERROR: ${rawError}`, true);
        }
        return { success: false, error: rawError, rawError };
    }
}

async function sendYoutubeDownloadWithCompatibility(
    youtubeUrl: string,
    fileName: string,
    clickTsIso: string,
    detectedVideoUrlAtClick: string
): Promise<RuntimeResponse> {
    const commonPayload = {
        youtubeUrl,
        filename: fileName,
        title: document.title,
        quality: "best",
        pageUrl: location.href,
        streamSource: "gui-overlay",
        clickTsIso,
        detectedVideoUrl: detectedVideoUrlAtClick || "",
        canonicalYoutubeUrl: youtubeUrl
    };

    const direct = await sendDownloadToBackground(
        {
            type: "download_youtube",
            url: detectedVideoUrlAtClick || "",
            ...commonPayload
        },
        { suppressErrorToast: true }
    );
    if (direct.success) {
        return direct;
    }

    const directRawError = direct.rawError || direct.error;
    if (!isUnsupportedMessageTypeError(directRawError, "download_youtube")) {
        return direct;
    }

    log("warn", "gui.youtube.compat_legacy_fallback", {
        pageUrl: location.href,
        youtubeUrl,
        firstError: directRawError || ""
    });

    // Compatibility path for stale service workers that do not know download_youtube.
    return sendDownloadToBackground(
        {
            type: "download_with_mydm",
            url: youtubeUrl,
            ...commonPayload
        },
        { suppressErrorToast: true }
    );
}

async function showQualityModal(manifestUrl: string, mediaType: "hls" | "dash"): Promise<void> {
    document.querySelector(".mydm-quality-modal")?.remove();
    document.querySelector(".mydm-modal-backdrop")?.remove();

    let qualities: MediaQuality[] = [];
    try {
        const res = await fetch(manifestUrl);
        const content = await res.text();
        qualities = mediaType === "hls"
            ? parseHlsMaster(content, manifestUrl)
            : parseDashManifest(content, manifestUrl);
    } catch (error) {
        log("warn", "manifest_fetch_failed", { manifestUrl, mediaType, error: String(error) });
    }

    if (qualities.length === 0) {
        const result = await sendDownloadToBackground({
            type: "download_media",
            manifestUrl,
            mediaType,
            title: document.title
        });
        if (result.success) showNotification("Sent to MyDM");
        return;
    }

    const modal = document.createElement("div");
    modal.className = "mydm-quality-modal";

    const header = document.createElement("div");
    header.className = "mydm-modal-header";
    header.innerHTML = "<span>MyDM - Select Quality</span><button class=\"mydm-modal-close\">x</button>";
    modal.appendChild(header);

    const list = document.createElement("div");
    list.className = "mydm-quality-list";
    for (const q of qualities) {
        const item = document.createElement("button");
        item.className = "mydm-quality-item";
        item.innerHTML = `
            <span class="mydm-q-resolution">${q.resolution}</span>
            <span class="mydm-q-bitrate">${(q.bandwidth / 1000).toFixed(0)} kbps</span>
            ${q.codecs ? `<span class="mydm-q-codecs">${q.codecs}</span>` : ""}
        `;
        item.addEventListener("click", async () => {
            const result = await sendDownloadToBackground({
                type: "download_media",
                manifestUrl: q.url,
                mediaType,
                quality: q.resolution,
                title: document.title
            });
            if (result.success) {
                showNotification(`Downloading ${q.resolution} with MyDM`);
            }
            modal.remove();
            backdrop.remove();
        });
        list.appendChild(item);
    }
    modal.appendChild(list);

    const backdrop = document.createElement("div");
    backdrop.className = "mydm-modal-backdrop";

    const close = () => {
        modal.remove();
        backdrop.remove();
    };

    header.querySelector(".mydm-modal-close")?.addEventListener("click", close);
    backdrop.addEventListener("click", close);

    document.body.appendChild(backdrop);
    document.body.appendChild(modal);
}

function updateOverlayPosition(controller: OverlayController): void {
    const video = controller.video;
    if (!video.isConnected) return;

    const rect = video.getBoundingClientRect();
    const tooSmall = rect.width < 120 || rect.height < 80;
    const offscreen = rect.bottom < 0 || rect.right < 0 || rect.top > window.innerHeight || rect.left > window.innerWidth;

    if (tooSmall || offscreen) {
        controller.button.classList.remove("visible");
        controller.visible = false;
        return;
    }

    controller.host.style.left = `${Math.round(rect.left)}px`;
    controller.host.style.top = `${Math.round(rect.top)}px`;
    controller.host.style.width = `${Math.round(rect.width)}px`;
    controller.host.style.height = `${Math.round(rect.height)}px`;
}

function ensureTracking(controller: OverlayController): void {
    if (controller.rafHandle !== null) return;
    const tick = () => {
        updateOverlayPosition(controller);

        if (controller.visible || controller.pointerOnButton || controller.pointerOnVideo) {
            controller.rafHandle = requestAnimationFrame(tick);
        } else {
            controller.rafHandle = null;
        }
    };
    controller.rafHandle = requestAnimationFrame(tick);
}

function injectVideoOverlay(video: HTMLVideoElement): void {
    if (overlayByVideo.has(video)) return;

    const host = document.createElement("div");
    host.className = "mydm-overlay-host";
    host.style.cssText = [
        "position:fixed",
        "left:0",
        "top:0",
        "width:0",
        "height:0",
        "pointer-events:none",
        `z-index:${OVERLAY_Z_INDEX}`
    ].join(";");

    const shadow = host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = `
        :host {
            position: fixed;
            left: 0;
            top: 0;
            width: 0;
            height: 0;
            pointer-events: none;
            z-index: ${OVERLAY_Z_INDEX};
        }
        .mydm-btn {
            position: absolute;
            top: 10px;
            right: 10px;
            z-index: ${OVERLAY_Z_INDEX};
            background: linear-gradient(135deg, #3B82F6, #2563EB);
            color: white;
            border: none;
            border-radius: 8px;
            padding: 8px 16px;
            font-size: 13px;
            font-weight: 600;
            font-family: 'Segoe UI', system-ui, sans-serif;
            cursor: pointer;
            pointer-events: auto;
            opacity: 0;
            visibility: hidden;
            transform: translateY(-2px) scale(0.98);
            transition: opacity 0.2s ease, transform 0.2s ease, visibility 0.2s ease;
            box-shadow: 0 4px 16px rgba(59, 130, 246, 0.4);
            white-space: nowrap;
        }
        .mydm-btn.visible {
            opacity: 1;
            visibility: visible;
            transform: translateY(0) scale(1);
        }
        .mydm-btn:hover {
            background: linear-gradient(135deg, #2563EB, #1D4ED8);
            transform: translateY(0) scale(1.05);
        }
        .mydm-btn:active {
            transform: scale(0.97);
        }
    `;
    shadow.appendChild(style);

    const button = document.createElement("button");
    button.className = "mydm-btn";
    button.textContent = "Download MyDM";
    button.title = "Download with MyDM";
    shadow.appendChild(button);

    document.documentElement.appendChild(host);

    const controller: OverlayController = {
        video,
        host,
        button,
        hideTimer: null,
        rafHandle: null,
        pointerOnVideo: false,
        pointerOnButton: false,
        visible: false,
        resizeObserver: null,
        cleanup: () => { }
    };

    const clearHide = () => {
        if (controller.hideTimer) {
            clearTimeout(controller.hideTimer);
            controller.hideTimer = null;
        }
    };

    const show = () => {
        clearHide();
        controller.visible = true;
        button.classList.add("visible");
        updateOverlayPosition(controller);
        ensureTracking(controller);
    };

    const scheduleHide = () => {
        clearHide();
        controller.hideTimer = setTimeout(() => {
            if (controller.pointerOnVideo || controller.pointerOnButton) return;
            controller.visible = false;
            button.classList.remove("visible");
        }, HIDE_DELAY_MS);
    };

    const onVideoEnter = () => {
        controller.pointerOnVideo = true;
        show();
    };
    const onVideoMove = () => show();
    const onVideoLeave = () => {
        controller.pointerOnVideo = false;
        scheduleHide();
    };
    const onBtnEnter = () => {
        controller.pointerOnButton = true;
        show();
    };
    const onBtnLeave = () => {
        controller.pointerOnButton = false;
        scheduleHide();
    };

    video.addEventListener("pointerenter", onVideoEnter, { passive: true });
    video.addEventListener("pointermove", onVideoMove, { passive: true });
    video.addEventListener("pointerleave", onVideoLeave, { passive: true });
    button.addEventListener("pointerenter", onBtnEnter, { passive: true });
    button.addEventListener("pointerleave", onBtnLeave, { passive: true });

    const onScrollOrResize = () => {
        if (controller.visible) {
            updateOverlayPosition(controller);
        }
    };
    window.addEventListener("scroll", onScrollOrResize, { passive: true });
    window.addEventListener("resize", onScrollOrResize, { passive: true });

    if (typeof ResizeObserver !== "undefined") {
        controller.resizeObserver = new ResizeObserver(() => updateOverlayPosition(controller));
        controller.resizeObserver.observe(video);
    }

    button.addEventListener("click", async (event: MouseEvent) => {
        event.stopPropagation();
        event.preventDefault();
        const clickTsIso = new Date().toISOString();
        const detectedVideoUrlAtClick = getMediaUrl(video);

        log("info", "gui.icon_click", {
            clickTsIso,
            pageUrl: location.href,
            detectedVideoUrlAtClick: detectedVideoUrlAtClick || ""
        });

        // Log connection status but do not block - chrome.downloads fallback works without NativeHost.
        try {
            const status = await sendRuntimeMessage<RuntimeResponse>({ type: "get_connection_status" });
            if (!status?.connected) {
                log("info", "native_host_not_connected_using_fallback", {
                    reason: status?.disconnectReason || "MyDM desktop app is not running"
                });
            }
        } catch (err) {
            log("warn", "connection_precheck_failed", { error: String(err) });
        }

        let url = detectedVideoUrlAtClick;
        let resolvedQuality: string | undefined;
        let resolvedMimeType: string | undefined;
        let resolvedMuxed = true;
        let resolvedAudioUrl: string | undefined;
        let youtubeRouteError = "";

        if (isYoutubePage()) {
            const youtubeUrl = getCanonicalYoutubeVideoUrl();
            if (!youtubeUrl) {
                showNotification("Unable to resolve YouTube video URL", true);
                return;
            }

            log("info", "gui.youtube_url_resolved", {
                clickTsIso,
                pageUrl: location.href,
                detectedVideoUrlAtClick: detectedVideoUrlAtClick || "",
                canonicalYoutubeUrl: youtubeUrl
            });

            // Query available formats from yt-dlp
            showNotification("Fetching available qualities...");
            const formats = await listYouTubeFormats(youtubeUrl);

            if (formats.length > 0) {
                // Show format picker for user to choose resolution
                const selectedFormat = await pickYouTubeFormat(formats, document.title || "YouTube Video");
                if (!selectedFormat) {
                    showNotification("Download cancelled", true);
                    return;
                }

                // Download with the exact format ID chosen by user — fire-and-forget
                const ytFilename = `${sanitizeFileName(document.title || "youtube_video")}.${selectedFormat.ext || "mp4"}`;
                showNotification(`Downloading ${selectedFormat.qualityLabel}: ${ytFilename}`);

                // Send download request in the background (don't block UI)
                sendRuntimeMessageLong<RuntimeResponse>({
                    type: "download_youtube_format",
                    url: youtubeUrl,
                    youtubeUrl,
                    formatId: selectedFormat.formatId,
                    filename: ytFilename,
                    title: document.title,
                    quality: selectedFormat.qualityLabel,
                    pageUrl: location.href,
                    streamSource: "gui-overlay",
                    clickTsIso,
                    detectedVideoUrl: detectedVideoUrlAtClick || "",
                    canonicalYoutubeUrl: youtubeUrl,
                    requestId: genRequestId("ytdl"),
                    headers: buildRequestHeaders()
                }).then(result => {
                    if (result?.success) {
                        const savedName = result.fileName || ytFilename;
                        log("info", "gui.youtube.format_download.done", { savedName, quality: selectedFormat.qualityLabel });
                        showNotification(`✅ Downloaded: ${savedName}`);
                    } else {
                        const err = result?.error || "Download failed";
                        log("error", "gui.youtube.format_download.fail", { error: err });
                        showNotification(`Download failed: ${err}`, true);
                    }
                }).catch(err => {
                    log("error", "gui.youtube.format_download.error", { error: String(err) });
                    showNotification(`Download error: ${String(err)}`, true);
                });
                return;
            } else {
                // Fallback: try legacy quality: "best" flow if format listing failed
                log("warn", "gui.youtube.format_list_empty_using_legacy", { youtubeUrl });
                const ytFilename = `${sanitizeFileName(document.title || "youtube_video")}.mp4`;
                const result = await sendYoutubeDownloadWithCompatibility(
                    youtubeUrl,
                    ytFilename,
                    clickTsIso,
                    detectedVideoUrlAtClick || ""
                );
                if (isConfirmedYoutubeDownloadResult(result)) {
                    const savedName = result.fileName || ytFilename;
                    showNotification(`Saved with yt-dlp: ${savedName}`);
                    return;
                }
                youtubeRouteError = result.error || "";
            }

            log("warn", "gui.youtube.all_routes_failed_trying_stream_fallback", {
                pageUrl: location.href,
                youtubeUrl,
                error: youtubeRouteError
            });
            showNotification("Direct YouTube mode failed, trying stream fallback...");
        }

        log("info", "download_button_clicked", {
            rawUrl: url,
            isHttp: isHttpUrl(url),
            pageUrl: location.href
        });

        if (!isHttpUrl(url)) {
            showNotification("Finding downloadable stream...");

            // Ask inject.ts (MAIN world) to re-scan and re-broadcast URLs NOW
            try {
                document.dispatchEvent(new CustomEvent("mydm-request-scan"));
            } catch { /* ignore */ }

            // Small delay to let re-broadcast messages arrive
            await new Promise((r) => setTimeout(r, 500));
            let fallback = await resolveFallbackVideoSource();

            // RETRY: If first attempt fails, wait 3s and try again.
            if (!fallback?.url || !isHttpUrl(fallback.url)) {
                log("info", "first_resolve_failed_retrying", { pageUrl: location.href });
                await new Promise((r) => setTimeout(r, 3000));
                fallback = await resolveFallbackVideoSource();
            }

            if (fallback?.url && isHttpUrl(fallback.url)) {
                url = fallback.url;
                resolvedQuality = fallback.quality || undefined;
                resolvedMimeType = fallback.mimeType || undefined;
                resolvedMuxed = fallback.muxed ?? true;
                resolvedAudioUrl = fallback.audioUrl || undefined;
                log("info", "fallback_video_source_resolved", {
                    url,
                    audioUrl: resolvedAudioUrl,
                    quality: resolvedQuality,
                    mimeType: resolvedMimeType,
                    muxed: resolvedMuxed
                });
            } else {
                log("warn", "fallback_resolution_failed", {
                    fallbackError: fallback?.error || "no result",
                    pageUrl: location.href
                });
            }
        }

        if (!isHttpUrl(url)) {
            if (isYoutubePage() && youtubeRouteError.trim().length > 0) {
                showNotification(`YouTube download failed: ${youtubeRouteError}`, true);
            } else {
                showNotification("No downloadable stream found. Let the video play for a few seconds, then try again.", true);
            }
            return;
        }

        if (isYoutubePage() || url.includes("googlevideo.com")) {
            const qualities = await listStreamQualities();
            if (qualities.length > 0) {
                const selected = qualities.length === 1 ? qualities[0] : await pickStreamQuality(qualities);
                if (!selected) {
                    showNotification("Download cancelled", true);
                    return;
                }
                url = selected.url;
                resolvedAudioUrl = selected.audioUrl || undefined;
                resolvedMuxed = selected.muxed;
                resolvedQuality = selected.quality || resolvedQuality;
                resolvedMimeType = selected.mimeType || resolvedMimeType;
            }
        }

        const isManifest = url.includes(".m3u8") || url.includes(".mpd");
        if (isManifest) {
            await showQualityModal(url, url.includes(".m3u8") ? "hls" : "dash");
            return;
        }

        const extension = inferExtensionFromUrl(url) || inferExtensionFromMime(resolvedMimeType);
        const qualitySuffix = resolvedQuality ? `_${sanitizeFileName(resolvedQuality).replace(/\s+/g, "")}` : "";
        const filename = `${sanitizeFileName(document.title || "video")}${qualitySuffix}.${extension}`;

        let result: RuntimeResponse;
        if (!resolvedMuxed && resolvedAudioUrl) {
            // Split video+audio streams — use download_video so NativeHost can mux them
            result = await sendDownloadToBackground({
                type: "download_video",
                videoUrl: url,
                audioUrl: resolvedAudioUrl,
                filename,
                quality: resolvedQuality || ""
            });
        } else {
            // Muxed stream or direct video — simple download
            result = await sendDownloadToBackground({
                type: "download_with_mydm",
                url,
                filename
            });
        }

        if (result.success) {
            const qualityText = resolvedQuality ? ` (${resolvedQuality})` : "";
            showNotification(`Sent to MyDM${qualityText}`);
        }
    });

    controller.cleanup = () => {
        clearHide();
        if (controller.rafHandle !== null) {
            cancelAnimationFrame(controller.rafHandle);
            controller.rafHandle = null;
        }
        controller.resizeObserver?.disconnect();
        window.removeEventListener("scroll", onScrollOrResize);
        window.removeEventListener("resize", onScrollOrResize);
        video.removeEventListener("pointerenter", onVideoEnter);
        video.removeEventListener("pointermove", onVideoMove);
        video.removeEventListener("pointerleave", onVideoLeave);
        button.removeEventListener("pointerenter", onBtnEnter);
        button.removeEventListener("pointerleave", onBtnLeave);
        host.remove();
        activeOverlays.delete(controller);
    };

    overlayByVideo.set(video, controller);
    activeOverlays.add(controller);
    updateOverlayPosition(controller);
}

function getImageDownloadUrl(image: HTMLImageElement): string | null {
    const raw = image.currentSrc || image.src || image.getAttribute("src");
    if (!raw) return null;
    const absolute = resolveUrl(raw, location.href);
    if (!isHttpUrl(absolute)) return null;
    if (absolute.startsWith("blob:") || absolute.startsWith("data:")) return null;
    return absolute;
}

function injectImageOverlay(image: HTMLImageElement): void {
    if (imageOverlayByElement.has(image)) return;
    const initialRect = image.getBoundingClientRect();
    if (initialRect.width < MIN_IMAGE_OVERLAY_WIDTH || initialRect.height < MIN_IMAGE_OVERLAY_HEIGHT) return;

    const host = document.createElement("div");
    host.className = "mydm-image-overlay-host";
    host.style.cssText = [
        "position:fixed",
        "left:0",
        "top:0",
        "width:0",
        "height:0",
        "pointer-events:none",
        `z-index:${OVERLAY_Z_INDEX}`
    ].join(";");

    const shadow = host.attachShadow({ mode: "open" });
    const style = document.createElement("style");
    style.textContent = `
        .mydm-img-btn {
            position: absolute;
            right: 8px;
            top: 8px;
            border: none;
            border-radius: 6px;
            padding: 6px 10px;
            color: #fff;
            background: linear-gradient(135deg, #1d4ed8, #1e40af);
            font-size: 11px;
            font-weight: 600;
            font-family: 'Segoe UI', system-ui, sans-serif;
            cursor: pointer;
            pointer-events: auto;
            opacity: 0;
            visibility: hidden;
            transform: translateY(-2px);
            transition: opacity 0.2s ease, transform 0.2s ease, visibility 0.2s ease;
            box-shadow: 0 6px 18px rgba(30, 64, 175, 0.35);
            white-space: nowrap;
        }
        .mydm-img-btn.visible {
            opacity: 1;
            visibility: visible;
            transform: translateY(0);
        }
        .mydm-img-btn:hover {
            background: linear-gradient(135deg, #1e40af, #1e3a8a);
        }
    `;
    shadow.appendChild(style);

    const button = document.createElement("button");
    button.className = "mydm-img-btn";
    button.textContent = "Download Image";
    shadow.appendChild(button);

    document.documentElement.appendChild(host);

    const controller: ImageOverlayController = {
        image,
        host,
        button,
        hideTimer: null,
        rafHandle: null,
        pointerOnImage: false,
        pointerOnButton: false,
        visible: false,
        resizeObserver: null,
        cleanup: () => { }
    };

    const updateImageOverlayPosition = () => {
        if (!image.isConnected) return;
        const rect = image.getBoundingClientRect();
        const tooSmall = rect.width < MIN_IMAGE_OVERLAY_WIDTH || rect.height < MIN_IMAGE_OVERLAY_HEIGHT;
        const offscreen = rect.bottom < 0 || rect.right < 0 || rect.top > window.innerHeight || rect.left > window.innerWidth;
        if (tooSmall || offscreen) {
            controller.button.classList.remove("visible");
            controller.visible = false;
            return;
        }
        controller.host.style.left = `${Math.round(rect.left)}px`;
        controller.host.style.top = `${Math.round(rect.top)}px`;
        controller.host.style.width = `${Math.round(rect.width)}px`;
        controller.host.style.height = `${Math.round(rect.height)}px`;
    };

    const ensureTracking = () => {
        if (controller.rafHandle !== null) return;
        const tick = () => {
            updateImageOverlayPosition();
            if (controller.visible || controller.pointerOnButton || controller.pointerOnImage) {
                controller.rafHandle = requestAnimationFrame(tick);
            } else {
                controller.rafHandle = null;
            }
        };
        controller.rafHandle = requestAnimationFrame(tick);
    };

    const clearHide = () => {
        if (controller.hideTimer) {
            clearTimeout(controller.hideTimer);
            controller.hideTimer = null;
        }
    };

    const show = () => {
        clearHide();
        controller.visible = true;
        button.classList.add("visible");
        updateImageOverlayPosition();
        ensureTracking();
    };

    const scheduleHide = () => {
        clearHide();
        controller.hideTimer = setTimeout(() => {
            if (controller.pointerOnImage || controller.pointerOnButton) return;
            controller.visible = false;
            button.classList.remove("visible");
        }, HIDE_DELAY_MS);
    };

    const onImageEnter = () => {
        controller.pointerOnImage = true;
        show();
    };
    const onImageMove = () => show();
    const onImageLeave = () => {
        controller.pointerOnImage = false;
        scheduleHide();
    };
    const onButtonEnter = () => {
        controller.pointerOnButton = true;
        show();
    };
    const onButtonLeave = () => {
        controller.pointerOnButton = false;
        scheduleHide();
    };

    image.addEventListener("pointerenter", onImageEnter, { passive: true });
    image.addEventListener("pointermove", onImageMove, { passive: true });
    image.addEventListener("pointerleave", onImageLeave, { passive: true });
    button.addEventListener("pointerenter", onButtonEnter, { passive: true });
    button.addEventListener("pointerleave", onButtonLeave, { passive: true });

    const onScrollOrResize = () => {
        if (controller.visible) {
            updateImageOverlayPosition();
        }
    };
    window.addEventListener("scroll", onScrollOrResize, { passive: true });
    window.addEventListener("resize", onScrollOrResize, { passive: true });

    if (typeof ResizeObserver !== "undefined") {
        controller.resizeObserver = new ResizeObserver(() => updateImageOverlayPosition());
        controller.resizeObserver.observe(image);
    }

    button.addEventListener("click", async (event: MouseEvent) => {
        event.stopPropagation();
        event.preventDefault();

        const imageUrl = getImageDownloadUrl(image);
        if (!imageUrl) {
            showNotification("Unable to resolve image URL", true);
            return;
        }

        const filename = inferFileNameFromUrl(imageUrl, "image_download");
        const result = await sendDownloadToBackground({
            type: "download_with_mydm",
            url: imageUrl,
            filename
        });

        if (result.success) {
            showNotification("Image sent to MyDM");
        }
    });

    controller.cleanup = () => {
        clearHide();
        if (controller.rafHandle !== null) {
            cancelAnimationFrame(controller.rafHandle);
            controller.rafHandle = null;
        }
        controller.resizeObserver?.disconnect();
        window.removeEventListener("scroll", onScrollOrResize);
        window.removeEventListener("resize", onScrollOrResize);
        image.removeEventListener("pointerenter", onImageEnter);
        image.removeEventListener("pointermove", onImageMove);
        image.removeEventListener("pointerleave", onImageLeave);
        button.removeEventListener("pointerenter", onButtonEnter);
        button.removeEventListener("pointerleave", onButtonLeave);
        host.remove();
        activeImageOverlays.delete(controller);
    };

    imageOverlayByElement.set(image, controller);
    activeImageOverlays.add(controller);
    updateImageOverlayPosition();
}

function cleanupStaleOverlays(): void {
    for (const overlay of Array.from(activeOverlays)) {
        if (!overlay.video.isConnected) {
            overlay.cleanup();
        }
    }
    for (const overlay of Array.from(activeImageOverlays)) {
        if (!overlay.image.isConnected) {
            overlay.cleanup();
        }
    }
}

function scanForVideos(root: ParentNode = document): void {
    const videos = root.querySelectorAll("video");
    videos.forEach((video) => injectVideoOverlay(video as HTMLVideoElement));
}

function scanForImages(root: ParentNode = document): void {
    const images = root.querySelectorAll("img[src]");
    let processed = 0;
    for (const image of images) {
        injectImageOverlay(image as HTMLImageElement);
        processed++;
        if (processed >= 250) break;
    }
}

function isInterestingResource(url: string, kind: string): boolean {
    if (!url || url.startsWith("blob:")) return false;
    if (url.includes(".m3u8") || url.includes(".mpd")) return true;
    if (DOWNLOADABLE_EXT_REGEX.test(url)) return true;
    return kind === "video" || kind === "audio";
}

function collectResources(root: ParentNode = document): Array<Record<string, unknown>> {
    const resources: Array<Record<string, unknown>> = [];

    const push = (url: string, kind: string, source: string) => {
        if (!isInterestingResource(url, kind)) return;
        const absolute = resolveUrl(url, location.href);
        const key = `${kind}|${absolute}`;
        if (reportedResources.has(key)) return;

        reportedResources.add(key);
        if (reportedResources.size > 1500) {
            // Prevent unbounded memory growth on long SPA sessions.
            reportedResources.clear();
        }

        resources.push({
            url: absolute,
            kind,
            source,
            pageUrl: location.href,
            pageTitle: document.title || ""
        });
    };

    root.querySelectorAll("a[href]").forEach((el) => {
        const a = el as HTMLAnchorElement;
        const href = a.href || a.getAttribute("href");
        if (!href) return;
        if (a.hasAttribute("download") || DOWNLOADABLE_EXT_REGEX.test(href)) {
            push(href, "link", "dom-anchor");
        }
    });

    root.querySelectorAll("img[src]").forEach((el) => {
        const src = (el as HTMLImageElement).src;
        if (src) push(src, "image", "dom-img");
    });

    root.querySelectorAll("audio[src], video[src], source[src]").forEach((el) => {
        const src = (el as HTMLMediaElement).src || (el as HTMLSourceElement).src;
        if (!src) return;
        const tag = el.tagName.toLowerCase();
        const kind = tag === "audio" ? "audio" : "video";
        push(src, kind, `dom-${tag}`);
    });

    return resources.slice(0, MAX_RESOURCE_REPORT);
}

async function reportResources(resources: Array<Record<string, unknown>>): Promise<void> {
    if (resources.length === 0) return;
    try {
        await sendRuntimeMessage<RuntimeResponse>({
            type: "detected_resources",
            resources,
            requestId: genRequestId("detect")
        });
    } catch (error) {
        log("warn", "resource_report_failed", { error: String(error) });
    }
}

function scheduleRescan(reason: string): void {
    if (scanTimer) return;
    scanTimer = setTimeout(() => {
        scanTimer = null;
        scanForVideos();
        scanForImages();
        cleanupStaleOverlays();
        void reportResources(collectResources());
        log("info", "rescan", { reason, url: location.href });
    }, 180);
}

function onUrlChange(reason: string): void {
    if (location.href === lastUrl) return;
    lastUrl = location.href;
    log("info", "spa_navigation", { reason, url: lastUrl });
    scheduleRescan("url-change");
    setTimeout(() => scheduleRescan("url-change-delayed-1"), 500);
    setTimeout(() => scheduleRescan("url-change-delayed-2"), 1400);
}

function installNavigationHooks(): void {
    const originalPushState = history.pushState;
    const originalReplaceState = history.replaceState;

    history.pushState = function (...args): void {
        originalPushState.apply(this, args);
        onUrlChange("pushState");
    };

    history.replaceState = function (...args): void {
        originalReplaceState.apply(this, args);
        onUrlChange("replaceState");
    };

    window.addEventListener("popstate", () => onUrlChange("popstate"));
    window.addEventListener("hashchange", () => onUrlChange("hashchange"));
    window.addEventListener("yt-navigate-finish", () => onUrlChange("yt-navigate-finish"));

    setInterval(() => onUrlChange("interval"), 1200);
}

function installMutationObserver(): void {
    const observer = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
            if (mutation.type === "childList") {
                mutation.addedNodes.forEach((node) => {
                    if (!(node instanceof Element)) return;
                    if (node.tagName === "VIDEO" || node.querySelector("video")) {
                        scheduleRescan("mutation-video");
                    } else if (node.querySelector("a[href],img[src],audio[src],video[src],source[src]")) {
                        scheduleRescan("mutation-resource");
                    }
                });
            } else if (mutation.type === "attributes") {
                scheduleRescan("mutation-attr");
            }
        }
    });

    observer.observe(document.documentElement, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ["src", "href"]
    });
}

// Manifest detection - only checks DOM attributes, not network interception.
// Network interception is handled exclusively by inject.ts in the MAIN world.
function installManifestRequestHooks(): void {
    // Check existing source elements for manifests
    const reportIfManifest = (url: string) => {
        if (!url.includes(".m3u8") && !url.includes(".mpd")) return;
        void sendRuntimeMessage<RuntimeResponse>({
            type: "detected_media",
            url,
            mediaType: url.includes(".m3u8") ? "hls" : "dash",
            requestId: genRequestId("manifest")
        }).catch(() => { });
    };

    // Scan existing video/audio source elements
    document.querySelectorAll("source[src], video[src], audio[src]").forEach((el) => {
        const src = el.getAttribute("src");
        if (src) reportIfManifest(src);
    });
}

// MAIN world stream URL relay.
// The inject.ts script (running in the page's MAIN world) sends captured
// googlevideo.com / media URLs via window.postMessage. We listen here
// (in the ISOLATED world) and forward them to the background service worker.
function installMainWorldRelay(): void {
    // Handle real-time URL messages from inject.ts
    window.addEventListener("message", (event) => {
        if (event.source !== window) return;
        if (event.data?.type !== "MYDM_STREAM_INTERCEPT") return;
        const url = event.data.url;
        const source = typeof event.data.source === "string" ? event.data.source : "unknown";
        if (typeof url !== "string" || !url.startsWith("http")) return;

        log("info", "main_world_stream_intercepted", { url: url.substring(0, 120) });

        // Forward to background so it can build a stream candidate
        sendRuntimeMessage({
            type: "report_stream_url",
            url,
            pageUrl: location.href,
            streamSource: source
        }).catch(() => { /* background might not be ready yet */ });
    });

    // CRITICAL: Read URLs that inject.ts already captured BEFORE this script loaded.
    // inject.ts runs at document_start, this script runs at document_idle.
    // The postMessage events fired by inject.ts are already lost by now.
    // But inject.ts stores all captured URLs in a DOM data attribute.
    try {
        const stored = document.documentElement.dataset.mydmStreams;
        if (stored) {
            const parsed = JSON.parse(stored) as unknown;
            const entries = Array.isArray(parsed) ? parsed : [];
            log("info", "reading_pre_captured_urls", { count: entries.length });
            for (const entry of entries) {
                const url = typeof entry === "string"
                    ? entry
                    : (entry && typeof entry === "object" && typeof (entry as Record<string, unknown>).url === "string"
                        ? (entry as Record<string, unknown>).url as string
                        : "");
                const source = entry && typeof entry === "object" && typeof (entry as Record<string, unknown>).source === "string"
                    ? (entry as Record<string, unknown>).source as string
                    : "unknown";

                if (url.startsWith("http")) {
                    sendRuntimeMessage({
                        type: "report_stream_url",
                        url,
                        pageUrl: location.href,
                        streamSource: source
                    }).catch(() => { });
                }
            }
        }
    } catch { /* ignore parse errors */ }
}

function bootstrap(): void {
    scanForVideos();
    scanForImages();
    void reportResources(collectResources());
    installMutationObserver();
    installNavigationHooks();
    installManifestRequestHooks();
    installMainWorldRelay();

    setInterval(() => {
        cleanupStaleOverlays();
        if (document.visibilityState === "visible") {
            scheduleRescan("periodic");
        }
    }, 3000);
}

bootstrap();

export { };
