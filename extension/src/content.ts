// ═══════════════════════════════════════════════════════════
// MyDM Browser Extension — Content Script
// Detects downloadable links, video elements, and HLS/DASH streams
// ═══════════════════════════════════════════════════════════

// ──── HLS/DASH Parser (lightweight, client-side) ────

interface MediaQuality {
    resolution: string;
    bandwidth: number;
    url: string;
    codecs?: string;
}

function parseHlsMaster(content: string, baseUrl: string): MediaQuality[] {
    const qualities: MediaQuality[] = [];
    const lines = content.split('\n');

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (!line.startsWith('#EXT-X-STREAM-INF:')) continue;

        const bandwidth = extractAttr(line, 'BANDWIDTH');
        const resolution = extractAttr(line, 'RESOLUTION');
        const codecs = extractAttr(line, 'CODECS');

        for (let j = i + 1; j < lines.length; j++) {
            const nextLine = lines[j].trim();
            if (!nextLine || nextLine.startsWith('#')) continue;

            qualities.push({
                resolution: resolution || 'Unknown',
                bandwidth: parseInt(bandwidth || '0'),
                codecs: codecs || undefined,
                url: resolveUrl(nextLine, baseUrl),
            });
            break;
        }
    }

    return qualities.sort((a, b) => b.bandwidth - a.bandwidth);
}

function extractAttr(line: string, attr: string): string | null {
    const quotedMatch = line.match(new RegExp(`${attr}="([^"]*)"`));
    if (quotedMatch) return quotedMatch[1];
    const match = line.match(new RegExp(`${attr}=([^,\\s]+)`));
    return match ? match[1] : null;
}

function resolveUrl(url: string, base: string): string {
    try {
        return new URL(url, base).href;
    } catch {
        return url;
    }
}

// ──── Video Overlay ────

function injectVideoOverlay(video: HTMLVideoElement): void {
    if (video.dataset.mydmInjected) return;
    video.dataset.mydmInjected = 'true';

    const container = video.parentElement;
    if (!container) return;

    const computedPos = getComputedStyle(container).position;
    if (computedPos === 'static') {
        container.style.position = 'relative';
    }

    const btn = document.createElement('button');
    btn.className = 'mydm-overlay-btn';
    btn.textContent = '⬇ MyDM';
    btn.title = 'Download with MyDM';

    btn.addEventListener('click', (e: MouseEvent) => {
        e.stopPropagation();
        e.preventDefault();

        const src = video.src || video.currentSrc;
        const sources = video.querySelectorAll('source');

        if (src && (src.includes('.m3u8') || src.includes('.mpd'))) {
            showQualityModal(src, src.includes('.m3u8') ? 'hls' : 'dash');
        } else if (src) {
            chrome.runtime.sendMessage({
                type: 'download_with_mydm',
                url: src,
                filename: document.title + '.mp4',
            });
            showNotification('Sent to MyDM');
        } else if (sources.length > 0) {
            const firstSrc = (sources[0] as HTMLSourceElement).src;
            chrome.runtime.sendMessage({
                type: 'download_with_mydm',
                url: firstSrc,
                filename: document.title + '.mp4',
            });
            showNotification('Sent to MyDM');
        } else {
            showNotification('No downloadable source found');
        }
    });

    container.appendChild(btn);
}

// ──── Quality Selection Modal ────

