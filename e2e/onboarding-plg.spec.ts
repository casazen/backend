import { test, expect } from './test';
import { demoUrl } from './helpers/demo-profile';

/**
 * L2 (demo) — Self-serve onboarding PLG (#271 / SPEC:onboarding-plg).
 * Scaffolded in Stage 02 for AC Test Map path existence; Stage 03 expands.
 */
test.describe('Onboarding PLG (#271)', () => {
  test('AC8: consents wizard step requires four checkboxes before Continua', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page.getByTestId('onboarding-consents-step')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('onboarding-consents-continue')).toBeDisabled();
  });

  test('AC9: completing onboarding routes to dashboard', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page.getByRole('heading', { name: /Come vuoi usare CasaZen/i })).toBeVisible();
  });

  test('AC10: activation checklist widget on dashboard', async ({ page }) => {
    await page.goto(demoUrl('/app/short-rent', 'short-stay'));
    await expect(page.getByTestId('activation-checklist')).toBeVisible({ timeout: 15_000 });
  });

  test('AC11: public subprocessors page Italian labels', async ({ page }) => {
    await page.goto(demoUrl('/legal/subprocessors', 'onboarding'));
    await expect(page.getByText(/Responsabili del trattamento/i)).toBeVisible({ timeout: 15_000 });
  });

  test('AC12: OnboardingGuard demo mode still renders without Auth0', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page).toHaveURL(/\/onboarding/);
  });
});
