import { ICON_ETAG, THEME_CACHE_KEY } from './config.js';

/**
 * Recursive median cut color quantization.
 * @param {number[][]} pixels - Array of [r, g, b] arrays
 * @param {number} depth - Recursion depth (0 = base case)
 * @returns {{color: number[], count: number}[]}
 */
function medianCut(pixels, depth) {
    if (depth === 0 || pixels.length === 0) {
        if (pixels.length === 0) {
            return [];
        }
        let rSum = 0, gSum = 0, bSum = 0;
        for (const p of pixels) {
            rSum += p[0];
            gSum += p[1];
            bSum += p[2];
        }
        const n = pixels.length;

        return [{ color: [Math.round(rSum / n), Math.round(gSum / n), Math.round(bSum / n)], count: n }];
    }

    let rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
    for (const p of pixels) {
        if (p[0] < rMin) { rMin = p[0]; }
        if (p[0] > rMax) { rMax = p[0]; }
        if (p[1] < gMin) { gMin = p[1]; }
        if (p[1] > gMax) { gMax = p[1]; }
        if (p[2] < bMin) { bMin = p[2]; }
        if (p[2] > bMax) { bMax = p[2]; }
    }

    const rRange = rMax - rMin;
    const gRange = gMax - gMin;
    const bRange = bMax - bMin;
    const channel = rRange >= gRange && rRange >= bRange ? 0 : gRange >= bRange ? 1 : 2;

    pixels.sort((a, b) => a[channel] - b[channel]);
    const mid = Math.floor(pixels.length / 2);

    return [
        ...medianCut(pixels.slice(0, mid), depth - 1),
        ...medianCut(pixels.slice(mid), depth - 1)
    ];
}

/**
 * Convert RGB (0-255) to HSL.
 * @returns {{h: number, s: number, l: number}}
 */
function rgbToHsl(r, g, b) {
    r /= 255;
    g /= 255;
    b /= 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    const d = max - min;

    if (d === 0) {
        return { h: 0, s: 0, l: l * 100 };
    }

    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === r) {
        h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
    } else if (max === g) {
        h = ((b - r) / d + 2) / 6;
    } else {
        h = ((r - g) / d + 4) / 6;
    }

    return { h: h * 360, s: s * 100, l: l * 100 };
}

/**
 * Convert HSL to RGB array [r, g, b] (0-255).
 * @returns {number[]}
 */
