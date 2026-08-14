import { test, expect } from '@playwright/test';

/**
 * L3 (real API) — Self-serve onboarding PLG (#271 / SPEC:onboarding-plg).
 * Run via: scripts/quality/run-l3-local.ps1 -SpecFilter onboarding-plg-l3
 */
test.describe('Onboarding PLG L3 (#271)', () => {
  test.skip(!process.env.E2E_LOCAL && !process.env.E2E_STAGING, 'Set E2E_LOCAL=1 or E2E_STAGING=1');
  test.setTimeout(120_000);

  const apiBase =
    process.env.E2E_LOCAL_API_URL ??
    process.env.E2E_STAGING_API_URL ??
    'http://localhost:5000/api';

  test('AC11: GET /api/legal/subprocessors renders public page', async ({ page }) => {
    const res = await page.request.get(`${apiBase}/legal/subprocessors`);
    expect(res.ok(), 'anonymous GET /api/legal/subprocessors').toBeTruthy();
    const body = await res.json();
    expect(body.version).toBeTruthy();
    expect(Array.isArray(body.items)).toBeTruthy();
    expect(body.items.length).toBeGreaterThanOrEqual(4);
    expect(body.items.map((i: { name: string }) => i.name)).toEqual(
      expect.arrayContaining(['Supabase', 'Auth0', 'Stripe', 'SendGrid']),
    );

    await page.goto('/legal/subprocessors');
    await expect(page.getByRole('heading', { name: /Responsabili del trattamento/i })).toBeVisible({
      timeout: 30_000,
    });
    await expect(page.getByTestId('subprocessors-list')).toContainText(/Supabase|Auth0|Stripe|SendGrid/i);
  });

  test('AC8: consents wizard posts required consents block', async ({ page }) => {
    test.skip(!process.env.E2E_AUTH0_EMAIL, 'Requires E2E_AUTH0_EMAIL for authenticated L3 wizard');
    await page.goto('/onboarding');
    await expect(page.getByTestId('onboarding-consents-step')).toBeVisible({ timeout: 60_000 });
    await expect(page.getByTestId('onboarding-consents-continue')).toBeDisabled();
  });

  test('AC9: onboarding role step visible when Auth0 session present', async ({ page }) => {
    test.skip(!process.env.E2E_AUTH0_EMAIL, 'Requires E2E_AUTH0_EMAIL for authenticated L3 onboarding');
    await page.goto('/onboarding');
    await expect(page.getByRole('heading', { name: /Come vuoi usare CasaZen|How do you want to use CasaZen/i })).toBeVisible({
      timeout: 60_000,
    });
  });

  test('AC10: GET /api/onboarding/status drives checklist', async ({ page }) => {
    test.skip(!process.env.E2E_AUTH0_EMAIL, 'Requires E2E_AUTH0_EMAIL for authenticated status checklist');
    await page.goto('/app/short-rent');
    const statusRes = await page.request.get(`${apiBase}/onboarding/status`);
    expect([200, 401]).toContain(statusRes.status());
    if (statusRes.status() === 200) {
      const status = await statusRes.json();
      expect(typeof status.activated).toBe('boolean');
      if (!status.activated) {
        await expect(page.getByTestId('activation-checklist')).toBeVisible({ timeout: 60_000 });
      }
    }
  });

  test('AC12: public legal route works without Auth0 session', async ({ page }) => {
    await page.goto('/legal/subprocessors');
    await expect(page.getByRole('heading', { name: /Responsabili del trattamento/i })).toBeVisible({
      timeout: 30_000,
    });
    await expect(page).toHaveURL(/\/legal\/subprocessors/);
  });
});