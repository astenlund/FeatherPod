// Prototype extensions (side-effects on import)

Number.prototype.sigDig = function(minSigDigs) {
    if (this.valueOf() === 0) {
        return '0';
    }

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
 * Whether a queue entry has active work (not yet finished).
 * @param {{status: string}} entry
 * @returns {boolean}
 */
export function isActiveWork(entry) {
    return entry.status === 'queued' || entry.status === 'uploading' || entry.status === 'normalizing';
}

/**
 * Check if a file is a valid audio file by MIME type.
 * @param {File} file
 * @returns {boolean}
 */
export function isValidAudioFile(file) {
    if (!file.type) {
        return true;
    }

    return file.type.startsWith('audio/');
}

/**
 * Format a duration from TimeSpan string to human-readable.
 * @param {string} duration
 * @returns {string}
 */
export function formatDuration(duration) {
    if (!duration) {
        return '';
    }

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
 * Format a date as "28 nov 2025" (day, short month lowercase, year).
 * @param {string|null} dateString
 * @returns {string}
 */
export function formatDate(dateString) {
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
 * Format a date as relative time (e.g., "2 minutes ago").
 * @param {string|null} dateString
 * @returns {string}
 */
export function formatRelativeTime(dateString) {
    if (!dateString) {
        return '';
    }

    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;

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
 * Safely parse JSON, returning null if parsing fails.
 * @param {string} text
 * @returns {Object|null}
 */
export function tryParseJson(text) {
    try {
        return JSON.parse(text);
    } catch {
        return null;
    }
}

/**
 * Simple focus trap for modal dialogs.
 * @param {KeyboardEvent} e
 * @param {HTMLElement} container
 */
export function trapFocus(e, container) {
    if (e.key !== 'Tab') {
        return;
    }

    const focusable = container.querySelectorAll('input, textarea, button:not([hidden])');
    if (focusable.length === 0) {
        return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];

    if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
    }
}
