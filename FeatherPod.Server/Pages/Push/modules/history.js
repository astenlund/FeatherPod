import { FEED_ID, HISTORY_STORAGE_KEY, HISTORY_FILTER_KEY, MAX_LOCAL_HISTORY } from './config.js';
import { formatDuration, formatDate, formatRelativeTime } from './utils.js';
import { getApiKey } from './auth.js';
import { getCurrentState, getCollapsedHeight, getCachedContainerWidth, getCachedCollapsedMargin, COLLAPSED_WIDTH } from './state.js';
import { showContextMenu } from './editing.js';

/** @type {Array<Object>|null} */
let historyData = null;
/** @type {string|null} */
let historySelectedId = null;
/** @type {'local'|'browser'|'all'} */
let historyFilter = 'local';
/** @type {boolean} */
let historyPanelPushedState = false;
/** @type {number} */
let pendingFilterRequest = 0;
/** @type {Array<Object>|null} */
let cachedBrowserUploads = null;
/** @type {Array<Object>|null} */
let cachedAllUploads = null;

// Animation timing constants (match CSS --h-* variables)
const H_BLUR_DELAY = 150;
const H_PAUSE = 100;
const H_MORPH = 400;
const HISTORY_TRANSITION_DURATION = H_BLUR_DELAY + H_PAUSE + H_MORPH;

export function getHistoryFilter() {
    return historyFilter;
}

export function getHistoryPanelPushedState() {
    return historyPanelPushedState;
}

export function setHistoryPanelPushedState(value) {
    historyPanelPushedState = value;
}

export function getHistoryData() {
    return historyData;
}

export function getHistorySelectedId() {
    return historySelectedId;
}

export function invalidateBrowserUploadsCache() {
    cachedBrowserUploads = null;
}

export function invalidateAllUploadsCache() {
    cachedAllUploads = null;
}

/**
 * Immediately collapse the history section without animation.
 * Strips all animation classes, clears inline styles, resets aria and toggle text.
 * Pops the pushed browser history entry if one exists (so back button navigates normally).
 * Safe to call even if history is already collapsed or not yet initialized.
 */
export function collapseHistoryImmediate() {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const frostedOverlay = document.getElementById('frosted-overlay');
    const selectFileBtn = document.getElementById('select-file');

    if (section) {
        section.classList.remove('history-section--expanded', 'history-section--settled', 'history-section--collapsing', 'history-section--fade-out');
        section.style.height = '';
        section.style.width = '';
        section.style.marginLeft = '';
        section.style.marginRight = '';
    }

    if (toggle) {
        toggle.setAttribute('aria-expanded', 'false');
        const textSpan = toggle.querySelector('.history-toggle-text');
        if (textSpan) {
            textSpan.textContent = 'History';
        }
        toggle.classList.remove('text-fading');
        toggle.style.width = '';
    }

    if (frostedOverlay) {
        frostedOverlay.classList.remove('frosted-overlay--active');
    }

    // Re-enable select-file button (may have been disabled when history was expanded in ready state)
    if (selectFileBtn) {
        selectFileBtn.disabled = false;
    }

    // Pop the pushed history entry if one exists
    if (historyPanelPushedState) {
        historyPanelPushedState = false;
        history.back();
    }
}

/**
 * Load upload history from localStorage for this feed.
 * Returns an empty array if no history exists or if parsing fails.
 * @returns {Array<Object>}
 */
function loadLocalHistory() {
    try {
        const stored = localStorage.getItem(HISTORY_STORAGE_KEY);
        if (!stored) {
            return [];
        }

        const hist = JSON.parse(stored);

        return Array.isArray(hist) ? hist : [];
    } catch (e) {
        console.warn('Failed to load history from localStorage:', e);

        return [];
    }
}

/**
 * Save an episode to localStorage history.
 * Removes any existing entry with the same ID and prepends the new episode.
 * Trims history to MAX_LOCAL_HISTORY items. Fails silently on localStorage errors.
 * @param {Object} episode
 */
