// Makes the documentation screenshots in src/assets/screenshots/ from running copies of the site:
//
//   npm run screenshots
//
// BASE_URL (default http://localhost:5183): an instance with the demo library, reachable from this machine and with
//   TRUSTED_NETWORKS including localhost. The script creates a demo admin there on first use (random password, saved
//   in DEMO_ACCOUNT, by default ../demo-data/docs-account.json, which is git-ignored) and two more users.
// SETUP_URL (optional, e.g. http://localhost:5184): an empty instance without TRUSTED_NETWORKS, for the setup page
//   with its setup code. Skipped when not set.
//
// Run the instances with the demo library, never with a real one: the screenshots are published.
import { chromium } from 'playwright';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const base = process.env.BASE_URL ?? 'http://localhost:5183';
const setupUrl = process.env.SETUP_URL;
const accountFile = process.env.DEMO_ACCOUNT ?? fileURLToPath(new URL('../../demo-data/docs-account.json', import.meta.url));
const out = fileURLToPath(new URL('../src/assets/screenshots/', import.meta.url));
mkdirSync(out, { recursive: true });

const desktop = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 };
const wide = { viewport: { width: 2560, height: 1440 }, deviceScaleFactor: 1 };
const phone = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true };

// WebGL for the 3D viewer without a GPU.
const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });

function newPassword() {
  return randomBytes(18).toString('base64url');
}

async function newContext(options, colorScheme, storageState) {
  return browser.newContext({ ...options, colorScheme, reducedMotion: 'reduce', storageState });
}

/** Logs in as the demo admin (creating it first on a fresh demo instance) and returns the session cookies. */
async function signIn() {
  const context = await newContext(desktop, 'light');
  const page = await context.newPage();
  await page.goto(`${base}/setup`);
  if (page.url().includes('/setup') && (await page.locator('input[name="UserName"]').count()) > 0) {
    const account = { userName: 'alex', password: newPassword() };
    await page.fill('input[name="UserName"]', account.userName);
    await page.fill('input[name="Password"]', account.password);
    await page.fill('input[name="RepeatPassword"]', account.password);
    await Promise.all([page.waitForURL('**/browse'), page.click('button[type="submit"]')]);
    mkdirSync(dirname(accountFile), { recursive: true });
    writeFileSync(accountFile, JSON.stringify(account));
    for (const userName of ['sam', 'robin']) {
      await addUser(page, userName);
    }
  } else {
    if (!existsSync(accountFile)) throw new Error(`The demo instance has accounts, but ${accountFile} is missing.`);
    const account = JSON.parse(readFileSync(accountFile, 'utf8'));
    await page.goto(`${base}/login`);
    await page.fill('input[name="UserName"]', account.userName);
    await page.fill('input[name="Password"]', account.password);
    await Promise.all([page.waitForURL('**/browse**'), page.click('button[type="submit"]')]);
  }

  const state = await context.storageState();
  await context.close();
  return state;
}

async function addUser(page, userName) {
  await page.goto(`${base}/users`);
  const form = page.locator('form.add-user');
  const password = newPassword();
  await form.locator('input[name="userName"]').fill(userName);
  await form.locator('input[name="password"]').fill(password);
  await form.locator('input[name="repeatPassword"]').fill(password);
  await form.locator('button[type="submit"]').click();
  await page.waitForLoadState('networkidle');
}

/** Waits until the images on screen have loaded (lazy ones further down don't matter). */
async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.waitForFunction(() =>
    [...document.images]
      .filter((image) => {
        const box = image.getBoundingClientRect();
        // On screen in both directions: thumbnails scrolled out of a strip stay unloaded (lazy).
        return box.bottom > 0 && box.top < innerHeight && box.right > 0 && box.left < innerWidth && box.width > 0;
      })
      .every((image) => image.complete && !image.classList.contains('img-pending')));
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(300);
}

async function shot(name, path, { device = desktop, scheme = 'light', state, url = base, before, fullPage = false } = {}) {
  const context = await newContext(device, scheme, state);
  const page = await context.newPage();
  await page.goto(`${url}${path}`);
  await settle(page);
  if (before) await before(page);
  await page.screenshot({ path: `${out}${name}.jpg`, type: 'jpeg', quality: 85, fullPage });
  await context.close();
  console.log(`  ${name}.jpg`);
}

console.log(`Screenshots from ${base}`);
const state = await signIn();
const storageBox = '/file/Workshop/Gridfinity/Small%20Parts%20Storage%20Box.3mf';

await shot('hero-light', '/browse/Workshop/Gridfinity', { state });
await shot('hero-dark', '/browse/Workshop/Gridfinity', { state, scheme: 'dark' });
await shot('browse', '/browse', { state });
await shot('file', storageBox, { state });
await shot('file-wide', storageBox, { state, device: wide, scheme: 'dark' });
await shot('viewer', storageBox, {
  state,
  before: async (page) => {
    await page.click('#toggle-3d');
    await page.waitForFunction(() => document.querySelector('#stage-3d canvas') && !document.querySelector('#stage-status')?.textContent);
    await page.waitForTimeout(1500);
  },
});
await shot('subfolders', '/browse/Workshop?all=1', { state });
await shot('search', '/search?q=box', { state });
await shot('recent', '/recent', { state, scheme: 'dark' });
await shot('account', '/account', { state });
await shot('users', '/users', { state });
await shot('not-found', '/browse/Workshop/Old%20Projects', { state });
await shot('phone-browse', '/browse/Workshop/Gridfinity', { state, device: phone });
await shot('phone-file', storageBox, { state, device: phone });
await shot('phone-menu', '/browse', {
  state,
  device: phone,
  scheme: 'dark',
  before: async (page) => {
    await page.click('.menu-toggle');
    await page.waitForTimeout(300);
  },
});
if (setupUrl) {
  await shot('setup-code', '/setup', { url: setupUrl, scheme: 'dark' });
}

await browser.close();
console.log('Done.');
