import { STAGES_WITH_PROGRESS, TRANSCRIPTION_ACTIVE_STATUSES } from './config.js';
import { progressAnimator } from './progress.js';
import { truncate } from './utils.js';

// These callbacks are set by the orchestrator to avoid circular imports
let onRemoveFromQueue = null;
let onCancelEntry = null;
let onRetryEntry = null;
let onDismissEntry = null;
let getUploadQueue = null;

/**
 * Register callbacks from the orchestrator/queue module.
 * Called once during init to wire up action button handlers.
 */
export function registerQueueCallbacks({ removeFromQueue, cancelEntry, retryEntry, dismissEntry, getQueue }) {
    onRemoveFromQueue = removeFromQueue;
    onCancelEntry = cancelEntry;
    onRetryEntry = retryEntry;
    onDismissEntry = dismissEntry;
    getUploadQueue = getQueue;
}

export function renderQueueList(animateNew) {
    const container = document.getElementById('queue-list');
    if (!container) {
        return;
    }
    const existingIds = new Set(Array.from(container.children).map(el => el.id));
    container.innerHTML = '';

    const queue = getUploadQueue();
    for (const entry of queue) {
        const el = createQueueItemElement(entry);
        if (animateNew && !existingIds.has('queue-item-' + entry.id)) {
            el.style.animation = 'blur-fade-in 0.3s ease both';
        }
        container.prepend(el);
    }
}

export function createQueueItemElement(entry) {
    const item = document.createElement('div');
    item.className = 'queue-item queue-item--' + entry.status;
    item.id = 'queue-item-' + entry.id;

    const icon = document.createElement('span');
    icon.className = 'queue-item-icon ' + getIconClass(entry);
    icon.textContent = getIconText(entry);
    item.appendChild(icon);

    const name = document.createElement('span');
    name.className = 'queue-item-name';
    const displayName = entry.title || entry.fileName;
    name.textContent = truncate(displayName, 50);
    name.title = entry.title ? entry.fileName : displayName;
    item.appendChild(name);

    item.appendChild(createStatusElement(entry));
    item.appendChild(createProgressBar(entry));

    const actionBtn = createActionButton(entry);
    if (actionBtn) {
        item.appendChild(actionBtn);
    }

    return item;
}

function createStatusElement(entry) {
    const status = document.createElement('span');
    status.className = 'queue-item-status';
    status.id = 'queue-status-' + entry.id;
    if (entry.status === 'completed' && entry.transcriptionStatus === 'Failed') {
        status.classList.add('queue-item-status--trans-failed');
        status.textContent = '\u26A0 ' + getStatusText(entry);
        status.title = 'Transcript unavailable: ' + (entry.transcriptionError || 'transcription failed');
    } else {
        status.textContent = getStatusText(entry);
    }

    return status;
}

function createProgressBar(entry) {
    const progressWrap = document.createElement('div');
    progressWrap.className = 'queue-item-progress-wrap';
    const progressBar = document.createElement('div');
    progressBar.className = 'queue-item-progress';
    progressBar.id = 'queue-progress-' + entry.id;

    if (entry.status === 'uploading') {
        progressBar.style.width = entry.progress + '%';
    } else if (entry.status === 'normalizing') {
        const transcriptionActive = TRANSCRIPTION_ACTIVE_STATUSES.has(entry.transcriptionStatus);
        if (transcriptionActive && (entry.normalizationComplete || entry.stage === 'Finishing')) {
            progressBar.classList.add('indeterminate');
        } else if (entry.stage && !STAGES_WITH_PROGRESS.includes(entry.stage)) {
            progressBar.classList.add('indeterminate');
        } else {
            progressBar.style.width = entry.progress + '%';
        }
    }

    progressWrap.appendChild(progressBar);

    return progressWrap;
}