export function saveToLocalHistory(episode) {
    if (!episode || !episode.id) {
        return;
    }

    try {
        const hist = loadLocalHistory();

        // Remove existing entry with same ID (if re-uploading)
        const filtered = hist.filter(e => e.id !== episode.id);

        // Prepend new episode and trim to max size
        const updated = [episode, ...filtered].slice(0, MAX_LOCAL_HISTORY);

        localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(updated));
    } catch (e) {
        console.warn('Failed to save to localStorage:', e);
    }
}

/**
 * Remove an episode from localStorage history.
 * @param {string} episodeId
 */
export function removeFromLocalHistory(episodeId) {
    try {
        const hist = loadLocalHistory();
        const filtered = hist.filter(e => e.id !== episodeId);
        localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(filtered));
    } catch (e) {
        console.warn('Failed to update localStorage history:', e);
    }
}

/**
 * Update an episode's title in localStorage history.
 * @param {string} episodeId
 * @param {string} newTitle
 */
export function updateLocalHistoryTitle(episodeId, newTitle) {
    try {
        const hist = loadLocalHistory();
        const entry = hist.find(e => e.id === episodeId);
        if (entry) {
            entry.title = newTitle;
            localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(hist));
        }
    } catch (e) {
        console.warn('Failed to update localStorage history:', e);
    }
}

/**
 * Invalidates caches and re-renders the history panel if it's currently visible.
 * Call after any upload completion (sync or async) to keep the history list up to date.
 * @param {string} [selectEpisodeId] - Episode ID to select after refresh (e.g. newly uploaded episode)
 */
export async function refreshHistoryList(selectEpisodeId) {
    cachedBrowserUploads = null;
    cachedAllUploads = null;
    if (selectEpisodeId) {
        historySelectedId = selectEpisodeId;
    }
    const section = document.getElementById('history-section');
    if (!section || section.style.display === 'none') {
        return;
    }
    const uploads = await fetchHistoryByFilter();
    renderHistoryList(uploads, false, true);
}

/**
 * Load saved filter preference from localStorage.
 * @returns {'local'|'browser'|'all'}
 */
function loadFilterPreference() {
    try {
        const saved = localStorage.getItem(HISTORY_FILTER_KEY);
        if (saved && ['local', 'browser', 'all'].includes(saved)) {
            return saved;
        }
    } catch (e) {
        // Ignore
    }

    return 'local';
}

/**
 * Save filter preference to localStorage. Fails silently on errors.
 * @param {'local'|'browser'|'all'} filter
 */
function saveFilterPreference(filter) {
    try {
        localStorage.setItem(HISTORY_FILTER_KEY, filter);
    } catch (e) {
        // Ignore
    }
}

/**
 * Toggle the history section collapsed/expanded state with animation.
 * On desktop: morphs from drop zone size to full width with staggered content reveal.
 * On mobile: slides down as fullscreen overlay.
 * In ready state, drop zone fades out/in via CSS. In queue state, overlays queue content.
 * Pushes a browser history entry when expanding so the back button closes the panel.
 * @param {boolean} [expand]
 */
