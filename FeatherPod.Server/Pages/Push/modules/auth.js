import { FEED_ID, API_KEY_SESSION_KEY, API_KEY_LOCAL_KEY, API_KEY_COOKIE_KEY, STR_INVALID_KEY } from './config.js';
import { KEY_CHECK_STATUS, classifyKeyCheckStatus, retryKeyCheck } from './key-check.js';

/** @typedef {import('../push.js').ApiKeyValidationResult} ApiKeyValidationResult */

let apiKey = null;
/** Role that /api/users/me reported for each key validated in this page, keyed by the trimmed key */
const rolesByKey = new Map();

export function getApiKey() {
    return apiKey;
}

/**
 * Returns the role ('Admin' or 'FeedOwner') validated for the current API key, or null if that key has not been
 * validated. Roles are recorded per key, so a late validation of a different key cannot change the current key's role.
 * @returns {string|null}
 */
export function getUserRole() {
    return apiKey ? rolesByKey.get(apiKey.trim()) ?? null : null;
}

export function setApiKey(key) {
    apiKey = key;
}

function setCookie(name, value, days) {
    const maxAge = days * 24 * 60 * 60;
    const secure = window.location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = name + '=' + encodeURIComponent(value) + '; max-age=' + maxAge + '; path=/' + FEED_ID + '/push; SameSite=Strict' + secure;
}

function getCookie(name) {
    const match = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '=([^;]*)'));

    return match ? decodeURIComponent(match[1]) : null;
}

function deleteCookie(name) {
    document.cookie = name + '=; max-age=0; path=/' + FEED_ID + '/push; SameSite=Strict';
}

export function saveApiKey(key) {
    const trimmedKey = key.trim();
    apiKey = trimmedKey;
    try {
        sessionStorage.setItem(API_KEY_SESSION_KEY, trimmedKey);
    } catch (e) {
        // sessionStorage unavailable
    }
    try {
        localStorage.setItem(API_KEY_LOCAL_KEY, trimmedKey);
    } catch (e) {
        // localStorage unavailable
    }
    try {
        setCookie(API_KEY_COOKIE_KEY, trimmedKey, 365);
    } catch (e) {
        // Cookie write failed
    }
}

export function clearApiKey() {
    apiKey = null;
    rolesByKey.clear();
    try { sessionStorage.removeItem(API_KEY_SESSION_KEY); } catch (e) { /* ignore */ }
    try { localStorage.removeItem(API_KEY_LOCAL_KEY); } catch (e) { /* ignore */ }
    try { deleteCookie(API_KEY_COOKIE_KEY); } catch (e) { /* ignore */ }
}

export function getStoredApiKey() {
    try {
        const sessionKey = sessionStorage.getItem(API_KEY_SESSION_KEY);
        if (sessionKey) {
            return sessionKey;
        }
    } catch (e) { /* ignore */ }
    try {
        const localKey = localStorage.getItem(API_KEY_LOCAL_KEY);
        if (localKey) {
            return localKey;
        }
    } catch (e) { /* ignore */ }

    return getCookie(API_KEY_COOKIE_KEY);
}

/**
 * Check an API key against /api/users/me. Only a 401 rejects the key; any other failure status is reported as
 * `serverError` and a failed request as `networkError`, both of which leave the key's validity unknown.
 * @param {string} key
 * @returns {Promise<ApiKeyValidationResult>}
 */
export async function validateApiKey(key) {
    if (!key || key.trim().length === 0) {
        return { valid: false, user: null, feedAccess: false, error: 'API key is empty', networkError: false, serverError: false };
    }

    try {
        const response = await fetch('/api/users/me', {
            headers: { 'X-API-Key': key.trim() }
        });

        const status = classifyKeyCheckStatus(response.status);
        if (status === KEY_CHECK_STATUS.REJECTED) {
            return { valid: false, user: null, feedAccess: false, error: STR_INVALID_KEY, networkError: false, serverError: false };
        }

        if (status === KEY_CHECK_STATUS.SERVER_ERROR) {
            return { valid: false, user: null, feedAccess: false, error: 'Server error (' + response.status + ')', networkError: false, serverError: true };
        }

        const user = await response.json();
        rolesByKey.set(key.trim(), user.role || null);
        const feedAccess = user.role === 'Admin' || (user.role === 'FeedOwner' && user.ownedFeeds && user.ownedFeeds.includes(FEED_ID));

        return { valid: true, user, feedAccess, error: null, networkError: false, serverError: false };
    } catch {
        return { valid: false, user: null, feedAccess: false, error: 'Network error', networkError: true, serverError: false };
    }
}

/**
 * validateApiKey, repeated while the check is inconclusive (network or server error).
 * @param {string} key
 * @param {number} [retries=2] - Additional attempts after the first
 * @returns {Promise<ApiKeyValidationResult>}
 */
export function validateApiKeyWithRetry(key, retries = 2) {
    return retryKeyCheck(() => validateApiKey(key), { retries });
}
