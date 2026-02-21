// ═══════════════════════════════════════════════════════════
// MyDM Browser Extension — Background Service Worker
// ═══════════════════════════════════════════════════════════

const NATIVE_HOST_NAME = 'com.mydm.native';

// Downloadable file extensions
const DOWNLOAD_EXTENSIONS = new Set([
    'zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz', 'iso',
    'exe', 'msi', 'dmg', 'deb', 'rpm', 'apk',
    'pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx',
    'mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm',
    'mp3', 'flac', 'wav', 'aac', 'ogg', 'wma', 'm4a',
    'jpg', 'jpeg', 'png', 'gif', 'bmp', 'svg', 'webp',
]);

let nativePort: chrome.runtime.Port | null = null;

// ──── Native Messaging ────

function connectNativeHost(): chrome.runtime.Port | null {
    try {
        nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);

        nativePort.onMessage.addListener((msg: Record<string, unknown>) => {
            console.log('[MyDM] Native message received:', msg);
            chrome.storage.local.set({ lastNativeResponse: msg });
        });

        nativePort.onDisconnect.addListener(() => {
            console.log('[MyDM] Native port disconnected:', chrome.runtime.lastError?.message);
            nativePort = null;
            chrome.storage.local.set({ connectionStatus: 'disconnected' });
        });

        chrome.storage.local.set({ connectionStatus: 'connected' });
        console.log('[MyDM] Connected to native host');
        return nativePort;
    } catch (e) {
        console.error('[MyDM] Failed to connect:', e);
        chrome.storage.local.set({ connectionStatus: 'error' });
        return null;
    }
}

function sendToNative(message: Record<string, unknown>): void {
    if (!nativePort) {
        nativePort = connectNativeHost();
    }
    if (nativePort) {
        nativePort.postMessage(message);
    } else {
        console.error('[MyDM] Cannot send: not connected to native host');
    }
}

// ──── Context Menu ────

chrome.runtime.onInstalled.addListener(() => {
    chrome.contextMenus.create({
        id: 'mydm-download',
        title: 'Download with MyDM',
        contexts: ['link', 'image', 'video', 'audio'],
    });

    chrome.contextMenus.create({
        id: 'mydm-download-page',
        title: 'Download page URL with MyDM',
        contexts: ['page'],
    });

    // Try connecting to native host
    connectNativeHost();
    sendToNative({ type: 'ping', payload: {} });
});

chrome.contextMenus.onClicked.addListener(
    (info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab) => {
        const url = info.linkUrl || info.srcUrl || info.pageUrl;
        if (!url) return;

        sendToNative({
            type: 'add_download',
            payload: {
                url: url,
                referrer: tab?.url || '',
            },
        });
    }
);

// ──── Listen for messages from content script ────

interface ExtMessage {
    type: string;
    url?: string;
    filename?: string;
    manifestUrl?: string;
    mediaType?: string;
    quality?: string;
    title?: string;
}

chrome.runtime.onMessage.addListener(
    (
        message: ExtMessage,
        sender: chrome.runtime.MessageSender,
        sendResponse: (response: Record<string, unknown>) => void
    ) => {
        if (message.type === 'download_with_mydm') {
            sendToNative({
                type: 'add_download',
                payload: {
                    url: message.url,
                    filename: message.filename,
                    referrer: sender.tab?.url || '',
                },
            });
            sendResponse({ success: true });
        }

        if (message.type === 'download_media') {
            sendToNative({
                type: 'add_media_download',
                payload: {
                    manifestUrl: message.manifestUrl,
                    mediaType: message.mediaType,
                    quality: message.quality,
                    title: message.title,
                    referrer: sender.tab?.url || '',
                },
            });
            sendResponse({ success: true });
        }

        if (message.type === 'get_connection_status') {
            sendResponse({ connected: nativePort !== null });
        }

        if (message.type === 'detected_media') {
            chrome.storage.local.get(['detectedMedia'], (result: Record<string, unknown>) => {
                const media = (result['detectedMedia'] as Array<Record<string, unknown>>) || [];
                media.push({
                    url: message.url,
                    type: message.mediaType,
                    tabId: sender.tab?.id,
                    title: sender.tab?.title,
                    timestamp: Date.now(),
                });
                chrome.storage.local.set({ detectedMedia: media.slice(-50) });
            });
            sendResponse({ success: true });
        }

        return true; // async response
    }
);

// ──── Monitor web requests for media manifests ────

if (chrome.webRequest) {
    chrome.webRequest.onCompleted.addListener(
        (details) => {
            const url = details.url.toLowerCase();
            if (url.includes('.m3u8') || url.includes('.mpd')) {
                chrome.storage.local.get(['detectedMedia'], (result: Record<string, unknown>) => {
                    const media = (result['detectedMedia'] as Array<Record<string, unknown>>) || [];
                    const mediaType = url.includes('.m3u8') ? 'hls' : 'dash';
                    media.push({
                        url: details.url,
                        type: mediaType,
                        tabId: details.tabId,
                        timestamp: Date.now(),
                    });
                    chrome.storage.local.set({ detectedMedia: media.slice(-50) });
                });
            }
        },
        { urls: ['<all_urls>'], types: ['xmlhttprequest', 'media', 'other'] }
    );
}

export { };
