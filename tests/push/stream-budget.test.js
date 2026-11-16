import { test } from 'node:test';
import assert from 'node:assert/strict';

import { createStreamBudget } from '../../FeatherPod.Server/Pages/Push/modules/stream-budget.js';

function fakeStream() {
    const stream = { open: true };

    return { stream, isOpen: () => stream.open };
}

test('slots are granted up to the limit and refused beyond it', () => {
    const budget = createStreamBudget(3);

    const granted = [1, 2, 3, 4].map(() => budget.tryAcquire(fakeStream().isOpen));

    assert.deepEqual(granted, [true, true, true, false]);
    assert.equal(budget.openCount(), 3);
    assert.equal(budget.hasRoom(), false);
});

test('a refused acquisition registers nothing', () => {
    const budget = createStreamBudget(1);
    const first = fakeStream();
    budget.tryAcquire(first.isOpen);
    const refused = fakeStream();

    const result = budget.tryAcquire(refused.isOpen);
    first.stream.open = false;

    assert.equal(result, false);
    assert.equal(budget.openCount(), 0);
});

test('a stream closed by any path frees its slot at the next question', () => {
    const budget = createStreamBudget(2);
    const a = fakeStream();
    const b = fakeStream();
    budget.tryAcquire(a.isOpen);
    budget.tryAcquire(b.isOpen);

    a.stream.open = false;

    assert.equal(budget.hasRoom(), true);
    assert.equal(budget.openCount(), 1);
    assert.equal(budget.tryAcquire(fakeStream().isOpen), true);
    assert.equal(budget.hasRoom(), false);
});

test('a stream nobody closed keeps its slot', () => {
    const budget = createStreamBudget(1);
    budget.tryAcquire(fakeStream().isOpen);

    const room = budget.hasRoom();

    assert.equal(room, false);
});

test('two acquisitions sharing one predicate take two slots', () => {
    const budget = createStreamBudget(2);
    const shared = fakeStream();

    budget.tryAcquire(shared.isOpen);
    budget.tryAcquire(shared.isOpen);

    assert.equal(budget.openCount(), 2);
    assert.equal(budget.hasRoom(), false);
});

test('a predicate that throws keeps its slot', () => {
    const budget = createStreamBudget(1);
    budget.tryAcquire(() => {
        throw new Error('unreadable');
    });

    const result = budget.tryAcquire(fakeStream().isOpen);

    assert.equal(result, false);
    assert.equal(budget.openCount(), 1);
});

test('an empty budget has room and counts nothing', () => {
    const budget = createStreamBudget(3);

    const room = budget.hasRoom();
    const count = budget.openCount();

    assert.equal(room, true);
    assert.equal(count, 0);
});

test('a limit that is not a positive integer is refused', () => {
    for (const limit of [0, -1, 1.5, NaN, '3', undefined]) {
        assert.throws(() => createStreamBudget(limit), RangeError);
    }
});
