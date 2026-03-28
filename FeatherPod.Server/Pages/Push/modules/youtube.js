/**
 * YouTube import module -- URL detection (paste, drop, share target), format toggle, server submission.
 *
 * Detects YouTube URLs from:
 * - Document-level paste events (runs before API key paste detection)
 * - Drop zone (when no files are dropped, checks text/plain and text/uri-list)
 * - ?yt= query param (from PWA share target redirect)
 *
 * On detection, shows an import dialog with audio/video toggle, then POSTs to
 * /api/feeds/{feedId}/youtube with the oEmbed title for instant queue display.
 * On 202, creates a queue entry and hands off to existing queue monitoring.
 */

import { FEED_ID } from './config.js';
import { getApiKey, getUserRole } from './auth.js';
import { getCurrentState } from './state.js';

const YT_FORMAT_PREFS_KEY = 'featherpod_yt_format_prefs';
const YT_VIDEO_REGEX = /(?:youtube\.com\/watch\?v=|youtu\.be\/|m\.youtube\.com\/watch\?v=)([a-zA-Z0-9_-]{11})/;
const YT_REJECT_PATTERNS = [
    /[?&]list=/,
    /youtube\.com\/(?:channel\/|@|c\/)/,
    /youtube\.com\/shorts\//,
    /youtube\.com\/results/
];

/** @type {Function|null} Callback to create a queue entry from a YouTube 202 response */
let onYouTubeJobCreated = null;

/**
 * Register the callback invoked when a YouTube job is successfully created.
 * Called from push.js during init.
 * @param {Function} callback - (jobResponse: object) => void
 */
export function registerYouTubeJobCallback(callback) {
    onYouTubeJobCreated = callback;
}

/**
 * Extract a YouTube video URL from text. Returns the full URL if valid, null otherwise.
 * @param {string} text
 * @returns {string|null}
 */
