interface RuntimeResponse {
    success?: boolean;
    connected?: boolean;
    disconnectReason?: string;
    error?: string;
}

interface DetectedMediaItem {
    type?: string;
    title?: string;
    url?: string;
    tabId?: number;
}

interface DetectedResourceItem {
    kind?: string;
    title?: string;
    pageTitle?: string;
    url?: string;
    tabId?: number;
}

function sendMessage<T extends RuntimeResponse>(message: Record<string, unknown>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        chrome.runtime.sendMessage(message, (response: T) => {
            if (chrome.runtime.lastError) {
                reject(new Error(chrome.runtime.lastError.message));
                return;
            }
            resolve(response);
        });
    });
}

function prettifyKind(kind?: string): string {
    if (!kind) return "resource";
    return kind.replace(/_/g, " ").trim();
}

function inferNameFromUrl(url?: string): string {
    if (!url) return "download";
    try {
        const parsed = new URL(url);
        const file = decodeURIComponent(parsed.pathname.split("/").pop() || "");
        return file || parsed.hostname || "download";
    } catch {
        return "download";
    }
}

document.addEventListener("DOMContentLoaded", () => {
    const statusDot = document.getElementById("statusDot");
    const statusText = document.getElementById("statusText");
    const mediaList = document.getElementById("mediaList");
    const resourceList = document.getElementById("resourceList");
    const urlInput = document.getElementById("urlInput") as HTMLInputElement | null;
    const addBtn = document.getElementById("addBtn");
    const optionsLink = document.getElementById("optionsLink");

    const setStatus = (connected: boolean, reason?: string) => {
        if (connected) {
            statusDot?.classList.add("connected");
            if (statusText) statusText.textContent = "Connected to MyDM";
            return;
        }

        statusDot?.classList.remove("connected");
        if (statusText) {
            statusText.textContent = reason
                ? `Disconnected: ${reason}`
                : "Not connected - is MyDM running?";
        }
    };

    const loadConnection = async () => {
        try {
            const response = await sendMessage<RuntimeResponse>({ type: "get_connection_status" });
            setStatus(!!response.connected, response.disconnectReason);
        } catch (error) {
            setStatus(false, String(error));
        }
    };

    const loadDetectedMedia = async () => {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        const activeTabId = tab?.id;

        chrome.storage.local.get(["detectedMedia"], (result: Record<string, unknown>) => {
            const media = (result["detectedMedia"] as DetectedMediaItem[]) || [];
            const filtered = media
                .filter((m) => activeTabId == null || m.tabId == null || m.tabId === activeTabId)
                .slice(-10)
                .reverse();

            if (!mediaList) return;
            mediaList.innerHTML = "";

            if (filtered.length === 0) {
                mediaList.innerHTML = "<div class=\"empty\">No media detected on this page</div>";
                return;
            }

            for (const item of filtered) {
                if (!item.url) continue;
                const div = document.createElement("div");
                div.className = "media-item";
                div.innerHTML = `
                    <span class="media-type ${item.type || ""}">${item.type || "media"}</span>
                    <span class="media-title">${item.title || item.url || "Unknown"}</span>
                `;
                div.addEventListener("click", async () => {
                    try {
                        const response = await sendMessage<RuntimeResponse>({
                            type: "download_media",
                            manifestUrl: item.url,
                            mediaType: item.type,
                            title: item.title
                        });
                        if (response.success) {
                            window.close();
                        } else if (statusText) {
                            statusText.textContent = response.error || "Failed to send media download";
                        }
                    } catch (error) {
                        if (statusText) statusText.textContent = String(error);
                    }
                });
                mediaList.appendChild(div);
            }
        });
    };

    const loadDetectedResources = async () => {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        const activeTabId = tab?.id;

        chrome.storage.local.get(["detectedResources"], (result: Record<string, unknown>) => {
            const resources = (result["detectedResources"] as DetectedResourceItem[]) || [];
            const filtered = resources
                .filter((r) => activeTabId == null || r.tabId == null || r.tabId === activeTabId)
                .slice(-20)
                .reverse();

            if (!resourceList) return;
            resourceList.innerHTML = "";

            if (filtered.length === 0) {
                resourceList.innerHTML = "<div class=\"empty\">No files/images detected on this page</div>";
                return;
            }

            for (const item of filtered) {
                if (!item.url) continue;
                const div = document.createElement("div");
                div.className = "media-item";
                div.innerHTML = `
                    <span class="media-type resource">${prettifyKind(item.kind)}</span>
                    <span class="media-title">${item.title || inferNameFromUrl(item.url)}</span>
                `;
                div.addEventListener("click", async () => {
                    try {
                        const response = await sendMessage<RuntimeResponse>({
                            type: "download_with_mydm",
                            url: item.url,
                            filename: inferNameFromUrl(item.url)
                        });
                        if (response.success) {
                            window.close();
                        } else if (statusText) {
                            statusText.textContent = response.error || "Failed to start download";
                        }
                    } catch (error) {
                        if (statusText) statusText.textContent = String(error);
                    }
                });
                resourceList.appendChild(div);
            }
        });
    };

    addBtn?.addEventListener("click", async () => {
        const url = urlInput?.value?.trim();
        if (!url) return;

        try {
            const response = await sendMessage<RuntimeResponse>({
                type: "download_with_mydm",
                url
            });
            if (response.success) {
                window.close();
            } else if (statusText) {
                statusText.textContent = response.error || "Failed to start download";
            }
        } catch (error) {
            if (statusText) statusText.textContent = String(error);
        }
    });

    urlInput?.addEventListener("keydown", (e: KeyboardEvent) => {
        if (e.key === "Enter") addBtn?.click();
    });

    optionsLink?.addEventListener("click", (e: Event) => {
        e.preventDefault();
        chrome.runtime.openOptionsPage();
    });

    void loadConnection();
    void loadDetectedMedia();
    void loadDetectedResources();
});

