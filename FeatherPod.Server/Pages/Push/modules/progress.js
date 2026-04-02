import { PROGRESS_SMOOTHING, SHOW_GHOST, VELOCITY_OVERRIDES } from './config.js';
import './utils.js'; // Number.prototype.formatBytes

/**
 * Multi-entry progress animator with velocity prediction.
 * Manages per-entry animation slots via a Map<entryId, slotState>.
 * When PROGRESS_SMOOTHING is enabled, uses learned velocities and EMA
 * to produce smooth progress bar animation. When disabled, updates directly.
 *
 * Velocity learning: accumulates progress samples for 3 seconds (min 12 samples)
 * before updating the learned cold-start velocity via asymmetric EMA. Also tracks
 * overall stage velocity (persisted on stage end) for the restore path.
 * Learned velocities stored in localStorage as { coldStart, overall } per stage.
 */

/** @returns {SlotState} A fresh slot state with zero/null/false defaults */
function createSlot() {
    return {
        currentValue: 0,
        targetValue: 0,
        velocity: 0,
        displayVelocity: 0,
        acceleration: 0,
        speedFactor: 1,
        lastUpdateTime: 0,
        stageStartTime: 0,
        stageStartValue: 0,
        progressBar: null,
        ghostBar: null,
        currentStage: null,
        awaitingFirstUpdate: false,
        isRestoring: false,
        currentFileSize: 0,
        sampleCount: 0,
        coldStartLearned: false,
        lastTickMs: null
    };
}

