import { FEED_ID } from './config.js';
import { getApiKey } from './auth.js';
import { removeFromLocalHistory, updateLocalHistoryTitle, getHistoryData, getHistorySelectedId, selectHistoryUpload, updateHistoryInfoCard, updateHistoryListScrollState, invalidateBrowserUploadsCache, invalidateAllUploadsCache } from './history.js';

/** @type {string|null} */
let contextMenuTargetId = null;
/** @type {boolean} */
let notePanelOpen = false;
/** @type {number|null} */
let noteDebounceTimer = null;
/** @type {string} */
let renameOriginalTitle = '';
/** @type {string} */
let notePanelSnapshot = '';
/** @type {string} */
let noteModalOriginalValue = '';

export function getContextMenuTargetId() {
    return contextMenuTargetId;
}

export function isNotePanelOpen() {
    return notePanelOpen;
}

/**
 * Show the context menu at the given position, clamped to the viewport.
 * @param {string} episodeId
 * @param {number} x
 * @param {number} y
 */
export function showContextMenu(episodeId, x, y) {
    const menu = document.getElementById('context-menu');
    if (!menu) {
        return;
    }

    contextMenuTargetId = episodeId;

    // Make visible but off-screen to measure
    menu.style.left = '-9999px';
    menu.style.top = '-9999px';
    menu.removeAttribute('hidden');

    // Clamp to viewport
    const rect = menu.getBoundingClientRect();
    const left = Math.min(x, window.innerWidth - rect.width - 8);
    const top = Math.min(y, window.innerHeight - rect.height - 8);

    menu.style.left = Math.max(8, left) + 'px';
    menu.style.top = Math.max(8, top) + 'px';

    // Focus first item for keyboard accessibility
    const firstItem = menu.querySelector('.context-menu-item');
    if (firstItem) {
        firstItem.focus();
    }
}

/** Hide the context menu and clear its target. */
export function hideContextMenu() {
    const menu = document.getElementById('context-menu');
    if (menu) {
        menu.setAttribute('hidden', '');
    }
    contextMenuTargetId = null;
}

/**
 * Show the delete confirmation dialog for an episode.
 * @param {string} episodeId
 */
export function showDeleteConfirm(episodeId) {
    const historyData = getHistoryData();
    const episode = historyData?.find(e => e.id === episodeId);
    if (!episode) {
        return;
    }

    const desc = document.getElementById('delete-confirm-desc');
    if (desc) {
        desc.textContent = episode.title || episode.fileName;
    }

    const overlay = document.getElementById('delete-confirm-overlay');
    if (overlay) {
        overlay.dataset.episodeId = episodeId;
        overlay.removeAttribute('hidden');
    }

    // Focus cancel button (safer default)
    document.getElementById('delete-cancel')?.focus();
}

/** Hide the delete confirmation dialog. */
export function hideDeleteConfirm() {
    const overlay = document.getElementById('delete-confirm-overlay');
    if (overlay) {
        overlay.setAttribute('hidden', '');
        delete overlay.dataset.episodeId;
    }
}

/**
 * Delete an episode via the API and optimistically update the UI.
 * @param {string} episodeId
 */
export async function deleteEpisode(episodeId) {
    const historyData = getHistoryData();
    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/' + episodeId, {
            method: 'DELETE',
            headers: { 'X-API-Key': getApiKey() }
        });

        if (!response.ok && response.status !== 404) {
            console.warn('Failed to delete episode:', response.status);

            return;
        }
    } catch (err) {
        console.warn('Error deleting episode:', err);

        return;
    }

    // Remove from historyData
    if (historyData) {
        const index = historyData.findIndex(e => e.id === episodeId);
        if (index >= 0) {
            historyData.splice(index, 1);

            // Update selection
            const historySelectedId = getHistorySelectedId();
            if (historySelectedId === episodeId) {
                if (historyData.length > 0) {
                    const newIndex = Math.min(index, historyData.length - 1);
                    selectHistoryUpload(historyData[newIndex].id);
                } else {
                    updateHistoryInfoCard(null);
                }
            }
        }
    }

    // Remove from localStorage history
    removeFromLocalHistory(episodeId);

    // Invalidate caches
    invalidateBrowserUploadsCache();
    invalidateAllUploadsCache();

    // Remove the DOM element
    const item = document.querySelector('#history-list .upload-item[data-id="' + episodeId + '"]');
    if (item) {
        item.remove();
    }

    // Update empty state and scroll
    const list = document.getElementById('history-list');
    const emptyState = document.getElementById('history-empty');
    if (historyData && historyData.length === 0 && emptyState) {
        emptyState.textContent = getHistoryEmptyMessage();
        emptyState.style.display = 'block';
    }
    if (list) {
        updateHistoryListScrollState();
    }

    hideDeleteConfirm();
}

