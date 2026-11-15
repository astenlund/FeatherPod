import { test } from 'node:test';
import assert from 'node:assert/strict';

import { isAddableServerJob, pairInterruptedUploads } from '../../FeatherPod.Server/Pages/Push/modules/interrupted-uploads.js';

const INTERRUPTED = 'Upload interrupted';
const T0 = Date.parse('2026-11-15T10:00:00Z');

/**
 * Build a reload-interrupted queue entry.
 * @param {string} id
 * @param {string} fileName
 * @param {number} startedAt - Epoch milliseconds
 * @param {Object} [overrides]
 */
function interrupted(id, fileName, startedAt, overrides = {}) {
    return { id, fileName, startedAt, status: 'failed', jobId: null, error: INTERRUPTED, ...overrides };
}

/**
 * Build a server job.
 * @param {string} jobId
 * @param {string} fileName
 * @param {number|null} queuedAt - Epoch milliseconds, or null for a job without queuedAt
 */
function job(jobId, fileName, queuedAt) {
    return { jobId, fileName, status: 'Running', ...(queuedAt === null ? {} : { queuedAt: new Date(queuedAt).toISOString() }) };
}

test('a job for the same file queued after the entry started replaces it', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'talk.mp3', T0 + 5000)], INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), entry);
    assert.equal(pairs.size, 1);
});

test('a job queued in the same millisecond the entry started still replaces it', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'talk.mp3', T0)], INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), entry);
});

test('a job for another file name replaces nothing', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'other.mp3', T0 + 5000)], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('a job queued before the entry started cannot claim it', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j-old', 'talk.mp3', T0 - 60000)], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('a job without a usable queuedAt replaces nothing', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);
    const unparseable = { ...job('j2', 'talk.mp3', null), queuedAt: 'not a date' };

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j1', 'talk.mp3', null), unparseable], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('entries that are not reload-interrupted uploads are never replaced', () => {
    // Arrange
    const withJob = interrupted('e1', 'talk.mp3', T0, { jobId: 'j-known' });
    const otherError = interrupted('e2', 'talk.mp3', T0, { error: 'Network error' });
    const notFailed = interrupted('e3', 'talk.mp3', T0, { status: 'normalizing' });
    const noStart = interrupted('e4', 'talk.mp3', undefined);

    // Act
    const pairs = pairInterruptedUploads([withJob, otherError, notFailed, noStart], [job('j1', 'talk.mp3', T0 + 5000)], INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('each job replaces at most one entry and each entry is replaced at most once', () => {
    // Arrange
    const first = interrupted('e1', 'talk.mp3', T0);
    const second = interrupted('e2', 'talk.mp3', T0 + 10000);
    const jobs = [job('j2', 'talk.mp3', T0 + 25000), job('j1', 'talk.mp3', T0 + 15000), job('j3', 'talk.mp3', T0 + 30000)];

    // Act
    const pairs = pairInterruptedUploads([first, second], jobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.get('j1'), first);
    assert.equal(pairs.get('j2'), second);
    assert.equal(pairs.has('j3'), false);
    assert.equal(pairs.size, 2);
});

test('a single interrupted entry is replaced by only one of several matching jobs', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const pairs = pairInterruptedUploads([entry], [job('j2', 'talk.mp3', T0 + 9000), job('j1', 'talk.mp3', T0 + 3000)], INTERRUPTED);

    // Assert
    assert.deepEqual([...pairs.keys()], ['j1']);
});

test('the merge adds neither a cancelled job nor a dismissed or already-queued one', () => {
    // Arrange
    const excluded = new Set(['j-dismissed', 'j-queued']);

    // Act
    const addable = jobId => isAddableServerJob({ ...job(jobId, 'talk.mp3', T0), status: jobId === 'j-cancelled' ? 'Cancelled' : 'Running' }, excluded);

    // Assert
    assert.equal(addable('j-running'), true);
    assert.equal(addable('j-cancelled'), false);
    assert.equal(addable('j-dismissed'), false);
    assert.equal(addable('j-queued'), false);
});

test('a cancelled or dismissed job matching an interrupted entry replaces nothing', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);
    const cancelled = { ...job('j-cancelled', 'talk.mp3', T0 + 5000), status: 'Cancelled' };
    const dismissed = job('j-dismissed', 'talk.mp3', T0 + 6000);
    const dismissedJobIds = new Map([['j-dismissed', T0 + 7000]]);

    // Act
    const addedJobs = [cancelled, dismissed].filter(j => isAddableServerJob(j, dismissedJobIds));
    const pairs = pairInterruptedUploads([entry], addedJobs, INTERRUPTED);

    // Assert
    assert.equal(pairs.size, 0);
});

test('no jobs or no entries yield no pairs', () => {
    // Arrange
    const entry = interrupted('e1', 'talk.mp3', T0);

    // Act
    const noJobs = pairInterruptedUploads([entry], [], INTERRUPTED);
    const noEntries = pairInterruptedUploads([], [job('j1', 'talk.mp3', T0 + 5000)], INTERRUPTED);

    // Assert
    assert.equal(noJobs.size, 0);
    assert.equal(noEntries.size, 0);
});
