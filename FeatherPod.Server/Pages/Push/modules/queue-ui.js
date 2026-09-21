import { STAGES_WITH_PROGRESS, TRANSCRIPTION_ACTIVE_STATUSES, COLLAPSED_HEIGHT_DEFAULT } from './config.js';
import { isInUploadPhase } from './utils.js';
import { progressAnimator } from './progress.js';
import { getCollapsedHeight, COLLAPSED_WIDTH } from './state.js';

const Q_MORPH_DURATION = 400;

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

/**
 * Animate the queue drop zone morphing from the ready-state drop zone dimensions.
 * Mirrors the history section morph pattern: set explicit start -> reflow -> transition to target.
 */
export function animateQueueDropZoneMorph() {
    const queueDZ = document.getElementById('queue-drop-zone');
    if (!queueDZ) {
        return;
    }

    const targetHeight = queueDZ.getBoundingClientRect().height;

    queueDZ.classList.add('queue-drop-zone--morphing');
    queueDZ.style.height = getCollapsedHeight() + 'px';

    void queueDZ.offsetHeight;
    queueDZ.style.height = targetHeight + 'px';

    setTimeout(() => {
        queueDZ.classList.remove('queue-drop-zone--morphing');
        queueDZ.style.height = '';
    }, Q_MORPH_DURATION);
}

/**
 * Prepare the ready-state drop zone for a morph animation before it becomes visible.
 * Sets the morphing class and start height while #drop-zone is still hidden (display: none),
 * so blur-fade-in is suppressed when showState('ready') makes it visible.
 * @param {number} startHeight - The height to start from (queue drop zone height).
 */
export function prepareReadyDropZoneMorph(startHeight) {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {
        return;
    }

    dropZone.classList.add('drop-zone--morphing');
    dropZone.style.height = startHeight + 'px';
}

/**
 * Run the ready-state drop zone morph transition. Must be called after showState('ready')
 * and prepareReadyDropZoneMorph() so the element is visible with its start height committed.
 */
export function animateReadyDropZoneMorph() {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {
        return;
    }

    const targetHeight = dropZone.classList.contains('drop-zone--has-artwork')
        ? COLLAPSED_WIDTH
        : COLLAPSED_HEIGHT_DEFAULT;

    void dropZone.offsetHeight;

    dropZone.style.height = targetHeight + 'px';

    setTimeout(() => {
        dropZone.style.animation = 'none';
        dropZone.querySelector('.btn-primary')?.style.setProperty('animation', 'none');
        dropZone.querySelector('.hint')?.style.setProperty('animation', 'none');
        dropZone.classList.remove('drop-zone--morphing');
        dropZone.style.height = '';
    }, Q_MORPH_DURATION);
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
    name.textContent = displayName;
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
        const label = getStatusText(entry);
        status.textContent = label ? '\u26A0 ' + label : '\u26A0';
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

    if (isInUploadPhase(entry)) {
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
        case 'saving':
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
        case 'saving':
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
        case 'saving':
            return 'Saving';
        case 'normalizing': {
            const transcriptionActive = TRANSCRIPTION_ACTIVE_STATUSES.has(entry.transcriptionStatus);
            if (transcriptionActive && (entry.normalizationComplete || entry.stage === 'Finishing')) {
                return 'Transcribing';
            }

            return entry.stage || 'Queued';
        }
        case 'completed':
            // No label: the left tick already signals success.
            return '';
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

    if (isInUploadPhase(entry) || entry.status === 'normalizing') {
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

/**
 * Paint an entry's current title without rebuilding its row or progress bar.
 * The queue owns title mutations; keeping the existing progress element preserves
 * the animator slot during both job updates and history renames.
 * @param {import('../push.js').QueueEntry} entry
 */
export function updateQueueItemName(entry) {
    const nameEl = document.querySelector('#queue-item-' + entry.id + ' .queue-item-name');
    if (nameEl) {
        nameEl.textContent = entry.title || entry.fileName;
        nameEl.title = entry.fileName;
    }
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
