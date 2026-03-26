import importPlugin from 'eslint-plugin-import';

const pushDir = 'FeatherPod.Server/Pages/Push/';

// Globals injected by the HTML template's inline <script> block
const templateGlobals = {
    FEED_ID: 'readonly',
    ICON_ETAG: 'readonly',
    IS_DEV: 'readonly',
    PROGRESS_SMOOTHING: 'readonly',
    VAPID_PUBLIC_KEY: 'readonly',
};

// Browser globals available in window context
const browserGlobals = {
    window: 'readonly',
    document: 'readonly',
    history: 'readonly',
    console: 'readonly',
    localStorage: 'readonly',
    sessionStorage: 'readonly',
    navigator: 'readonly',
    location: 'readonly',
    fetch: 'readonly',
    Response: 'readonly',
    URL: 'readonly',
    URLSearchParams: 'readonly',
    FormData: 'readonly',
    File: 'readonly',
    XMLHttpRequest: 'readonly',
    EventSource: 'readonly',
    Image: 'readonly',
    DOMException: 'readonly',
    performance: 'readonly',
    requestAnimationFrame: 'readonly',
    cancelAnimationFrame: 'readonly',
    setTimeout: 'readonly',
    clearTimeout: 'readonly',
    setInterval: 'readonly',
    clearInterval: 'readonly',
    getComputedStyle: 'readonly',
    matchMedia: 'readonly',
    Notification: 'readonly',
    AbortController: 'readonly',
    queueMicrotask: 'readonly',
    structuredClone: 'readonly',
    atob: 'readonly',
    btoa: 'readonly',
    caches: 'readonly',
};

// Shared rules for all Push page JS
const sharedRules = {
    // Formatting (auto-fixable)
    'semi': ['error', 'always'],
    'quotes': ['error', 'single', { avoidEscape: true, allowTemplateLiterals: true }],
    'no-trailing-spaces': 'error',
    'eol-last': ['error', 'always'],
    'indent': ['error', 4, { SwitchCase: 1 }],
    'no-multiple-empty-lines': ['error', { max: 1, maxEOF: 0 }],

    // Error detection
    'no-unused-vars': ['warn', { args: 'none', caughtErrors: 'none' }],
    'no-undef': 'error',
    'no-redeclare': 'error',
    'no-duplicate-case': 'error',
    'no-dupe-keys': 'error',
    'no-unreachable': 'error',
    'no-constant-condition': ['error', { checkLoops: false }],
    'eqeqeq': ['error', 'always', { null: 'ignore' }],
};

export default [
    {
        ignores: ['node_modules/**'],
    },

    // Push page main module (push.js served as app.js)
    {
        files: [`${pushDir}push.js`],
        languageOptions: {
            ecmaVersion: 'latest',
            sourceType: 'module',
            globals: { ...browserGlobals, ...templateGlobals },
        },
        rules: sharedRules,
    },

    // Service worker (non-module script)
    {
        files: [`${pushDir}push-sw.js`],
        languageOptions: {
            ecmaVersion: 'latest',
            sourceType: 'script',
            globals: browserGlobals,
        },
        rules: sharedRules,
    },

    // Push page ES modules (future: modules/ directory)
    {
        files: [`${pushDir}modules/**/*.js`],
        plugins: {
            import: importPlugin,
        },
        languageOptions: {
            ecmaVersion: 'latest',
            sourceType: 'module',
            globals: browserGlobals,
        },
        rules: {
            ...sharedRules,
            'import/no-unresolved': 'error',
            'import/no-duplicates': 'error',
            'import/named': 'error',
        },
    },

    // Service worker specific globals
    {
        files: [`${pushDir}push-sw.js`],
        languageOptions: {
            globals: {
                self: 'readonly',
                clients: 'readonly',
            },
        },
    },
];
