interface MediaQuality {
    resolution: string;
    bandwidth: number;
    url: string;
    codecs?: string;
}

interface RuntimeResponse {
    success?: boolean;
    error?: string;
    connected?: boolean;
    disconnectReason?: string;
    requestId?: string;
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

const OVERLAY_Z_INDEX = 2147483647;
const HIDE_DELAY_MS = 550;
const MESSAGE_TIMEOUT_MS = 10000;
const MAX_RESOURCE_REPORT = 120;
const DOWNLOADABLE_EXT_REGEX = /\.(zip|rar|7z|tar|gz|bz2|xz|iso|exe|msi|dmg|deb|rpm|apk|pdf|docx?|xlsx?|pptx?|txt|csv|mp4|mkv|avi|mov|wmv|flv|webm|m4v|mp3|flac|wav|aac|ogg|wma|m4a|jpe?g|png|gif|bmp|svg|webp)(?:$|[?#])/i;

const overlayByVideo = new WeakMap<HTMLVideoElement, OverlayController>();
const activeOverlays = new Set<OverlayController>();
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
    return new Promise<T>((resolve, reject) => {
        let done = false;
        const timer = setTimeout(() => {
            if (done) return;
            done = true;
            reject(new Error("Timed out waiting for extension response"));
        }, MESSAGE_TIMEOUT_MS);

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

async function sendDownloadToBackground(payload: Record<string, unknown>): Promise<boolean> {
    const requestId = genRequestId("dl");
    try {
        const response = await sendRuntimeMessage<RuntimeResponse>({
            ...payload,
            requestId,
            headers: buildRequestHeaders()
        });
        if (response?.success) {
            return true;
        }
        showNotification(`❌ ${response?.error || "Unable to send download to MyDM"}`, true);
        return false;
    } catch (error) {
        showNotification(`❌ ${String(error)}`, true);
        return false;
    }
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
        const ok = await sendDownloadToBackground({
            type: "download_media",
            manifestUrl,
            mediaType,
            title: document.title
        });
        if (ok) showNotification("✅ Sent to MyDM");
        return;
    }

    const modal = document.createElement("div");
    modal.className = "mydm-quality-modal";

    const header = document.createElement("div");
    header.className = "mydm-modal-header";
    header.innerHTML = "<span>⬇ MyDM - Select Quality</span><button class=\"mydm-modal-close\">✕</button>";
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
            const ok = await sendDownloadToBackground({
                type: "download_media",
                manifestUrl: q.url,
                mediaType,
                quality: q.resolution,
                title: document.title
            });
            if (ok) {
                showNotification(`✅ Downloading ${q.resolution} with MyDM`);
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
    button.textContent = "⬇ MyDM";
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

        const url = getMediaUrl(video);
        if (!url) {
            showNotification("⚠ No downloadable source found", true);
            return;
        }

        const isManifest = url.includes(".m3u8") || url.includes(".mpd");
        if (isManifest) {
            await showQualityModal(url, url.includes(".m3u8") ? "hls" : "dash");
            return;
        }

        const ok = await sendDownloadToBackground({
            type: "download_with_mydm",
            url,
            filename: `${sanitizeFileName(document.title || "video")}.mp4`
        });

        if (ok) {
            showNotification("✅ Sent to MyDM");
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

function cleanupStaleOverlays(): void {
    for (const overlay of Array.from(activeOverlays)) {
        if (!overlay.video.isConnected) {
            overlay.cleanup();
        }
    }
}

function scanForVideos(root: ParentNode = document): void {
    const videos = root.querySelectorAll("video");
    videos.forEach((video) => injectVideoOverlay(video as HTMLVideoElement));
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

function installManifestRequestHooks(): void {
    const reportManifest = (url: string) => {
        if (!url.includes(".m3u8") && !url.includes(".mpd")) return;
        void sendRuntimeMessage<RuntimeResponse>({
            type: "detected_media",
            url,
            mediaType: url.includes(".m3u8") ? "hls" : "dash",
            requestId: genRequestId("manifest")
        }).catch((error) => {
            log("warn", "detected_media_send_failed", { url, error: String(error) });
        });
    };

    const originalXhrOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (
        this: XMLHttpRequest,
        method: string,
        url: string | URL,
        async?: boolean,
        username?: string | null,
        password?: string | null
    ): void {
        reportManifest(url.toString());
        originalXhrOpen.call(this, method, url, async ?? true, username, password);
    };

    const originalFetch = window.fetch;
    window.fetch = function (input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
        reportManifest(input.toString());
        return originalFetch.call(this, input, init);
    };
}

function bootstrap(): void {
    scanForVideos();
    void reportResources(collectResources());
    installMutationObserver();
    installNavigationHooks();
    installManifestRequestHooks();

    setInterval(() => {
        cleanupStaleOverlays();
        if (document.visibilityState === "visible") {
            scheduleRescan("periodic");
        }
    }, 3000);
}

bootstrap();

export { };