export function toggleHistorySection(expand) {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const selectFileBtn = document.getElementById('select-file');
    const isMobile = window.matchMedia('(max-width: 768px)').matches;
    const isQueueState = getCurrentState() === 'queue';
    if (!section || !toggle) {
        return;
    }

    const isExpanded = toggle.getAttribute('aria-expanded') === 'true';
    const newState = expand !== undefined ? expand : !isExpanded;

    toggle.setAttribute('aria-expanded', newState.toString());

    // Push browser history entry when expanding so the back button closes the panel
    if (newState && !historyPanelPushedState) {
        history.pushState({ historyPanel: true }, '');
        historyPanelPushedState = true;
    }

    // Animate text change: 1) delay, 2) fade out, 3) resize, 4) fade in, 5) end
    const newText = newState ? '\u2190 Back' : 'History';
    const textSpan = toggle.querySelector('.history-toggle-text');
    const TEXT_FADE = 150;
    const WIDTH_ANIM = 150;

    // On mobile, button is full-width so skip width animation
    if (isMobile) {
        // Simple text swap with fade
        const delay = newState ? H_BLUR_DELAY + H_PAUSE : 0;
        setTimeout(() => {
            toggle.classList.add('text-fading');
        }, delay);
        setTimeout(() => {
            textSpan.textContent = newText;
            toggle.classList.remove('text-fading');
        }, delay + TEXT_FADE);
    } else {
        // Desktop: full animation with width change
        const currentWidth = toggle.offsetWidth;

        // Measure target width with new text (while hidden)
        textSpan.style.visibility = 'hidden';
        textSpan.textContent = newText;
        toggle.style.width = 'auto';
        const newWidth = toggle.offsetWidth;
        textSpan.textContent = newState ? 'History' : '\u2190 Back'; // restore old text
        textSpan.style.visibility = '';
        toggle.style.width = currentWidth + 'px';

        // Same cadence in both ready and queue states
        const totalDuration = newState
            ? H_BLUR_DELAY + H_PAUSE + H_MORPH
            : H_MORPH;
        const fadeInStart = totalDuration - TEXT_FADE;
        const widthStart = fadeInStart - WIDTH_ANIM;
        const fadeOutStart = Math.max(0, widthStart - TEXT_FADE);

        // Step 2: Fade out old text
        setTimeout(() => {
            toggle.classList.add('text-fading');
        }, fadeOutStart);

        // Step 3: Change text and animate width
        setTimeout(() => {
            textSpan.textContent = newText;
            toggle.style.width = newWidth + 'px';
        }, widthStart);

        // Step 4: Fade in new text
        setTimeout(() => {
            toggle.classList.remove('text-fading');
        }, fadeInStart);

        // Step 5: Clean up after animation ends
        setTimeout(() => {
            toggle.style.width = '';
        }, totalDuration);
    }

    // In ready state, disable select-file button when expanded (hidden via CSS but could still be activated)
    if (!isQueueState && selectFileBtn) {
        selectFileBtn.disabled = newState;
    }

    // Mobile: toggle frosted overlay
    const frostedOverlay = document.getElementById('frosted-overlay');
    if (isMobile && frostedOverlay) {
        if (newState) {
            frostedOverlay.classList.add('frosted-overlay--active');
        }
        // When collapsing, delay removing frosted overlay until after panel fades out
        // (handled below with COLLAPSE_DURATION timeout)
    }

    if (newState) {
        // Expanding: measure natural height, animate from collapsed to expanded

        // Clear blur-fade-in animation so filter property is free for CSS transitions.
        // Force reflow so the base filter: blur(0) is committed before the expanded
        // class triggers blur(12px), ensuring the transition animates.
        const container = document.querySelector('.container');
        const blurTarget = container?.classList.contains('state-queue')
            ? document.getElementById('queue')
            : document.getElementById('drop-zone');
        if (blurTarget) {
            blurTarget.style.animation = 'none';
            void blurTarget.offsetHeight;
        }

        // Use cached values for consistent animations
        const containerWidth = getCachedContainerWidth();
        const collapsedMargin = getCachedCollapsedMargin() + 'px';

        // Measure natural height by temporarily expanding
        section.style.height = 'auto';
        section.classList.add('history-section--expanded');
        const naturalHeight = section.offsetHeight;
        section.classList.remove('history-section--expanded');

        // Set explicit starting state for history section
        // Only on desktop (mobile uses position: fixed fullscreen, no animation needed)
        if (!isMobile) {
            section.style.height = getCollapsedHeight() + 'px';
            section.style.width = COLLAPSED_WIDTH + 'px';
            section.style.marginLeft = collapsedMargin;
            section.style.marginRight = collapsedMargin;
            // Force reflow to ensure starting values are applied
            void section.offsetHeight;
        }

        // Animate to expanded state
        section.classList.add('history-section--expanded');
        if (!isMobile) {
            // Desktop: animate to calculated dimensions
            section.style.height = naturalHeight + 'px';
            section.style.width = containerWidth + 'px';
            section.style.marginLeft = '0';
            section.style.marginRight = '0';
        }

        // After transition, clear inline styles and mark as settled
        setTimeout(() => {
            if (!isMobile) {
                section.style.height = 'auto';
                section.style.width = '';
                section.style.marginLeft = '';
                section.style.marginRight = '';
            }
            // Mark as settled so tab switches don't have expand animation delays
            section.classList.add('history-section--settled');
        }, HISTORY_TRANSITION_DURATION);

        // Focus the selected item (or first item) after transition
        setTimeout(() => {
            const selectedItem = document.querySelector('#history-list .upload-item--selected');
            const firstItem = document.querySelector('#history-list .upload-item');
            const itemToFocus = selectedItem || firstItem;
            if (itemToFocus) {
                itemToFocus.focus();
            }
        }, HISTORY_TRANSITION_DURATION);
    } else {
        // Collapsing: morph back to collapsed dimensions

        // Use cached values for consistent animations
        const containerWidth = getCachedContainerWidth();
        const collapsedMargin = getCachedCollapsedMargin() + 'px';
        const currentHeight = section.offsetHeight;

        // Set explicit starting state (expanded)
        // Only on desktop (mobile uses position: fixed fullscreen, no animation needed)
        if (!isMobile) {
            section.style.height = currentHeight + 'px';
            section.style.width = containerWidth + 'px';
            section.style.marginLeft = '0';
            section.style.marginRight = '0';
            // Force reflow to ensure starting values are applied
            void section.offsetHeight;
        }

        // Step 1: Add collapsing class (removes expand animations, enables collapse transitions)
        section.classList.add('history-section--collapsing');
        section.classList.remove('history-section--expanded', 'history-section--settled');

        // Step 2: Fade out content AND animate panel dimensions simultaneously
        void section.offsetHeight; // Force reflow
        section.classList.add('history-section--fade-out');
        if (!isMobile) {
            section.style.height = getCollapsedHeight() + 'px';
            section.style.width = COLLAPSED_WIDTH + 'px';
            section.style.marginLeft = collapsedMargin;
            section.style.marginRight = collapsedMargin;
        }

        // After transition, clear all inline styles and collapsing classes
        setTimeout(() => {
            section.classList.remove('history-section--collapsing', 'history-section--fade-out');
            if (!isMobile) {
                section.style.height = '';
                section.style.width = '';
                section.style.marginLeft = '';
                section.style.marginRight = '';
            }
            // Remove frosted overlay after panel has faded out
            if (isMobile && frostedOverlay) {
                frostedOverlay.classList.remove('frosted-overlay--active');
            }
        }, H_MORPH);

        // Reset scroll position and selection to first item
        const list = document.getElementById('history-list');
        if (list) {
            list.scrollTop = 0;
        }
        if (historyData && historyData.length > 0) {
            historySelectedId = historyData[0].id;
            list?.querySelectorAll('.upload-item').forEach((item, index) => {
                const isFirst = index === 0;
                item.classList.toggle('upload-item--selected', isFirst);
                item.setAttribute('aria-selected', isFirst.toString());
            });
            updateHistoryInfoCard(historyData[0]);
        }

        // In ready state, return focus to the upload button
        if (!isQueueState && selectFileBtn) {
            selectFileBtn.focus();
        }
    }
}

