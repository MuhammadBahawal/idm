// MyDM Extension — Popup Script
document.addEventListener('DOMContentLoaded', () => {
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');
    const mediaList = document.getElementById('mediaList');
    const urlInput = document.getElementById('urlInput') as HTMLInputElement | null;
    const addBtn = document.getElementById('addBtn');
    const optionsLink = document.getElementById('optionsLink');

    // Check connection status
    chrome.runtime.sendMessage(
        { type: 'get_connection_status' },
        (response: { connected?: boolean } | undefined) => {
            if (response?.connected) {
                statusDot?.classList.add('connected');
                if (statusText) statusText.textContent = 'Connected to MyDM';
            } else {
                if (statusText) statusText.textContent = 'Not connected — is MyDM running?';
            }
        }
    );

    // Load detected media
    chrome.storage.local.get(['detectedMedia'], (result: Record<string, unknown>) => {
        const media = (result['detectedMedia'] as Array<{
            type?: string; title?: string; url?: string;
        }>) || [];
        if (media.length === 0) return;

        if (mediaList) {
            mediaList.innerHTML = '';
            const recent = media.slice(-10).reverse();
            for (const item of recent) {
                const div = document.createElement('div');
                div.className = 'media-item';
                div.innerHTML = `
          <span class="media-type ${item.type || ''}">${item.type || 'unknown'}</span>
          <span class="media-title">${item.title || item.url || 'Unknown'}</span>
        `;
                div.addEventListener('click', () => {
                    chrome.runtime.sendMessage({
                        type: 'download_media',
                        manifestUrl: item.url,
                        mediaType: item.type,
                        title: item.title,
                    });
                    window.close();
                });
                mediaList.appendChild(div);
            }
        }
    });

    // Quick add URL
    addBtn?.addEventListener('click', () => {
        const url = urlInput?.value?.trim();
        if (!url) return;
        chrome.runtime.sendMessage({
            type: 'download_with_mydm',
            url: url,
        });
        window.close();
    });

    urlInput?.addEventListener('keydown', (e: KeyboardEvent) => {
        if (e.key === 'Enter') addBtn?.click();
    });

    // Options link
    optionsLink?.addEventListener('click', (e: Event) => {
        e.preventDefault();
        chrome.runtime.openOptionsPage();
    });
});
