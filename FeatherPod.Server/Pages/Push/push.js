const FEED_ID = '{{FEED_ID}}';
const IS_DEV = '{{IS_DEV}}' === 'true';
const SHOW_GHOST = IS_DEV && window.location.search.includes('ghost');
const VELOCITY_OVERRIDES = IS_DEV ? parseVelocityOverrides() : {};
const ALLOWED_EXTENSIONS = ['.mp3', '.m4a', '.wav', '.ogg', '.flac', '.aac'];

/**
 * @typedef {Object} Episode
 * @property {string} id - Episode ID
 * @property {string} title - Episode title
 * @property {string} fileName - Audio file name
 * @property {number} [fileSize] - File size in bytes
 * @property {string} [duration] - Duration string (e.g. "1:23:45")
 * @property {string} [publishedDate] - ISO date string
 * @property {string} [uploadedAt] - ISO date string
 */

/**
 * Parse velocity override URL params (dev only).
 * @returns {{Uploading?: number, Analyzing?: number, Normalizing?: number}}
 */
function parseVelocityOverrides() {
    const params = new URLSearchParams(window.location.search);
    const overrides = {};
    const mapping = { vup: 'Uploading', vanal: 'Analyzing', vnorm: 'Normalizing' };
    for (const [param, stage] of Object.entries(mapping)) {
        const value = params.get(param);
        if (value != null) {
            const parsed = parseFloat(value);
            if (!isNaN(parsed)) {
                overrides[stage] = parsed * 1024; // kB/s to bytes/s
            }
        }
    }

    return overrides;
}
let apiKey = null;
const states = ['no-key', 'ready', 'uploading', 'normalizing', 'success', 'error'];
const JOB_STORAGE_KEY = 'featherpod_job_' + FEED_ID;
const HISTORY_STORAGE_KEY = 'featherpod_history_' + FEED_ID;
const HISTORY_FILTER_KEY = 'featherpod_history_filter_' + FEED_ID;
const MAX_LOCAL_HISTORY = 50;

/** @type {Array<Object>|null} */
let recentUploadsData = null;
/** @type {string|null} */
let selectedUploadId = null;

/** @type {Array<Object>|null} - History data for ready state */
let historyData = null;
/** @type {string|null} - Selected upload ID in history */
let historySelectedId = null;
/** @type {'local'|'browser'|'all'} - Current history filter */
let historyFilter = 'local';
/** @type {number} - Counter for tracking pending filter requests (prevents race conditions) */
let pendingFilterRequest = 0;

/** @param {string} stateName */
function showState(stateName) {
    states.forEach(s => document.getElementById(s).style.display = s === stateName ? '' : 'none');

    // Update container state class for CSS styling
    const container = document.querySelector('.container');
    if (container) {
        states.forEach(s => container.classList.remove('state-' + s));
        container.classList.add('state-' + stateName);
    }

    // Update page title based on state
    const titleEl = document.getElementById('page-title');
    if (titleEl) {
        if (stateName === 'uploading' || stateName === 'normalizing') {
            titleEl.textContent = 'Pushing to Feed';
        } else if (stateName === 'success') {
            titleEl.textContent = 'Pushed to Feed';
        } else {
            titleEl.textContent = 'Push to Feed';
        }
    }
}

/** @param {File} file */
function isValidAudioFile(file) {
    const extension = '.' + file.name.split('.').pop().toLowerCase();

    return ALLOWED_EXTENSIONS.includes(extension);
}


async function init() {
    const fragment = window.location.hash.slice(1);
    if (fragment) {
        apiKey = fragment;
        sessionStorage.setItem('featherpod_api_key_' + FEED_ID, apiKey);
        history.replaceState(null, '', window.location.pathname + window.location.search);
    } else {
        const storedKey = sessionStorage.getItem('featherpod_api_key_' + FEED_ID);
        if (storedKey) {
            apiKey = storedKey;
        } else {
            showState('no-key');
            return;
        }
    }

    // Debug: show error state with ?error query param (dev only)
    if (IS_DEV && window.location.search.includes('error')) {
        showError('This is a test error message');

        return;
    }

    // Try to restore previous job state (e.g., after page refresh)
    if (await restoreJobState()) {
        return;
    }

    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
}

document.getElementById('select-file').addEventListener('click', () => {
    document.getElementById('file-input').click();
});

document.getElementById('upload-another').addEventListener('click', async () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

document.getElementById('try-another').addEventListener('click', async () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    await initHistorySection();
    document.getElementById('select-file').focus();
});

document.getElementById('file-input').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

// Drag and drop support
const dropZone = document.getElementById('drop-zone');

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', async (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
    const file = e.dataTransfer.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

/** @param {File} file */
async function uploadFile(file) {
    clearJobState();
    showState('uploading');
    document.getElementById('file-name').textContent = file.name;
    document.getElementById('upload-status').textContent = 'Uploading...';
    const progressBar = document.getElementById('upload-progress');
    const progressContainer = progressBar.parentElement;
    progressContainer.setAttribute('aria-valuenow', '0');
    progressAnimator.startWithAssumption('Uploading', progressBar, file.size);
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    progressAnimator.setTarget(percent, 'Uploading');
                    progressContainer.setAttribute('aria-valuenow', percent.toString());
                }
            });
            xhr.onload = () => {
                progressAnimator.reset();
                resolve({ status: xhr.status, body: xhr.responseText });
            };
            xhr.onerror = () => {
                progressAnimator.reset();
                reject(new Error('Network error'));
            };
            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes?normalize=true&source=Browser');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            const episode = JSON.parse(response.body);
            saveJobState({ status: 'success', fileName: file.name, episode: episode });
            await showSuccess(episode);
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            saveJobState({
                status: 'normalizing',
                jobId: jobResponse.jobId,
                fileName: file.name,
                fileSize: file.size
            });
            monitorNormalizationJob(jobResponse.jobId, file.name, file.size);
        } else if (response.status === 401) {
            showError('Invalid API key');
        } else if (response.status === 403) {
            showError('API key does not have access to this feed');
        } else {
            const error = tryParseJson(response.body);
            showError(error?.error || 'Upload failed');
        }
    } catch (err) {
        showError(err.message || 'Upload failed');
    }
}

