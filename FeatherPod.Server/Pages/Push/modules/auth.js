import { FEED_ID, API_KEY_SESSION_KEY, API_KEY_LOCAL_KEY, API_KEY_COOKIE_KEY, STR_INVALID_KEY } from './config.js';

let apiKey = null;

export function getApiKey() {
    return apiKey;
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

export async function validateApiKey(key) {
    if (!key || key.trim().length === 0) {
        return { valid: false, user: null, feedAccess: false, error: 'API key is empty', networkError: false };
    }

    try {
        const response = await fetch('/api/users/me', {
            headers: { 'X-API-Key': key.trim() }
        });

        if (!response.ok) {
            if (response.status === 401) {
                return { valid: false, user: null, feedAccess: false, error: STR_INVALID_KEY, networkError: false };
            }

            return { valid: false, user: null, feedAccess: false, error: 'Server error (' + response.status + ')', networkError: false };
        }

        const user = await response.json();
        const feedAccess = user.role === 'Admin' || (user.role === 'FeedOwner' && user.ownedFeeds && user.ownedFeeds.includes(FEED_ID));

        return { valid: true, user, feedAccess, error: null, networkError: false };
    } catch (err) {
        return { valid: false, user: null, feedAccess: false, error: 'Network error', networkError: true };
    }
}

export async function validateApiKeyWithRetry(key, retries = 2) {
    const result = await validateApiKey(key);
    if (result.networkError && retries > 0) {
        await new Promise(r => setTimeout(r, 1000));

        return validateApiKeyWithRetry(key, retries - 1);
    }

    return result;
}
