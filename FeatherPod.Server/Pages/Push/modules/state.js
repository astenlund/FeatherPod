import { STATES, DEBUG_TITLE_ANIMATION, STR_INVALID_KEY, STR_NO_ACCESS, STR_API_KEY_REQUIRED } from './config.js';
import { setNotificationToggleVisible } from './notifications.js';
import { setWakeLockToggleVisible } from './wake-lock.js';

// Title animation state
let titleAnimationId = null;
let currentTitleText = 'Push';
let isFirstStateChange = true;

// Title animation timing (ms)
const TITLE_ANIMATION_CHAR_DELAY = 150;
const TITLE_ANIMATION_LOAD_DELAY = 600;
const TITLE_ANIMATION_PAUSE_DELAY = 300;

// Layout state
let cachedContainerWidth = 0;
let cachedCollapsedMargin = 0;
export const COLLAPSED_WIDTH = 500;
const COLLAPSED_HEIGHT_DEFAULT = 280;

function animateTitle(targetWord) {
    const titleEl = document.getElementById('page-title');
    if (!titleEl) {
        return;
    }

    const suffix = ' to Feed';

    if (titleAnimationId != null) {
        clearTimeout(titleAnimationId);
        titleAnimationId = null;
    }

    let commonLength = 0;
    while (commonLength < currentTitleText.length &&
           commonLength < targetWord.length &&
           currentTitleText[commonLength] === targetWord[commonLength]) {
        commonLength++;
    }

    const steps = [];

    for (let i = currentTitleText.length; i > commonLength; i--) {
        steps.push({ text: currentTitleText.slice(0, i - 1), pause: false });
    }

    const hasErased = currentTitleText.length > commonLength;
    const hasToAdd = targetWord.length > commonLength;
    if (hasErased && hasToAdd) {
        steps.push({ text: currentTitleText.slice(0, commonLength), pause: true });
    }

    for (let i = commonLength + 1; i <= targetWord.length; i++) {
        steps.push({ text: targetWord.slice(0, i), pause: false });
    }

    let stepIndex = 0;

    function nextStep() {
        if (stepIndex >= steps.length) {
            titleAnimationId = null;

            return;
        }

        const step = steps[stepIndex];
        titleEl.textContent = step.text + suffix;
        currentTitleText = step.text;
        stepIndex++;
        const delay = step.pause ? TITLE_ANIMATION_PAUSE_DELAY : TITLE_ANIMATION_CHAR_DELAY;
        titleAnimationId = setTimeout(nextStep, delay);
    }

    if (steps.length > 0) {
        nextStep();
    }
}

/**
 * Switch between UI states (no-key, ready, queue, error).
 * @param {string} stateName
 * @param {boolean} [hasActiveWork=false] - Whether the queue has active work
 * @param {Function} [collapseHistoryImmediate] - History collapse function (injected to avoid circular dep)
 */
export function showState(stateName, hasActiveWork = false, collapseHistoryImmediate) {
    if (collapseHistoryImmediate) {
        collapseHistoryImmediate();
    }

    STATES.forEach(s => document.getElementById(s).style.display = s === stateName ? '' : 'none');

    const hasActive = stateName === 'queue' && hasActiveWork;
    setNotificationToggleVisible(hasActive);
    setWakeLockToggleVisible(hasActive);

    const container = document.querySelector('.container');
    if (container) {
        STATES.forEach(s => container.classList.remove('state-' + s));
        container.classList.add('state-' + stateName);
    }

    let targetWord;
    if (stateName === 'queue') {
        targetWord = hasActive ? 'Pushing' : 'Pushed';
    } else {
        targetWord = 'Push';
    }

    if (isFirstStateChange && DEBUG_TITLE_ANIMATION) {
        let startWord;
        if (stateName === 'queue') {
            startWord = 'Push';
        } else if (stateName === 'error') {
            startWord = 'Pushing';
        } else {
            startWord = 'Pushed';
        }
        currentTitleText = startWord;
        const titleEl = document.getElementById('page-title');
        if (titleEl) {
            titleEl.textContent = startWord + ' to Feed';
        }
    }

    if (targetWord !== currentTitleText) {
        if (isFirstStateChange && !DEBUG_TITLE_ANIMATION) {
            const titleEl = document.getElementById('page-title');
            if (titleEl) {
                titleEl.textContent = targetWord + ' to Feed';
            }
            currentTitleText = targetWord;
        } else {
            setTimeout(() => animateTitle(targetWord), TITLE_ANIMATION_LOAD_DELAY);
        }
    }
    isFirstStateChange = false;
}