Number.prototype.sigDig = function(minSigDigs) {
    if (this.valueOf() === 0) return '0';
    const magnitude = Math.floor(Math.log10(Math.abs(this)));
    const decimals = Math.max(0, minSigDigs - 1 - magnitude);

    return this.toFixed(decimals);
};

Number.prototype.formatBytes = function formatBytes(sigDigs = 2, unitSuffix = '') {
    const value = this.valueOf();
    const absValue = Math.abs(value);
    const sign = value < 0 ? '-' : '';

    if (absValue === 0) {
        return '0 B' + unitSuffix;
    }

    const k = 1024;
    const units = ['B', 'kB', 'MB', 'GB'];
    const i = Math.floor(Math.log(absValue) / Math.log(k));

    if (i >= units.length) {
        return value + ' B' + unitSuffix;
    }

    const number = parseFloat((absValue / Math.pow(k, i)).toFixed(1)).sigDig(sigDigs);

    return sign + number + ' ' + units[i] + unitSuffix;
};

/**
 * Format a duration from TimeSpan string (HH:MM:SS or similar) to human-readable.
 * @param {string} duration - Duration in TimeSpan format
 * @returns {string}
 */
function formatDuration(duration) {
    if (!duration) {
        return '';
    }

    // Parse TimeSpan format: "HH:MM:SS" or "D.HH:MM:SS"
    const parts = duration.split(':');
    if (parts.length < 2) {
        return '';
    }

    const hours = parseInt(parts[0], 10) || 0;
    const minutes = parseInt(parts[1], 10) || 0;
    const seconds = parseInt(parts[2]?.split('.')[0], 10) || 0;

    if (hours > 0) {
        return hours + 'h ' + minutes + 'm';
    } else if (minutes > 0) {
        return minutes + 'm ' + seconds + 's';
    }

    return seconds + 's';
}

/**
 * Display the success state and fetch recent uploads.
 * @param {Episode|string} episodeOrFileName - Episode object or just filename string
 */
async function showSuccess(episodeOrFileName) {
    showState('success');

    // Save to localStorage if we have full episode data
    if (typeof episodeOrFileName === 'object' && episodeOrFileName !== null) {
        saveToLocalHistory(episodeOrFileName);
    }

    // Determine the selected episode ID (if we have full episode data)
    const currentEpisodeId = (typeof episodeOrFileName === 'object' && episodeOrFileName !== null)
        ? episodeOrFileName.id
        : null;

    // Update the info card with the current episode
    updateInfoCard(episodeOrFileName);

    // Fetch and display recent uploads (errors shouldn't break success state)
    try {
        const recentUploads = await fetchRecentUploads();
        renderRecentUploads(recentUploads, currentEpisodeId);
    } catch (err) {
        console.error('Failed to display recent uploads:', err);
    }

    document.getElementById('upload-another').focus();
}

/**
 * Fetch recent browser uploads for this feed.
 * @returns {Promise<Array<{id: string, title: string, fileName: string, fileSize: number, duration: string, uploadedAt: string|null}>>}
 */
async function fetchRecentUploads() {
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?source=Browser&limit=5', {
            headers: { 'X-API-Key': apiKey }
        });

        if (!response.ok) {
            console.warn('Failed to fetch recent uploads:', response.status);

            return [];
        }

        return await response.json();
    } catch (err) {
        console.warn('Error fetching recent uploads:', err);

        return [];
    }
}

/**
 * Fetch the most recent browser upload (used after normalization completes).
 * @returns {Promise<Object|null>}
 */
async function fetchMostRecentEpisode() {
    try {
        const uploads = await fetchRecentUploads();
        if (uploads && uploads.length > 0) {
            return uploads[0];
        }

        return null;
    } catch (err) {
        console.warn('Error fetching most recent episode:', err);

        return null;
    }
}

/**
 * Format a date as "28 nov 2025" (day, short month lowercase, year).
 * @param {string|null} dateString - ISO date string or null
 * @returns {string}
 */
function formatDate(dateString) {
    if (!dateString) {
        return '';
    }

    const date = new Date(dateString);
    const day = date.getDate();
    const months = ['jan', 'feb', 'mar', 'apr', 'maj', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'];
    const month = months[date.getMonth()];
    const year = date.getFullYear();

    return `${day} ${month} ${year}`;
}

/**
 * Format a date as relative time (e.g., "2 minutes ago", "3 hours ago").
 * @param {string|null} dateString - ISO date string or null
 * @returns {string}
 */
function formatRelativeTime(dateString) {
    if (!dateString) {
        return '';
    }

    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;

    // Handle future dates gracefully
    if (diffMs < 0) {
        return 'just now';
    }

    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) {
        return 'just now';
    }

    if (diffMins < 60) {
        return diffMins === 1 ? '1 minute ago' : diffMins + ' minutes ago';
    }

    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) {
        return diffHours === 1 ? '1 hour ago' : diffHours + ' hours ago';
    }

    const diffDays = Math.floor(diffHours / 24);

    return diffDays === 1 ? '1 day ago' : diffDays + ' days ago';
}

