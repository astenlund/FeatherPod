const FEED_ID = '{{FEED_ID}}';
const ALLOWED_EXTENSIONS = ['.mp3', '.m4a', '.wav', '.ogg', '.flac', '.aac'];
let apiKey = null;
const states = ['no-key', 'ready', 'uploading', 'normalizing', 'success', 'error'];
const JOB_STORAGE_KEY = 'featherpod_job_' + FEED_ID;

Number.prototype.sigDig = function(minSigDigs) {
    if (this.valueOf() === 0) return '0';
    const magnitude = Math.floor(Math.log10(Math.abs(this)));
    const decimals = Math.max(0, minSigDigs - 1 - magnitude);
    return this.toFixed(decimals);
};

/** @param {string} stateName */
function showState(stateName) {
    states.forEach(s => document.getElementById(s).style.display = s === stateName ? 'block' : 'none');
}

/** @param {File} file */
function isValidAudioFile(file) {
    const extension = '.' + file.name.split('.').pop().toLowerCase();
    return ALLOWED_EXTENSIONS.includes(extension);
}

function init() {
    const fragment = window.location.hash.slice(1);
    if (fragment) {
        apiKey = fragment;
        sessionStorage.setItem('featherpod_api_key_' + FEED_ID, apiKey);
        history.replaceState(null, '', window.location.pathname);
    } else {
        const storedKey = sessionStorage.getItem('featherpod_api_key_' + FEED_ID);
        if (storedKey) {
            apiKey = storedKey;
        } else {
            showState('no-key');
            return;
        }
    }

    // Try to restore previous job state (e.g., after page refresh)
    if (restoreJobState()) {
        return;
    }

    showState('ready');
    document.getElementById('select-file').focus();
}

document.getElementById('select-file').addEventListener('click', () => {
    document.getElementById('file-input').click();
});

document.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && document.getElementById('ready').style.display !== 'none') {
        document.getElementById('file-input').click();
    }
});

document.getElementById('upload-another').addEventListener('click', () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    document.getElementById('select-file').focus();
});

document.getElementById('try-another').addEventListener('click', () => {
    clearJobState();
    document.getElementById('file-input').value = '';
    showState('ready');
    document.getElementById('select-file').focus();
});

document.getElementById('file-input').addEventListener('change', async (e) => {
    const file = e.target.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

// Drag and drop support
const dropZone = document.getElementById('drop-zone');

dropZone.addEventListener('dragover', (e) => {
    e.preventDefault();
    dropZone.classList.add('drag-over');
});

dropZone.addEventListener('dragleave', (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
});

dropZone.addEventListener('drop', async (e) => {
    e.preventDefault();
    dropZone.classList.remove('drag-over');
    const file = e.dataTransfer.files[0];
    if (!file) return;
    if (!isValidAudioFile(file)) {
        showError('Unsupported file type. Use MP3, M4A, WAV, OGG, FLAC, or AAC.');
        return;
    }
    await uploadFile(file);
});

/** @param {File} file */
async function uploadFile(file) {
    clearJobState();
    showState('uploading');
    document.getElementById('file-name').textContent = file.name;
    document.getElementById('upload-status').textContent = 'Uploading...';
    const progressBar = document.getElementById('upload-progress');
    const progressContainer = progressBar.parentElement;
    progressContainer.setAttribute('aria-valuenow', '0');
    progressAnimator.startWithAssumption('Uploading', progressBar, file.size);
    const formData = new FormData();
    formData.append('file', file);

    try {
        const response = await new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.upload.addEventListener('progress', (e) => {
                if (e.lengthComputable) {
                    const percent = Math.round((e.loaded / e.total) * 100);
                    progressAnimator.setTarget(percent, 'Uploading');
                    progressContainer.setAttribute('aria-valuenow', percent.toString());
                }
            });
            xhr.onload = () => {
                progressAnimator.reset();
                resolve({ status: xhr.status, body: xhr.responseText });
            };
            xhr.onerror = () => {
                progressAnimator.reset();
                reject(new Error('Network error'));
            };
            xhr.open('POST', '/api/feeds/' + FEED_ID + '/episodes?normalize=true');
            xhr.setRequestHeader('X-API-Key', apiKey);
            xhr.send(formData);
        });

        if (response.status === 201) {
            saveJobState({ status: 'success', fileName: file.name });
            showSuccess(file.name);
        } else if (response.status === 202) {
            const jobResponse = JSON.parse(response.body);
            saveJobState({
                status: 'normalizing',
                jobId: jobResponse.jobId,
                fileName: file.name,
                fileSize: file.size
            });
            monitorNormalizationJob(jobResponse.jobId, file.name, file.size);
        } else if (response.status === 401) {
            showError('Invalid API key');
        } else if (response.status === 403) {
            showError('API key does not have access to this feed');
        } else {
            const error = JSON.parse(response.body);
            showError(error.error || 'Upload failed');
        }
    } catch (err) {
        showError(err.message || 'Upload failed');
    }
}