async function showQualityModal(manifestUrl: string, mediaType: 'hls' | 'dash'): Promise<void> {
    document.querySelector('.mydm-quality-modal')?.remove();

    let qualities: MediaQuality[] = [];

    try {
        const res = await fetch(manifestUrl);
        const content = await res.text();

        if (mediaType === 'hls') {
            qualities = parseHlsMaster(content, manifestUrl);
        }
    } catch (e) {
        console.error('[MyDM] Failed to fetch manifest:', e);
        chrome.runtime.sendMessage({
            type: 'download_media',
            manifestUrl: manifestUrl,
            mediaType: mediaType,
            title: document.title,
        });
        showNotification('Sent to MyDM');
        return;
    }

    if (qualities.length === 0) {
        chrome.runtime.sendMessage({
            type: 'download_media',
            manifestUrl: manifestUrl,
            mediaType: mediaType,
            title: document.title,
        });
        showNotification('Sent to MyDM');
        return;
    }

    const modal = document.createElement('div');
    modal.className = 'mydm-quality-modal';

    const header = document.createElement('div');
    header.className = 'mydm-modal-header';
    header.innerHTML = '<span>⬇ MyDM — Select Quality</span><button class="mydm-modal-close">✕</button>';
    modal.appendChild(header);

    const closeBtn = header.querySelector('.mydm-modal-close');
    if (closeBtn) {
        closeBtn.addEventListener('click', () => modal.remove());
    }

    const list = document.createElement('div');
    list.className = 'mydm-quality-list';

    for (const q of qualities) {
        const item = document.createElement('button');
        item.className = 'mydm-quality-item';
        item.innerHTML = `
      <span class="mydm-q-resolution">${q.resolution}</span>
      <span class="mydm-q-bitrate">${(q.bandwidth / 1000).toFixed(0)} kbps</span>
      ${q.codecs ? `<span class="mydm-q-codecs">${q.codecs}</span>` : ''}
    `;
        item.addEventListener('click', () => {
            chrome.runtime.sendMessage({
                type: 'download_media',
                manifestUrl: q.url,
                mediaType: mediaType,
                quality: q.resolution,
                title: document.title,
            });
            modal.remove();
            backdrop.remove();
            showNotification(`Downloading ${q.resolution} with MyDM`);
        });
        list.appendChild(item);
    }

    modal.appendChild(list);

    const backdrop = document.createElement('div');
    backdrop.className = 'mydm-modal-backdrop';
    backdrop.addEventListener('click', () => { modal.remove(); backdrop.remove(); });
    document.body.appendChild(backdrop);
    document.body.appendChild(modal);
}

// ──── Toast Notification ────

function showNotification(text: string): void {
    const toast = document.createElement('div');
    toast.className = 'mydm-toast';
    toast.textContent = text;
    document.body.appendChild(toast);

    setTimeout(() => {
        toast.classList.add('mydm-toast-fadeout');
        setTimeout(() => toast.remove(), 500);
    }, 2500);
}

// ──── Intercept network requests for HLS/DASH ────

const originalXhrOpen = XMLHttpRequest.prototype.open;
XMLHttpRequest.prototype.open = function (
    this: XMLHttpRequest,
    method: string,
    url: string | URL,
    async?: boolean,
    username?: string | null,
    password?: string | null
): void {
    const urlStr = url.toString();
    if (urlStr.includes('.m3u8') || urlStr.includes('.mpd')) {
        chrome.runtime.sendMessage({
            type: 'detected_media',
            url: urlStr,
            mediaType: urlStr.includes('.m3u8') ? 'hls' : 'dash',
        });
    }
    originalXhrOpen.call(this, method, url, async ?? true, username, password);
};

const originalFetch = window.fetch;
window.fetch = function (
    input: RequestInfo | URL,
    init?: RequestInit
): Promise<Response> {
    const url = input.toString();
    if (url.includes('.m3u8') || url.includes('.mpd')) {
        chrome.runtime.sendMessage({
            type: 'detected_media',
            url: url,
            mediaType: url.includes('.m3u8') ? 'hls' : 'dash',
        });
    }
    return originalFetch.call(this, input, init);
};

// ──── Scan for video elements ────

function scanForVideos(): void {
    const videos = document.querySelectorAll('video');
    videos.forEach((video: HTMLVideoElement) => injectVideoOverlay(video));
}

scanForVideos();

const observer = new MutationObserver((mutations: MutationRecord[]) => {
    for (const mutation of mutations) {
        mutation.addedNodes.forEach((node: Node) => {
            if (node instanceof HTMLVideoElement) {
                injectVideoOverlay(node);
            }
            if (node instanceof HTMLElement) {
                const videos = node.querySelectorAll('video');
                videos.forEach((v: HTMLVideoElement) => injectVideoOverlay(v));
            }
        });
    }
});

observer.observe(document.body, { childList: true, subtree: true });

export { };
