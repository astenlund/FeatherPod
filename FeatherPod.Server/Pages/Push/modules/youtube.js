/**
 * YouTube import module -- URL detection (paste, drop, share target), format toggle, server submission.
 *
 * Detects YouTube URLs from:
 * - Document-level paste events (runs before API key paste detection)
 * - Drop zones (text/uri-list then text/plain, checked before any dropped files so a
 *   linked-thumbnail drag from YouTube search results imports instead of uploading the image)
 * - ?yt= query param (from PWA share target redirect)
 * - Long-press (500ms) on select-file button or drop zone reads clipboard (iOS);
 *   falls back to a paste-input modal when Clipboard API is denied (iOS PWA)
 *
 * URL parsing lives in youtube-url.js (pure, unit-tested); every detection path
 * above funnels its text through extractYouTubeUrl, which returns the canonical
 * watch URL rebuilt from the matched video id.
 *
 * On detection, shows an import dialog with audio/video toggle, then POSTs to
 * /api/feeds/{feedId}/youtube with the oEmbed title for instant queue display.
 * On 202, creates a queue entry and hands off to existing queue monitoring.
 *
 * Bot detection surfaces asynchronously: the job fails with authRequired after the 202,
 * and queue.js opens the cookie dialog with a retry descriptor for that failed entry.
 * After a successful cookie upload the import is re-submitted from that descriptor and
 * the replacement 202 dismisses the failed entry; without a descriptor (the ?ytcookies
 * dev path) the modal simply closes.
 */

import { FEED_ID } from './config.js';
import { getApiKey, getUserRole } from './auth.js';
import { getCurrentState } from './state.js';
import { showToast } from './utils.js';
import { canonicalYouTubeUrl, extractYouTubeUrl } from './youtube-url.js';

const YT_FORMAT_PREFS_KEY = 'featherpod_yt_format_prefs';

/** @type {Function|null} Callback to create a queue entry from a YouTube 202 response */
let onYouTubeJobCreated = null;

/**
 * Register the callback invoked when a YouTube job is successfully created.
 * Called from push.js during init.
 * @param {Function} callback - (jobResponse: object, options: {replacesEntryId: string|null}) => void;
 *   replacesEntryId names the failed queue entry this job retries after a cookie upload
 */
export function registerYouTubeJobCallback(callback) {
    onYouTubeJobCreated = callback;
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
    beginImport(url);

    return true;
}

/**
 * Handle a drop event. Scans the text payload for a YouTube URL, text/uri-list first
 * (the canonical link slot of an anchor or linked-image drag) then text/plain, regardless
 * of whether files were dropped alongside. Returns true if a YouTube URL was found, in
 * which case the caller must not treat the dropped files as an upload.
 * @param {DragEvent} e
 * @returns {boolean}
 */
