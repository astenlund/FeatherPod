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

    const dismissedAt = s.record([{ jobId: 'j1' }, { jobId: 'j2' }]);

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
    tabB.record([{ jobId: 'from-b' }]);
    clock.t = T0 + 2000;
    tabA.record([{ jobId: 'from-a' }]);

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
    s.record([{ jobId: 'old' }]);

    clock.t = T0 + TTL + 1;
    s.record([{ jobId: 'new' }]);

    assert.deepEqual(storage.saved(), [{ jobId: 'new', dismissedAt: T0 + TTL + 1 }]);
});

for (const [label, text] of [['unparseable', '{not json'], ['a non-array', '{"jobId":"j1","dismissedAt":1}'], ['null', 'null']]) {
    test(`${label} saved data loads as no dismissals and is replaced by the next write`, () => {
        const storage = memoryStorage({ [KEY]: text });
        const clock = { t: T0 };
        const s = store(storage, clock);

        s.load();
        const loadedAny = s.has('j1');
        s.record([{ jobId: 'j2' }]);

        assert.ok(!loadedAny);
        assert.deepEqual(storage.saved(), [{ jobId: 'j2', dismissedAt: T0 }]);
    });
}

test('malformed saved entries are skipped and the rest kept', () => {
    const storage = memoryStorage({
        [KEY]: JSON.stringify([null, 'j0', { jobId: '', dismissedAt: T0 }, { jobId: 'j1' }, { jobId: 7, dismissedAt: T0 }, { jobId: 'j3', dismissedAt: String(T0) }, { jobId: 'j2', dismissedAt: T0 }])
    });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(s.has('j2'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j2', dismissedAt: T0 }]);
});

test('drop removes the dismissal it recorded', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });
    const dismissedAt = s.record([{ jobId: 'j1' }]);

    s.drop('j1', dismissedAt);

    assert.ok(!s.has('j1'));
    assert.deepEqual(storage.saved(), []);
});

test('drop keeps a dismissal whose timestamp another path replaced in this tab', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    const first = s.record([{ jobId: 'j1' }]);
    clock.t = T0 + 1000;
    s.record([{ jobId: 'j1' }]);

    s.drop('j1', first);

    assert.ok(s.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000 }]);
});

test('drop keeps a saved dismissal another tab replaced, while dropping this tab\'s own', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    const first = tabA.record([{ jobId: 'j1' }]);
    clock.t = T0 + 1000;
    tabB.record([{ jobId: 'j1' }]);

    tabA.drop('j1', first);

    assert.ok(!tabA.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000 }]);
});

test('a pending dismissal is honored by the tab that recorded it and saved as pending', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });

    s.record([{ jobId: 'j1' }], { pending: true });

    assert.ok(s.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0, pending: true }]);
});

test('a later load skips a pending dismissal but keeps it in storage', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    store(storage, clock).record([{ jobId: 'pending-job' }], { pending: true });
    store(storage, clock).record([{ jobId: 'ordinary-job' }]);

    const reloaded = store(storage, clock);
    reloaded.load();

    assert.ok(!reloaded.has('pending-job'));
    assert.ok(reloaded.has('ordinary-job'));
    assert.deepEqual(storage.saved(), [{ jobId: 'pending-job', dismissedAt: T0, pending: true }, { jobId: 'ordinary-job', dismissedAt: T0 }]);
});

test('a confirmed pending dismissal is honored by later loads', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    const dismissedAt = s.record([{ jobId: 'j1' }], { pending: true });

    s.confirm('j1', dismissedAt);
    const reloaded = store(storage, clock);
    reloaded.load();

    assert.ok(s.has('j1'));
    assert.ok(reloaded.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 }]);
});

test('a pending dismissal can be confirmed after another tab loaded past it', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const cancelling = store(storage, clock);
    const dismissedAt = cancelling.record([{ jobId: 'j1' }], { pending: true });
    store(storage, clock).load();

    cancelling.confirm('j1', dismissedAt);

    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 }]);
});

test('confirm leaves a dismissal another path or tab replaced', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    const first = tabA.record([{ jobId: 'j1' }], { pending: true });
    clock.t = T0 + 1000;
    tabB.record([{ jobId: 'j1' }], { pending: true });

    tabA.confirm('j1', first);

    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000, pending: true }]);
});

