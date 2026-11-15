import { test } from 'node:test';
import assert from 'node:assert/strict';

import { createDismissalStore } from '../../FeatherPod.Server/Pages/Push/modules/dismissal-store.js';

const KEY = 'featherpod_dismissed_test-feed';
const TTL = 60 * 60 * 1000;
const T0 = Date.parse('2026-11-15T10:00:00Z');

/**
 * A localStorage stand-in shared by the stores of several simulated tabs.
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

/**
 * Build a store over the given storage with a controllable clock.
 * @param {Object} storage
 * @param {{t: number}} clock
 */
function store(storage, clock) {
    return createDismissalStore({ getStorage: () => storage, key: KEY, ttlMs: TTL, now: () => clock.t });
}

test('a recorded dismissal is kept in memory and saved', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);

    const dismissedAt = s.record(['j1', 'j2']);

    assert.equal(dismissedAt, T0);
    assert.ok(s.has('j1') && s.has('j2'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 }, { jobId: 'j2', dismissedAt: T0 }]);
});

test('recording no jobs writes nothing', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });

    s.record([]);

    assert.equal(storage.getItem(KEY), null);
});

test('a write keeps dismissals another tab saved after this tab loaded', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    tabA.load();
    tabB.load();

    clock.t = T0 + 1000;
    tabB.record(['from-b']);
    clock.t = T0 + 2000;
    tabA.record(['from-a']);

    assert.deepEqual(storage.saved().map(e => e.jobId).sort(), ['from-a', 'from-b']);
    assert.ok(!tabA.has('from-b'), 'another tab\'s later dismissal reaches this tab only on its next load');
});

test('load reads the dismissals other tabs saved', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ jobId: 'j1', dismissedAt: T0 }]) });
    const s = store(storage, { t: T0 + 1000 });

    s.load();

    assert.ok(s.has('j1'));
});

test('a dismissal older than the TTL is ignored on load and dropped from storage', () => {
    const storage = memoryStorage({
        [KEY]: JSON.stringify([{ jobId: 'old', dismissedAt: T0 }, { jobId: 'kept', dismissedAt: T0 + 1 }])
    });
    const s = store(storage, { t: T0 + 1 + TTL });

    s.load();

    assert.ok(!s.has('old'));
    assert.ok(s.has('kept'), 'a dismissal exactly TTL old is kept');
    assert.deepEqual(storage.saved(), [{ jobId: 'kept', dismissedAt: T0 + 1 }]);
});

test('an expired dismissal is dropped from storage by a later write', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    s.record(['old']);

    clock.t = T0 + TTL + 1;
    s.record(['new']);

    assert.deepEqual(storage.saved(), [{ jobId: 'new', dismissedAt: T0 + TTL + 1 }]);
});

for (const [label, text] of [['unparseable', '{not json'], ['a non-array', '{"jobId":"j1","dismissedAt":1}'], ['null', 'null']]) {
    test(`${label} saved data loads as no dismissals and is replaced by the next write`, () => {
        const storage = memoryStorage({ [KEY]: text });
        const clock = { t: T0 };
        const s = store(storage, clock);

        s.load();
        const loadedAny = s.has('j1');
        s.record(['j2']);

        assert.ok(!loadedAny);
        assert.deepEqual(storage.saved(), [{ jobId: 'j2', dismissedAt: T0 }]);
    });
}

test('malformed saved entries are skipped and the rest kept', () => {
    const storage = memoryStorage({
        [KEY]: JSON.stringify([null, 'j0', { jobId: '', dismissedAt: T0 }, { jobId: 'j1' }, { jobId: 'j2', dismissedAt: T0 }])
    });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(s.has('j2'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j2', dismissedAt: T0 }]);
});

test('drop removes the dismissal it recorded', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });
    const dismissedAt = s.record(['j1']);

    s.drop('j1', dismissedAt);

    assert.ok(!s.has('j1'));
    assert.deepEqual(storage.saved(), []);
});

test('drop keeps a dismissal whose timestamp another path replaced in this tab', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    const first = s.record(['j1']);
    clock.t = T0 + 1000;
    s.record(['j1']);

    s.drop('j1', first);

    assert.ok(s.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000 }]);
});

test('drop keeps a saved dismissal another tab replaced, while dropping this tab\'s own', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    const first = tabA.record(['j1']);
    clock.t = T0 + 1000;
    tabB.record(['j1']);

    tabA.drop('j1', first);

    assert.ok(!tabA.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000 }]);
});

test('storage that throws leaves the in-memory dismissals working', () => {
    const throwing = () => {
        throw new Error('SecurityError');
    };
    const s = createDismissalStore({ getStorage: throwing, key: KEY, ttlMs: TTL, now: () => T0 });

    s.load();
    const dismissedAt = s.record(['j1']);

    assert.ok(s.has('j1'));
    s.drop('j1', dismissedAt);
    assert.ok(!s.has('j1'));
});

test('a failing write keeps the previous saved list', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ jobId: 'j0', dismissedAt: T0 }]) });
    storage.setItem = () => {
        throw new Error('QuotaExceededError');
    };
    const s = store(storage, { t: T0 });

    s.record(['j1']);

    assert.ok(s.has('j1'));
    assert.deepEqual(JSON.parse(storage.getItem(KEY)), [{ jobId: 'j0', dismissedAt: T0 }]);
});