export function handleDrop(e) {
    const text = ['text/uri-list', 'text/plain']
        .map(type => e.dataTransfer.getData(type))
        .filter(Boolean)
        .join('\n');
    const url = extractYouTubeUrl(text);
    if (!url) {
        return false;
    }

    e.preventDefault();
    beginImport(url);

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
        // Defer past page init before showing the dialog
        beginImport(url, 500);
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

/**
 * @type {number}
 * Generation of the interaction that currently owns the modal. Advanced whenever
 * ownership changes: a new import dialog, the cookie dialog, or closing. Every
 * continuation that resumes after a network await (import submit, cookie upload, oEmbed
 * metadata) captures the generation when it starts and leaves the modal alone once it
 * has moved on, so an older interaction can never close, clear or re-enable a newer one.
 */
let modalGeneration = 0;

/**
 * @type {{url: string, channel: string|null, title: string|null, replacesEntryId: string|null, submitting: boolean}|null}
 * Import currently shown in the dialog. `channel` and `title` arrive later via oEmbed.
 * `replacesEntryId` names the failed queue entry this import retries after a cookie
 * upload; it is bound to this import only, so a later import of another video can
 * never dismiss that entry. `submitting` is true while its POST is in flight and keeps
 * the Import button disabled whatever the radios or metadata do meanwhile.
 */
let pendingImport = null;

/**
 * @type {{videoId: string, format: 'audio'|'video', title: string|null, entryId: string}|null}
 * Import to re-submit after a successful cookie upload, built by queue.js from the failed
 * entry whose authRequired failure opened the cookie dialog. Null when the dialog was
 * opened without one (the ?ytcookies dev path). Consumed by the cookie upload and
 * cleared whenever the modal closes or a new import starts.
 */
let pendingRetry = null;

/**
 * Kick off a YouTube import: optionally defer, then show the confirmation dialog.
 * @param {string} url
 * @param {number} [deferMs=0] - Delay before showing the dialog (used to wait past page init)
 */
function beginImport(url, deferMs = 0) {
    if (deferMs > 0) {
        setTimeout(() => showImportDialog(url), deferMs);
    } else {
        showImportDialog(url);
    }
}

/**
 * Show the YouTube import confirmation dialog. Starts the oEmbed metadata fetch internally.
 * @param {string} url
 */
function showImportDialog(url) {
    const generation = ++modalGeneration;
    pendingImport = { url, channel: null, title: null, replacesEntryId: null, submitting: false };
    pendingRetry = null;

    const overlay = document.getElementById('youtube-modal-overlay');
    if (!overlay) {
        return;
    }

    // A new import takes the modal over from the cookie dialog if that is what is showing
    hideCookieDialog();

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

    // Fetch oEmbed metadata and apply it when it arrives
    fetchVideoMeta(url).then(data => {
        if (!data || generation !== modalGeneration) {
            return;
        }
        if (titleEl && data.title) {
            titleEl.textContent = data.title;
            pendingImport.title = data.title;
        }
        if (metaEl && data.author_name) {
            metaEl.textContent = data.author_name;
            pendingImport.channel = data.author_name;
        }

        // Select remembered format for this channel (if any)
        const pref = data.author_name ? getChannelFormatPref(data.author_name) : null;
        if (pref) {
            const radio = overlay.querySelector(`input[name="yt-format"][value="${pref}"]`);
            if (radio) {
                radio.checked = true;
            }
        }
        updateImportButtonState(overlay);
    });

    overlay.hidden = false;
}

function updateImportButtonState(overlay) {
    const importBtn = overlay?.querySelector('.yt-modal-import');
    const checked = overlay?.querySelector('input[name="yt-format"]:checked');
    if (importBtn) {
        importBtn.disabled = !checked || Boolean(pendingImport?.submitting);
    }
}

/**
 * Fetch video title and channel via YouTube oEmbed API.
 * Returns { title, author_name } or null on failure.
 * @param {string} url
 * @returns {Promise<{title?: string, author_name?: string}|null>}
 */
async function fetchVideoMeta(url) {
    const oembedUrl = `https://www.youtube.com/oembed?url=${encodeURIComponent(url)}&format=json`;

    try {
        const response = await fetch(oembedUrl);

        return response.ok ? await response.json() : null;
    } catch {
        return null;
    }
}

/**
 * Hide the import dialog.
 */
export function hideImportDialog() {
    modalGeneration++;
    const overlay = document.getElementById('youtube-modal-overlay');
    if (overlay) {
        overlay.hidden = true;
    }
    pendingImport = null;
    pendingRetry = null;
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
    if (!pendingImport || pendingImport.submitting) {
        return;
    }

    const overlay = document.getElementById('youtube-modal-overlay');
    const importBtn = overlay?.querySelector('.yt-modal-import');
    const errorEl = overlay?.querySelector('.yt-modal-error');
    const spinner = overlay?.querySelector('.yt-modal-spinner');

    // Bind to this request and this modal generation: the dialog can be dismissed or
    // handed to another interaction while the POST is in flight, and a job the server
    // accepted must still reach the queue.
    const request = pendingImport;
    const generation = modalGeneration;
    const dialogStillOurs = () => generation === modalGeneration;
    request.submitting = true;

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
            body: JSON.stringify({ url: request.url, format, title: request.title })
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            throw new Error(data.error || `Server returned ${response.status}`);
        }

        const jobResponse = await response.json();

        saveChannelFormatPref(request.channel, format);
        if (dialogStillOurs()) {
            hideImportDialog();
        }

        if (onYouTubeJobCreated) {
            onYouTubeJobCreated(jobResponse, { replacesEntryId: request.replacesEntryId });
        }
    } catch (err) {
        request.submitting = false;
        if (!dialogStillOurs()) {
            return;
        }
        if (errorEl) {
            errorEl.textContent = err.message || 'Import failed';
            errorEl.hidden = false;
        }
        updateImportButtonState(overlay);
    } finally {
        if (spinner && dialogStillOurs()) {
            spinner.hidden = true;
        }
    }
}

