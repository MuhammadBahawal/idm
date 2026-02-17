// ═══════════════════════════════════════════════════════════
// MyDM Browser Extension — Content Script
// Detects downloadable links, video elements, and HLS/DASH streams
// ═══════════════════════════════════════════════════════════

const DOWNLOAD_EXTENSIONS = [
    'zip', 'rar', '7z', 'tar', 'gz', 'exe', 'msi', 'dmg', 'deb', 'rpm',
    'pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx',
    'mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm',
    'mp3', 'flac', 'wav', 'aac', 'ogg', 'wma', 'm4a',
    'iso', 'img', 'bin',
];

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

        // Next non-comment line is the URL
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

    // Ensure container is positioned
    const computedPos = getComputedStyle(container).position;
    if (computedPos === 'static') {
        container.style.position = 'relative';
    }

    // Create overlay button
    const btn = document.createElement('button');
    btn.className = 'mydm-overlay-btn';
    btn.textContent = '⬇ MyDM';
    btn.title = 'Download with MyDM';

    btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        e.preventDefault();

        // Try to detect the media source
        const src = video.src || video.currentSrc;
        const sources = video.querySelectorAll('source');

        if (src && (src.includes('.m3u8') || src.includes('.mpd'))) {
            // It's a streaming source — show quality selection
            showQualityModal(src, src.includes('.m3u8') ? 'hls' : 'dash');
        } else if (src) {
            // Direct video URL
            chrome.runtime.sendMessage({
                type: 'download_with_mydm',
                url: src,
                filename: document.title + '.mp4',
            });
            showNotification('Sent to MyDM');
        } else if (sources.length > 0) {
            // Multiple sources — use the first one
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
    // Remove existing modal
    document.querySelector('.mydm-quality-modal')?.remove();

    let qualities: MediaQuality[] = [];

    try {
        const res = await fetch(manifestUrl);
        const content = await res.text();

        if (mediaType === 'hls') {
            qualities = parseHlsMaster(content, manifestUrl);
        }
        // DASH parsing would need XML — simplified here
    } catch (e) {
        console.error('[MyDM] Failed to fetch manifest:', e);
        // Fallback: just download the manifest URL
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
        // No qualities found — send as-is
        chrome.runtime.sendMessage({
            type: 'download_media',
            manifestUrl: manifestUrl,
            mediaType: mediaType,
            title: document.title,
        });
        showNotification('Sent to MyDM');
        return;
    }

    // Create modal
    const modal = document.createElement('div');
    modal.className = 'mydm-quality-modal';

    const header = document.createElement('div');
    header.className = 'mydm-modal-header';
    header.innerHTML = '<span>⬇ MyDM — Select Quality</span><button class="mydm-modal-close">✕</button>';
    modal.appendChild(header);

    header.querySelector('.mydm-modal-close')!.addEventListener('click', () => modal.remove());

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
            showNotification(`Downloading ${q.resolution} with MyDM`);
        });
        list.appendChild(item);
    }

    modal.appendChild(list);

    // Backdrop
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
XMLHttpRequest.prototype.open = function (method: string, url: string | URL, ...args: any[]) {
    const urlStr = url.toString();
    if (urlStr.includes('.m3u8') || urlStr.includes('.mpd')) {
        chrome.runtime.sendMessage({
            type: 'detected_media',
            url: urlStr,
            mediaType: urlStr.includes('.m3u8') ? 'hls' : 'dash',
        });
    }
    return originalXhrOpen.apply(this, [method, url, ...args] as any);
};

const originalFetch = window.fetch;
window.fetch = function (...args: any[]) {
    const url = args[0]?.toString() || '';
    if (url.includes('.m3u8') || url.includes('.mpd')) {
        chrome.runtime.sendMessage({
            type: 'detected_media',
            url: url,
            mediaType: url.includes('.m3u8') ? 'hls' : 'dash',
        });
    }
    return originalFetch.apply(this, args as any);
};

// ──── Scan for video elements ────

function scanForVideos(): void {
    const videos = document.querySelectorAll('video');
    videos.forEach((video) => injectVideoOverlay(video as HTMLVideoElement));
}

// Initial scan
scanForVideos();

// Observe DOM for dynamically added videos
const observer = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
        for (const node of mutation.addedNodes) {
            if (node instanceof HTMLVideoElement) {
                injectVideoOverlay(node);
            }
            if (node instanceof HTMLElement) {
                const videos = node.querySelectorAll('video');
                videos.forEach((v) => injectVideoOverlay(v as HTMLVideoElement));
            }
        }
    }
});

observer.observe(document.body, { childList: true, subtree: true });

export { };