/**
 * Update the episode info card with the given episode data.
 * @param {Episode|string} episode - Episode object or just filename string
 */
function updateInfoCard(episode) {
    const infoCard = document.getElementById('episode-info');
    const fallbackFilename = document.getElementById('ep-filename');

    if (episode && typeof episode === 'object') {
        fallbackFilename.style.display = 'none';
        infoCard.style.display = 'grid';

        document.getElementById('info-title').textContent = episode.title || episode.fileName;
        document.getElementById('info-filename').textContent = episode.fileName || '';

        // Combine duration and size: "16m 40s (31 MB)"
        const duration = formatDuration(episode.duration);
        const size = episode.fileSize ? episode.fileSize.formatBytes() : '';
        let durationText = duration;
        if (duration && size) {
            durationText = duration + ' (' + size + ')';
        } else if (size) {
            durationText = size;
        }
        document.getElementById('info-duration').textContent = durationText;

        document.getElementById('info-published').textContent = formatDate(episode.publishedDate);

        // Uploaded time (shown on mobile only via CSS)
        const uploadedTime = formatRelativeTime(episode.uploadedAt);
        document.getElementById('info-uploaded').textContent = uploadedTime;
        document.getElementById('info-uploaded-label').style.display = uploadedTime ? '' : 'none';
        document.getElementById('info-uploaded').style.display = uploadedTime ? '' : 'none';
    } else {
        infoCard.style.display = 'none';
        fallbackFilename.style.display = 'block';
        fallbackFilename.textContent = episode || '';
    }
}

/**
 * Render the recent uploads list in the success state.
 * @param {Array<Object>} uploads - Array of episode objects
 * @param {string|null} [initialSelectedId] - ID of the initially selected episode
 */
function renderRecentUploads(uploads, initialSelectedId = null) {
    const container = document.getElementById('recent-uploads');
    if (!container) {
        return;
    }

    if (!uploads || !Array.isArray(uploads) || uploads.length === 0) {
        container.style.display = 'none';
        recentUploadsData = null;

        return;
    }

    recentUploadsData = uploads;
    selectedUploadId = initialSelectedId;
    container.style.display = 'block';
    const list = container.querySelector('.upload-list');
    list.innerHTML = '';

    uploads.forEach(upload => {
        const item = document.createElement('div');
        item.className = 'upload-item';
        item.dataset.id = upload.id;
        item.tabIndex = 0;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', (upload.id === selectedUploadId).toString());

        if (upload.id === selectedUploadId) {
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

        item.addEventListener('click', () => selectUpload(upload.id));

        list.appendChild(item);
    });
}

/**
 * Select an upload from the recent uploads list and update the info card.
 * @param {string} uploadId - The ID of the upload to select
 */
function selectUpload(uploadId) {
    if (!recentUploadsData) {
        return;
    }

    const upload = recentUploadsData.find(u => u.id === uploadId);
    if (!upload) {
        return;
    }

    selectedUploadId = uploadId;

    // Update visual selection and aria-selected (target success state's list specifically)
    const list = document.querySelector('#recent-uploads .upload-list');
    if (list) {
        list.querySelectorAll('.upload-item').forEach(item => {
            const isSelected = item.dataset.id === uploadId;
            item.classList.toggle('upload-item--selected', isSelected);
            item.setAttribute('aria-selected', isSelected.toString());
        });
    }

    // Update info card
    updateInfoCard(upload);

    // Scroll to top so the info card is visible
    window.scrollTo({ top: 0, behavior: 'smooth' });
}

// ============================================================================
// HISTORY SECTION (Ready State)
// ============================================================================

/**
 * Load upload history from localStorage for this feed.
 * Returns an empty array if no history exists or if parsing fails.
 * @returns {Array<Episode>} Array of episodes uploaded from this browser, most recent first
 */
function loadLocalHistory() {
    try {
        const stored = localStorage.getItem(HISTORY_STORAGE_KEY);
        if (!stored) {
            return [];
        }

        const history = JSON.parse(stored);

        return Array.isArray(history) ? history : [];
    } catch (e) {
        console.warn('Failed to load history from localStorage:', e);

        return [];
    }
}

/**
 * Save an episode to localStorage history.
 * Removes any existing entry with the same ID and prepends the new episode.
 * Trims history to MAX_LOCAL_HISTORY items. Fails silently on localStorage errors.
 * @param {Episode} episode - Episode to save (must have an id property)
 */
function saveToLocalHistory(episode) {
    if (!episode || !episode.id) {
        return;
    }

    try {
        const history = loadLocalHistory();

        // Remove existing entry with same ID (if re-uploading)
        const filtered = history.filter(e => e.id !== episode.id);

        // Prepend new episode and trim to max size
        const updated = [episode, ...filtered].slice(0, MAX_LOCAL_HISTORY);

        localStorage.setItem(HISTORY_STORAGE_KEY, JSON.stringify(updated));
    } catch (e) {
        console.warn('Failed to save to localStorage:', e);
    }
}

/**
 * Load saved filter preference from localStorage.
 * Returns 'local' as the default if no preference is saved or if parsing fails.
 * @returns {'local'|'browser'|'all'} The saved filter preference
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
 * @param {'local'|'browser'|'all'} filter - The filter mode to save
 */
function saveFilterPreference(filter) {
    try {
        localStorage.setItem(HISTORY_FILTER_KEY, filter);
    } catch (e) {
        // Ignore
    }
}

/**
 * Toggle the history section collapsed/expanded state.
 * Updates ARIA attributes, toggles the expanded class on the section,
 * and disables the select-file button when expanded (since it's hidden).
 * When expanding, focuses the selected item in the list for keyboard navigation.
 * When collapsing, returns focus to the toggle button.
 * @param {boolean} [expand] - Force expand (true) or collapse (false). If omitted, toggles.
 */
function toggleHistorySection(expand) {
    const section = document.getElementById('history-section');
    const toggle = document.getElementById('history-toggle');
    const selectFileBtn = document.getElementById('select-file');
    if (!section || !toggle) {
        return;
    }

    const isExpanded = toggle.getAttribute('aria-expanded') === 'true';
    const newState = expand !== undefined ? expand : !isExpanded;

    toggle.setAttribute('aria-expanded', newState.toString());
    toggle.textContent = newState ? '← Back' : 'Recent Uploads';
    section.classList.toggle('history-section--expanded', newState);

    // Disable select-file button when expanded (it's hidden via CSS but could still be activated)
    if (selectFileBtn) {
        selectFileBtn.disabled = newState;
    }

    // Focus management
    if (newState) {
        // Expanding: focus the selected item (or first item) after transition
        requestAnimationFrame(() => {
            const selectedItem = document.querySelector('#history-list .upload-item--selected');
            const firstItem = document.querySelector('#history-list .upload-item');
            const itemToFocus = selectedItem || firstItem;
            if (itemToFocus) {
                itemToFocus.focus();
            }
        });
    } else {
        // Collapsing: reset scroll position and selection to first item
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

        // Return focus to the upload button
        if (selectFileBtn) {
            selectFileBtn.focus();
        }
    }
}

/**
 * Fetch uploads based on current filter mode.
 * - 'local': Returns episodes from localStorage (no network request)
 * - 'browser': Fetches recent browser uploads from API (source=Browser)
 * - 'all': Fetches all recent uploads from API (no source filter)
 * @returns {Promise<Array<Episode>>} Array of episodes matching the current filter
 */
async function fetchHistoryByFilter() {
    if (historyFilter === 'local') {
        return loadLocalHistory();
    } else if (historyFilter === 'browser') {
        return await fetchRecentUploads();
    } else {
        // 'all' - fetch without source filter
        try {
            const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/recent-uploads?limit=20', {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                return [];
            }

            return await response.json();
        } catch (err) {
            console.warn('Error fetching all uploads:', err);

            return [];
        }
    }
}

