/**
 * YouTube URL parsing -- pure functions with no DOM or window dependencies.
 *
 * Kept separate from youtube.js so the parsing can be unit-tested under Node
 * (tests/push/youtube-url.test.js, run via `npm test`).
 */

const YT_VIDEO_REGEX = /(?:youtube\.com\/watch\?v=|youtu\.be\/|m\.youtube\.com\/watch\?v=)([a-zA-Z0-9_-]{11})/;
const YT_REJECT_PATTERNS = [
    /[?&]list=/,
    /youtube\.com\/(?:channel\/|@|c\/)/,
    /youtube\.com\/shorts\//,
    /youtube\.com\/results/
];
const YT_FILE_NAME_REGEX = /^([a-zA-Z0-9_-]{11})\.(m4a|mp4)$/;

/**
 * Build the canonical watch URL for a video id.
 * @param {string} videoId - 11-character YouTube video id
 * @returns {string}
 */
export function canonicalYouTubeUrl(videoId) {
    return `https://www.youtube.com/watch?v=${videoId}`;
}

/**
 * Extract a YouTube video URL from text. Returns the canonical watch URL rebuilt
 * from the matched video id, so surrounding text, other URLs and tracking or
 * timestamp params never leak into the result. Returns null when the text
 * contains no supported video URL or matches a rejected form (playlist, channel,
 * shorts, search results).
 * @param {string} text
 * @returns {string|null}
 */
export function extractYouTubeUrl(text) {
    if (!text) {
        return null;
    }

    for (const pattern of YT_REJECT_PATTERNS) {
        if (pattern.test(text)) {
            return null;
        }
    }

    const match = text.match(YT_VIDEO_REGEX);

    return match ? canonicalYouTubeUrl(match[1]) : null;
}

/**
 * Parse the server-shaped file name of a YouTube import job (<videoId>.m4a for audio,
 * <videoId>.mp4 for video) back into its video id and format. Returns null for any
 * other name, including ordinary uploads.
 * @param {string|null|undefined} fileName
 * @returns {{videoId: string, format: 'audio'|'video'}|null}
 */
export function parseYouTubeFileName(fileName) {
    const match = typeof fileName === 'string' ? fileName.match(YT_FILE_NAME_REGEX) : null;

    return match ? { videoId: match[1], format: match[2] === 'mp4' ? 'video' : 'audio' } : null;
}
