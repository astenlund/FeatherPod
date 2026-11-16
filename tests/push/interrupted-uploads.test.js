import { test } from 'node:test';
import assert from 'node:assert/strict';

import { isAddableServerJob, pairInterruptedUploads, selectAddableServerJobs } from '../../FeatherPod.Server/Pages/Push/modules/interrupted-uploads.js';

const INTERRUPTED = 'Upload interrupted';
const T0 = Date.parse('2026-11-15T10:00:00Z');
const HOUR = 60 * 60 * 1000;

/**
 * Build a reload-interrupted queue entry.
 * @param {string} id
 * @param {string|null} clientUploadId
 * @param {Object} [overrides]
 */
function interrupted(id, clientUploadId, overrides = {}) {
    return { id, fileName: 'talk.mp3', startedAt: T0, clientUploadId, status: 'failed', jobId: null, error: INTERRUPTED, ...overrides };
}

/**
 * Build a server job.
 * @param {string} jobId
 * @param {string|null} clientUploadId - null for a job from a client that sends none (the CLI)
 * @param {Object} [overrides]
 */
function job(jobId, clientUploadId, overrides = {}) {
    return { jobId, fileName: 'talk.mp3', status: 'Running', queuedAt: new Date(T0 + 5000).toISOString(), clientUploadId, ...overrides };
}

test('the job carrying the entry\'s clientUploadId replaces it', () => {
    // Arrange
    const entry = interrupted('e1', 'u1');

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'u1')], INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), entry);
    assert.equal(pairs.size, 1);
});

test('the entry\'s own job replaces it with the device clock ahead of the server', () => {
    // Arrange: the page stamped startedAt an hour ahead of the server's queuedAt
    const entry = interrupted('e1', 'u1', { startedAt: T0 + HOUR });

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'u1', { queuedAt: new Date(T0).toISOString() })], INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), entry);
});

test('the entry\'s own job replaces it whatever its queuedAt, including none', () => {
    // Arrange
    const entries = [interrupted('e1', 'u1'), interrupted('e2', 'u2')];
    const jobs = [job('j1', 'u1', { queuedAt: undefined }), job('j2', 'u2', { queuedAt: 'not a date' })];

    // Act
    const pairs = pairInterruptedUploads(entries, jobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), entries[0]);
    assert.equal(pairs.get('j2'), entries[1]);
});

test('a same-name job from a client without a clientUploadId replaces nothing, with the device clock behind the server', () => {
    // Arrange: the page stamped startedAt an hour behind, so the old timing rule would have let a CLI job claim it
    const entry = interrupted('e1', 'u1', { startedAt: T0 - HOUR });

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j-cli', null)], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('a same-name job carrying another clientUploadId replaces nothing', () => {
    // Arrange
    const entry = interrupted('e1', 'u1');

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j-other', 'u-other')], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('an entry without a clientUploadId is replaced by nothing', () => {
    // Arrange: an entry saved before uploads carried an id
    const entries = [interrupted('e1', null), interrupted('e2', undefined)];

    // Act
    const pairs = pairInterruptedUploads(entries, [job('j-cli', null), job('j1', 'u1')], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('entries that are not reload-interrupted uploads are never replaced', () => {
    // Arrange
    const withJob = interrupted('e1', 'u1', { jobId: 'j-known' });
    const otherError = interrupted('e2', 'u2', { error: 'Network error' });
    const notFailed = interrupted('e3', 'u3', { status: 'normalizing' });
    const jobs = [job('j1', 'u1'), job('j2', 'u2'), job('j3', 'u3')];

    // Act
    const pairs = pairInterruptedUploads([withJob, otherError, notFailed], jobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('several interrupted entries for the same file each pair with their own job', () => {
    // Arrange
    const first = interrupted('e1', 'u1');
    const second = interrupted('e2', 'u2', { startedAt: T0 + 10000 });
    const jobs = [job('j2', 'u2'), job('j3', 'u3'), job('j1', 'u1')];

    // Act
    const pairs = pairInterruptedUploads([first, second], jobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), first);
    assert.equal(pairs.get('j2'), second);
    assert.equal(pairs.has('j3'), false);
    assert.equal(pairs.size, 2);
});

test('the merge adds neither a cancelled job nor a dismissed or already-queued one', () => {
    // Arrange
    const excluded = new Set(['j-dismissed', 'j-queued']);

    // Act
    const addable = jobId => isAddableServerJob({ ...job(jobId, null), status: jobId === 'j-cancelled' ? 'Cancelled' : 'Running' }, excluded);

    // Assert
    assert.equal(addable('j-running'), true);
    assert.equal(addable('j-cancelled'), false);
    assert.equal(addable('j-dismissed'), false);
    assert.equal(addable('j-queued'), false);
});

test('the merge holds back the job of an upload still in flight but adds same-name jobs from other sources', () => {
    // Arrange: one entry still uploading talk.mp3 and one saving it, beside a finished entry for the same file
    const isUploading = entry => entry.status === 'uploading' || entry.status === 'saving';
    const entries = [
        { id: 'e1', fileName: 'talk.mp3', status: 'uploading', clientUploadId: 'u-uploading' },
        { id: 'e2', fileName: 'talk.mp3', status: 'saving', clientUploadId: 'u-saving' },
        { id: 'e3', fileName: 'talk.mp3', status: 'completed', clientUploadId: 'u-done', jobId: 'j-done' }
    ];
    const jobs = [job('j-own', 'u-uploading'), job('j-own-saving', 'u-saving'), job('j-cli', null), job('j-other', 'u-other'), job('j-done-again', 'u-done')];

    // Act
    const added = selectAddableServerJobs(jobs, entries, new Set(), isUploading);

    // Assert
    assert.deepEqual(added.map(j => j.jobId), ['j-cli', 'j-other', 'j-done-again']);
});

test('a cancelled or dismissed job carrying an interrupted entry\'s clientUploadId replaces nothing', () => {
    // Arrange
    const entries = [interrupted('e1', 'u1'), interrupted('e2', 'u2')];
    const cancelled = job('j-cancelled', 'u1', { status: 'Cancelled' });
    const dismissed = job('j-dismissed', 'u2');
    const dismissedJobIds = new Map([['j-dismissed', T0 + 7000]]);

    // Act
    const addedJobs = [cancelled, dismissed].filter(j => isAddableServerJob(j, dismissedJobIds));
    const pairs = pairInterruptedUploads(entries, addedJobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('no jobs or no entries yield no pairs', () => {
    // Arrange
    const entry = interrupted('e1', 'u1');

    // Act
    const noJobs = pairInterruptedUploads([entry], [], INTERRUPTED);
    const noEntries = pairInterruptedUploads([], [job('j1', 'u1')], INTERRUPTED);

    // Assert
    assert.equal(noJobs.size, 0);
    assert.equal(noEntries.size, 0);
});