// ============================================================================
// Cookie upload dialog (shown on YouTube bot detection)
// ============================================================================

/**
 * Show the cookie upload dialog inside the YouTube modal. Takes over the modal from any
 * import interaction still in flight (its accepted job still reaches the queue).
 * Admin users see a file picker; non-admin users see a "temporarily unavailable" message.
 */
function showCookieDialog() {
    modalGeneration++;
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
    const generation = modalGeneration;

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

        if (generation !== modalGeneration) {
            // The dialog that started this upload was dismissed or replaced meanwhile; the
            // cookies are stored server-side regardless, but no UI belongs to it any more.
            return;
        }

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            throw new Error(data.error || `Upload failed (${response.status})`);
        }

        hideCookieDialog();
        const retry = pendingRetry;
        pendingRetry = null;
        if (retry) {
            await resubmitImport(retry);
        } else {
            hideImportDialog();
            showToast('Cookies uploaded');
        }
    } catch (err) {
        if (statusEl && generation === modalGeneration) {
            statusEl.textContent = err.message || 'Upload failed';
            statusEl.className = 'yt-cookie-status yt-cookie-error';
        }
    }
}

/**
 * Re-run an import whose job failed on bot detection, now that cookies are uploaded:
 * rebuild the import dialog for the video, preselect the format the failed job used,
 * and submit immediately. The 202 then replaces the failed entry via the job callback;
 * the entry id travels on pendingImport so only this video's success can dismiss it.
 * @param {{videoId: string, format: 'audio'|'video', title: string|null, entryId: string}} retry
 */
async function resubmitImport(retry) {
    showImportDialog(canonicalYouTubeUrl(retry.videoId));
    pendingImport.title = retry.title;
    pendingImport.replacesEntryId = retry.entryId;

    const overlay = document.getElementById('youtube-modal-overlay');
    const titleEl = overlay?.querySelector('.yt-modal-video-title');
    if (titleEl && retry.title) {
        titleEl.textContent = retry.title;
    }
    const radio = overlay?.querySelector(`input[name="yt-format"][value="${retry.format}"]`);
    if (radio) {
        radio.checked = true;
    }

    await submitImport();
}

/**
 * Hide the cookie dialog and restore normal import UI.
 */
function hideCookieDialog() {
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
 * Show the cookie dialog from external callers (queue.js on authRequired failure, the
 * ?ytcookies dev flag).
 * @param {{videoId: string, format: 'audio'|'video', title: string|null, entryId: string}|null} [retry]
 *   Import to re-submit after a successful cookie upload; null just closes the modal afterwards
 */
export function showYouTubeCookieDialog(retry = null) {
    pendingRetry = retry;
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

// ============================================================================
// Long-press clipboard import (iOS)
// ============================================================================

/** @type {boolean} Set when a long-press fires; consumed by click handlers to suppress file picker */
let longPressConsumed = false;

/**
 * Returns and resets the long-press consumed flag.
 * Call at the top of click handlers that should be suppressed after a long-press.
 * @returns {boolean}
 */
export function consumeLongPressFlag() {
    const consumed = longPressConsumed;
    longPressConsumed = false;

    return consumed;
}

/**
 * Show a modal with a paste-input field as fallback when the Clipboard API
 * is denied (e.g. iOS PWA standalone mode). Auto-focuses the input so iOS
 * shows the native "Paste" pill above the keyboard.
 */
function showClipboardFallbackModal() {
    // Reuse if already open
    let overlay = document.getElementById('clipboard-fallback-overlay');
    if (overlay) {
        overlay.hidden = false;
        overlay.querySelector('.modal-input')?.focus();

        return;
    }

    overlay = document.createElement('div');
    overlay.id = 'clipboard-fallback-overlay';
    overlay.className = 'modal-overlay';

    const modal = document.createElement('div');
    modal.className = 'modal modal--narrow';
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-label', 'Paste YouTube URL');
    modal.setAttribute('aria-modal', 'true');

    const title = document.createElement('h3');
    title.className = 'modal-title';
    title.textContent = 'Paste YouTube URL';

    const body = document.createElement('div');
    body.className = 'modal-body';

    const input = document.createElement('input');
    input.type = 'url';
    input.className = 'modal-input';
    input.placeholder = 'https://youtube.com/watch?v=...';
    input.enterKeyHint = 'go';
    input.autocomplete = 'off';

    body.appendChild(input);

    const actions = document.createElement('div');
    actions.className = 'modal-actions';

    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'btn-modal btn-modal--secondary';
    cancelBtn.textContent = 'Cancel';

    actions.appendChild(cancelBtn);

    modal.append(title, body, actions);
    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    const cleanup = () => {
        overlay.hidden = true;
        input.value = '';
    };

    const trySubmit = (text, event) => {
        const url = extractYouTubeUrl(text?.trim());
        if (url) {
            event?.preventDefault();
            cleanup();
            beginImport(url);
        }
    };

    input.addEventListener('paste', (e) => {
        trySubmit(e.clipboardData?.getData('text/plain') || '', e);
    });

    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            trySubmit(input.value);
        }
        if (e.key === 'Escape') {
            cleanup();
        }
    });

    cancelBtn.addEventListener('click', cleanup);
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) {
            cleanup();
        }
    });

    // Focus after a microtask so the overlay transition starts
    requestAnimationFrame(() => input.focus());
}