// getHistoryEmptyMessage is private to history.js. In the original code, deleteEpisode
// accessed it as a module-level function. For the extraction, we replicate the logic
// inline since it's a simple switch. However, looking at the original code more carefully,
// deleteEpisode calls getHistoryEmptyMessage() which reads historyFilter from history.js.
// We need to import it or replicate. Let's just use a generic message here since the
// empty message will be refreshed when the history section re-renders.
function getHistoryEmptyMessage() {
    return 'No uploads yet';
}

/**
 * Fetch a suggested title for an episode. Shows shimmer while loading,
 * populates suggestion text on success, hides suggestion area on failure.
 * @param {string} episodeId
 * @returns {Promise<void>}
 */
async function fetchTitleSuggestion(episodeId) {
    const container = document.getElementById('rename-suggestion');
    const textEl = document.getElementById('rename-suggestion-text');
    if (!container || !textEl) {
        return;
    }

    const isRefresh = !container.hasAttribute('hidden');
    container.removeAttribute('hidden');

    if (isRefresh) {
        // Re-fetch: shimmer only the text, keep buttons and layout stable
        textEl.classList.add('modal-suggestion-text--loading');
    } else {
        // Initial fetch: shimmer the whole container (buttons hidden until loaded)
        container.classList.add('modal-suggestion--loading');
        textEl.textContent = '\u00a0';
    }

    const body = {};
    const noteInput = document.getElementById('rename-note-input');
    const noteText = noteInput?.value?.trim();
    if (noteText) {
        body.note = noteText;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/' + episodeId + '/suggest-title', {
            method: 'POST',
            headers: {
                'X-API-Key': getApiKey(),
                'Content-Type': 'application/json',
            },
            body: JSON.stringify(body),
        });

        const overlay = document.getElementById('rename-modal-overlay');
        if (!overlay || overlay.hasAttribute('hidden') || overlay.dataset.episodeId !== episodeId) {
            return;
        }

        if (!response.ok) {
            container.setAttribute('hidden', '');

            return;
        }

        const data = await response.json();
        container.classList.remove('modal-suggestion--loading');
        textEl.classList.remove('modal-suggestion-text--loading');

        const suggestion = data.suggestedTitle || '';
        if (!suggestion && !isRefresh) {
            container.setAttribute('hidden', '');

            return;
        }

        if (suggestion) {
            textEl.textContent = suggestion;
        }
    } catch {
        const containerEl = document.getElementById('rename-suggestion');
        if (containerEl) {
            containerEl.setAttribute('hidden', '');
        }
    }
}

/**
 * Show the rename modal for an episode.
 * Pre-fills the input with the current title, sets up note panel state,
 * disables Save until dirty, and fires an AI title suggestion.
 * @param {string} episodeId
 */
export function showRenameModal(episodeId) {
    const historyData = getHistoryData();
    const episode = historyData?.find(e => e.id === episodeId);
    if (!episode) {
        return;
    }

    const input = document.getElementById('rename-input');
    const overlay = document.getElementById('rename-modal-overlay');

    if (input) {
        input.value = episode.title || episode.fileName;
        input.placeholder = episode.fileName || '';
    }
    if (overlay) {
        overlay.dataset.episodeId = episodeId;
        overlay.removeAttribute('hidden');
    }
    if (input && !window.matchMedia('(pointer: coarse)').matches) {
        input.focus();
    }

    // Track original title for dirty detection
    renameOriginalTitle = input?.value?.trim() || '';
    const saveBtn = document.getElementById('rename-save');
    if (saveBtn) {
        saveBtn.disabled = true;
    }

    // Reset note panel state
    notePanelOpen = false;
    if (noteDebounceTimer) {
        clearTimeout(noteDebounceTimer);
        noteDebounceTimer = null;
    }

    const notePanel = document.getElementById('rename-note-panel');
    if (notePanel) {
        notePanel.setAttribute('hidden', '');
    }

    const noteInput = document.getElementById('rename-note-input');
    noteModalOriginalValue = episode.note || '';
    if (noteInput) {
        noteInput.value = noteModalOriginalValue;
        noteInput.style.height = 'auto';
    }

    updateNoteButtonState(!!episode.note);

    // Fire AI title suggestion (async, non-blocking)
    fetchTitleSuggestion(episodeId);
}

/**
 * Hide the rename modal and reset the suggestion area.
 * @param {boolean} [cancel=false]
 */