function getIconClass(entry) {
    switch (entry.status) {
        case 'uploading':
        case 'normalizing':
            return 'queue-item-icon--active';
        case 'completed':
            return 'queue-item-icon--done';
        case 'failed':
            return 'queue-item-icon--failed';
        case 'cancelled':
            return 'queue-item-icon--cancelled';
        default:
            return 'queue-item-icon--queued';
    }
}

function getIconText(entry) {
    switch (entry.status) {
        case 'uploading':
        case 'normalizing':
            return '\u25CF';
        case 'completed':
            return '\u2713';
        case 'failed':
            return '\u2717';
        case 'cancelled':
            // Intentionally shares the queued glyph: both are "nothing to show".
            // The distinct icon class (queue-item-icon--cancelled) carries the visual difference.
            return '\u2013';
        default:
            return '\u2013';
    }
}

function getStatusText(entry) {
    switch (entry.status) {
        case 'uploading':
            return 'Uploading';
        case 'normalizing': {
            const transcriptionActive = TRANSCRIPTION_ACTIVE_STATUSES.has(entry.transcriptionStatus);
            if (transcriptionActive && (entry.normalizationComplete || entry.stage === 'Finishing')) {
                return 'Transcribing';
            }

            return entry.stage || 'Queued';
        }
        case 'completed':
            return 'Done';
        case 'failed':
            return entry.error || 'Failed';
        case 'cancelled':
            return 'Cancelled';
        default:
            return 'Waiting';
    }
}

function createActionButton(entry) {
    if (entry.status === 'queued') {
        const btn = document.createElement('button');
        btn.className = 'queue-item-action queue-item-action--cancel';
        btn.type = 'button';
        btn.title = 'Remove from queue';
        btn.textContent = '\u00D7';
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            onRemoveFromQueue(entry.id);
        });

        return btn;
    }

    if (entry.status === 'uploading' || entry.status === 'normalizing') {
        const btn = document.createElement('button');
        btn.className = 'queue-item-action queue-item-action--cancel';
        btn.type = 'button';
        btn.title = 'Cancel';
        btn.textContent = '\u00D7';
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            void onCancelEntry(entry.id);
        });

        return btn;
    }

    if (entry.status === 'failed') {
        const dismissBtn = createDismissButton(entry);

        if (!entry.validationError && entry.file) {
            const wrapper = document.createElement('span');
            wrapper.className = 'queue-item-actions';

            const retryBtn = document.createElement('button');
            retryBtn.className = 'queue-item-action queue-item-action--retry';
            retryBtn.type = 'button';
            retryBtn.textContent = 'Retry';
            retryBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                onRetryEntry(entry.id);
            });
            wrapper.appendChild(retryBtn);
            wrapper.appendChild(dismissBtn);

            return wrapper;
        }

        return dismissBtn;
    }

    if (entry.status === 'completed') {
        return createDismissButton(entry);
    }

    return null;
}

function createDismissButton(entry) {
    const btn = document.createElement('button');
    btn.className = 'queue-item-action queue-item-action--cancel';
    btn.type = 'button';
    btn.title = 'Dismiss';
    btn.textContent = '\u00D7';
    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        onDismissEntry(entry.id);
    });

    return btn;
}

export function updateQueueItemInDOM(entry) {
    const existingEl = document.getElementById('queue-item-' + entry.id);
    if (!existingEl) {
        return;
    }
    existingEl.replaceWith(createQueueItemElement(entry));
    progressAnimator.rebindProgressBar(entry.id, getEntryProgressBar(entry.id));
}

export function updateQueueItemProgress(entry) {
    const statusEl = document.getElementById('queue-status-' + entry.id);
    if (statusEl) {
        statusEl.textContent = getStatusText(entry);
    }
}

export function removeQueueItemFromDOM(entryId) {
    const el = document.getElementById('queue-item-' + entryId);
    if (el) {
        el.remove();
    }
}

export function getEntryProgressBar(entryId) {
    return document.getElementById('queue-progress-' + entryId);
}

export function rebindProgressAnimator() {
    progressAnimator.rebindAllProgressBars(getEntryProgressBar);
}