/**
 * Read clipboard and open import dialog if a YouTube URL is found.
 * Called from touchend to ensure transient user activation on iOS Safari.
 * Falls back to a paste-input modal when the Clipboard API is unavailable
 * or denied (iOS PWA standalone mode).
 */
async function readClipboardAndImport() {
    const state = getCurrentState();
    if (state !== 'ready' && state !== 'queue') {
        return;
    }

    if (!navigator.clipboard?.readText) {
        showClipboardFallbackModal();

        return;
    }

    try {
        const text = await navigator.clipboard.readText();
        const url = extractYouTubeUrl(text?.trim());
        if (url) {
            beginImport(url);
        } else {
            showToast('No YouTube link on clipboard', 3000, 'clipboard-toast');
        }
    } catch {
        showClipboardFallbackModal();
    }
}

/**
 * Attach long-press (500ms) touch listeners to an element. On successful long-press,
 * reads the clipboard for a YouTube URL. Uses a split-phase design: the timer in
 * touchstart determines IF a long-press occurred, but the clipboard read happens in
 * touchend which provides a fresh user activation context (required by iOS Safari).
 *
 * Each call creates independent closure state, so multiple elements can be wired independently.
 * @param {HTMLElement} element
 */
export function handleLongPressClipboard(element) {
    if (!element) {
        return;
    }

    let longPressTimer = null;
    let longPressCompleted = false;
    let touchStartX = 0;
    let touchStartY = 0;

    // Suppress native image/link context menu so long-press fires our handler instead
    element.addEventListener('contextmenu', (e) => e.preventDefault());

    element.addEventListener('touchstart', (e) => {
        if (element.disabled) {
            return;
        }

        longPressCompleted = false;
        const touch = e.touches[0];
        touchStartX = touch.clientX;
        touchStartY = touch.clientY;
        longPressTimer = setTimeout(() => {
            longPressTimer = null;
            longPressCompleted = true;
        }, 500);
    });

    element.addEventListener('touchmove', (e) => {
        if (longPressTimer === null) {
            return;
        }

        const touch = e.touches[0];
        const dx = touch.clientX - touchStartX;
        const dy = touch.clientY - touchStartY;
        if (dx * dx + dy * dy > 100) {
            clearTimeout(longPressTimer);
            longPressTimer = null;
            longPressCompleted = false;
        }
    });

    element.addEventListener('touchend', () => {
        if (longPressTimer !== null) {
            clearTimeout(longPressTimer);
            longPressTimer = null;
        }

        if (longPressCompleted) {
            longPressCompleted = false;
            longPressConsumed = true;
            // Safety reset in case the browser suppresses the click event after a long-press
            setTimeout(() => { longPressConsumed = false; }, 300);
            readClipboardAndImport();
        }
    });

    element.addEventListener('touchcancel', () => {
        if (longPressTimer !== null) {
            clearTimeout(longPressTimer);
            longPressTimer = null;
        }
        longPressCompleted = false;
    });
}
