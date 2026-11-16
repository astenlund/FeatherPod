import { defineConfig, devices } from '@playwright/test';

// Browser tests of the push page through its real push.js startup (run via `npm run test:browser`). They live under
// tests/browser/ as *.spec.js so the Node unit-test glob of `npm test` (tests/**/*.test.js) never picks them up.
export default defineConfig({
    testDir: 'tests/browser',
    testMatch: '**/*.spec.js',
    fullyParallel: true,
    reporter: 'list',
    // Keep failure artifacts in the git-ignored scratch folder rather than a test-results/ folder at the repo root
    outputDir: '.tmp/playwright-results',
    use: {
        ...devices['Desktop Chrome'],
        // The fixture page registers no service worker, but block one regardless: a worker's own fetches bypass route
        // interception, which is how the tests stand in for the API.
        serviceWorkers: 'block',
    },
});