export const progressAnimator = {
    DEFAULT_INITIAL_VELOCITIES: {
        'Uploading': 1024 * 1024,
        'Analyzing': 200 * 1024,
        'Normalizing': 200 * 1024,
        'Transcribing': 100 * 1024,
        'Downloading': 1024 * 1024
    },
    MAX_INITIAL_VELOCITIES: {
        'Uploading': 10 * 1024 * 1024,
        'Downloading': 10 * 1024 * 1024
    },
    LEARNED_INITIAL_VELOCITY_STORAGE_KEY: 'featherpod_learned_initial_velocity',
    /** @type {Map<string, SlotState>} */
    slots: new Map(),
    /** @type {number|null} Shared requestAnimationFrame ID */
    animationId: null,
    /** @type {number} Shared last frame timestamp */
    lastFrameTime: 0,

    /**
     * Get the learned initial velocity for a stage, reading file size from the slot.
     * @param {string} stage
     * @param {{ preferOverall?: boolean }} options
     * @param {SlotState} slot
     * @returns {{ velocity: number, wasClamped: boolean }}
     */
    getLearnedInitialVelocity(stage, { preferOverall = false } = {}, slot) {
        if (VELOCITY_OVERRIDES[stage] != null) {
            const bytesPerSec = VELOCITY_OVERRIDES[stage];
            if (slot.currentFileSize > 0) {
                return { velocity: (bytesPerSec / slot.currentFileSize) * 100, wasClamped: false };
            }

            return { velocity: 1, wasClamped: false };
        }

        let bytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

        try {
            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            if (stored) {
                const values = JSON.parse(stored);
                const entry = values[stage];
                if (entry != null && typeof entry === 'object') {
                    if (preferOverall && entry.overall != null) {
                        bytesPerSec = entry.overall;
                    } else if (entry.coldStart != null) {
                        bytesPerSec = entry.coldStart;
                    }
                }
                // Old flat-number format: discard (fall through to default)
            }
        } catch {
            // Ignore localStorage errors
        }

        const maxBytesPerSec = this.MAX_INITIAL_VELOCITIES[stage];
        let wasClamped = false;
        if (maxBytesPerSec != null && bytesPerSec > maxBytesPerSec) {
            bytesPerSec = maxBytesPerSec;
            wasClamped = true;
        }

        if (slot.currentFileSize > 0) {
            return { velocity: (bytesPerSec / slot.currentFileSize) * 100, wasClamped };
        }

        return { velocity: 1, wasClamped: false };
    },

    /**
     * Update the learned cold-start velocity for a stage.
     * @param {string} stage
     * @param {number} actualVelocity - In percent-per-second
     * @param {SlotState} slot
     * @returns {boolean}
     */
    updateLearnedInitialVelocity(stage, actualVelocity, slot) {
        if (slot.currentFileSize <= 0 || actualVelocity < 0.1) {
            return false;
        }

        try {
            const actualBytesPerSec = (actualVelocity / 100) * slot.currentFileSize;

            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};
            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

            // Read coldStart from new format, discard old flat numbers
            const entry = values[stage];
            let currentBytesPerSec = defaultBytesPerSec;
            if (entry != null && typeof entry === 'object' && entry.coldStart != null) {
                currentBytesPerSec = entry.coldStart;
            }

            const targetBytesPerSec = actualBytesPerSec * 0.9;
            const alpha = targetBytesPerSec < currentBytesPerSec ? 0.8 : 0.2;
            const updatedBytesPerSec = currentBytesPerSec * (1 - alpha) + targetBytesPerSec * alpha;

            // Write back in new { coldStart, overall } format, preserving overall if present
            if (entry != null && typeof entry === 'object') {
                entry.coldStart = updatedBytesPerSec;
            } else {
                values[stage] = { coldStart: updatedBytesPerSec };
            }
            localStorage.setItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY, JSON.stringify(values));

            const current = currentBytesPerSec.formatBytes(2, '/s');
            const updated = updatedBytesPerSec.formatBytes(2, '/s');
            const actual = actualBytesPerSec.formatBytes(2, '/s');
            console.log(`[${stage}] Cold-start velocity: ${current} -> ${updated} (actual: ${actual})`);

            return true;
        } catch {
            return false;
        }
    },

    /**
     * Finalize overall stage velocity and persist it. No-op if guards fail.
     * @param {SlotState} slot
     */
    finalizeStageVelocity(slot) {
        if (slot.currentStage == null || slot.targetValue < 1 || slot.currentFileSize <= 0 || slot.sampleCount < 2) {
            return;
        }

        const now = performance.now();
        const elapsed = (now - slot.stageStartTime) / 1000;
        if (elapsed <= 0) {
            return;
        }

        const stage = slot.currentStage;
        slot.currentStage = null;

        const velocityPctPerSec = (slot.targetValue - slot.stageStartValue) / elapsed;
        const overallBytesPerSec = (velocityPctPerSec / 100) * slot.currentFileSize;

        try {
            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};

            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;
            let entry = values[stage];

            // Discard old flat-number format
            if (entry != null && typeof entry !== 'object') {
                entry = null;
            }

            let currentOverall = defaultBytesPerSec;
            if (entry?.overall != null) {
                currentOverall = entry.overall;
            }

            // Same asymmetric EMA as cold-start: faster to decrease, slower to increase
            const targetBytesPerSec = overallBytesPerSec * 0.9;
            const alpha = targetBytesPerSec < currentOverall ? 0.8 : 0.2;
            const updatedOverall = currentOverall * (1 - alpha) + targetBytesPerSec * alpha;

            if (entry) {
                entry.overall = updatedOverall;
            } else {
                values[stage] = { overall: updatedOverall };
            }
            localStorage.setItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY, JSON.stringify(values));

            const current = currentOverall.formatBytes(2, '/s');
            const updated = updatedOverall.formatBytes(2, '/s');
            const actual = overallBytesPerSec.formatBytes(2, '/s');
            console.log(`[${stage}] Overall velocity: ${current} -> ${updated} (actual: ${actual})`);
        } catch {
            // Ignore localStorage errors
        }
    },

    /**
     * Begin a new stage for an entry, optionally setting file size.
     * @param {string} stage
     * @param {HTMLElement} progressBar
     * @param {string} entryId
     * @param {number} [fileSize]
     */
    startWithAssumption(stage, progressBar, entryId, fileSize) {
        let slot = this.slots.get(entryId) || createSlot();

        if (slot.currentStage) {
            this.finalizeStageVelocity(slot);
        }

        const wasRestoring = slot.isRestoring;
        const preservedFileSize = slot.currentFileSize;
        const preservedCurrentValue = slot.currentValue;
        const preservedTargetValue = slot.targetValue;

        // Reset slot state (preserve progress position for continuous display across stages)
        Object.assign(slot, createSlot());
        slot.isRestoring = wasRestoring;
        slot.currentFileSize = preservedFileSize;
        slot.currentValue = preservedCurrentValue;
        slot.targetValue = preservedTargetValue;

        if (fileSize != null) {
            slot.currentFileSize = fileSize;
        }

        const now = performance.now();
        slot.currentStage = stage;
        slot.lastUpdateTime = now;
        slot.stageStartTime = now;
        slot.stageStartValue = slot.targetValue;
        this.slots.set(entryId, slot);

        if (!PROGRESS_SMOOTHING) {
            slot.progressBar = progressBar;

            return;
        }

        if (wasRestoring) {
            slot.progressBar = progressBar;
            slot.awaitingFirstUpdate = true;
            slot.isRestoring = true;
        } else {
            const { velocity: learnedInitialVelocity, wasClamped } = this.getLearnedInitialVelocity(stage, {}, slot);
            if (slot.currentValue === 0) {
                slot.targetValue = Math.min(learnedInitialVelocity * 1, 30);
            }
            slot.velocity = learnedInitialVelocity;
            slot.displayVelocity = learnedInitialVelocity;
            slot.awaitingFirstUpdate = true;
            const bytesPerSec = (learnedInitialVelocity / 100) * slot.currentFileSize;
            const clampedSuffix = wasClamped ? ' (clamped)' : '';
            console.log(`[${stage}] Initial velocity: ${bytesPerSec.formatBytes(2, '/s')}${clampedSuffix}`);
            this.start(progressBar, entryId);
        }
    },

    /**
     * Update target progress for an entry.
     * @param {number} value - Progress percentage (0-100)
     * @param {string} stage
     * @param {string} entryId
     * @param {number} [tickMs] - Server-measured ms since job start (immune to tab suspension)
     */
    setTarget(value, stage, entryId, tickMs) {
        const slot = this.slots.get(entryId);
        if (!slot) {
            return;
        }

        if (!PROGRESS_SMOOTHING) {
            if (slot.progressBar) {
                slot.progressBar.style.width = value + '%';
            }

            return;
        }

        if (slot.awaitingFirstUpdate && stage === slot.currentStage) {
            const now = performance.now();
            const dt = (now - slot.lastUpdateTime) / 1000;

            if (slot.isRestoring) {
                const { velocity: learnedVelocity } = this.getLearnedInitialVelocity(stage, { preferOverall: true }, slot);
                slot.currentValue = value;
                slot.targetValue = value;
                slot.velocity = learnedVelocity;
                slot.displayVelocity = learnedVelocity;
                slot.lastUpdateTime = now;
                slot.lastTickMs = tickMs ?? null;
                slot.awaitingFirstUpdate = false;
                slot.isRestoring = false;
                if (slot.progressBar) {
                    slot.progressBar.style.width = value + '%';
                    this.start(slot.progressBar, entryId);
                }

                return;
            }

            slot.sampleCount++;

            if (!slot.coldStartLearned) {
                const elapsed = (now - slot.stageStartTime) / 1000;
                if (elapsed >= 3 && slot.sampleCount >= 12) {
                    const avgVelocity = (value - slot.stageStartValue) / elapsed;
                    if (this.updateLearnedInitialVelocity(stage, avgVelocity, slot)) {
                        slot.coldStartLearned = true;
                    }
                }
            }

            // Update velocity/target tracking (prefer server ticks when available)
            const accumDt = (tickMs != null && slot.lastTickMs != null) ? (tickMs - slot.lastTickMs) / 1000 : dt;
            if (accumDt > 0.05) {
                const totalElapsed = (now - slot.stageStartTime) / 1000;
                const realVelocity = totalElapsed > 0 ? (value - slot.stageStartValue) / totalElapsed : slot.velocity;
                slot.velocity = realVelocity;
                slot.displayVelocity = slot.velocity;
                slot.targetValue = value;
                slot.acceleration = 0;
                slot.lastUpdateTime = now;
                slot.lastTickMs = tickMs ?? null;
            } else {
                slot.targetValue = value;
            }

            // Stop awaiting after cold-start is learned (accumulation complete).
            // Seed steady-state velocity from the cumulative average so the EMA
            // doesn't jump on the first post-accumulation sample.
            if (slot.coldStartLearned) {
                const totalElapsed = (now - slot.stageStartTime) / 1000;
                if (totalElapsed > 0) {
                    slot.velocity = (value - slot.stageStartValue) / totalElapsed;
                    slot.displayVelocity = slot.velocity;
                }
                slot.awaitingFirstUpdate = false;
            }

            return;
        }

        const now = performance.now();
        const clientDt = (now - slot.lastUpdateTime) / 1000;
        const dt = (tickMs != null && slot.lastTickMs != null) ? (tickMs - slot.lastTickMs) / 1000 : clientDt;

        if (dt > 0.05) {
            const instantVelocity = (value - slot.targetValue) / dt;
            const prevVelocity = slot.velocity;
            slot.velocity = slot.velocity * 0.5 + instantVelocity * 0.5;

            const instantAcceleration = (slot.velocity - prevVelocity) / dt;
            slot.acceleration = slot.acceleration * 0.5 + instantAcceleration * 0.5;

            if (SHOW_GHOST) {
                const bytesPerSec = (instantVelocity / 100) * slot.currentFileSize;
                const deltaBytesPerSec = ((slot.displayVelocity - instantVelocity) / 100) * slot.currentFileSize;
                const deltaSign = deltaBytesPerSec >= 0 ? '+' : '';
                console.log(`[${stage}] Instant velocity: ${bytesPerSec.formatBytes(2, '/s')} (${deltaSign}${deltaBytesPerSec.formatBytes(2, '/s')})`);
            }
        }

        slot.targetValue = value;
        slot.lastUpdateTime = now;
        slot.lastTickMs = tickMs ?? null;

        if (slot.ghostBar) {
            slot.ghostBar.style.width = value + '%';
        }
    },

    /**
     * Bind a progress bar to a slot and start animation if needed.
     * @param {HTMLElement} progressBar
     * @param {string} entryId
     */
    start(progressBar, entryId) {
        const slot = this.slots.get(entryId);
        if (!slot) {
            return;
        }

        slot.progressBar = progressBar;
        if (!PROGRESS_SMOOTHING) {
            return;
        }

        if (SHOW_GHOST && progressBar) {
            const ghostId = progressBar.id + '-ghost';
            slot.ghostBar = document.getElementById(ghostId);
            if (slot.ghostBar) {
                slot.ghostBar.parentElement.classList.add('visible');
            }
        }

        if (!this.animationId) {
            this.lastFrameTime = performance.now();
            this.animate();
        }
    },

    /**
     * Reset a slot's animation state. isRestoring survives. Does not remove from map.
     * @param {string} entryId
     */
    reset(entryId) {
        const slot = this.slots.get(entryId);
        if (!slot) {
            return;
        }

        this.finalizeStageVelocity(slot);

        if (slot.ghostBar) {
            slot.ghostBar.parentElement.classList.remove('visible');
        }

        const preservedRestoring = slot.isRestoring;
        const preservedFileSize = slot.currentFileSize;
        Object.assign(slot, createSlot());
        slot.isRestoring = preservedRestoring;
        slot.currentFileSize = preservedFileSize;
        // Shared loop self-terminates when no active slots remain
    },

    /**
     * Remove a slot entirely from the map.
     * @param {string} entryId
     */
    removeSlot(entryId) {
        this.slots.delete(entryId);
    },

    /**
     * Set file size for an entry's slot (creates slot if needed).
     * @param {string} entryId
     * @param {number} fileSize
     */
    setFileSize(entryId, fileSize) {
        let slot = this.slots.get(entryId);
        if (!slot) {
            slot = createSlot();
        }
        slot.currentFileSize = fileSize;
        this.slots.set(entryId, slot);
    },

    /**
     * Get the current animation stage for an entry.
     * @param {string} entryId
     * @returns {string|null}
     */
    getCurrentStage(entryId) {
        return this.slots.get(entryId)?.currentStage ?? null;
    },

    /**
     * Check if any slot has an active stage.
     * @returns {boolean}
     */
    hasActiveSlots() {
        for (const slot of this.slots.values()) {
            if (slot.currentStage != null) {
                return true;
            }
        }

        return false;
    },

    /**
     * Mark all active slots as restoring (awaiting first real update).
     */
    setRestoring() {
        for (const slot of this.slots.values()) {
            if (slot.currentStage != null) {
                slot.awaitingFirstUpdate = true;
                slot.isRestoring = true;
            }
        }
    },

    /**
     * Rebind all progress bars using a lookup function.
     * @param {(entryId: string) => HTMLElement|null} getBarFn
     */
    rebindAllProgressBars(getBarFn) {
        for (const [entryId, slot] of this.slots) {
            slot.progressBar = getBarFn(entryId);
        }
    },

    /**
     * Rebind the progress bar for a single entry.
     * @param {string} entryId
     * @param {HTMLElement|null} progressBar
     */
    rebindProgressBar(entryId, progressBar) {
        const slot = this.slots.get(entryId);
        if (slot) {
            slot.progressBar = progressBar;
        }
    },

    /**
     * Shared animation loop. Ticks all active slots, self-terminates when none remain.
     */
    animate() {
        const now = performance.now();
        const rawDt = (now - this.lastFrameTime) / 1000;
        this.lastFrameTime = now;
        const dt = Math.min(rawDt, 0.1);

        let anyActive = false;

        for (const slot of this.slots.values()) {
            if (slot.currentStage == null) {
                continue;
            }

            const timeSinceUpdate = (now - slot.lastUpdateTime) / 1000;
            const estimatedActual = slot.targetValue + slot.velocity * timeSinceUpdate;

            if (rawDt > 1) {
                slot.currentValue = Math.max(slot.currentValue, Math.min(estimatedActual, slot.targetValue));
                slot.displayVelocity = slot.velocity;
                slot.speedFactor = 1;
            }

            const velocityEaseRate = 3;
            slot.displayVelocity += (slot.velocity - slot.displayVelocity) * Math.min(1, velocityEaseRate * dt);

            const velocityGap = slot.velocity - slot.displayVelocity;
            const baseProjectedLag = velocityGap / velocityEaseRate;

            const accelAdjustment = Math.max(-0.3, Math.min(0.3, slot.acceleration * 0.05));
            const projectedLag = baseProjectedLag * (1 - accelAdjustment);

            const compensatedTarget = estimatedActual + projectedLag;
            const error = compensatedTarget - slot.currentValue;

            const maxSpeedAdjust = 0.3;
            const targetSpeedFactor = 1 + Math.max(-maxSpeedAdjust, Math.min(maxSpeedAdjust, error * 0.3));
            const easingRate = 3;
            slot.speedFactor += (targetSpeedFactor - slot.speedFactor) * Math.min(1, easingRate * dt);

            slot.currentValue += slot.displayVelocity * dt * slot.speedFactor;
            slot.currentValue = Math.max(0, Math.min(100, slot.currentValue));

            if (slot.progressBar) {
                slot.progressBar.style.width = slot.currentValue + '%';
            }
            if (slot.ghostBar) {
                slot.ghostBar.style.width = slot.targetValue + '%';
            }

            if (slot.currentValue < 99.9) {
                anyActive = true;
            }
        }

        if (anyActive) {
            this.animationId = requestAnimationFrame(() => this.animate());
        } else {
            this.animationId = null;
        }
    }
};