export function getCurrentState() {
    return STATES.find(s => {
        const el = document.getElementById(s);

        return el && el.style.display !== 'none';
    }) || null;
}

/**
 * Update queue title to "Pushing" or "Pushed".
 * @param {boolean} hasActiveWork
 */
export function updateQueueTitle(hasActiveWork) {
    if (getCurrentState() !== 'queue') {
        return;
    }
    animateTitle(hasActiveWork ? 'Pushing' : 'Pushed');
}

export function showError(message) {
    showState('error');
    document.getElementById('error-message').textContent = message;
    document.getElementById('try-another').focus();
}

export function showWarningBanner(message, duration = 5000) {
    const existingBanner = document.getElementById('warning-banner');
    if (existingBanner) {
        existingBanner.remove();
    }

    const banner = document.createElement('div');
    banner.id = 'warning-banner';
    banner.className = 'warning-banner';

    const textSpan = document.createElement('span');
    textSpan.className = 'warning-banner-text';
    textSpan.textContent = message;
    banner.appendChild(textSpan);

    const dismissBtn = document.createElement('button');
    dismissBtn.className = 'warning-banner-dismiss';
    dismissBtn.setAttribute('aria-label', 'Dismiss');
    dismissBtn.textContent = '\u00d7';
    banner.appendChild(dismissBtn);

    document.body.appendChild(banner);

    banner.querySelector('.warning-banner-dismiss').addEventListener('click', () => {
        dismissBanner();
    });

    requestAnimationFrame(() => {
        banner.classList.add('warning-banner--visible');
    });

    const timeoutId = setTimeout(dismissBanner, duration);

    function dismissBanner() {
        clearTimeout(timeoutId);
        banner.classList.remove('warning-banner--visible');
        setTimeout(() => banner.remove(), 300);
    }
}

export function setNoKeyError(errorType) {
    const noKeyTitleEl = document.getElementById('no-key-title');
    if (!noKeyTitleEl) {
        return;
    }

    if (errorType === 'no-access') {
        noKeyTitleEl.textContent = STR_NO_ACCESS;
    } else if (errorType === 'invalid') {
        noKeyTitleEl.textContent = STR_INVALID_KEY;
    } else {
        noKeyTitleEl.textContent = STR_API_KEY_REQUIRED;
    }
}

export function getCollapsedHeight() {
    const dropZone = document.getElementById('drop-zone');
    if (!dropZone) {
        return COLLAPSED_HEIGHT_DEFAULT;
    }

    const savedHeight = dropZone.style.height;
    dropZone.style.height = '';
    const measured = dropZone.offsetHeight;
    dropZone.style.height = savedHeight;

    if (measured > 0) {
        return measured;
    }

    if (dropZone.classList.contains('drop-zone--has-artwork')) {
        return COLLAPSED_WIDTH;
    }

    return COLLAPSED_HEIGHT_DEFAULT;
}

export function cacheLayoutDimensions() {
    const container = document.querySelector('.container');
    cachedContainerWidth = container?.clientWidth || 800;
    cachedCollapsedMargin = Math.max(0, (cachedContainerWidth - COLLAPSED_WIDTH) / 2);

    document.documentElement.style.setProperty('--history-container-width', cachedContainerWidth + 'px');
    document.documentElement.style.setProperty('--history-collapsed-margin', cachedCollapsedMargin + 'px');
}

export function getCachedContainerWidth() {
    return cachedContainerWidth;
}

export function getCachedCollapsedMargin() {
    return cachedCollapsedMargin;
}
