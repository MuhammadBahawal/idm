// Popup script
document.addEventListener('DOMContentLoaded', () => {
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');
    const mediaList = document.getElementById('mediaList');
    const urlInput = document.getElementById('urlInput') as HTMLInputElement;
    const addBtn = document.getElementById('addBtn');
    const optionsLink = document.getElementById('optionsLink');

    // Check connection status
    chrome.runtime.sendMessage({ type: 'get_connection_status' }, (response) => {
        if (response?.connected) {
            statusDot?.classList.add('connected');
            if (statusText) statusText.textContent = 'Connected to MyDM';
        } else {
            if (statusText) statusText.textContent = 'Not connected — is MyDM running?';
        }
    });

    // Load detected media
    chrome.storage.local.get(['detectedMedia'], (result) => {
        const media = result.detectedMedia || [];
        if (media.length === 0) return;

        if (mediaList) {
            mediaList.innerHTML = '';
            // Show most recent first, max 10
            const recent = media.slice(-10).reverse();
            for (const item of recent) {
                const div = document.createElement('div');
                div.className = 'media-item';
                div.innerHTML = `
          <span class="media-type ${item.type}">${item.type}</span>
          <span class="media-title">${item.title || item.url}</span>
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

    urlInput?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') addBtn?.click();
    });

    // Options link
    optionsLink?.addEventListener('click', (e) => {
        e.preventDefault();
        chrome.runtime.openOptionsPage();
    });
});