test('confirm of a dismissal no longer saved writes nothing back', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    const dismissedAt = s.record([{ jobId: 'j1' }], { pending: true });
    s.drop('j1', dismissedAt);

    s.confirm('j1', dismissedAt);

    assert.deepEqual(storage.saved(), []);
});

test('dropping a pending dismissal removes it from this tab and from storage', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });
    const dismissedAt = s.record([{ jobId: 'j1' }], { pending: true });

    s.drop('j1', dismissedAt);

    assert.ok(!s.has('j1'));
    assert.deepEqual(storage.saved(), []);
});

test('an ordinary dismissal replaces a pending one for the same job', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);
    s.record([{ jobId: 'j1' }], { pending: true });
    clock.t = T0 + 1000;

    s.record([{ jobId: 'j1' }]);
    const reloaded = store(storage, clock);
    reloaded.load();

    assert.ok(reloaded.has('j1'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000 }]);
});

test('a pending dismissal expires after the TTL like an ordinary one', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    store(storage, clock).record([{ jobId: 'j1' }], { pending: true });
    clock.t = T0 + TTL + 1;

    store(storage, clock).load();

    assert.deepEqual(storage.saved(), []);
});

test('a saved pending field other than true reads as an ordinary dismissal', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ jobId: 'j1', dismissedAt: T0, pending: 'yes' }, { jobId: 'j2', dismissedAt: T0, pending: false }]) });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(s.has('j1') && s.has('j2'));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 }, { jobId: 'j2', dismissedAt: T0 }]);
});

test('storage that throws leaves the in-memory dismissals working', () => {
    const throwing = () => {
        throw new Error('SecurityError');
    };
    const s = createDismissalStore({ getStorage: throwing, key: KEY, ttlMs: TTL, now: () => T0 });

    s.load();
    const dismissedAt = s.record([{ jobId: 'j1' }]);

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

    s.record([{ jobId: 'j1' }]);

    assert.ok(s.has('j1'));
    assert.deepEqual(JSON.parse(storage.getItem(KEY)), [{ jobId: 'j0', dismissedAt: T0 }]);
});

test('a dismissal of an earlier attempt never hides a later attempt of the job, in this tab or a later load', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const s = store(storage, clock);

    s.record([{ jobId: 'j1', attempt: 1 }]);
    const reloaded = store(storage, clock);
    reloaded.load();

    assert.ok(s.has('j1', 1) && reloaded.has('j1', 1));
    assert.ok(!s.has('j1', 2), 'the dismissing tab still shows the retried attempt');
    assert.ok(!reloaded.has('j1', 2), 'a later load still shows the retried attempt');
});

test('a dismissal of a later attempt hides it and the attempts before it, and saves the attempt', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });

    s.record([{ jobId: 'j1', attempt: 3 }]);

    assert.ok(s.has('j1', 2) && s.has('j1', 3) && s.has('j1'));
    assert.ok(!s.has('j1', 4));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0, attempt: 3 }]);
});

test('a saved dismissal without an attempt dismissed the first attempt only', () => {
    const storage = memoryStorage({ [KEY]: JSON.stringify([{ jobId: 'j1', dismissedAt: T0 }, { jobId: 'j2', dismissedAt: T0, attempt: 'x' }]) });
    const s = store(storage, { t: T0 });

    s.load();

    assert.ok(s.has('j1', 1) && s.has('j2', 1));
    assert.ok(!s.has('j1', 2) && !s.has('j2', 2));
});

test('recording an earlier attempt again keeps the later attempt dismissed', () => {
    const storage = memoryStorage();
    const clock = { t: T0 };
    const tabA = store(storage, clock);
    const tabB = store(storage, clock);
    tabA.record([{ jobId: 'j1', attempt: 2 }]);
    clock.t = T0 + 1000;

    tabB.record([{ jobId: 'j1', attempt: 1 }]);
    tabA.record([{ jobId: 'j1', attempt: 1 }]);

    assert.ok(tabA.has('j1', 2));
    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0 + 1000, attempt: 2 }]);
});

test('confirming a pending dismissal of a later attempt keeps its attempt', () => {
    const storage = memoryStorage();
    const s = store(storage, { t: T0 });
    const dismissedAt = s.record([{ jobId: 'j1', attempt: 2 }], { pending: true });

    s.confirm('j1', dismissedAt);

    assert.deepEqual(storage.saved(), [{ jobId: 'j1', dismissedAt: T0, attempt: 2 }]);
});
