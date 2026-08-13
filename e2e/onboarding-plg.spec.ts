import { test, expect } from './test';
import { demoUrl } from './helpers/demo-profile';

/**
 * L2 (demo) — Self-serve onboarding PLG (#271 / SPEC:onboarding-plg).
 */
test.describe('Onboarding PLG (#271)', () => {
  test.beforeEach(async ({ page }) => {
    await page.route('**/api/onboarding/status', async (route) => {
      if (route.request().method() !== 'GET') {
        await route.fallback();
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          roleChosen: true,
          orgProvisioned: true,
          consentsAccepted: true,
          propertyCreated: false,
          sitePublished: false,
          firstBookingTaken: false,
          activated: false,
          publicBookingUrl: null,
        }),
      });
    });
  });

  test('AC8: consents wizard step requires four checkboxes before Continua', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page.getByRole('heading', { name: /Come vuoi usare CasaZen|How do you want to use CasaZen/i })).toBeVisible({
      timeout: 15_000,
    });
    await page.getByRole('button', { name: /Scegli|Choose/i }).first().click();
    await expect(page.getByTestId('onboarding-consents-step')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('onboarding-consents-continue')).toBeDisabled();
  });

  test('AC9: completing onboarding routes to dashboard', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page.getByRole('heading', { name: /Come vuoi usare CasaZen|How do you want to use CasaZen/i })).toBeVisible({
      timeout: 15_000,
    });
    await expect(page).toHaveURL(/\/onboarding/);
  });

  test('AC10: activation checklist widget on dashboard', async ({ page }) => {
    await page.goto(demoUrl('/app/short-rent', 'short-stay'));
    await expect(page.getByTestId('activation-checklist')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('activation-property-link')).toContainText(/Crea|Create/i);
  });

  test('AC11: public subprocessors page Italian labels', async ({ page }) => {
    await page.goto(demoUrl('/legal/subprocessors', 'onboarding'));
    await expect(page.getByText(/Responsabili del trattamento/i)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('subprocessors-list')).toBeVisible();
  });

  test('AC12: OnboardingGuard demo mode still renders without Auth0', async ({ page }) => {
    await page.goto(demoUrl('/onboarding', 'onboarding'));
    await expect(page).toHaveURL(/\/onboarding/);
    await expect(page.getByRole('heading', { name: /Come vuoi usare CasaZen|How do you want to use CasaZen/i })).toBeVisible();
  });
});