/**
 * Fetch and cache browser uploads from server.
 * Cache is invalidated in refreshHistoryList() after each upload completes.
 * @returns {Promise<Array<Object>>}
 */
export async function fetchBrowserUploads() {
    if (cachedBrowserUploads !== null) {
        return cachedBrowserUploads;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?source=Browser&limit=50', {
            headers: { 'X-API-Key': getApiKey() }
        });

        if (!response.ok) {
            return cachedBrowserUploads || [];
        }

        cachedBrowserUploads = await response.json();

        return cachedBrowserUploads;
    } catch (err) {
        console.warn('Error fetching browser uploads:', err);

        return cachedBrowserUploads || [];
    }
}

/**
 * Fetch and cache all uploads from server.
 * Cache is invalidated in refreshHistoryList() after each upload completes.
 * @returns {Promise<Array<Object>>}
 */
async function fetchAllUploads() {
    if (cachedAllUploads !== null) {
        return cachedAllUploads;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?limit=50', {
            headers: { 'X-API-Key': getApiKey() }
        });

        if (!response.ok) {
            return cachedAllUploads || [];
        }

        cachedAllUploads = await response.json();

        return cachedAllUploads;
    } catch (err) {
        console.warn('Error fetching all uploads:', err);

        return cachedAllUploads || [];
    }
}