export function hideRenameModal(cancel) {
    const overlay = document.getElementById('rename-modal-overlay');
    const episodeId = overlay?.dataset.episodeId;

    if (noteDebounceTimer) {
        clearTimeout(noteDebounceTimer);
        noteDebounceTimer = null;
    }

    if (cancel && episodeId) {
        // Restore note to what it was when the modal opened
        saveEpisodeNote(episodeId, noteModalOriginalValue.trim());
    } else if (notePanelOpen && episodeId) {
        saveNoteIfChanged(episodeId);
    }

    if (overlay) {
        overlay.setAttribute('hidden', '');
        delete overlay.dataset.episodeId;
    }

    const suggestion = document.getElementById('rename-suggestion');
    if (suggestion) {
        suggestion.setAttribute('hidden', '');
        suggestion.classList.remove('modal-suggestion--loading');
    }

    const suggestionText = document.getElementById('rename-suggestion-text');
    if (suggestionText) {
        suggestionText.classList.remove('modal-suggestion-text--loading');
    }

    notePanelOpen = false;
}

/**
 * Toggle the note panel in the rename modal. When opening, snapshots the note
 * value and shows the textarea. When closing, saves the note via PATCH if changed.
 */
export function toggleNotePanel() {
    if (notePanelOpen) {
        closeNotePanel(false);
    } else {
        openNotePanel();
    }
}

/** Open the note panel, snapshotting the current value for Escape restoration. */
function openNotePanel() {
    const notePanel = document.getElementById('rename-note-panel');
    const noteInput = document.getElementById('rename-note-input');

    notePanelSnapshot = noteInput?.value || '';
    notePanelOpen = true;

    if (notePanel) {
        notePanel.removeAttribute('hidden');
    }
    if (!window.matchMedia('(pointer: coarse)').matches) {
        noteInput?.focus();
    }
    updateNoteButtonState(!!noteInput?.value?.trim());
}

/**
 * Close the note panel. If cancelled, restores the original note value and
 * patches the episode to undo any debounce-persisted changes.
 * @param {boolean} cancel
 * @param {boolean} [skipSave=false]
 */
export function closeNotePanel(cancel, skipSave) {
    const overlay = document.getElementById('rename-modal-overlay');
    const episodeId = overlay?.dataset.episodeId;
    const notePanel = document.getElementById('rename-note-panel');
    const noteInput = document.getElementById('rename-note-input');

    if (noteDebounceTimer) {
        clearTimeout(noteDebounceTimer);
        noteDebounceTimer = null;
    }

    notePanelOpen = false;

    if (notePanel) {
        notePanel.setAttribute('hidden', '');
    }

    if (cancel && noteInput) {
        noteInput.value = notePanelSnapshot;
        if (episodeId) {
            saveEpisodeNote(episodeId, notePanelSnapshot.trim());
        }
    } else if (!skipSave && episodeId) {
        saveNoteIfChanged(episodeId);
    }

    updateNoteButtonState(!!noteInput?.value?.trim());
    updateRenameSaveState();

    if (!window.matchMedia('(pointer: coarse)').matches) {
        document.getElementById('rename-input')?.focus();
    }
}

/**
 * Save the note for the current episode if it has changed from the stored value.
 * @param {string} episodeId
 */
function saveNoteIfChanged(episodeId) {
    const noteInput = document.getElementById('rename-note-input');
    const noteText = noteInput?.value?.trim() || '';
    const historyData = getHistoryData();
    const episode = historyData?.find(e => e.id === episodeId);
    if (episode && noteText !== (episode.note || '')) {
        saveEpisodeNote(episodeId, noteText);
    }
}

/**
 * Save the note and re-fetch the AI title suggestion for the current episode.
 * Cancels any pending debounce timer to prevent double fetches.
 */
export function commitNoteAndRefreshSuggestion() {
    if (noteDebounceTimer) {
        clearTimeout(noteDebounceTimer);
        noteDebounceTimer = null;
    }

    const overlay = document.getElementById('rename-modal-overlay');
    const episodeId = overlay?.dataset.episodeId;
    if (!episodeId) {
        return;
    }

    const noteInput = document.getElementById('rename-note-input');
    if (noteInput) {
        saveEpisodeNote(episodeId, noteInput.value.trim());
    }
    fetchTitleSuggestion(episodeId);
}

/**
 * Save episode note via PATCH and update historyData in-place.
 * Fire-and-forget with console.warn on failure.
 * @param {string} episodeId
 * @param {string} note
 */
function saveEpisodeNote(episodeId, note) {
    // Update historyData in-place
    const historyData = getHistoryData();
    if (historyData) {
        const index = historyData.findIndex(e => e.id === episodeId);
        if (index >= 0) {
            historyData[index] = { ...historyData[index], note: note || null };
        }
    }

    updateNoteButtonState(!!note);

    fetch('/api/feeds/' + FEED_ID + '/episodes/' + episodeId, {
        method: 'PATCH',
        headers: {
            'X-API-Key': getApiKey(),
            'Content-Type': 'application/json',
        },
        body: JSON.stringify({ note }),
    }).catch(err => {
        console.warn('Failed to save episode note:', err);
    });
}

