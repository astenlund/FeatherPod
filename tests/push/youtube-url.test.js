import { test } from 'node:test';
import assert from 'node:assert/strict';

import { canonicalYouTubeUrl, extractYouTubeUrl } from '../../FeatherPod.Server/Pages/Push/modules/youtube-url.js';

const VIDEO_ID = 'dQw4w9WgXcQ';
const CANONICAL = `https://www.youtube.com/watch?v=${VIDEO_ID}`;

test('returns the YouTube URL when another URL precedes it in the text', () => {
    const text = `Check https://example.com then https://youtu.be/${VIDEO_ID} later`;

    assert.equal(extractYouTubeUrl(text), CANONICAL);
});

test('canonicalizes every supported host form', () => {
    const forms = [
        `https://www.youtube.com/watch?v=${VIDEO_ID}`,
        `https://youtube.com/watch?v=${VIDEO_ID}`,
        `https://m.youtube.com/watch?v=${VIDEO_ID}`,
        `https://youtu.be/${VIDEO_ID}`,
        `http://youtu.be/${VIDEO_ID}`
    ];

    for (const form of forms) {
        assert.equal(extractYouTubeUrl(form), CANONICAL, form);
    }
});

test('drops tracking and timestamp params', () => {
    assert.equal(extractYouTubeUrl(`https://www.youtube.com/watch?v=${VIDEO_ID}&pp=ygUFaGVsbG8%3D`), CANONICAL);
    assert.equal(extractYouTubeUrl(`https://youtu.be/${VIDEO_ID}?t=42`), CANONICAL);
    assert.equal(extractYouTubeUrl(`https://www.youtube.com/watch?v=${VIDEO_ID}&t=1m30s&feature=share`), CANONICAL);
});

test('returns the video URL from surrounding prose without leaking the prose', () => {
    assert.equal(extractYouTubeUrl(`Look at this: https://www.youtube.com/watch?v=${VIDEO_ID}, amazing!`), CANONICAL);
});

test('rejects playlist, channel, shorts and search-results URLs', () => {
    const rejected = [
        `https://www.youtube.com/watch?v=${VIDEO_ID}&list=PLabcdef`,
        'https://www.youtube.com/playlist?list=PLabcdef',
        'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv',
        'https://www.youtube.com/@somecreator',
        'https://www.youtube.com/c/somecreator',
        `https://www.youtube.com/shorts/${VIDEO_ID}`,
        'https://www.youtube.com/results?search_query=hello'
    ];

    for (const url of rejected) {
        assert.equal(extractYouTubeUrl(url), null, url);
    }
});

test('returns null for empty input and text without a video URL', () => {
    assert.equal(extractYouTubeUrl(''), null);
    assert.equal(extractYouTubeUrl(null), null);
    assert.equal(extractYouTubeUrl(undefined), null);
    assert.equal(extractYouTubeUrl('https://example.com/watch?v=notyoutube1'), null);
    assert.equal(extractYouTubeUrl('just some text'), null);
});

test('canonicalYouTubeUrl builds the www watch URL', () => {
    assert.equal(canonicalYouTubeUrl(VIDEO_ID), CANONICAL);
});
