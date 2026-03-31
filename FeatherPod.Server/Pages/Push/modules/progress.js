import { PROGRESS_SMOOTHING, SHOW_GHOST, VELOCITY_OVERRIDES } from './config.js';
import './utils.js'; // Number.prototype.formatBytes

/**
 * Singleton progress animator with velocity prediction.
 * When PROGRESS_SMOOTHING is enabled, uses learned velocities and EMA
 * to produce smooth progress bar animation. When disabled, updates directly.
 *
 * Velocity learning: accumulates progress samples for 5 seconds (min 2 samples)
 * before updating the learned cold-start velocity via asymmetric EMA. Also tracks
 * overall stage velocity (persisted on stage end) for the restore path.
 * Learned velocities stored in localStorage as { coldStart, overall } per stage.
 */
export const progressAnimator = {
    DEFAULT_INITIAL_VELOCITIES: {
        'Uploading': 1024 * 1024,
        'Analyzing': 100 * 1024,
        'Normalizing': 100 * 1024,
        'Downloading': 1024 * 1024,
        'Transcribing': 100 * 1024
    },
    MAX_INITIAL_VELOCITIES: {
        'Uploading': 10 * 1024 * 1024,
        'Downloading': 10 * 1024 * 1024
    },
    LEARNED_INITIAL_VELOCITY_STORAGE_KEY: 'featherpod_learned_initial_velocity',
    currentFileSize: 0,

    getLearnedInitialVelocity(stage, { preferOverall = false } = {}) {
        if (VELOCITY_OVERRIDES[stage] != null) {
            const bytesPerSec = VELOCITY_OVERRIDES[stage];
            if (this.currentFileSize > 0) {
                return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped: false };
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

        if (this.currentFileSize > 0) {
            return { velocity: (bytesPerSec / this.currentFileSize) * 100, wasClamped };
        }

        return { velocity: 1, wasClamped: false };
    },

    updateLearnedInitialVelocity(stage, actualVelocity) {
        if (this.currentFileSize <= 0 || actualVelocity < 0.1) {
            return false;
        }

        try {
            const actualBytesPerSec = (actualVelocity / 100) * this.currentFileSize;

            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};
            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

            // Read coldStart from new format, discard old flat numbers
            const entry = values[stage];
            let currentBytesPerSec = defaultBytesPerSec;
            if (entry != null && typeof entry === 'object' && entry.coldStart != null) {
                currentBytesPerSec = entry.coldStart;
            }

            const targetBytesPerSec = actualBytesPerSec * 0.8;
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

    finalizeStageVelocity() {
        if (this.currentStage == null || this.targetValue < 1 || this.currentFileSize <= 0) {
            return;
        }

        const now = performance.now();
        const elapsed = (now - this.stageStartTime) / 1000;
        if (elapsed <= 0) {
            return;
        }

        const stage = this.currentStage;
        this.currentStage = null;

        const velocityPctPerSec = this.targetValue / elapsed;
        const overallBytesPerSec = (velocityPctPerSec / 100) * this.currentFileSize;

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
            const targetBytesPerSec = overallBytesPerSec * 0.8;
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

    currentValue: 0,
    targetValue: 0,
    velocity: 0,
    acceleration: 0,
    displayVelocity: 0,
    lastUpdateTime: 0,
    lastFrameTime: 0,
    stageStartTime: 0,
    speedFactor: 1,
    animationId: null,
    progressBar: null,
    ghostBar: null,
    currentStage: null,
    awaitingFirstUpdate: false,
    isRestoring: false,
    sampleCount: 0,
    coldStartLearned: false,

    startWithAssumption(stage, progressBar, fileSize) {
        const wasRestoring = this.isRestoring;
        this.finalizeStageVelocity();
        this.reset();
        if (fileSize != null) {
            this.currentFileSize = fileSize;
        }
        const now = performance.now();
        this.currentStage = stage;
        this.lastUpdateTime = now;
        this.stageStartTime = now;

        if (!PROGRESS_SMOOTHING) {
            this.progressBar = progressBar;

            return;
        }

        if (wasRestoring) {
            this.progressBar = progressBar;
            this.awaitingFirstUpdate = true;
            this.isRestoring = true;
        } else {
            const { velocity: learnedInitialVelocity, wasClamped } = this.getLearnedInitialVelocity(stage);
            this.targetValue = learnedInitialVelocity;
            this.velocity = learnedInitialVelocity;
            this.displayVelocity = learnedInitialVelocity;
            this.awaitingFirstUpdate = true;
            const bytesPerSec = (learnedInitialVelocity / 100) * this.currentFileSize;
            const clampedSuffix = wasClamped ? ' (clamped)' : '';
            console.log(`[${stage}] Initial velocity: ${bytesPerSec.formatBytes(2, '/s')}${clampedSuffix}`);
            this.start(progressBar);
        }
    },

    setTarget(value, stage) {
        if (!PROGRESS_SMOOTHING) {
            if (this.progressBar) {
                this.progressBar.style.width = value + '%';
            }

            return;
        }

        if (this.awaitingFirstUpdate && stage === this.currentStage) {
            const now = performance.now();
            const dt = (now - this.lastUpdateTime) / 1000;

            if (this.isRestoring) {
                const { velocity: learnedVelocity } = this.getLearnedInitialVelocity(stage, { preferOverall: true });
                this.currentValue = value;
                this.targetValue = value;
                this.velocity = learnedVelocity;
                this.displayVelocity = learnedVelocity;
                this.lastUpdateTime = now;
                this.awaitingFirstUpdate = false;
                this.isRestoring = false;
                if (this.progressBar) {
                    this.progressBar.style.width = value + '%';
                    this.start(this.progressBar);
                }

                return;
            }

            this.sampleCount++;

            if (!this.coldStartLearned) {
                const elapsed = (now - this.stageStartTime) / 1000;
                if (elapsed >= 5 && this.sampleCount >= 2) {
                    const avgVelocity = value / elapsed;
                    if (this.updateLearnedInitialVelocity(stage, avgVelocity)) {
                        this.coldStartLearned = true;
                    }
                }
            }

            // Update velocity/target tracking (same as before)
            if (dt > 0.05) {
                const totalElapsed = (now - this.stageStartTime) / 1000;
                const realVelocity = totalElapsed > 0 ? value / totalElapsed : this.velocity;
                this.velocity = realVelocity;
                this.displayVelocity = realVelocity;
                this.targetValue = value;
                this.acceleration = 0;
                this.lastUpdateTime = now;
            } else {
                this.targetValue = value;
            }

            // Stop awaiting after cold-start is learned (accumulation complete)
            if (this.coldStartLearned) {
                this.awaitingFirstUpdate = false;
            }

            return;
        }

        const now = performance.now();
        const dt = (now - this.lastUpdateTime) / 1000;

        if (dt > 0 && dt < 5) {
            const instantVelocity = (value - this.targetValue) / dt;
            const prevVelocity = this.velocity;
            this.velocity = this.velocity * 0.5 + instantVelocity * 0.5;

            const instantAcceleration = (this.velocity - prevVelocity) / dt;
            this.acceleration = this.acceleration * 0.5 + instantAcceleration * 0.5;

            if (SHOW_GHOST) {
                const bytesPerSec = (instantVelocity / 100) * this.currentFileSize;
                const deltaBytesPerSec = ((this.displayVelocity - instantVelocity) / 100) * this.currentFileSize;
                const deltaSign = deltaBytesPerSec >= 0 ? '+' : '';
                console.log(`[${stage}] Instant velocity: ${bytesPerSec.formatBytes(2, '/s')} (${deltaSign}${deltaBytesPerSec.formatBytes(2, '/s')})`);
            }
        }

        this.targetValue = value;
        this.lastUpdateTime = now;

        if (this.ghostBar) {
            this.ghostBar.style.width = value + '%';
        }
    },

    start(progressBar) {
        this.progressBar = progressBar;
        if (!PROGRESS_SMOOTHING) {
            return;
        }
        if (SHOW_GHOST && progressBar) {
            const ghostId = progressBar.id + '-ghost';
            this.ghostBar = document.getElementById(ghostId);
            if (this.ghostBar) {
                this.ghostBar.parentElement.classList.add('visible');
            }
        }
        if (!this.animationId) {
            this.lastFrameTime = performance.now();
            this.animate();
        }
    },

    stop() {
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    },

    reset() {
        this.finalizeStageVelocity();
        this.stop();
        this.currentValue = 0;
        this.targetValue = 0;
        this.velocity = 0;
        this.acceleration = 0;
        this.displayVelocity = 0;
        this.lastUpdateTime = 0;
        this.stageStartTime = 0;
        this.speedFactor = 1;
        this.currentStage = null;
        this.awaitingFirstUpdate = false;
        this.sampleCount = 0;
        this.coldStartLearned = false;
        if (this.ghostBar) {
            this.ghostBar.parentElement.classList.remove('visible');
            this.ghostBar = null;
        }
        // isRestoring survives reset() - cleared in setTarget() after use
    },

    setRestoring() {
        this.isRestoring = true;
    },

    animate() {
        const now = performance.now();
        const rawDt = (now - this.lastFrameTime) / 1000;
        this.lastFrameTime = now;
        const dt = Math.min(rawDt, 0.1);

        const timeSinceUpdate = (now - this.lastUpdateTime) / 1000;
        const estimatedActual = this.targetValue + this.velocity * timeSinceUpdate;

        if (rawDt > 1) {
            this.currentValue = Math.max(this.currentValue, Math.min(estimatedActual, this.targetValue));
            this.displayVelocity = this.velocity;
            this.speedFactor = 1;
        }

        const velocityEaseRate = 3;
        this.displayVelocity += (this.velocity - this.displayVelocity) * Math.min(1, velocityEaseRate * dt);

        const velocityGap = this.velocity - this.displayVelocity;
        const baseProjectedLag = velocityGap / velocityEaseRate;

        const accelAdjustment = Math.max(-0.3, Math.min(0.3, this.acceleration * 0.05));
        const projectedLag = baseProjectedLag * (1 - accelAdjustment);

        const compensatedTarget = estimatedActual + projectedLag;
        const error = compensatedTarget - this.currentValue;

        const progressFactor = Math.max(0, (this.targetValue - 67) / 33);
        const maxSpeedAdjust = 0.3 + progressFactor * 0.7;
        const targetSpeedFactor = 1 + Math.max(-maxSpeedAdjust, Math.min(maxSpeedAdjust, error * 0.3));
        const easingRate = 3 + progressFactor * 3;
        this.speedFactor += (targetSpeedFactor - this.speedFactor) * Math.min(1, easingRate * dt);

        this.currentValue += this.displayVelocity * dt * this.speedFactor;
        this.currentValue = Math.max(0, Math.min(100, this.currentValue));

        if (this.progressBar) {
            this.progressBar.style.width = this.currentValue + '%';
        }
        if (this.ghostBar) {
            this.ghostBar.style.width = this.targetValue + '%';
        }

        if (this.currentValue < 99.9) {
            this.animationId = requestAnimationFrame(() => this.animate());
        } else {
            this.animationId = null;
        }
    }
};
