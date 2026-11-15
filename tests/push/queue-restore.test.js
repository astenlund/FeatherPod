import { test } from 'node:test';
import assert from 'node:assert/strict';

import { selectRestorableEntries } from '../../FeatherPod.Server/Pages/Push/modules/queue-restore.js';

const TTL = 60 * 60 * 1000;
const NOW = Date.parse('2026-11-15T10:00:00Z');
const noneDismissed = () => false;

test('an entry whose job another tab dismissed is not restored', () => {
    const entries = [{ id: 'q1', jobId: 'j1', status: 'completed', startedAt: NOW - 1000 }, { id: 'q2', jobId: 'j2', status: 'normalizing', startedAt: NOW - 1000 }];

    const restored = selectRestorableEntries(entries, { now: NOW, ttlMs: TTL, isDismissedJob: jobId => jobId === 'j1' });

    assert.deepEqual(restored.map(e => e.id), ['q2']);
});

test('an entry without a job is restored whatever is dismissed', () => {
    const entries = [{ id: 'q1', jobId: null, status: 'failed', startedAt: NOW - 1000 }, { id: 'q2', status: 'uploading', startedAt: NOW - 1000 }];

    const restored = selectRestorableEntries(entries, { now: NOW, ttlMs: TTL, isDismissedJob: () => true });

    assert.deepEqual(restored.map(e => e.id), ['q1', 'q2']);
});

test('entries older than the TTL or without startedAt are not restored', () => {
    const entries = [
        { id: 'old', jobId: 'j1', startedAt: NOW - TTL - 1 },
        { id: 'edge', jobId: 'j2', startedAt: NOW - TTL },
        { id: 'legacy', jobId: 'j3' },
        { id: 'zero', jobId: 'j4', startedAt: 0 }
    ];

    const restored = selectRestorableEntries(entries, { now: NOW, ttlMs: TTL, isDismissedJob: noneDismissed });

    assert.deepEqual(restored.map(e => e.id), ['edge']);
});

test('malformed saved entries are skipped', () => {
    const entries = [null, 'q0', { id: 'q1', startedAt: NOW }];

    const restored = selectRestorableEntries(entries, { now: NOW, ttlMs: TTL, isDismissedJob: noneDismissed });

    assert.deepEqual(restored.map(e => e.id), ['q1']);
});

test('the saved order is kept', () => {
    const entries = [{ id: 'b', startedAt: NOW - 2 }, { id: 'a', startedAt: NOW - 1 }, { id: 'c', startedAt: NOW - 3 }];

    const restored = selectRestorableEntries(entries, { now: NOW, ttlMs: TTL, isDismissedJob: noneDismissed });

    assert.deepEqual(restored.map(e => e.id), ['b', 'a', 'c']);
});