/** @param {string} fileName */
function showSuccess(fileName) {
    showState('success');
    document.getElementById('ep-filename').textContent = fileName;
    document.getElementById('upload-another').focus();
}

/** @param {string} message */
function showError(message) {
    showState('error');
    document.getElementById('error-message').textContent = message;
    document.getElementById('try-another').focus();
}

/** @param {number} bytes */
function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'kB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

/**
 * @typedef {Object} JobStatus
 * @property {string} status - Queued, Processing, Completed, Failed
 * @property {string} [stage] - Queued, Preparing, Analyzing, Normalizing, Finishing, Completed, Failed
 * @property {number} [progressPercent] - Progress percentage (0-100) for Analyzing, Normalizing stages
 * @property {string} [error]
 */

/**
 * Progress animator with velocity smoothing and continuous interpolation.
 * Uses requestAnimationFrame for smooth 60fps updates between SSE events.
 */
const progressAnimator = {
    // Default initial velocities in bytes/second (used if no learned value exists)
    DEFAULT_INITIAL_VELOCITIES: {
        'Uploading': 1024 * 1024, // 1 MB/s
        'Analyzing': 100 * 1024,  // 100 kB/s
        'Normalizing': 100 * 1024 // 100 kB/s
    },
    LEARNED_INITIAL_VELOCITY_STORAGE_KEY: 'featherpod_learned_initial_velocity',
    currentFileSize: 0,

    /**
     * Get learned initial velocity for a stage from localStorage.
     * Stored as bytes/second, converted to %/second using current file size.
     * @param {string} stage
     * @returns {number} Velocity in %/second
     */
    getLearnedInitialVelocity(stage) {
        let bytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;

        try {
            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            if (stored) {
                const values = JSON.parse(stored);
                if (values[stage] != null) {
                    bytesPerSec = values[stage];
                }
            }
        } catch (e) {
            // Ignore localStorage errors
        }

        if (this.currentFileSize > 0) {
            return (bytesPerSec / this.currentFileSize) * 100;
        }

        return 1; // Fallback if no file size set
    },

    /**
     * Update learned initial velocity for a stage using EMA.
     * Receives velocity in %/second, converts to bytes/second for storage.
     * @param {string} stage
     * @param {number} actualVelocity - The actual velocity in %/second
     * @returns {boolean} True if we learned (velocity was meaningful)
     */
    updateLearnedInitialVelocity(stage, actualVelocity) {
        // Skip learning if no file size or near-zero velocity
        if (this.currentFileSize <= 0 || actualVelocity < 0.1) {
            return false;
        }

        try {
            // Convert %/s to B/s
            const actualBytesPerSec = (actualVelocity / 100) * this.currentFileSize;

            const stored = localStorage.getItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY);
            const values = stored ? JSON.parse(stored) : {};
            const defaultBytesPerSec = this.DEFAULT_INITIAL_VELOCITIES[stage] ?? 100 * 1024;
            const currentBytesPerSec = values[stage] ?? defaultBytesPerSec;

            // Target 80% of actual velocity, asymmetric EMA (fast to decrease, slow to increase)
            const targetBytesPerSec = actualBytesPerSec * 0.8;
            const alpha = targetBytesPerSec < currentBytesPerSec ? 0.8 : 0.2;
            const updatedBytesPerSec = currentBytesPerSec * (1 - alpha) + targetBytesPerSec * alpha;

            values[stage] = updatedBytesPerSec;
            localStorage.setItem(this.LEARNED_INITIAL_VELOCITY_STORAGE_KEY, JSON.stringify(values));

            const currentKbps = (currentBytesPerSec / 1024).sigDig(2);
            const updatedKbps = (updatedBytesPerSec / 1024).sigDig(2);
            const actualKbps = (actualBytesPerSec / 1024).sigDig(2);
            console.log(`[progress] Updating learned initial velocity (kB/s) for ${stage}: ${currentKbps} -> ${updatedKbps} (actual: ${actualKbps})`);

            return true;
        } catch (e) {
            // Ignore localStorage errors
            return false;
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
    currentStage: null,
    awaitingFirstUpdate: false,
    isRestoring: false,

    /**
     * Start animating with learned initial velocity before real data arrives.
     * @param {string} stage - The stage name
     * @param {HTMLElement} progressBar - The progress bar element
     * @param {number} [fileSize] - File size in bytes (sets currentFileSize if provided)
     */
    startWithAssumption(stage, progressBar, fileSize) {
        const wasRestoring = this.isRestoring;
        this.reset();
        if (fileSize != null) {
            this.currentFileSize = fileSize;
        }
        const now = performance.now();
        this.currentStage = stage;
        this.lastUpdateTime = now;
        this.stageStartTime = now;

        if (wasRestoring) {
            this.progressBar = progressBar;
            this.awaitingFirstUpdate = true;
            this.isRestoring = true;
        } else {
            const learnedInitialVelocity = this.getLearnedInitialVelocity(stage);
            this.targetValue = learnedInitialVelocity;
            this.velocity = learnedInitialVelocity;
            this.displayVelocity = learnedInitialVelocity;
            this.awaitingFirstUpdate = true;
            this.start(progressBar);
        }
    },

    /**
     * Set new target from SSE update.
     * @param {number} value - New progress target (0-100)
     * @param {string} stage - Current stage name
     */
    setTarget(value, stage) {
        // Initial calibration phase
        if (this.awaitingFirstUpdate && stage === this.currentStage) {
            const now = performance.now();
            const dt = (now - this.lastUpdateTime) / 1000;

            if (this.isRestoring) {
                const learnedVelocity = this.getLearnedInitialVelocity(stage);
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

            // Recalibrate velocity from real data
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

            // Learn velocity for future assumptions
            const totalElapsed = (now - this.stageStartTime) / 1000;
            const learningVelocity = totalElapsed > 0.05 ? value / totalElapsed : 0;
            if (this.updateLearnedInitialVelocity(stage, learningVelocity)) {
                this.awaitingFirstUpdate = false;
            }

            return;
        }

        // Normal updates (after calibration)
        const now = performance.now();
        const dt = (now - this.lastUpdateTime) / 1000;

        if (dt > 0 && dt < 5) {
            const instantVelocity = (value - this.targetValue) / dt;
            const prevVelocity = this.velocity;
            this.velocity = this.velocity * 0.5 + instantVelocity * 0.5;

            const instantAcceleration = (this.velocity - prevVelocity) / dt;
            this.acceleration = this.acceleration * 0.5 + instantAcceleration * 0.5;
        }

        this.targetValue = value;
        this.lastUpdateTime = now;
    },

    /**
     * Start the animation loop.
     * @param {HTMLElement} progressBar - The progress bar element
     */
    start(progressBar) {
        this.progressBar = progressBar;
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
        // isRestoring survives reset() - cleared in setTarget() after use
    },

    /** Mark that we're restoring after page refresh (snap to actual value). */
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

        // Snap to estimated position if tab was inactive (capped at target to avoid overshooting)
        if (rawDt > 0.5) {
            this.currentValue = Math.max(this.currentValue, Math.min(estimatedActual, this.targetValue));
            this.displayVelocity = this.velocity;
            this.speedFactor = 1;
        }

        // Ease displayVelocity towards actual velocity
        const velocityEaseRate = 3;
        this.displayVelocity += (this.velocity - this.displayVelocity) * Math.min(1, velocityEaseRate * dt);

        // Predict lag from displayVelocity catching up (integral of velocity gap)
        const velocityGap = this.velocity - this.displayVelocity;
        const baseProjectedLag = velocityGap / velocityEaseRate;

        // Adjust lag prediction based on acceleration
        const accelAdjustment = Math.max(-0.3, Math.min(0.3, this.acceleration * 0.05));
        const projectedLag = baseProjectedLag * (1 - accelAdjustment);

        // Target position with lag compensation
        const compensatedTarget = estimatedActual + projectedLag;
        const error = compensatedTarget - this.currentValue;

        // Ease speed factor based on position error
        const targetSpeedFactor = 1 + Math.max(-0.3, Math.min(0.3, error * 0.3));
        this.speedFactor += (targetSpeedFactor - this.speedFactor) * Math.min(1, 3 * dt);

        this.currentValue += this.displayVelocity * dt * this.speedFactor;
        this.currentValue = Math.max(0, Math.min(100, this.currentValue));

        if (this.progressBar) {
            this.progressBar.style.width = this.currentValue + '%';
        }

        if (this.currentValue < 99.9) {
            this.animationId = requestAnimationFrame(() => this.animate());
        } else {
            this.animationId = null;
        }
    }
};

/**
 * Update the normalizing status display and progress bar.
 * @param {JobStatus} job
 */
function updateNormalizingStatus(job) {
    const statusEl = document.getElementById('normalizing-status');
    const progressBar = document.getElementById('normalizing-progress');
    const progressContainer = progressBar.parentElement;

    if (job.stage) {
        const stagesWithProgress = ['Analyzing', 'Normalizing'];
        const isProgressStage = stagesWithProgress.includes(job.stage);

        statusEl.textContent = job.stage + '...';

        if (isProgressStage) {
            progressBar.classList.remove('indeterminate');

            if (progressAnimator.currentStage !== job.stage) {
                progressAnimator.startWithAssumption(job.stage, progressBar);
            }

            if (job.progressPercent != null) {
                progressAnimator.setTarget(job.progressPercent, job.stage);
                progressContainer.setAttribute('aria-valuenow', job.progressPercent.toString());
            }

            progressAnimator.start(progressBar);
        } else {
            progressAnimator.reset();
            progressBar.classList.add('indeterminate');
            progressBar.style.width = '';
            progressContainer.setAttribute('aria-valuenow', '0');
        }
    }
}

/**
 * Monitor normalization job via SSE with polling fallback.
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
function monitorNormalizationJob(jobId, fileName, fileSize) {
    showState('normalizing');
    document.getElementById('normalizing-file-name').textContent = fileName;
    document.getElementById('normalizing-status').textContent = 'Initializing...';

    const progressBar = document.getElementById('normalizing-progress');
    progressBar.classList.add('indeterminate');
    progressBar.style.width = '';
    progressAnimator.reset();
    progressAnimator.currentFileSize = fileSize;

    const sseUrl = '/api/jobs/' + jobId + '/progress';

    if (typeof EventSource === 'undefined') {
        void pollNormalizationJobFallback(jobId, fileName, fileSize);
        return;
    }

    const eventSource = new EventSource(sseUrl);
    let lastStatus = null;
    let connectionEstablished = false;
    let jobFinished = false;

    const connectionTimeout = setTimeout(() => {
        if (!connectionEstablished) {
            eventSource.close();
            void pollNormalizationJobFallback(jobId, fileName, fileSize);
        }
    }, 5000);

    eventSource.onopen = () => {
        connectionEstablished = true;
        clearTimeout(connectionTimeout);
    };

    eventSource.addEventListener('progress', (e) => {
        lastStatus = JSON.parse(e.data);
        updateNormalizingStatus(lastStatus);
    });

    eventSource.addEventListener('done', () => {
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        if (lastStatus?.status === 'Completed') {
            saveJobState({ status: 'success', fileName });
            showSuccess(fileName);
        } else {
            const errorMsg = lastStatus?.error || 'Normalization failed';
            saveJobState({ status: 'error', fileName, error: errorMsg });
            showError(errorMsg);
        }
    });

    // Named 'error' event from server (e.g., job not found)
    // Distinct from onerror which handles connection failures
    eventSource.addEventListener('error', (e) => {
        clearTimeout(connectionTimeout);
        jobFinished = true;
        eventSource.close();
        const data = JSON.parse(e.data);
        saveJobState({ status: 'error', fileName, error: data.error });
        showError(data.error);
    });

    // Connection error (network failure or unexpected disconnect) - fall back to polling
    eventSource.onerror = () => {
        if (jobFinished) {
            return;
        }
        clearTimeout(connectionTimeout);
        eventSource.close();
        void pollNormalizationJobFallback(jobId, fileName, fileSize);
    };
}

/**
 * Poll normalization job status (fallback when SSE unavailable).
 * @param {string} jobId
 * @param {string} fileName
 * @param {number} fileSize - File size in bytes for velocity calculations
 */
async function pollNormalizationJobFallback(jobId, fileName, fileSize) {
    showState('normalizing');
    document.getElementById('normalizing-file-name').textContent = fileName;
    progressAnimator.currentFileSize = fileSize;

    const pollInterval = 2000;

    while (true) {
        try {
            const response = await fetch('/api/jobs/' + jobId, {
                headers: { 'X-API-Key': apiKey }
            });

            if (!response.ok) {
                saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
                showError('Failed to check job status');

                return;
            }

            const job = await response.json();

            if (job.status === 'Completed') {
                saveJobState({ status: 'success', fileName });
                showSuccess(fileName);

                return;
            } else if (job.status === 'Failed') {
                const errorMsg = job.error || 'Normalization failed';
                saveJobState({ status: 'error', fileName, error: errorMsg });
                showError(errorMsg);

                return;
            }

            updateNormalizingStatus(job);

            await new Promise(resolve => setTimeout(resolve, pollInterval));
        } catch (err) {
            saveJobState({ status: 'error', fileName, error: 'Failed to check job status' });
            showError('Failed to check job status');

            return;
        }
    }
}

/** @param {object} state */
function saveJobState(state) {
    sessionStorage.setItem(JOB_STORAGE_KEY, JSON.stringify(state));
}

function clearJobState() {
    sessionStorage.removeItem(JOB_STORAGE_KEY);
}

function restoreJobState() {
    const saved = sessionStorage.getItem(JOB_STORAGE_KEY);
    if (!saved) {
        return false;
    }

    const job = JSON.parse(saved);

    if (job.status === 'success') {
        showSuccess(job.fileName);
        return true;
    } else if (job.status === 'error') {
        showError(job.error);
        return true;
    } else if (job.status === 'normalizing') {
        progressAnimator.setRestoring();
        monitorNormalizationJob(job.jobId, job.fileName, job.fileSize || 0);
        document.getElementById('normalizing-progress').style.width = '0%';
        return true;
    }

    return false;
}

window.addEventListener('DOMContentLoaded', init);
window.addEventListener('hashchange', init);

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && progressAnimator.currentStage) {
        // Tab became visible while progress is being tracked - snap to actual value on next SSE update
        progressAnimator.awaitingFirstUpdate = true;
        progressAnimator.isRestoring = true;
    }
});