/**
 * Fetch uploads based on current filter mode. Uses cached data when available.
 * @returns {Promise<Array<Object>>}
 */
async function fetchHistoryByFilter() {
    if (historyFilter === 'local') {
        // Get local history and validate against cached server data
        const localHistory = loadLocalHistory();
        if (localHistory.length === 0) {
            return [];
        }

        // Use cached browser uploads to check which local episodes still exist
        const serverEpisodes = await fetchBrowserUploads();
        const serverIds = new Set(serverEpisodes.map(e => e.id));

        // Filter local history to only include episodes that exist on server
        const validHistory = localHistory.filter(e => serverIds.has(e.id));

        // Update localStorage to remove deleted episodes
        if (validHistory.length !== localHistory.length) {
            try {
                localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(validHistory));
            } catch (e) {
                // Ignore localStorage errors
            }
        }

        return validHistory;
    } else if (historyFilter === 'browser') {
        return await fetchBrowserUploads();
    } else {
        return await fetchAllUploads();
    }
}

/**
 * Get the appropriate empty state message for the current filter.
 * @returns {string}
 */
export function getHistoryEmptyMessage() {
    switch (historyFilter) {
        case 'local':
            return 'No uploads from this browser yet';
        case 'browser':
            return 'No browser uploads yet';
        case 'all':
            return 'No uploads yet';
        default:
            return 'No uploads yet';
    }
}

/**
 * Update the history info card with episode data.
 * Populates the info card fields if an episode is provided, otherwise hides the card.
 * Title and filename are truncated with ellipsis; clicking them toggles expanded view.
 * Expanded state resets when switching episodes.
 * @param {Object|null} episode
 */
export function updateHistoryInfoCard(episode) {
    const infoCard = document.getElementById('history-info');
    if (!infoCard) {
        return;
    }

    if (!episode) {
        infoCard.style.display = 'none';

        return;
    }

    infoCard.style.display = 'grid';
    const titleEl = document.getElementById('history-info-title');
    titleEl.textContent = episode.title || episode.fileName;
    titleEl.classList.remove('expanded');
    const filenameEl = document.getElementById('history-info-filename');
    filenameEl.textContent = episode.fileName || '';
    filenameEl.classList.remove('expanded');
    document.getElementById('history-info-published').textContent = formatDate(episode.publishedDate);

    // Combine duration and size: "16m 40s (31 MB)"
    const duration = formatDuration(episode.duration);
    const size = episode.fileSize ? episode.fileSize.formatBytes() : '';
    let durationText = duration;
    if (duration && size) {
        durationText = duration + ' (' + size + ')';
    } else if (size) {
        durationText = size;
    }
    document.getElementById('history-info-duration').textContent = durationText;

    const uploadedTime = formatRelativeTime(episode.uploadedAt);
    document.getElementById('history-info-uploaded').textContent = uploadedTime;
    document.getElementById('history-info-uploaded-label').style.display = uploadedTime ? '' : 'none';
    document.getElementById('history-info-uploaded').style.display = uploadedTime ? '' : 'none';
}

