// Server-injected globals (set by inline <script> in push.html)
export const FEED_ID = window.FEED_ID;
export const ICON_ETAG = window.ICON_ETAG;
export const IS_DEV = window.IS_DEV;
export const PROGRESS_SMOOTHING = window.PROGRESS_SMOOTHING;
export const VAPID_PUBLIC_KEY = window.VAPID_PUBLIC_KEY;

// Dev flags (parsed from query string, IS_DEV only)
export const SHOW_GHOST = IS_DEV && window.location.search.includes('ghost');
export const DEBUG_TITLE_ANIMATION = IS_DEV && window.location.search.includes('alive');
export const FAKE_PWA = IS_DEV && window.location.search.includes('pwa');
export const VELOCITY_OVERRIDES = IS_DEV ? parseVelocityOverrides() : {};

function parseVelocityOverrides() {
    const params = new URLSearchParams(window.location.search);
    const overrides = {};
    const mapping = { vup: 'Uploading', vanal: 'Analyzing', vnorm: 'Normalizing' };
    for (const [param, stage] of Object.entries(mapping)) {
        const value = params.get(param);
        if (value != null) {
            const parsed = parseFloat(value);
            if (!isNaN(parsed)) {
                overrides[stage] = parsed * 1024;
            }
        }
    }

    return overrides;
}

// Storage keys (feed-scoped)
export const QUEUE_STORAGE_KEY = 'featherpod_queue_' + FEED_ID;
export const HISTORY_STORAGE_KEY = 'featherpod_history_' + FEED_ID;
export const HISTORY_FILTER_KEY = 'featherpod_history_filter_' + FEED_ID;
export const API_KEY_SESSION_KEY = 'featherpod_api_key_' + FEED_ID;
export const API_KEY_LOCAL_KEY = 'featherpod_api_key_local_' + FEED_ID;
export const API_KEY_COOKIE_KEY = 'featherpod_key_' + FEED_ID;
export const DISMISSED_STORAGE_KEY = 'featherpod_dismissed_' + FEED_ID;
export const THEME_CACHE_KEY = 'featherpod_theme_' + FEED_ID;
export const NOTIF_ENABLED_KEY = 'featherpod_notif_' + FEED_ID;
export const NOTIF_HINT_SHOWN_KEY = 'featherpod_notif_hint_shown';
export const WAKE_LOCK_KEY = 'featherpod_wake_' + FEED_ID;

// Constants
export const MAX_LOCAL_HISTORY = 50;
export const QUEUE_SYNC_TIMEOUT = 3000;
export const STATES = ['no-key', 'ready', 'queue', 'error'];

// No-key state UI strings
export const STR_PASTE_KEY_BELOW = 'Paste key below';
export const STR_PASTE_KEY = 'Paste here';
export const STR_SAVE_KEY = 'Save key';
export const STR_API_KEY_REQUIRED = 'API key required';
export const STR_INVALID_KEY = 'Invalid key';
export const STR_NO_ACCESS = 'No access';
export const STR_NO_FEED_ACCESS = 'This key does not have access to this feed';
