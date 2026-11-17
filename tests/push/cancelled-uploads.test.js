import { test } from 'node:test';
import assert from 'node:assert/strict';

import { classifyCancelledUploadJobs, createCancelledUploadStore, generateClientUploadId } from '../../FeatherPod.Server/Pages/Push/modules/cancelled-uploads.js';

const KEY = 'featherpod_cancelled_uploads_test-feed';
const TTL = 60 * 60 * 1000;
const T0 = Date.parse('2026-11-15T10:00:00Z');

/**
 * A localStorage stand-in shared by the stores of several simulated tabs or page loads.
 * @param {Object<string, string>} [initial]
 */
function memoryStorage(initial = {}) {
    const items = new Map(Object.entries(initial));

    return {
        getItem: k => (items.has(k) ? items.get(k) : null),
        setItem: (k, v) => items.set(k, String(v)),
        saved: () => JSON.parse(items.get(KEY))
    };
}

function store(storage, clock) {
    return createCancelledUploadStore({ getStorage: () => storage, key: KEY, ttlMs: TTL, now: () => clock.t });
}

test('a recorded upload is remembered in memory and saved', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });

    s.record('u1');

    assert.ok(s.has('u1'));
    assert.deepEqual(storage.saved(), [{ clientUploadId: 'u1', cancelledAt: T0 }]);
});

test('a reload remembers the uploads cancelled before it', () => {
    const storage = memoryStorage();
    store(storage, { t: T0 }).record('u1');

    const reloaded = store(storage, { t: T0 + 1000 });
    reloaded.load();

    assert.ok(reloaded.has('u1'));
});

test('an id older than the TTL is not loaded and is dropped from storage', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ clientUploadId: 'old', cancelledAt: T0 - TTL - 1 }, { clientUploadId: 'new', cancelledAt: T0 }]) });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(!s.has('old'));
    assert.ok(s.has('new'));
    assert.deepEqual(storage.saved(), [{ clientUploadId: 'new', cancelledAt: T0 }]);
});

test('malformed saved entries are ignored', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([null, { clientUploadId: 7, cancelledAt: T0 }, { clientUploadId: 'u1' }, { clientUploadId: 'u2', cancelledAt: T0 }]) });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(s.has('u2'));
    assert.ok(!s.has('u1'));
    assert.deepEqual(storage.saved(), [{ clientUploadId: 'u2', cancelledAt: T0 }]);
});

test('unparseable saved data loads nothing', () => {
    const storage = memoryStorage({ [KEY]: '{not json' });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(!s.has('u1'));
});

test('forgetting an id keeps the ids another tab saved since this tab loaded', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    tabA.load();
    tabB.load();
    tabA.record('a1');
    tabB.record('b1');

    tabA.forget('a1');

    assert.ok(!tabA.has('a1'));
    assert.deepEqual(storage.saved(), [{ clientUploadId: 'b1', cancelledAt: T0 }]);
});

test('a refresh picks up an id another tab saved after this tab loaded, without writing', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    tabB.load();
    tabA.record('a1');
    const before = storage.getItem(KEY);

    assert.ok(!tabB.has('a1'));
    tabB.refresh();

    assert.ok(tabB.has('a1'));
    assert.equal(storage.getItem(KEY), before);
});

test('a refresh skips expired and empty ids', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ clientUploadId: 'old', cancelledAt: T0 - TTL - 1 }, { clientUploadId: '', cancelledAt: T0 }]) });
    const s = store(storage, { t: T0 });

    s.refresh();

    assert.ok(!s.has('old'));
    assert.ok(!s.has(''));
});

test('a refresh with a storage that throws keeps the ids this tab holds', () => {
    let broken = false;
    const storage = memoryStorage();
    const s = createCancelledUploadStore({ getStorage: () => { if (broken) { throw new Error('SecurityError'); } return storage; }, key: KEY, ttlMs: TTL, now: () => T0 });
    s.record('u1');
    broken = true;

    s.refresh();

    assert.ok(s.has('u1'));
});

test('a storage that throws leaves the in-memory record working', () => {
    const s = createCancelledUploadStore({ getStorage: () => { throw new Error('SecurityError'); }, key: KEY, ttlMs: TTL, now: () => T0 });

    s.load();
    s.record('u1');

    assert.ok(s.has('u1'));
    s.forget('u1');
    assert.ok(!s.has('u1'));
});

test('jobs of cancelled uploads that are still running, queued or failed are withheld', () => {
    const jobs = [
        { jobId: 'j1', clientUploadId: 'u1', status: 'Queued' },
        { jobId: 'j2', clientUploadId: 'u2', status: 'Processing' },
        { jobId: 'j3', clientUploadId: 'u3', status: 'Failed' }
    ];

    const { withheld, settled } = classifyCancelledUploadJobs(jobs, () => true);

    assert.deepEqual(withheld.map(j => j.jobId), ['j1', 'j2', 'j3']);
    assert.deepEqual(settled, []);
});

test('a cancelled or completed job settles its cancelled upload without being withheld', () => {
    const jobs = [
        { jobId: 'j1', clientUploadId: 'u1', status: 'Cancelled' },
        { jobId: 'j2', clientUploadId: 'u2', status: 'Completed' }
    ];

    const { withheld, settled } = classifyCancelledUploadJobs(jobs, () => true);

    assert.deepEqual(withheld, []);
    assert.deepEqual(settled, ['u1', 'u2']);
});

test('a job a normalization retry moved past its upload attempt settles the cancelled upload instead of being withheld', () => {
    // The upload's cancellation was decided on attempt 1; the retry the user started since owns the job
    const jobs = [{ jobId: 'j1', clientUploadId: 'u1', status: 'Processing', attempt: 2 }];

    const { withheld, settled } = classifyCancelledUploadJobs(jobs, id => id === 'u1');

    assert.deepEqual(withheld, []);
    assert.deepEqual(settled, ['u1']);
});

test('jobs without a client upload id, or whose upload was not cancelled, are left alone', () => {
    const jobs = [
        { jobId: 'j1', status: 'Queued' },
        { jobId: 'j2', clientUploadId: null, status: 'Queued' },
        { jobId: 'j3', clientUploadId: 'kept', status: 'Queued' },
        { jobId: 'j4', clientUploadId: 'gone', status: 'Queued' }
    ];

    const { withheld, settled } = classifyCancelledUploadJobs(jobs, id => id === 'gone');

    assert.deepEqual(withheld.map(j => j.jobId), ['j4']);
    assert.deepEqual(settled, []);
});

test('a client upload id is 32 hex characters the server accepts, fresh each call', () => {
    const ids = new Set(Array.from({ length: 50 }, () => generateClientUploadId()));

    assert.equal(ids.size, 50);
    for (const id of ids) {
        assert.match(id, /^[0-9a-f]{32}$/);
    }
});

test('a client upload id needs only getRandomValues', () => {
    const fakeCrypto = { getRandomValues: arr => arr.fill(171) };

    assert.equal(generateClientUploadId(fakeCrypto), 'ab'.repeat(16));
});