/**
 * Update the scroll fade mask based on scroll position.
 */
export function updateHistoryListScrollState() {
    const list = document.getElementById('history-list');
    if (!list) {
        return;
    }

    const isScrollable = list.scrollHeight > list.clientHeight;
    const isAtTop = list.scrollTop <= 2;
    const isAtBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - 2;

    list.classList.remove('fade-top', 'fade-bottom', 'fade-both');

    if (!isScrollable) {
        // No fade needed
    } else if (isAtTop && !isAtBottom) {
        list.classList.add('fade-bottom');
    } else if (!isAtTop && isAtBottom) {
        list.classList.add('fade-top');
    } else if (!isAtTop && !isAtBottom) {
        list.classList.add('fade-both');
    }
}

/**
 * Render the history list in the ready state.
 * Clears existing list, creates upload items, and preserves the current selection if possible.
 * Shows an empty state message if no uploads are provided.
 * @param {Array<Object>} uploads
 * @param {boolean} [focusFirst=false]
 * @param {boolean} [skipAnimation=false]
 */
function renderHistoryList(uploads, focusFirst = false, skipAnimation = false) {
    const list = document.getElementById('history-list');
    const emptyState = document.getElementById('history-empty');
    if (!list) {
        return;
    }

    const section = document.getElementById('history-section');
    if (section) {
        section.classList.toggle('history-section--no-animate', skipAnimation);
    }

    list.innerHTML = '';
    list.classList.remove('fade-top', 'fade-bottom', 'fade-both');

    if (!uploads || uploads.length === 0) {
        historyData = null;
        historySelectedId = null;
        updateHistoryInfoCard(null);
        if (emptyState) {
            emptyState.textContent = getHistoryEmptyMessage();
            emptyState.style.display = 'block';
        }

        return;
    }

    if (emptyState) {
        emptyState.style.display = 'none';
    }

    historyData = uploads;

    // Preserve selection if the previously selected episode is still in the list
    const preserved = historySelectedId && uploads.find(u => u.id === historySelectedId);
    if (preserved) {
        updateHistoryInfoCard(preserved);
    } else {
        historySelectedId = uploads[0].id;
        updateHistoryInfoCard(uploads[0]);
    }

    uploads.forEach((upload, index) => {
        const item = document.createElement('div');
        item.className = 'upload-item';
        item.dataset.id = upload.id;
        item.tabIndex = 0;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', (upload.id === historySelectedId).toString());

        // Staggered animation delay for each item (via CSS custom property)
        item.style.setProperty('--stagger-index', String(index));

        if (upload.id === historySelectedId) {
            item.classList.add('upload-item--selected');
        }

        const title = document.createElement('span');
        const titleText = upload.title || upload.fileName;
        title.className = 'upload-title';
        title.textContent = titleText;

        const time = document.createElement('span');
        time.className = 'upload-time';
        time.textContent = formatRelativeTime(upload.uploadedAt);

        item.appendChild(title);
        if (upload.uploadedAt) {
            item.appendChild(time);
        }

        item.addEventListener('click', () => selectHistoryUpload(upload.id));

        // Desktop right-click context menu
        item.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            selectHistoryUpload(upload.id);
            showContextMenu(upload.id, e.clientX, e.clientY);
        });

        // Mobile long-press context menu
        let longPressTimer = null;
        let touchStartX = 0;
        let touchStartY = 0;

        item.addEventListener('touchstart', (e) => {
            const touch = e.touches[0];
            touchStartX = touch.clientX;
            touchStartY = touch.clientY;
            longPressTimer = setTimeout(() => {
                longPressTimer = null;
                selectHistoryUpload(upload.id);
                showContextMenu(upload.id, touch.clientX, touch.clientY);
            }, 500);
        });

        item.addEventListener('touchmove', (e) => {
            if (longPressTimer === null) {
                return;
            }
            const touch = e.touches[0];
            const dx = touch.clientX - touchStartX;
            const dy = touch.clientY - touchStartY;
            if (dx * dx + dy * dy > 100) { // 10px threshold
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }
        });

        item.addEventListener('touchend', () => {
            if (longPressTimer !== null) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }
        });

        item.addEventListener('touchcancel', () => {
            if (longPressTimer !== null) {
                clearTimeout(longPressTimer);
                longPressTimer = null;
            }
        });

        list.appendChild(item);
    });

    // Check scroll state after render (use requestAnimationFrame to ensure layout is complete)
    requestAnimationFrame(() => {
        updateHistoryListScrollState();

        // Scroll selected item into view and optionally focus it
        const selectedItem = list.querySelector('.upload-item--selected');
        if (selectedItem) {
            selectedItem.scrollIntoView({ block: 'nearest' });
            if (focusFirst) {
                selectedItem.focus();
            }
        } else if (focusFirst) {
            const firstItem = list.querySelector('.upload-item');
            if (firstItem) {
                firstItem.focus();
            }
        }
    });
}