export function extractYouTubeUrl(text) {
    if (!text) {
        return null;
    }

    for (const pattern of YT_REJECT_PATTERNS) {
        if (pattern.test(text)) {
            return null;
        }
    }

    const match = text.match(YT_VIDEO_REGEX);
    if (!match) {
        return null;
    }

    // Return the matched portion that contains the video URL
    // Try to extract a full URL from the text
    const urlMatch = text.match(/https?:\/\/[^\s<>"']+/);

    return urlMatch ? urlMatch[0] : `https://www.youtube.com/watch?v=${match[1]}`;
}

/**
 * Handle a document-level paste event. Returns true if a YouTube URL was detected
 * (so the caller can skip API key paste detection).
 * @param {ClipboardEvent} e
 * @returns {boolean}
 */
export function handlePaste(e) {
    const state = getCurrentState();
    if (state !== 'ready' && state !== 'queue') {
        return false;
    }

    const text = e.clipboardData?.getData('text/plain') || '';
    const url = extractYouTubeUrl(text);
    if (!url) {
        return false;
    }

    e.preventDefault();
    const metaPromise = fetchVideoMeta(url);
    showImportDialog(url, metaPromise);

    return true;
}

/**
 * Handle a drop event when no files are present. Returns true if a YouTube URL was found.
 * @param {DragEvent} e
 * @returns {boolean}
 */
export function handleDrop(e) {
    const text = e.dataTransfer.getData('text/plain') || e.dataTransfer.getData('text/uri-list') || '';
    const url = extractYouTubeUrl(text);
    if (!url) {
        return false;
    }

    const metaPromise = fetchVideoMeta(url);
    showImportDialog(url, metaPromise);

    return true;
}

/**
 * Check for ?yt= query param (from PWA share target). Call on page load.
 */
export function checkSharedUrl() {
    const params = new URLSearchParams(window.location.search);
    const sharedUrl = params.get('yt');
    if (!sharedUrl) {
        return;
    }

    // Clean up URL bar
    const clean = new URL(window.location);
    clean.searchParams.delete('yt');
    history.replaceState(null, '', clean);

    const url = extractYouTubeUrl(sharedUrl);
    if (url) {
        // Start fetch immediately, show dialog after page init
        const metaPromise = fetchVideoMeta(url);
        setTimeout(() => showImportDialog(url, metaPromise), 500);
    }
}

// ============================================================================
// Per-channel format preference
// ============================================================================

function getChannelFormatPref(channel) {
    try {
        const prefs = JSON.parse(localStorage.getItem(YT_FORMAT_PREFS_KEY) || '{}');

        return prefs[channel] || null;
    } catch {
        return null;
    }
}

function saveChannelFormatPref(channel, format) {
    if (!channel) {
        return;
    }

    try {
        const prefs = JSON.parse(localStorage.getItem(YT_FORMAT_PREFS_KEY) || '{}');
        prefs[channel] = format;
        localStorage.setItem(YT_FORMAT_PREFS_KEY, JSON.stringify(prefs));
    } catch {
        // Best-effort
    }
}

// ============================================================================
// Import dialog
// ============================================================================

/** @type {string|null} URL currently shown in the dialog */
let pendingUrl = null;
/** @type {string|null} Channel name from oEmbed (for format pref) */
let pendingChannel = null;
/** @type {string|null} Video title from oEmbed (sent to server for instant queue display) */
let pendingTitle = null;

/**
 * Show the YouTube import confirmation dialog.
 * @param {string} url
 * @param {Promise<{title?: string, author_name?: string}|null>} [metaPromise] - Pre-started oEmbed fetch
 */
function showImportDialog(url, metaPromise) {
    pendingUrl = url;
    pendingChannel = null;
    pendingTitle = null;

    const overlay = document.getElementById('youtube-modal-overlay');
    if (!overlay) {
        return;
    }

    // Reset state
    const titleEl = overlay.querySelector('.yt-modal-video-title');
    const metaEl = overlay.querySelector('.yt-modal-video-meta');
    const errorEl = overlay.querySelector('.yt-modal-error');
    const importBtn = overlay.querySelector('.yt-modal-import');
    const spinner = overlay.querySelector('.yt-modal-spinner');

    // Show video ID as placeholder, then fetch title via oEmbed
    const vidMatch = url.match(/[?&]v=([a-zA-Z0-9_-]{11})/);
    const displayId = vidMatch ? vidMatch[1] : url;

    if (titleEl) {
        titleEl.textContent = displayId;
    }
    if (metaEl) {
        metaEl.textContent = '';
    }
    if (errorEl) {
        errorEl.hidden = true;
    }
    if (importBtn) {
        importBtn.disabled = true;
    }
    if (spinner) {
        spinner.hidden = true;
    }

    // Start with both radios unselected until we know the channel
    overlay.querySelectorAll('input[name="yt-format"]').forEach(r => { r.checked = false; });

    // Apply pre-fetched oEmbed metadata when it arrives
    if (metaPromise) {
        metaPromise.then(data => {
            if (!data || pendingUrl !== url) {
                return;
            }
            if (titleEl && data.title) {
                titleEl.textContent = data.title;
                pendingTitle = data.title;
            }
            if (metaEl && data.author_name) {
                metaEl.textContent = data.author_name;
                pendingChannel = data.author_name;
            }

            // Select remembered format for this channel (if any)
            const pref = data?.author_name ? getChannelFormatPref(data.author_name) : null;
            if (pref) {
                const radio = overlay.querySelector(`input[name="yt-format"][value="${pref}"]`);
                if (radio) {
                    radio.checked = true;
                }
            }
            updateImportButtonState(overlay);
        });
    }

    overlay.hidden = false;
}

function updateImportButtonState(overlay) {
    const importBtn = overlay?.querySelector('.yt-modal-import');
    const checked = overlay?.querySelector('input[name="yt-format"]:checked');
    if (importBtn) {
        importBtn.disabled = !checked;
    }
}

/**
 * Fetch video title and channel via YouTube oEmbed API.
 * Returns { title, author_name } or null on failure.
 * @param {string} url
 * @returns {Promise<{title?: string, author_name?: string}|null>}
 */
function fetchVideoMeta(url) {
    const oembedUrl = `https://www.youtube.com/oembed?url=${encodeURIComponent(url)}&format=json`;

    return fetch(oembedUrl)
        .then(r => r.ok ? r.json() : null)
        .catch(() => null);
}

/**
 * Hide the import dialog.
 */
export function hideImportDialog() {
    const overlay = document.getElementById('youtube-modal-overlay');
    if (overlay) {
        overlay.hidden = true;
    }
    pendingUrl = null;
}

/**
 * Get the currently selected format from the radio buttons.
 * @returns {'audio'|'video'}
 */
function getSelectedFormat() {
    const overlay = document.getElementById('youtube-modal-overlay');
    const checked = overlay?.querySelector('input[name="yt-format"]:checked');

    return checked?.value === 'video' ? 'video' : 'audio';
}

/**
 * Submit the YouTube import request to the server.
 */
async function submitImport() {
    if (!pendingUrl) {
        return;
    }

    const overlay = document.getElementById('youtube-modal-overlay');
    const importBtn = overlay?.querySelector('.yt-modal-import');
    const errorEl = overlay?.querySelector('.yt-modal-error');
    const spinner = overlay?.querySelector('.yt-modal-spinner');

    if (importBtn) {
        importBtn.disabled = true;
    }
    if (errorEl) {
        errorEl.hidden = true;
    }
    if (spinner) {
        spinner.hidden = false;
    }

    const format = getSelectedFormat();
    const apiKey = getApiKey();

    try {
        const response = await fetch(`/api/feeds/${FEED_ID}/youtube`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-API-Key': apiKey
            },
            body: JSON.stringify({ url: pendingUrl, format, title: pendingTitle })
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            throw new Error(data.error || `Server returned ${response.status}`);
        }

        const jobResponse = await response.json();

        saveChannelFormatPref(pendingChannel, format);
        hideImportDialog();

        if (onYouTubeJobCreated) {
            onYouTubeJobCreated(jobResponse);
        }
    } catch (err) {
        if (errorEl) {
            errorEl.textContent = err.message || 'Import failed';
            errorEl.hidden = false;
        }
        if (importBtn) {
            importBtn.disabled = false;
        }
    } finally {
        if (spinner) {
            spinner.hidden = true;
        }
    }
}

// ============================================================================
// Cookie upload dialog (shown on YouTube bot detection)
// ============================================================================

/** @type {boolean} Whether the cookie dialog is currently showing */
let cookieDialogActive = false;

/**
 * Show the cookie upload dialog inside the YouTube modal.
 * Admin users see a file picker; non-admin users see a "temporarily unavailable" message.
 */
function showCookieDialog() {
    if (cookieDialogActive) {
        return;
    }
    cookieDialogActive = true;

    const overlay = document.getElementById('youtube-modal-overlay');
    if (!overlay) {
        return;
    }

    const contentEl = overlay.querySelector('.yt-modal-content');
    if (!contentEl) {
        return;
    }

    const isAdmin = getUserRole() === 'Admin';

    // Hide normal import UI, show cookie dialog
    contentEl.querySelectorAll('.yt-modal-import-section').forEach(el => { el.hidden = true; });

    const cookieSection = contentEl.querySelector('.yt-modal-cookie-section');
    if (cookieSection) {
        cookieSection.hidden = false;
        const uploadArea = cookieSection.querySelector('.yt-cookie-upload-area');
        const adminMsg = cookieSection.querySelector('.yt-cookie-admin-msg');
        const noAdminMsg = cookieSection.querySelector('.yt-cookie-noadmin-msg');

        if (uploadArea) {
            uploadArea.hidden = !isAdmin;
        }
        if (adminMsg) {
            adminMsg.hidden = !isAdmin;
        }
        if (noAdminMsg) {
            noAdminMsg.hidden = isAdmin;
        }

        // Reset state
        const statusEl = cookieSection.querySelector('.yt-cookie-status');
        if (statusEl) {
            statusEl.textContent = '';
            statusEl.hidden = true;
        }
    }

    overlay.hidden = false;
}

/**
 * Handle cookie file upload from the dialog.
 * @param {File} file
 */
async function uploadCookieFile(file) {
    const overlay = document.getElementById('youtube-modal-overlay');
    const statusEl = overlay?.querySelector('.yt-cookie-status');

    if (statusEl) {
        statusEl.textContent = 'Uploading...';
        statusEl.hidden = false;
        statusEl.className = 'yt-cookie-status';
    }

    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await fetch('/api/youtube/cookies', {
            method: 'POST',
            headers: { 'X-API-Key': getApiKey() },
            body: formData
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            throw new Error(data.error || `Upload failed (${response.status})`);
        }

        if (statusEl) {
            statusEl.textContent = 'Cookies uploaded. Retrying import...';
        }

        // Close cookie dialog and retry the original import
        hideCookieDialog();
        await submitImport();
    } catch (err) {
        if (statusEl) {
            statusEl.textContent = err.message || 'Upload failed';
            statusEl.className = 'yt-cookie-status yt-cookie-error';
        }
    }
}

/**
 * Hide the cookie dialog and restore normal import UI.
 */
function hideCookieDialog() {
    cookieDialogActive = false;
    const overlay = document.getElementById('youtube-modal-overlay');
    if (!overlay) {
        return;
    }

    const contentEl = overlay.querySelector('.yt-modal-content');
    if (!contentEl) {
        return;
    }

    contentEl.querySelectorAll('.yt-modal-import-section').forEach(el => { el.hidden = false; });
    const cookieSection = contentEl.querySelector('.yt-modal-cookie-section');
    if (cookieSection) {
        cookieSection.hidden = true;
    }
}

/**
 * Show the cookie dialog from external callers (e.g., queue.js on authRequired failure).
 */
export function showYouTubeCookieDialog() {
    showCookieDialog();
}

// ============================================================================
// DOM wiring (called once from push.js)
// ============================================================================

/**
 * Initialize YouTube import UI: format toggle, import/cancel buttons.
 */
export function initYouTubeImport() {
    const overlay = document.getElementById('youtube-modal-overlay');
    if (!overlay) {
        return;
    }

    // Enable Import button when a format is selected
    overlay.querySelectorAll('input[name="yt-format"]').forEach(r => {
        r.addEventListener('change', () => updateImportButtonState(overlay));
    });

    // Import button
    overlay.querySelector('.yt-modal-import')?.addEventListener('click', submitImport);

    // Cancel buttons (one in import section, one in cookie section)
    overlay.querySelectorAll('.yt-modal-cancel').forEach(btn => {
        btn.addEventListener('click', () => {
            hideCookieDialog();
            hideImportDialog();
        });
    });

    // Cookie file input
    const cookieInput = overlay.querySelector('.yt-cookie-file-input');
    if (cookieInput) {
        cookieInput.addEventListener('change', (e) => {
            const file = e.target.files?.[0];
            if (file) {
                uploadCookieFile(file);
            }
        });
    }

    // Overlay click to close
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) {
            hideCookieDialog();
            hideImportDialog();
        }
    });

    // Escape key
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && !overlay.hidden) {
            hideCookieDialog();
            hideImportDialog();
        }
    });

    // Check for shared URL on load
    checkSharedUrl();
}
