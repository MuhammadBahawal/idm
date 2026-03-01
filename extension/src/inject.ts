/**
 * inject.ts — MAIN world script for capturing video stream URLs.
 *
 * This runs in the page's MAIN world (not the extension's ISOLATED world),
 * so it has access to YouTube's actual JavaScript objects and network calls.
 *
 * Strategy (ordered by reliability):
 *   1. Intercept JSON.parse — catches ytInitialPlayerResponse the exact moment
 *      YouTube parses it, before any timers or DOM mutations
 *   2. Intercept fetch() — catches /youtubei/v1/player API (SPA navigations)
 *      and googlevideo.com/videoplayback URLs
 *   3. Intercept XMLHttpRequest — fallback for older YouTube code paths
 *   4. DOM scanning — polls for ytInitialPlayerResponse global + #movie_player API
 *   5. yt-navigate-finish event — YouTube custom event for SPA navigation completion
 *
 * Captured URLs are sent to the content script (ISOLATED world) via
 * window.postMessage, which then forwards them to the background service worker.
 */
(function () {
    "use strict";
    const MSG_TYPE = "MYDM_STREAM_INTERCEPT";
    const capturedUrls = new Map<string, string>();
    let totalCaptured = 0;

    function report(url: string, source?: string): void {
        if (!url || typeof url !== "string") return;
        if (capturedUrls.has(url)) return;
        const normalizedSource = (source || "unknown").toLowerCase();
        capturedUrls.set(url, normalizedSource);
        totalCaptured++;

        // Cap map size to prevent memory leaks on very long sessions
        if (capturedUrls.size > 1000) {
            const toDelete = [...capturedUrls.keys()].slice(0, 500);
            toDelete.forEach((u) => capturedUrls.delete(u));
        }

        try {
            console.log(`[MyDM] 📡 Captured stream (${source || "unknown"}):`, url.substring(0, 120));
            window.postMessage({ type: MSG_TYPE, url, source: normalizedSource }, "*");
        } catch { /* ignore */ }

        // CRITICAL: Also store in DOM so content.ts (ISOLATED world) can read
        // pre-captured URLs even if it loads AFTER inject.ts has already fired.
        // Both worlds share the DOM, so data attributes work cross-world.
        persistUrlsToDom();
    }

    function persistUrlsToDom(): void {
        try {
            const payload = [...capturedUrls.entries()].map(([streamUrl, streamSource]) => ({
                url: streamUrl,
                source: streamSource
            }));
            document.documentElement.dataset.mydmStreams = JSON.stringify(payload);
        } catch { /* ignore */ }
    }

    // Re-broadcast ALL captured URLs via postMessage (called on demand by content.ts)
    function rebroadcastAll(): void {
        const entries = [...capturedUrls.entries()];
        const urls = entries;
        console.log(`[MyDM] 🔁 Re-broadcasting ${urls.length} captured URLs`);
        for (const [url, source] of entries) {
            try {
                window.postMessage({ type: MSG_TYPE, url, source }, "*");
            } catch { /* ignore */ }
        }
    }

    // ════════════════════════════════════════════════════════
    // STRATEGY 1: Intercept JSON.parse — MOST RELIABLE
    // YouTube calls JSON.parse on the player response data.
    // We intercept this to extract streams before anything else.
    // ════════════════════════════════════════════════════════

    const origJSONParse = JSON.parse;
    JSON.parse = function (...args: Parameters<typeof JSON.parse>) {
        const result = origJSONParse.apply(this, args);
        try {
            if (result && typeof result === "object") {
                // Check if this looks like a YouTube player response
                if (result.streamingData && (result.videoDetails || result.playabilityStatus)) {
                    console.log("[MyDM] 🎯 Intercepted YouTube player response via JSON.parse");
                    extractStreamsFromPlayerResponse(result, "json_parse");
                }
                // Also check for embedded player response within other structures
                if (result.playerResponse && result.playerResponse.streamingData) {
                    console.log("[MyDM] 🎯 Intercepted nested playerResponse via JSON.parse");
                    extractStreamsFromPlayerResponse(result.playerResponse, "json_parse_nested");
                }
            }
        } catch { /* don't break YouTube's JSON.parse */ }
        return result;
    };

    // ════════════════════════════════════════════════════════
    // YouTube Player Response Extraction
    // ════════════════════════════════════════════════════════

    function extractStreamsFromPlayerResponse(data: any, source: string): void {
        try {
            const streaming = data?.streamingData;
            if (!streaming) {
                console.log("[MyDM] ⚠ Player response has no streamingData property");
                return;
            }

            const formats = streaming.formats || [];
            const adaptive = streaming.adaptiveFormats || [];
            const totalFormats = formats.length + adaptive.length;

            if (totalFormats === 0) {
                console.log("[MyDM] ⚠ streamingData has 0 formats (video may be DRM-protected or age-restricted)");
                return;
            }

            console.log(`[MyDM] ✅ Found ${formats.length} muxed + ${adaptive.length} adaptive formats (source: ${source})`);

            let captured = 0;
            for (const fmt of formats) {
                const url = extractUrlFromFormat(fmt);
                if (url) { report(url, source); captured++; }
            }
            for (const fmt of adaptive) {
                const url = extractUrlFromFormat(fmt);
                if (url) { report(url, source); captured++; }
            }

            console.log(`[MyDM] 📊 Captured ${captured}/${totalFormats} stream URLs from this response`);
        } catch (e) {
            console.log("[MyDM] ❌ Error parsing player response:", e);
        }
    }

    function extractUrlFromFormat(fmt: any): string | null {
        // Priority 1: Direct URL (most common for non-DRM content)
        if (fmt.url) return fmt.url;

        // Priority 2: signatureCipher / cipher (older format)
        const cipher = fmt.signatureCipher || fmt.cipher;
        if (cipher) {
            try {
                const params = new URLSearchParams(cipher);
                const url = params.get("url");
                const sp = params.get("sp") || "signature";
                const signed = params.get("sig") || params.get("signature");
                const undeciphered = params.get("s");
                if (!url) return null;
                if (signed) {
                    // Try to append the signature — may work for some videos
                    return `${url}&${sp}=${encodeURIComponent(signed)}`;
                }
                if (undeciphered) {
                    return null;
                }
                return url;
            } catch {
                return null;
            }
        }

        return null;
    }

    // ════════════════════════════════════════════════════════
    // STRATEGY 2: Intercept fetch() — SPA navigations + media URLs
    // ════════════════════════════════════════════════════════

    const origFetch = window.fetch;
    window.fetch = function (input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
        let urlStr = "";
        try {
            urlStr = typeof input === "string"
                ? input
                : input instanceof URL
                    ? input.toString()
                    : (input as Request).url;
        } catch { /* ignore */ }

        const promise = origFetch.call(this, input, init);

        if (urlStr) {
            // Capture media URLs directly
            if (isMediaUrl(urlStr)) {
                report(urlStr, "fetch");
            }

            // Intercept YouTube player API response to extract stream URLs
            if (urlStr.includes("/youtubei/v1/player")) {
                promise.then((response) => {
                    try {
                        response.clone().json().then((data) => {
                            console.log("[MyDM] 🔄 Intercepted /youtubei/v1/player fetch response");
                            extractStreamsFromPlayerResponse(data, "fetch_api");
                        }).catch(() => { /* not JSON */ });
                    } catch { /* ignore */ }
                }).catch(() => { /* network error */ });
            }
        }

        return promise;
    };

    // ════════════════════════════════════════════════════════
    // STRATEGY 3: Intercept XMLHttpRequest — fallback
    // ════════════════════════════════════════════════════════

    const origXhrOpen = XMLHttpRequest.prototype.open;
    const origXhrSend = XMLHttpRequest.prototype.send;

    (XMLHttpRequest.prototype as any).open = function (
        method: string,
        url: string | URL,
        async?: boolean,
        username?: string | null,
        password?: string | null
    ) {
        const urlStr = typeof url === "string" ? url : url.toString();
        (this as any).__mydm_url = urlStr;

        if (isMediaUrl(urlStr)) {
            report(urlStr, "xhr");
        }

        return origXhrOpen.call(this, method, url, async ?? true, username, password);
    };

    XMLHttpRequest.prototype.send = function (body?: any) {
        const xhrUrl = (this as any).__mydm_url as string | undefined;

        if (xhrUrl && xhrUrl.includes("/youtubei/v1/player")) {
            this.addEventListener("load", function () {
                try {
                    if (this.responseType === "" || this.responseType === "text") {
                        const data = JSON.parse(this.responseText);
                        console.log("[MyDM] 🔄 Intercepted /youtubei/v1/player XHR response");
                        extractStreamsFromPlayerResponse(data, "xhr_api");
                    }
                } catch { /* not JSON or parse error */ }
            });
        }

        return origXhrSend.call(this, body);
    };

    // ════════════════════════════════════════════════════════
    // STRATEGY 4: DOM scanning for YouTube global variables
    // ════════════════════════════════════════════════════════

    function scanForPlayerResponse(): void {
        const loc = location.hostname.toLowerCase();
        if (!loc.includes("youtube.com") && loc !== "youtu.be" && loc !== "m.youtube.com") return;

        console.log(`[MyDM] 🔍 Scanning for YouTube player data... (already captured: ${totalCaptured})`);

        // Method 1: Global variable ytInitialPlayerResponse
        try {
            const ipr = (window as any).ytInitialPlayerResponse;
            if (ipr && ipr.streamingData) {
                extractStreamsFromPlayerResponse(ipr, "global_var");
            }
        } catch { /* ignore */ }

        // Method 2: ytplayer.config
        try {
            const ytplayer = (window as any).ytplayer;
            if (ytplayer?.config?.args?.raw_player_response?.streamingData) {
                extractStreamsFromPlayerResponse(
                    ytplayer.config.args.raw_player_response, "ytplayer_config"
                );
            }
        } catch { /* ignore */ }

        // Method 3: #movie_player element API
        try {
            const player = document.querySelector("#movie_player") as any;
            if (player && typeof player.getPlayerResponse === "function") {
                const resp = player.getPlayerResponse();
                if (resp?.streamingData) {
                    extractStreamsFromPlayerResponse(resp, "player_api");
                }
            }
        } catch { /* ignore */ }
    }

    // ════════════════════════════════════════════════════════
    // STRATEGY 5: YouTube SPA navigation events
    // ════════════════════════════════════════════════════════

    // YouTube fires this custom event when a page navigation completes
    document.addEventListener("yt-navigate-finish", () => {
        console.log("[MyDM] 🔄 YouTube SPA navigation detected (yt-navigate-finish)");
        // Clear previously sent URLs for the new page
        capturedUrls.clear();
        totalCaptured = 0;
        // Schedule scans with increasing delays
        setTimeout(scanForPlayerResponse, 500);
        setTimeout(scanForPlayerResponse, 2000);
        setTimeout(scanForPlayerResponse, 5000);
    });

    // Also listen for yt-page-data-updated which fires when player data is ready
    document.addEventListener("yt-page-data-updated", () => {
        console.log("[MyDM] 🔄 YouTube page data updated (yt-page-data-updated)");
        setTimeout(scanForPlayerResponse, 300);
    });

    // History API hooks for non-YouTube sites
    const origPushState = history.pushState;
    const origReplaceState = history.replaceState;

    history.pushState = function (...args: any[]) {
        const result = origPushState.apply(this, args as any);
        setTimeout(scanForPlayerResponse, 1000);
        return result;
    };
    history.replaceState = function (...args: any[]) {
        const result = origReplaceState.apply(this, args as any);
        setTimeout(scanForPlayerResponse, 1000);
        return result;
    };
    window.addEventListener("popstate", () => setTimeout(scanForPlayerResponse, 1000));

    // ════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════

    function isMediaUrl(url: string): boolean {
        const lower = url.toLowerCase();

        // YouTube streaming
        if (lower.includes("googlevideo.com/videoplayback")) return true;
        if (lower.includes("googlevideo.com") && lower.includes("itag=")) return true;

        // Instagram video/media
        if ((lower.includes("cdninstagram.com") || lower.includes("scontent")) &&
            (lower.includes("/v/") || lower.includes("/t50.") || lower.includes("/t51."))) return true;

        // Facebook video
        if (lower.includes("fbcdn.net") && (lower.includes("/v/") || lower.includes("video"))) return true;
        if (lower.includes("video.xx.fbcdn.net")) return true;

        // Twitter/X video
        if (lower.includes("video.twimg.com")) return true;
        if (lower.includes("abs.twimg.com") && lower.includes("video")) return true;

        // Manifest formats
        if (lower.includes(".m3u8") || lower.includes(".mpd")) return true;

        // Direct media files
        const mediaExts = [".mp4", ".webm", ".m4v", ".m4a", ".m4s", ".ts", ".flv",
            ".mkv", ".mp3", ".ogg", ".opus", ".aac", ".wav"];
        const pathPart = lower.split("?")[0];
        if (mediaExts.some((ext) => pathPart.endsWith(ext))) return true;

        // YouTube query params
        try {
            const p = new URL(url);
            if (p.searchParams.has("itag") && (p.searchParams.has("mime") || p.searchParams.has("clen"))) return true;
        } catch { /* ignore */ }

        return false;
    }

    // ════════════════════════════════════════════════════════
    // Initialization
    // ════════════════════════════════════════════════════════

    console.log("[MyDM] 🚀 Inject script loaded (MAIN world)");

    // Listen for scan requests from content.ts (dispatched when user clicks download)
    // CustomEvents on document are visible to both MAIN and ISOLATED worlds.
    document.addEventListener("mydm-request-scan", () => {
        console.log("[MyDM] 📩 Scan requested by content script");
        scanForPlayerResponse();
        // Re-broadcast all previously captured URLs so content.ts can forward them
        setTimeout(rebroadcastAll, 100);
    });

    // Schedule DOM scans
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", () => {
            setTimeout(scanForPlayerResponse, 300);
            setTimeout(scanForPlayerResponse, 1500);
            setTimeout(scanForPlayerResponse, 4000);
        });
    } else {
        // Page already loaded
        setTimeout(scanForPlayerResponse, 100);
        setTimeout(scanForPlayerResponse, 1000);
        setTimeout(scanForPlayerResponse, 3000);
    }
})();