/**
 * Select an upload in the history list.
 * Updates the visual selection state and populates the info card with the selected episode.
 * @param {string} uploadId
 * @param {boolean} [moveFocus=false]
 */
export function selectHistoryUpload(uploadId, moveFocus = false) {
    if (!historyData) {
        return;
    }

    const upload = historyData.find(u => u.id === uploadId);
    if (!upload) {
        return;
    }

    historySelectedId = uploadId;

    // Update visual selection and aria-selected
    const list = document.getElementById('history-list');
    if (list) {
        list.querySelectorAll('.upload-item').forEach(item => {
            const isSelected = item.dataset.id === uploadId;
            item.classList.toggle('upload-item--selected', isSelected);
            item.setAttribute('aria-selected', isSelected.toString());
            if (isSelected && moveFocus) {
                item.focus();
            }
        });
    }

    updateHistoryInfoCard(upload);
}

/**
 * Update filter tab visual state.
 */
function updateFilterTabs() {
    const tabs = document.querySelectorAll('#history-section .filter-tab');
    const tabpanel = document.getElementById('history-tabpanel');
    tabs.forEach(tab => {
        const isActive = tab.dataset.filter === historyFilter;
        tab.classList.toggle('filter-tab--active', isActive);
        tab.setAttribute('aria-selected', isActive.toString());
        if (isActive && tabpanel) {
            tabpanel.setAttribute('aria-labelledby', tab.id);
        }
    });
}

/**
 * Handle filter tab change.
 * Uses request tracking to prevent race conditions when rapidly switching filters.
 * @param {'local'|'browser'|'all'} filter
 * @param {boolean} [focusFirst=false]
 */
export async function changeHistoryFilter(filter, focusFirst = false) {
    if (filter === historyFilter) {
        return;
    }

    historyFilter = filter;
    saveFilterPreference(filter);
    updateFilterTabs();

    const requestId = ++pendingFilterRequest;
    const uploads = await fetchHistoryByFilter();

    // Ignore stale response if another filter change happened while fetching
    if (requestId !== pendingFilterRequest) {
        return;
    }

    renderHistoryList(uploads, focusFirst, true);
}

/**
 * Initialize the history section in the ready or queue state.
 * Loads saved filter preference, fetches uploads, and renders the list.
 * The toggle and section are always shown (even if empty) so users can switch filters.
 */
export async function initHistorySection() {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    if (!section) {
        return;
    }

    // Load saved filter preference
    historyFilter = loadFilterPreference();
    updateFilterTabs();

    // Fetch and render history
    const uploads = await fetchHistoryByFilter();

    // Show toggle and section
    if (toggle) {
        toggle.style.display = 'block';
    }
    section.style.display = 'block';
    renderHistoryList(uploads || [], false, false);
}