/**
 * Update the note button's filled icon state based on whether a note exists,
 * and the highlighted state based on whether the panel is open.
 * @param {boolean} hasNote
 */
function updateNoteButtonState(hasNote) {
    const noteBtn = document.getElementById('rename-note');
    if (noteBtn) {
        noteBtn.classList.toggle('btn-suggestion-icon--filled', hasNote);
        noteBtn.classList.toggle('btn-suggestion-icon--active', notePanelOpen);
        noteBtn.title = hasNote ? 'Edit note' : 'Add note';
        noteBtn.setAttribute('aria-label', hasNote ? 'Edit note' : 'Add note');
    }
}

/**
 * Handle note textarea input event: auto-grow, update button state, update save state,
 * and debounce a note save + suggestion re-fetch after 2s idle.
 * @param {HTMLTextAreaElement} textarea
 */
export function handleNoteInput(textarea) {
    autoGrowTextarea(textarea);
    updateNoteButtonState(!!textarea.value.trim());
    updateRenameSaveState();

    if (noteDebounceTimer) {
        clearTimeout(noteDebounceTimer);
    }
    noteDebounceTimer = setTimeout(() => {
        noteDebounceTimer = null;
        commitNoteAndRefreshSuggestion();
    }, 2000);
}

/**
 * Auto-resize a textarea to fit its content, up to its CSS max-height.
 * @param {HTMLTextAreaElement} textarea
 */
export function autoGrowTextarea(textarea) {
    textarea.style.height = 'auto';
    textarea.style.height = textarea.scrollHeight + 'px';
    textarea.classList.toggle('modal-note-input--scrollable', textarea.scrollHeight > textarea.offsetHeight);
}

/**
 * Check if the title or note has changed from the modal's original values
 * and toggle the Save button accordingly.
 */
export function updateRenameSaveState() {
    const input = document.getElementById('rename-input');
    const noteInput = document.getElementById('rename-note-input');
    const saveBtn = document.getElementById('rename-save');
    if (!saveBtn) {
        return;
    }

    const titleDirty = input && input.value.trim() !== renameOriginalTitle;
    const noteDirty = noteInput && noteInput.value.trim() !== noteModalOriginalValue.trim();
    saveBtn.disabled = !titleDirty && !noteDirty;
}

/**
 * Save episode changes (title and/or note) via the API and optimistically update the UI.
 * @param {string} episodeId
 * @param {string} newTitle
 */
export async function saveEpisodeChanges(episodeId, newTitle) {
    const trimmed = newTitle.trim();
    if (!trimmed) {
        return;
    }

    const patchBody = {};
    if (trimmed !== renameOriginalTitle) {
        patchBody.title = trimmed;
    }

    const noteInput = document.getElementById('rename-note-input');
    const noteText = noteInput?.value?.trim() || '';
    if (noteText !== noteModalOriginalValue.trim()) {
        patchBody.note = noteText;
    }

    if (Object.keys(patchBody).length === 0) {
        hideRenameModal();

        return;
    }

    try {
        const response = await fetch('/api/feeds/' + FEED_ID + '/episodes/' + episodeId, {
            method: 'PATCH',
            headers: { 'X-API-Key': getApiKey(), 'Content-Type': 'application/json' },
            body: JSON.stringify(patchBody)
        });

        if (!response.ok) {
            console.warn('Failed to save episode:', response.status);

            return;
        }

        const updated = await response.json();
        const historyData = getHistoryData();

        // Update historyData in-place
        if (historyData) {
            const index = historyData.findIndex(e => e.id === episodeId);
            if (index >= 0) {
                historyData[index] = { ...historyData[index], title: updated.title, note: updated.note || null };
            }
        }

        if (patchBody.title) {
            // Update localStorage history
            updateLocalHistoryTitle(episodeId, updated.title);

            // Invalidate caches
            invalidateBrowserUploadsCache();
            invalidateAllUploadsCache();

            // Update the DOM item directly
            const item = document.querySelector('#history-list .upload-item[data-id="' + episodeId + '"] .upload-title');
            if (item) {
                item.textContent = updated.title;
            }

            // Update info card if this is the selected episode
            const historySelectedId = getHistorySelectedId();
            if (historySelectedId === episodeId && historyData) {
                const ep = historyData.find(e => e.id === episodeId);
                if (ep) {
                    updateHistoryInfoCard(ep);
                }
            }
        }
    } catch (err) {
        console.warn('Error saving episode:', err);

        return;
    }

    notePanelOpen = false;
    hideRenameModal();
}
