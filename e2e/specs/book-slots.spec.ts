import { test, expect } from '@playwright/test';

test('add a connector, book slots, see the token, cancel it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `DRV-${run}`;

  await page.goto('/');
  await page.getByLabel('Connector code').fill(code);
  await page.getByLabel('Slots on offer').fill('8');
  await page.getByRole('button', { name: 'Add connector' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Book a charger at ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('8');

  await page.getByLabel('Driver reference').fill(reference);
  await page.getByLabel('Slots', { exact: true }).fill('3');
  await page.getByRole('button', { name: 'Book slots' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Booked', { timeout: 20_000 });
  await expect(row.getByTestId('token')).toHaveText(/CT-[0-9A-F]{12}/);
  await expect(remaining).toHaveText('5');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Booked');
  await expect(page.getByTestId('selected-remaining')).toHaveText('5');

  await reloaded.getByRole('button', { name: 'Cancel' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Cancelled');
  await expect(page.getByTestId('selected-remaining')).toHaveText('8');
  // The issued token is not rolled back.
  await expect(reloaded.getByTestId('token')).toHaveText(/CT-[0-9A-F]{12}/);
  const reservations = await page.request.get(`/api/reservations?connectorCode=${code}`);
  const [reservation] = await reservations.json();
  const token = await page.request.get(`/api/tokens/${reservation.id}`);
  expect((await token.json()).status).toBe('Issued');
});
