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

    const status = document.createElement('span');
    status.className = 'queue-item-status';
    status.id = 'queue-status-' + entry.id;
    status.textContent = getStatusText(entry);
    item.appendChild(status);

    const progressWrap = document.createElement('div');
    progressWrap.className = 'queue-item-progress-wrap';
    const progressBar = document.createElement('div');
    progressBar.className = 'queue-item-progress';
    progressBar.id = 'queue-progress-' + entry.id;

    if (entry.status === 'uploading') {
        progressBar.style.width = entry.progress + '%';
    } else if (entry.status === 'normalizing') {
        if (entry.stage && !['Analyzing', 'Normalizing', 'Downloading'].includes(entry.stage)) {
            progressBar.classList.add('indeterminate');
        } else {
            progressBar.style.width = entry.progress + '%';
        }
    }

    progressWrap.appendChild(progressBar);
    item.appendChild(progressWrap);

    // Transcription progress bar (hidden until transcription starts)
    const transWrap = document.createElement('div');
    transWrap.className = 'queue-item-progress-wrap queue-item-progress-wrap--trans';
    transWrap.id = 'queue-trans-wrap-' + entry.id;
    const transBar = document.createElement('div');
    transBar.className = 'queue-item-progress';
    transBar.id = 'queue-progress-trans-' + entry.id;

    if (entry.transcriptionStatus === 'Running' && entry.transcriptionProgress != null) {
        transWrap.classList.add('active');
        transBar.style.width = entry.transcriptionProgress + '%';
    } else if (entry.transcriptionStatus === 'Failed') {
        transWrap.classList.add('active', 'trans-failed');
    } else if (entry.transcriptionStatus === 'Completed') {
        transWrap.classList.add('active');
        transBar.style.width = '100%';
    }

    transWrap.appendChild(transBar);
    item.appendChild(transWrap);

    const actionBtn = createActionButton(entry);
    if (actionBtn) {
        item.appendChild(actionBtn);
    }

    return item;
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
        default:
            return '\u2013';
    }
}

function getStatusText(entry) {
    switch (entry.status) {
        case 'uploading':
            return 'Uploading';
        case 'normalizing':
            return entry.stage || 'Queued';
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
        const dismissBtn = document.createElement('button');
        dismissBtn.className = 'queue-item-action queue-item-action--cancel';
        dismissBtn.type = 'button';
        dismissBtn.title = 'Dismiss';
        dismissBtn.textContent = '\u00D7';
        dismissBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            onDismissEntry(entry.id);
        });

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

    return null;
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

export function getTranscriptionProgressBar(entryId) {
    return document.getElementById('queue-progress-trans-' + entryId);
}

/**
 * Show the transcription progress bar for an entry.
 * @param {string} entryId
 */
export function showTranscriptionBar(entryId) {
    const wrap = document.getElementById('queue-trans-wrap-' + entryId);
    if (wrap) {
        wrap.classList.add('active');
        wrap.classList.remove('trans-failed');
    }
}

/**
 * Mark the transcription bar as failed.
 * @param {string} entryId
 */
export function setTranscriptionBarFailed(entryId) {
    const wrap = document.getElementById('queue-trans-wrap-' + entryId);
    if (wrap) {
        wrap.classList.add('active', 'trans-failed');
    }
}

export function rebindProgressAnimator() {
    progressAnimator.rebindAllProgressBars(getEntryProgressBar);
}