/**
 * Get the appropriate empty state message for the current filter.
 * @returns {string} A user-friendly message explaining why the list is empty
 */
function getHistoryEmptyMessage() {
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
 * @param {Episode|null} episode - Episode to display, or null to hide the card
 */
function updateHistoryInfoCard(episode) {
    const infoCard = document.getElementById('history-info');
    if (!infoCard) {
        return;
    }

    if (!episode) {
        infoCard.style.display = 'none';

        return;
    }

    infoCard.style.display = 'grid';
    document.getElementById('history-info-title').textContent = episode.title || episode.fileName;
    document.getElementById('history-info-filename').textContent = episode.fileName || '';
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
 * Removes the fade when scrolled to bottom or when list isn't scrollable.
 */
function updateHistoryListScrollState() {
    const list = document.getElementById('history-list');
    if (!list) {
        return;
    }

    const isScrollable = list.scrollHeight > list.clientHeight;
    const isAtBottom = list.scrollTop + list.clientHeight >= list.scrollHeight - 2;

    list.classList.toggle('not-scrollable', !isScrollable);
    list.classList.toggle('scrolled-to-bottom', isScrollable && isAtBottom);
}

/**
 * Render the history list in the ready state.
 * Clears existing list, creates upload items, and selects the first item by default.
 * Shows an empty state message if no uploads are provided.
 * @param {Array<Episode>} uploads - Array of episodes to render
 * @param {boolean} [focusFirst=false] - Whether to focus the first item after rendering
 */
function renderHistoryList(uploads, focusFirst = false) {
    const list = document.getElementById('history-list');
    const emptyState = document.getElementById('history-empty');
    if (!list) {
        return;
    }

    list.innerHTML = '';
    list.classList.remove('scrolled-to-bottom', 'not-scrollable');

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

    // Select first item by default
    historySelectedId = uploads[0].id;
    updateHistoryInfoCard(uploads[0]);

    uploads.forEach((upload, index) => {
        const item = document.createElement('div');
        item.className = 'upload-item';
        item.dataset.id = upload.id;
        item.tabIndex = 0;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', (upload.id === historySelectedId).toString());

        // Staggered animation delay for each item (via CSS custom property)
        item.style.setProperty('--stagger-index', index);

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

        list.appendChild(item);
    });

    // Check scroll state after render (use requestAnimationFrame to ensure layout is complete)
    requestAnimationFrame(() => {
        updateHistoryListScrollState();

        // Focus first item if requested (e.g., after switching tabs via keyboard)
        if (focusFirst) {
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
 * @param {string} uploadId - ID of the episode to select
 * @param {boolean} [moveFocus=false] - Whether to move focus to the selected item
 */
function selectHistoryUpload(uploadId, moveFocus = false) {
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
 * Sets the active class, aria-selected attribute on filter tabs, and updates
 * the tabpanel's aria-labelledby to point to the active tab.
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
 * @param {'local'|'browser'|'all'} filter - The filter mode to switch to
 * @param {boolean} [focusFirst=false] - Whether to focus the first list item after loading
 */
async function changeHistoryFilter(filter, focusFirst = false) {
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

    renderHistoryList(uploads, focusFirst);
}

/**
 * Initialize the history section in the ready state.
 * Loads saved filter preference, fetches uploads, and renders the list.
 * The toggle and section are always shown (even if empty) so users can switch filters.
 */
async function initHistorySection() {
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
    renderHistoryList(uploads || []);
}

/**
 * Safely parse JSON, returning null if parsing fails.
 * @param {string} text
 * @returns {Object|null}
 */
function tryParseJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

/** @param {string} message */
function showError(message) {
    showState('error');
    document.getElementById('error-message').textContent = message;
    document.getElementById('try-another').focus();
}

/**
 * @typedef {Object} JobStatus
 * @property {string} status - Queued, Processing, Completed, Failed
 * @property {string} [stage] - Queued, Preparing, Analyzing, Normalizing, Finishing, Completed, Failed
 * @property {number} [progressPercent] - Progress percentage (0-100) for Analyzing, Normalizing stages
 * @property {string} [error]
 */

/**
 * Progress animator with velocity smoothing and continuous interpolation.
 * Uses requestAnimationFrame for smooth 60fps updates between SSE events.
 */
const progressAnimator = {
    // Default initial velocities in bytes/second (used if no learned value exists)
    DEFAULT_INITIAL_VELOCITIES: {
        'Uploading': 1024 * 1024, // 1 MB/s
        'Analyzing': 100 * 1024,  // 100 kB/s
        'Normalizing': 100 * 1024 // 100 kB/s
    },
    // Max initial velocities for animation (prevents overshoot before real data arrives)
    MAX_INITIAL_VELOCITIES: {
        'Uploading': 10 * 1024 * 1024 // 10 MB/s
    },
    LEARNED_INITIAL_VELOCITY_STORAGE_KEY: 'featherpod_learned_initial_velocity',
    currentFileSize: 0,

    /**
     * Get learned initial velocity for a stage from localStorage.
     * Stored as bytes/second, converted to %/second using current file size.
     * @param {string} stage
     * @returns {{velocity: number, wasClamped: boolean}} Velocity in %/second and whether it was clamped
     */
    getLearnedInitialVelocity(stage) {
        // Check URL overrides first (dev only)
        if (VELOCITY_OVERRIDES[stage] != null) {
            const bytesPerSec = VELOCITY_OVERRIDES[stage];
            if (this.currentFileSize > 0) {
                return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped: false };
            }

            return { velocity: 1, wasClamped: false };
        }

        let bytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

        try {
            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            if (stored) {
                const values = JSON.parse(stored);
                if (values[stage] != null) {
                    bytesPerSec = values[stage];
                }
            }
        } catch (e) {
            // Ignore localStorage errors
        }

        // Cap initial velocity for animation (still learn the real value, just don't overshoot)
        const maxBytesPerSec = this.MAX_INITIAL_VELOCITIES[stage];
        let wasClamped = false;
        if (maxBytesPerSec != null && bytesPerSec > maxBytesPerSec) {
            bytesPerSec = maxBytesPerSec;
            wasClamped = true;
        }

        if (this.currentFileSize > 0) {
            return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped };
        }

        return { velocity: 1, wasClamped: false }; // Fallback if no file size set
    },

    /**
     * Update learned initial velocity for a stage using EMA.
     * Receives velocity in %/second, converts to bytes/second for storage.
     * @param {string} stage
     * @param {number} actualVelocity - The actual velocity in %/second
     * @returns {boolean} True if we learned (velocity was meaningful)
     */
    updateLearnedInitialVelocity(stage, actualVelocity) {
        // Skip learning if no file size or near-zero velocity
        if (this.currentFileSize <= 0 || actualVelocity < 0.1) {
            return false;
        }

        try {
            // Convert %/s to B/s
            const actualBytesPerSec = (actualVelocity / 100) * this.currentFileSize;

            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};
            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;
            const currentBytesPerSec = values[stage] ?? defaultBytesPerSec;

            // Target 80% of actual velocity, asymmetric EMA (fast to decrease, slow to increase)
            const targetBytesPerSec = actualBytesPerSec * 0.8;
            const alpha = targetBytesPerSec < currentBytesPerSec ? 0.8 : 0.2;
            const updatedBytesPerSec = currentBytesPerSec * (1 - alpha) + targetBytesPerSec * alpha;

            values[stage] = updatedBytesPerSec;
            localStorage.setItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY, JSON.stringify(values));

            const current = currentBytesPerSec.formatBytes(2, '/s');
            const updated = updatedBytesPerSec.formatBytes(2, '/s');
            const actual = actualBytesPerSec.formatBytes(2, '/s');
            console.log(`[${stage}] Initial velocity: ${current} -> ${updated} (actual: ${actual})`);

            return true;
        } catch (e) {
            // Ignore localStorage errors
            return false;
        }
    },

    currentValue: 0,
    targetValue: 0,
    velocity: 0,
    acceleration: 0,
    displayVelocity: 0,
    lastUpdateTime: 0,
    lastFrameTime: 0,
    stageStartTime: 0,
    speedFactor: 1,
    animationId: null,
    progressBar: null,
    ghostBar: null,
    currentStage: null,
    awaitingFirstUpdate: false,
    isRestoring: false,

    /**
     * Start animating with learned initial velocity before real data arrives.
     * @param {string} stage - The stage name
     * @param {HTMLElement} progressBar - The progress bar element
     * @param {number} [fileSize] - File size in bytes (sets currentFileSize if provided)
     */
    startWithAssumption(stage, progressBar, fileSize) {
        const wasRestoring = this.isRestoring;
        this.reset();
        if (fileSize != null) {
            this.currentFileSize = fileSize;
        }
        const now = performance.now();
        this.currentStage = stage;
        this.lastUpdateTime = now;
        this.stageStartTime = now;

        if (wasRestoring) {
            this.progressBar = progressBar;
            this.awaitingFirstUpdate = true;
            this.isRestoring = true;
        } else {
            const { velocity: learnedInitialVelocity, wasClamped } = this.getLearnedInitialVelocity(stage);
            this.targetValue = learnedInitialVelocity;
            this.velocity = learnedInitialVelocity;
            this.displayVelocity = learnedInitialVelocity;
            this.awaitingFirstUpdate = true;
            const bytesPerSec = (learnedInitialVelocity / 100) * this.currentFileSize;
            const clampedSuffix = wasClamped ? ' (clamped)' : '';
            console.log(`[${stage}] Initial velocity: ${bytesPerSec.formatBytes(2, '/s')}${clampedSuffix}`);
            this.start(progressBar);
        }
    },

    /**
     * Set new target from SSE update.
     * @param {number} value - New progress target (0-100)
     * @param {string} stage - Current stage name
     */
    setTarget(value, stage) {
        // Initial calibration phase
        if (this.awaitingFirstUpdate && stage === this.currentStage) {
            const now = performance.now();
            const dt = (now - this.lastUpdateTime) / 1000;

            if (this.isRestoring) {
                const { velocity: learnedVelocity } = this.getLearnedInitialVelocity(stage);
                this.currentValue = value;
                this.targetValue = value;
                this.velocity = learnedVelocity;
                this.displayVelocity = learnedVelocity;
                this.lastUpdateTime = now;
                this.awaitingFirstUpdate = false;
                this.isRestoring = false;
                if (this.progressBar) {
                    this.progressBar.style.width = value + '%';
                    this.start(this.progressBar);
                }

                return;
            }

            // Recalibrate velocity from real data
            if (dt > 0.05) {
                const totalElapsed = (now - this.stageStartTime) / 1000;
                const realVelocity = totalElapsed > 0 ? value / totalElapsed : this.velocity;
                this.velocity = realVelocity;
                this.displayVelocity = realVelocity;
                this.targetValue = value;
                this.acceleration = 0;
                this.lastUpdateTime = now;
            } else {
                this.targetValue = value;
            }

            // Learn velocity for future assumptions
            const totalElapsed = (now - this.stageStartTime) / 1000;
            const learningVelocity = totalElapsed > 0.05 ? value / totalElapsed : 0;
            if (this.updateLearnedInitialVelocity(stage, learningVelocity)) {
                this.awaitingFirstUpdate = false;
            }

            return;
        }

        // Normal updates (after calibration)
        const now = performance.now();
        const dt = (now - this.lastUpdateTime) / 1000;

        if (dt > 0 && dt < 5) {
            const instantVelocity = (value - this.targetValue) / dt;
            const prevVelocity = this.velocity;
            this.velocity = this.velocity * 0.5 + instantVelocity * 0.5;

            const instantAcceleration = (this.velocity - prevVelocity) / dt;
            this.acceleration = this.acceleration * 0.5 + instantAcceleration * 0.5;

            if (SHOW_GHOST) {
                const bytesPerSec = (instantVelocity / 100) * this.currentFileSize;
                const deltaBytesPerSec = ((this.displayVelocity - instantVelocity) / 100) * this.currentFileSize;
                const deltaSign = deltaBytesPerSec >= 0 ? '+' : '';
                console.log(`[${stage}] Instant velocity: ${bytesPerSec.formatBytes(2, '/s')} (${deltaSign}${deltaBytesPerSec.formatBytes(2, '/s')})`);
            }
        }

        this.targetValue = value;
        this.lastUpdateTime = now;

        // Update ghost bar immediately (it shows unfiltered value)
        if (this.ghostBar) {
            this.ghostBar.style.width = value + '%';
        }
    },

    /**
     * Start the animation loop.
     * @param {HTMLElement} progressBar - The progress bar element
     */
    start(progressBar) {
        this.progressBar = progressBar;
        // Set up ghost bar if enabled
        if (SHOW_GHOST && progressBar) {
            const ghostId = progressBar.id + '-ghost';
            this.ghostBar = document.getElementById(ghostId);
            if (this.ghostBar) {
                this.ghostBar.parentElement.classList.add('visible');
            }
        }
        if (!this.animationId) {
            this.lastFrameTime = performance.now();
            this.animate();
        }
    },

    stop() {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    },

    reset() {
        this.stop();
        this.currentValue = 0;
        this.targetValue = 0;
        this.velocity = 0;
        this.acceleration = 0;
        this.displayVelocity = 0;
        this.lastUpdateTime = 0;
        this.stageStartTime = 0;
        this.speedFactor = 1;
        this.currentStage = null;
        this.awaitingFirstUpdate = false;
        if (this.ghostBar) {
            this.ghostBar.parentElement.classList.remove('visible');
            this.ghostBar = null;
        }
        // isRestoring survives reset() - cleared in setTarget() after use
    },

    /** Mark that we're restoring after page refresh (snap to actual value). */
    setRestoring() {
        this.isRestoring = true;
    },

    animate() {
        const now = performance.now();
        const rawDt = (now - this.lastFrameTime) / 1000;
        this.lastFrameTime = now;
        const dt = Math.min(rawDt, 0.1);

        const timeSinceUpdate = (now - this.lastUpdateTime) / 1000;
        const estimatedActual = this.targetValue + this.velocity * timeSinceUpdate;

        // Snap to estimated position if tab was inactive (capped at target to avoid overshooting)
        if (rawDt > 1) {
            this.currentValue = Math.max(this.currentValue, Math.min(estimatedActual, this.targetValue));
            this.displayVelocity = this.velocity;
            this.speedFactor = 1;
        }

        // Ease displayVelocity towards actual velocity
        const velocityEaseRate = 3;
        this.displayVelocity += (this.velocity - this.displayVelocity) * Math.min(1, velocityEaseRate * dt);

        // Predict lag from displayVelocity catching up (integral of velocity gap)
        const velocityGap = this.velocity - this.displayVelocity;
        const baseProjectedLag = velocityGap / velocityEaseRate;

        // Adjust lag prediction based on acceleration
        const accelAdjustment = Math.max(-0.3, Math.min(0.3, this.acceleration * 0.05));
        const projectedLag = baseProjectedLag * (1 - accelAdjustment);

        // Target position with lag compensation
        const compensatedTarget = estimatedActual + projectedLag;
        const error = compensatedTarget - this.currentValue;

        // Ease speed factor based on position error
        // Increase clamp limits and easing rate as target approaches 100% for faster convergence
        const progressFactor = Math.max(0, (this.targetValue - 67) / 33); // 0 at ≤67%, 1 at 100%
        const maxSpeedAdjust = 0.3 + progressFactor * 0.7; // 0.3 normally, up to 1.0 at 100%
        const targetSpeedFactor = 1 + Math.max(-maxSpeedAdjust, Math.min(maxSpeedAdjust, error * 0.3));
        const easingRate = 3 + progressFactor * 3; // 3 normally, up to 6 at 100%
        this.speedFactor += (targetSpeedFactor - this.speedFactor) * Math.min(1, easingRate * dt);

        this.currentValue += this.displayVelocity * dt * this.speedFactor;
        this.currentValue = Math.max(0, Math.min(100, this.currentValue));

        if (this.progressBar) {
            this.progressBar.style.width = this.currentValue + '%';
        }
        if (this.ghostBar) {
            this.ghostBar.style.width = this.targetValue + '%';
        }

        if (this.currentValue < 99.9) {
            this.animationId = requestAnimationFrame(() => this.animate());
        } else {
            this.animationId = null;
        }
    }
};

/**
 * Update the normalizing status display and progress bar.
 * @param {JobStatus} job
 */
function updateNormalizingStatus(job) {
    const statusEl = document.getElementById('normalizing-status');
    const progressBar = document.getElementById('normalizing-progress');
    const progressContainer = progressBar.parentElement;

    if (job.stage) {
        const stagesWithProgress = ['Analyzing', 'Normalizing'];
        const isProgressStage = stagesWithProgress.includes(job.stage);

        const ellipsis = job.stage.endsWith('ing') ? '...' : '';
        statusEl.textContent = job.stage + ellipsis;

        if (isProgressStage) {
            progressBar.classList.remove('indeterminate');

            if (progressAnimator.currentStage !== job.stage) {
                progressAnimator.startWithAssumption(job.stage, progressBar);
            }

            if (job.progressPercent != null) {
                progressAnimator.setTarget(job.progressPercent, job.stage);
                progressContainer.setAttribute('aria-valuenow', job.progressPercent.toString());
            }

            progressAnimator.start(progressBar);
        } else {
            progressAnimator.reset();
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
            progressContainer.setAttribute('aria-valuenow', '0');

            // Show ghost bar at 100% during indeterminate stages to prevent layout jump
            if (SHOW_GHOST) {
                const ghostBar = document.getElementById('normalizing-progress-ghost');
                if (ghostBar) {
                    ghostBar.style.width = '100%';
                    ghostBar.parentElement.classList.add('visible');
                }
            }
        }
    }
}

/**
 * Monitor normalization job via SSE with polling fallback.
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
function monitorNormalizationJob(jobId, fileName, fileSize) {
    showState('normalizing');
    document.getElementById('normalizing-file-name').textContent = fileName;
    document.getElementById('normalizing-status').textContent = 'Initializing...';

    const progressBar = document.getElementById('normalizing-progress');
    progressBar.classList.add('indeterminate');
    progressBar.style.width = '';
    progressAnimator.reset();
    progressAnimator.currentFileSize = fileSize;

    const sseUrl = '/api/jobs/' + jobId + '/progress';

    if (typeof EventSource === 'undefined') {
        void pollNormalizationJobFallback(jobId, fileName, fileSize);
        return;
    }

    const eventSource = new EventSource(sseUrl);
    let lastStatus = null;
    let connectionEstablished = false;
    let jobFinished = false;

    const connectionTimeout = setTimeout(() => {
        if (!connectionEstablished) {
            eventSource.close();
            void pollNormalizationJobFallback(jobId, fileName, fileSize);
        }
    }, 5000);

    eventSource.onopen = () => {
        connectionEstablished = true;
        clearTimeout(connectionTimeout);
    };

    eventSource.addEventListener('progress', (e) => {
        const parsed = tryParseJson(e.data);
        if (parsed) {
            lastStatus = parsed;
            updateNormalizingStatus(lastStatus);
        }
    });

    eventSource.addEventListener('done', async () => {
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        if (lastStatus?.status === 'Completed') {
            // Fetch the episode details for the info card
            const episode = await fetchMostRecentEpisode();
            saveJobState({ status: 'success', fileName, episode: episode });
            await showSuccess(episode || fileName);
        } else {
            const errorMsg = lastStatus?.error || 'Normalization failed';
            saveJobState({ status: 'error', fileName, error: errorMsg });
            showError(errorMsg);
        }
    });

    // Named 'error' event from server (e.g., job not found)
    // Only handle if it has data (server-sent), otherwise let onerror handle it
    eventSource.addEventListener('error', (e) => {
        if (!e.data) {
            // This is a connection error, not a server-sent error event
            // Let onerror handle it (falls back to polling)
            return;
        }
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        const data = tryParseJson(e.data);
        const errorMsg = data?.error || 'An error occurred';
        saveJobState({ status: 'error', fileName, error: errorMsg });
        showError(errorMsg);
    });

    // Connection error (network failure or unexpected disconnect) - fall back to polling
    eventSource.onerror = () => {
        if (jobFinished) {
            return;
        }
        clearTimeout(connectionTimeout);
        eventSource.close();
        void pollNormalizationJobFallback(jobId, fileName, fileSize);
    };
}

/**
 * Poll normalization job status (fallback when SSE unavailable).
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
async function pollNormalizationJobFallback(jobId, fileName, fileSize) {
    showState('normalizing');
    document.getElementById('normalizing-file-name').textContent = fileName;
    progressAnimator.currentFileSize = fileSize;

    const pollInterval = 2000;

    while (true) {
        try {
            const response = await fetch('/api/jobs/' + jobId, {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
                showError('Failed to check job status');

                return;
            }

            const job = await response.json();

            if (job.status === 'Completed') {
                const episode = await fetchMostRecentEpisode();
                saveJobState({ status: 'success', fileName, episode: episode });
                await showSuccess(episode || fileName);

                return;
            } else if (job.status === 'Failed') {
                const errorMsg = job.error || 'Normalization failed';
                saveJobState({ status: 'error', fileName, error: errorMsg });
                showError(errorMsg);

                return;
            }

            updateNormalizingStatus(job);

            await new Promise(resolve => setTimeout(resolve, pollInterval));
        } catch (err) {
            saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
            showError('Failed to check job status');

            return;
        }
    }
}

/** @param {object} state */
function saveJobState(state) {
    sessionStorage.setItem(JOB_STORAGE_KEY, JSON.stringify(state));
}

function clearJobState() {
    sessionStorage.removeItem(JOB_STORAGE_KEY);
}

async function restoreJobState() {
    const saved = sessionStorage.getItem(JOB_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const job = tryParseJson(saved);
    if (!job) {
        clearJobState();

        return false;
    }

    if (job.status === 'success') {
        await showSuccess(job.episode || job.fileName);

        return true;
    } else if (job.status === 'error') {
        showError(job.error);
        return true;
    } else if (job.status === 'normalizing') {
        progressAnimator.setRestoring();
        monitorNormalizationJob(job.jobId, job.fileName, job.fileSize || 0);
        document.getElementById('normalizing-progress').style.width = '0%';
        return true;
    }

    return false;
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && progressAnimator.currentStage) {
        // Tab became visible while progress is being tracked - snap to actual value on next SSE update
        progressAnimator.awaitingFirstUpdate = true;
        progressAnimator.isRestoring = true;
    }
});

// History section event listeners
// Toggle button - toggles between expanded/collapsed
document.getElementById('history-toggle')?.addEventListener('click', () => {
    toggleHistorySection();
});

document.querySelectorAll('#history-section .filter-tab').forEach(tab => {
    tab.addEventListener('click', () => {
        const filter = tab.dataset.filter;
        if (filter) {
            changeHistoryFilter(filter);
        }
    });
});

// Scroll event for fade mask
document.getElementById('history-list')?.addEventListener('scroll', updateHistoryListScrollState);

// Global keyboard shortcuts for history panel
document.addEventListener('keydown', (e) => {
    const section = document.getElementById('history-section');
    if (!section?.classList.contains('history-section--expanded')) {
        return;
    }

    // Escape to close
    if (e.key === 'Escape') {
        e.preventDefault();
        toggleHistorySection(false);

        return;
    }

    // Left/Right arrows or Q/E to switch filter tabs
    if (e.key === 'ArrowLeft' || e.key === 'q' || e.key === 'Q' ||
        e.key === 'ArrowRight' || e.key === 'e' || e.key === 'E') {
        const filters = ['local', 'browser', 'all'];
        const currentIndex = filters.indexOf(historyFilter);
        let newIndex = currentIndex;

        if (e.key === 'ArrowLeft' || e.key === 'q' || e.key === 'Q') {
            newIndex = Math.max(currentIndex - 1, 0);
        } else {
            newIndex = Math.min(currentIndex + 1, filters.length - 1);
        }

        if (newIndex !== currentIndex) {
            e.preventDefault();
            changeHistoryFilter(filters[newIndex], true);
        }

        return;
    }

    // Up/Down arrows to navigate history list
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (!historyData || historyData.length === 0) {
            return;
        }

        const currentIndex = historyData.findIndex(u => u.id === historySelectedId);
        let newIndex = currentIndex;

        if (e.key === 'ArrowDown') {
            newIndex = Math.min(currentIndex + 1, historyData.length - 1);
        } else {
            newIndex = Math.max(currentIndex - 1, 0);
        }

        if (newIndex !== currentIndex) {
            e.preventDefault();
            selectHistoryUpload(historyData[newIndex].id, true);
        }
    }
});

// Global keyboard shortcuts for success state
document.addEventListener('keydown', (e) => {
    const successState = document.getElementById('success');
    if (!successState || successState.style.display === 'none') {
        return;
    }

    if (!recentUploadsData || recentUploadsData.length === 0) {
        return;
    }

    const currentIndex = recentUploadsData.findIndex(u => u.id === selectedUploadId);
    let newIndex = currentIndex;

    if (e.key === 'ArrowDown') {
        e.preventDefault();
        newIndex = Math.min(currentIndex + 1, recentUploadsData.length - 1);
    } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        newIndex = Math.max(currentIndex - 1, 0);
    }

    if (newIndex !== currentIndex) {
        selectUpload(recentUploadsData[newIndex].id);
    }
});