function hslToRgb(h, s, l) {
    s /= 100;
    l /= 100;
    const c = (1 - Math.abs(2 * l - 1)) * s;
    const x = c * (1 - Math.abs((h / 60) % 2 - 1));
    const m = l - c / 2;
    let r, g, b;
    if (h < 60) { r = c; g = x; b = 0; }
    else if (h < 120) { r = x; g = c; b = 0; }
    else if (h < 180) { r = 0; g = c; b = x; }
    else if (h < 240) { r = 0; g = x; b = c; }
    else if (h < 300) { r = x; g = 0; b = c; }
    else { r = c; g = 0; b = x; }

    return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

/**
 * Extract primary and accent colors from a loaded image using median cut.
 * @param {HTMLImageElement} img
 * @returns {{primaryHue: number, accentHue: number}|null}
 */
function extractColors(img) {
    const w = img.naturalWidth;
    const h = img.naturalHeight;
    if (w === 0 || h === 0) {
        return null;
    }

    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    ctx.drawImage(img, 0, 0);

    let data;
    try {
        data = ctx.getImageData(0, 0, w, h).data;
    } catch (e) {
        return null;
    }

    const targetSamples = 2500;
    const stride = Math.max(1, Math.floor(Math.sqrt(w * h / targetSamples)));
    const pixels = [];
    for (let y = 0; y < h; y += stride) {
        for (let x = 0; x < w; x += stride) {
            const i = (y * w + x) * 4;
            pixels.push([data[i], data[i + 1], data[i + 2]]);
        }
    }

    const palette = medianCut(pixels, 3);

    const candidates = [];
    for (const entry of palette) {
        const hsl = rgbToHsl(entry.color[0], entry.color[1], entry.color[2]);
        if (hsl.l < 12 || hsl.l > 88) {
            continue;
        }
        candidates.push({ hsl, count: entry.count });
    }

    if (candidates.length === 0) {
        return null;
    }

    let totalSat = 0;
    let totalCount = 0;
    for (const c of candidates) {
        totalSat += c.hsl.s * c.count;
        totalCount += c.count;
    }
    if (totalCount === 0 || totalSat / totalCount < 10) {
        return null;
    }

    candidates.sort((a, b) => b.count - a.count);
    const primaryHue = candidates[0].hsl.h;

    let accentHue = (primaryHue + 20) % 360;
    let bestAccentSat = -1;
    for (let i = 1; i < candidates.length; i++) {
        const hueDiff = Math.abs(candidates[i].hsl.h - primaryHue);
        const angularDiff = Math.min(hueDiff, 360 - hueDiff);
        if (angularDiff >= 30 && candidates[i].hsl.s > bestAccentSat) {
            bestAccentSat = candidates[i].hsl.s;
            accentHue = candidates[i].hsl.h;
        }
    }

    return { primaryHue, accentHue };
}

function applyArtworkPalette(hue, accentHue) {
    const s = document.body.style;

    s.setProperty('--bg-base', 'hsl(' + hue + ', 28%, 14%)');
    s.setProperty('--bg-elevated', 'hsl(' + hue + ', 31%, 24%)');
    s.setProperty('--bg-surface', 'hsl(' + hue + ', 27%, 20%)');
    s.setProperty('--bg-grad-1', 'hsl(' + hue + ', 30%, 22%)');
    s.setProperty('--bg-grad-2', 'hsl(' + hue + ', 31%, 20%)');
    s.setProperty('--bg-grad-3', 'hsl(' + hue + ', 30%, 18%)');
    s.setProperty('--bg-grad-4', 'hsl(' + hue + ', 30%, 16%)');

    s.setProperty('--border-subtle', 'hsl(' + hue + ', 22%, 29%)');
    s.setProperty('--border-muted', 'hsl(' + hue + ', 18%, 35%)');

    s.setProperty('--primary-900', 'hsl(' + hue + ', 47%, 34%)');
    s.setProperty('--primary-800', 'hsl(' + hue + ', 55%, 41%)');
    s.setProperty('--primary-500', 'hsl(' + hue + ', 84%, 67%)');
    s.setProperty('--primary-400', 'hsl(' + hue + ', 89%, 74%)');
    s.setProperty('--primary-300', 'hsl(' + hue + ', 94%, 82%)');
    s.setProperty('--primary-200', 'hsl(' + hue + ', 96%, 89%)');

    s.setProperty('--accent-500', 'hsl(' + accentHue + ', 90%, 66%)');
    s.setProperty('--success', 'hsl(' + hue + ', 100%, 84%)');

    s.setProperty('--text-tertiary', 'hsl(' + hue + ', 20%, 77%)');
    s.setProperty('--text-muted', 'hsl(' + hue + ', 18%, 66%)');

    const primary500 = hslToRgb(hue, 84, 67);
    const alphas = [
        ['--primary-a5', 0.05], ['--primary-a8', 0.08], ['--primary-a10', 0.1],
        ['--primary-a12', 0.12], ['--primary-a15', 0.15], ['--primary-a20', 0.2],
        ['--primary-a25', 0.25], ['--primary-a30', 0.3], ['--primary-a40', 0.4]
    ];
    for (const [prop, alpha] of alphas) {
        s.setProperty(prop, 'rgba(' + primary500[0] + ', ' + primary500[1] + ', ' + primary500[2] + ', ' + alpha + ')');
    }

    const glow400 = hslToRgb(hue, 89, 74);
    s.setProperty('--glow-400-50', 'rgba(' + glow400[0] + ', ' + glow400[1] + ', ' + glow400[2] + ', 0.5)');
    s.setProperty('--glow-400-40', 'rgba(' + glow400[0] + ', ' + glow400[1] + ', ' + glow400[2] + ', 0.4)');

    const glow300 = hslToRgb(hue, 94, 82);
    s.setProperty('--glow-300-40', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.4)');
    s.setProperty('--glow-300-50', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.5)');
    s.setProperty('--glow-300-60', 'rgba(' + glow300[0] + ', ' + glow300[1] + ', ' + glow300[2] + ', 0.6)');

    const glow200 = hslToRgb(hue, 96, 89);
    s.setProperty('--glow-200-60', 'rgba(' + glow200[0] + ', ' + glow200[1] + ', ' + glow200[2] + ', 0.6)');
}

function applyDefaultPalette() {
    const s = document.body.style;

    s.setProperty('--bg-base', '#1a1a2e');
    s.setProperty('--bg-elevated', '#2a2a50');
    s.setProperty('--bg-surface', '#252541');
    s.setProperty('--bg-grad-1', '#28284a');
    s.setProperty('--bg-grad-2', '#242445');
    s.setProperty('--bg-grad-3', '#202040');
    s.setProperty('--bg-grad-4', '#1c1c35');

    s.setProperty('--border-subtle', '#3a3a5a');
    s.setProperty('--border-muted', '#4b4a6a');

    s.setProperty('--primary-900', '#312e81');
    s.setProperty('--primary-800', '#3730a3');
    s.setProperty('--primary-500', '#6366f1');
    s.setProperty('--primary-400', '#818cf8');
    s.setProperty('--primary-300', '#a5b4fc');
    s.setProperty('--primary-200', '#c7d2fe');

    s.setProperty('--accent-500', '#8b5cf6');
    s.setProperty('--success', '#adb4ff');

    s.setProperty('--text-tertiary', '#b8b8d0');
    s.setProperty('--text-muted', '#9898b8');

    const alphaBase = [99, 102, 241];
    for (const [prop, a] of [
        ['--primary-a5', 0.05], ['--primary-a8', 0.08], ['--primary-a10', 0.1],
        ['--primary-a12', 0.12], ['--primary-a15', 0.15], ['--primary-a20', 0.2],
        ['--primary-a25', 0.25], ['--primary-a30', 0.3], ['--primary-a40', 0.4]
    ]) {
        s.setProperty(prop, 'rgba(' + alphaBase[0] + ', ' + alphaBase[1] + ', ' + alphaBase[2] + ', ' + a + ')');
    }

    s.setProperty('--glow-400-50', 'rgba(129, 140, 248, 0.5)');
    s.setProperty('--glow-400-40', 'rgba(129, 140, 248, 0.4)');
    s.setProperty('--glow-300-40', 'rgba(165, 180, 252, 0.4)');
    s.setProperty('--glow-300-50', 'rgba(165, 180, 252, 0.5)');
    s.setProperty('--glow-300-60', 'rgba(165, 180, 252, 0.6)');
    s.setProperty('--glow-200-60', 'rgba(199, 210, 254, 0.6)');
}

function savePaletteToCache(primaryHue, accentHue, etag) {
    try {
        localStorage.setItem(THEME_CACHE_KEY, JSON.stringify({ primaryHue, accentHue, etag }));
    } catch (_) {
        // Private browsing or quota exceeded
    }
}

function loadCachedPalette() {
    try {
        const raw = localStorage.getItem(THEME_CACHE_KEY);

        return raw ? JSON.parse(raw) : null;
    } catch (_) {
        return null;
    }
}

function buildBackdropGradient(primaryHue, accentHue) {
    return 'linear-gradient(135deg, hsl(' + primaryHue + ', 50%, 18%), hsl(' + accentHue + ', 50%, 18%))';
}

function clearPaletteCache() {
    try {
        localStorage.removeItem(THEME_CACHE_KEY);
    } catch (_) {
        // Ignore
    }
}

/**
 * Initialize feed artwork with dynamic color theming.
 * Loads the feed icon, extracts colors via median cut, and re-themes CSS custom properties.
 */
export function initFeedArtwork() {
    const artwork = document.getElementById('feed-artwork');
    if (!artwork) {
        clearPaletteCache();
        applyDefaultPalette();
        document.body.classList.add('theme-ready');

        return;
    }

    const cached = loadCachedPalette();
    if (cached && cached.primaryHue != null && cached.etag === ICON_ETAG) {
        applyArtworkPalette(cached.primaryHue, cached.accentHue);
        const backdrop = document.getElementById('artwork-backdrop');
        if (backdrop) {
            backdrop.style.background = buildBackdropGradient(cached.primaryHue, cached.accentHue);
        }
        document.body.classList.add('theme-ready');
    }

    let artworkProcessed = false;

    function processArtwork() {
        if (artworkProcessed) {
            return;
        }
        artworkProcessed = true;

        const colors = extractColors(artwork);

        const backdrop = document.getElementById('artwork-backdrop');
        if (!backdrop) {
            clearPaletteCache();
            applyDefaultPalette();
            document.body.classList.add('theme-ready');

            return;
        }

        const backdropImg = document.getElementById('artwork-backdrop-img');
        if (backdropImg) {
            backdropImg.src = artwork.src;
        }

        const dropZone = document.getElementById('drop-zone');
        if (dropZone && !dropZone.classList.contains('drop-zone--has-artwork')) {
            dropZone.classList.add('drop-zone--has-artwork');
            dropZone.addEventListener('click', () => {
                if (dropZone.classList.contains('drop-zone--has-artwork')) {
                    document.getElementById('file-input')?.click();
                }
            });
        }

        if (colors) {
            applyArtworkPalette(colors.primaryHue, colors.accentHue);
            backdrop.style.background = buildBackdropGradient(colors.primaryHue, colors.accentHue);
            savePaletteToCache(colors.primaryHue, colors.accentHue, ICON_ETAG);
        } else {
            clearPaletteCache();
            applyDefaultPalette();
        }

        document.body.classList.add('theme-ready');

        // Force layout read so the browser paints the image before the opacity transition
        backdrop.offsetHeight;
        backdrop.classList.add('artwork-backdrop--visible');
    }

    artwork.addEventListener('load', processArtwork);
    artwork.addEventListener('error', () => {
        artwork.remove();
        clearPaletteCache();
        document.getElementById('drop-zone')?.classList.remove('drop-zone--has-artwork');
        applyDefaultPalette();
        document.body.classList.add('theme-ready');
    });

    if (artwork.complete) {
        if (artwork.naturalWidth > 0) {
            processArtwork();
        } else {
            artwork.remove();
            clearPaletteCache();
            document.getElementById('drop-zone')?.classList.remove('drop-zone--has-artwork');
            applyDefaultPalette();
            document.body.classList.add('theme-ready');
        }
    }
}
