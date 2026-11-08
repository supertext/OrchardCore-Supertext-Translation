#!/usr/bin/env node
/**
 * Records the install and configuration movie (MP4 only, no GIF) from a FRESH local demo whose
 * module talks to the stand-in API (node stand-in.mjs), like screenshots.mjs. An administrator
 * installs the package (terminal: dotnet pack, dotnet add package, dotnet run), enables the
 * feature on the Features page, stores an API key in Settings → Supertext, sees the key check,
 * looks at the language mapping and shows where editors find "Translate with Supertext".
 * See docs/DEVELOPER.md -> Demo movie.
 *
 *   BASE_URL=http://127.0.0.1:8096 DEMO_ADMIN_EMAIL=… DEMO_ADMIN_PASSWORD=… npm run movie:install
 *
 * The demo must run WITHOUT SUPERTEXT_API_KEY (the script types the made-up key
 * st-demo-0000-0000-0000, which the stand-in accepts) and with SUPERTEXT_REPORT_URL pointing to
 * the stand-in. Before recording, the script disables the feature (not recorded), so it can be
 * enabled on camera. MOVIE_VOICE=0 makes a silent movie. MOVIE_OUT: where the MP4 goes
 * (default Tests/Docs/movie/out/OrchardCore-Supertext-Translation-install.mp4, git-ignored).
 * Needs ffmpeg; the voice needs `pip install piper-tts`.
 */
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { MovieKit } from './movie/movie-kit.mjs';

const B = (process.env.BASE_URL || 'http://127.0.0.1:8096').replace(/\/$/, '');
const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const out = process.env.MOVIE_OUT ?? here('movie/out/OrchardCore-Supertext-Translation-install.mp4');
const need = (name) => process.env[name] ?? (() => { throw new Error(`Set ${name}`); })();
const FAKE_KEY = 'st-demo-0000-0000-0000';

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1, locale: 'en-US' });
// The endpoint hint names the (local) stand-in URL: never show it.
await page.addInitScript(() => document.addEventListener('DOMContentLoaded', () =>
  document.querySelectorAll('.supertext-endpoint-env').forEach((e) => e.remove())));

// Sign in as the demo administrator (not recorded): configuration is an administrator's task.
await page.goto(`${B}/Login`);
await page.fill('input[name$="UserName"]', need('DEMO_ADMIN_EMAIL'));
await page.fill('input[type=password]', need('DEMO_ADMIN_PASSWORD'));
await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
if (page.url().includes('/Login')) throw new Error('Login failed');

// Disable the feature (not recorded), so the movie can enable it like after a fresh install.
await page.goto(`${B}/Admin/Features`);
const disable = page.locator('#btn-disable-Supertext_OrchardCore_Translation');
if (await disable.count()) {
  await disable.click();
  await page.locator('.modal.show button', { hasText: /^\s*Yes\s*$/ }).click();
  await page.waitForLoadState('load');
  await page.locator('#btn-enable-Supertext_OrchardCore_Translation, a[href$="/Supertext.OrchardCore.Translation/Enable"]').first().waitFor();
}

const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'orchardcore-movie-install-'));
await page.goto('about:blank');
const kit = await MovieKit.start(page, { workDir, assets: here('movie/assets'), voiceCache: here('movie/voice-cache') });

/** More commands in the terminal kit.terminal() opened (same look; kit.terminal() would start a new one). */
async function typeMore(lines) {
  await page.evaluate(() => document.querySelector('#x > .p.c:last-child')?.remove());
  for (const l of lines) {
    if (l.cmd !== undefined) {
      await page.evaluate(() => { const x = document.getElementById('x');
        const p = document.createElement('span'); p.className = 'p'; p.textContent = '$ '; x.appendChild(p);
        const c = document.createElement('span'); c.className = 'c'; x.appendChild(c); });
      for (const ch of l.cmd) { await page.evaluate((ch) => { document.querySelector('#x .c').textContent += ch; }, ch); await page.waitForTimeout(38); }
      await page.waitForTimeout(450);
      await page.evaluate(() => { document.querySelector('#x .c').classList.remove('c'); document.getElementById('x').append('\n'); });
    } else {
      await page.evaluate((o) => { const s = document.createElement('span'); s.className = 'o'; s.textContent = o + '\n'; document.getElementById('x').appendChild(s); }, l.out ?? '');
      await page.waitForTimeout(l.wait ?? 180);
    }
  }
  await page.evaluate(() => { const c = document.createElement('span'); c.className = 'p c'; c.textContent = '$ '; document.getElementById('x').appendChild(c); });
}

// 1. Terminal: the commands from docs/INSTALLATION.md (output shortened).
await kit.say("The module is a NuGet package. It isn't on nuget dot org yet, so first build the package from the Supertext repository.", {
  caption: 'Build the package: <b>dotnet pack</b>',
  during: () => kit.terminal({ title: 'my-site', lines: [
    { cmd: 'dotnet pack OrchardCore-Supertext-Translation/src/Supertext.OrchardCore.Translation -c Release -o ./packages' },
    { out: '  Supertext.OrchardCore.Translation -> …/bin/Release/net10.0/Supertext.OrchardCore.Translation.dll' },
    { out: "  Successfully created package './packages/Supertext.OrchardCore.Translation.0.1.0.nupkg'." },
  ] }),
});
await kit.say('Then add it to your Orchard Core web project.', {
  caption: 'Add it to your site: <b>dotnet add package</b>',
  during: () => typeMore([
    { cmd: 'dotnet add package Supertext.OrchardCore.Translation --source ./packages' },
    { out: "info : Adding PackageReference for package 'Supertext.OrchardCore.Translation' into project 'my-site.csproj'." },
    { out: "info : PackageReference for package 'Supertext.OrchardCore.Translation' version '0.1.0' added to file 'my-site.csproj'." },
  ]),
});
await kit.say('Build and restart the site.', {
  caption: 'Build and restart the site',
  during: () => typeMore([
    { cmd: 'dotnet run -c Release' },
    { out: 'Building...' },
    { out: 'info: Microsoft.Hosting.Lifetime[0]' },
    { out: '      Application started. Press Ctrl+C to shut down.' },
  ]),
});
await kit.hold(300);

// 2. Features: enable Supertext Translation (loading time cut).
kit.pause();
await page.goto(`${B}/Admin/Features`);
await page.locator('#search-box').waitFor();
await kit.sync();
kit.resume();
await kit.say('Now sign in to the Orchard Core admin, and open Tools, Features.', {
  caption: 'In the admin: <b>Tools → Features</b>',
  during: async () => { await kit.hold(500); await kit.moveTo(page.locator('#search-box')); },
});
const enable = page.locator('a[href$="/Supertext.OrchardCore.Translation/Enable"]').first();
await kit.say('Search for Supertext, and click Enable next to Supertext Translation.', {
  caption: 'Search <b>Supertext</b>, click <b>Enable</b>',
  during: async () => {
    await kit.type(page.locator('#search-box'), 'Supertext');
    await kit.hold(500);
    await kit.moveTo(enable);
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForNavigation(), enable.click()]);
    await page.locator('#btn-disable-Supertext_OrchardCore_Translation').waitFor();
    await kit.sync();
  },
});
await kit.say('The feature is on now. Orchard Core enables Content Localization along with it.', {
  caption: '<b>Supertext Translation</b> is enabled',
  during: async () => {
    await kit.type(page.locator('#search-box'), 'Supertext');
    await kit.moveTo(page.locator('#btn-disable-Supertext_OrchardCore_Translation'));
  },
});

// 3. Settings → Supertext: the API key.
await kit.say('The settings are under Settings, Supertext.', {
  caption: 'Open <b>Settings → Supertext</b>',
  during: async () => {
    const settings = page.locator('button.settings.item-label').first();
    await kit.click(settings);
    await kit.hold(400);
    const supertext = page.locator('a[href$="/Admin/Settings/supertext"]').first();
    await kit.moveTo(supertext);
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForURL(/Settings\/supertext/), supertext.click()]);
    await page.waitForLoadState('load');
    await kit.sync();
  },
});
const key = page.locator('input[name$="ApiKey"]');
await kit.say('Generate your API key at supertext.com, under Integrations, A P I. No account yet? Create one at supertext.com.', {
  caption: 'API key: <b>supertext.com → Integrations → API</b><br>No account yet? Create one at <b>supertext.com</b>',
  during: async () => { await kit.moveTo(page.locator('.supertext-key-help')); await kit.hold(600); },
});
const save = page.locator('.ta-content button[type=submit].btn-primary').first();
await kit.say('Paste the key into the API key field, and click Save. The key is stored encrypted.', {
  caption: 'Paste it into <b>API key</b>, click <b>Save</b>',
  during: async () => {
    await kit.type(key, FAKE_KEY);
    await kit.hold(300);
    await kit.moveTo(save);
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForNavigation(), save.click()]);
    await page.locator('.supertext-key-check .alert').waitFor();
    await kit.sync();
  },
});
const check = page.locator('.supertext-key-check .alert').first();
await kit.say('The API key check shows that the key works.', {
  caption: '<b>API key check</b>: the key works',
  during: async () => {
    await check.evaluate((el) => window.scrollBy({ top: el.getBoundingClientRect().top - 260, behavior: 'smooth' }));
    await kit.hold(700);
    await kit.moveTo(check);
  },
});

// 4. Language mapping.
const mapping = page.locator('.supertext-language-mapping');
const german = mapping.locator('select[name$=".Code"]').nth(1);
await kit.say("Below, the language mapping lists your site's cultures, each with its Supertext language, already preselected.", {
  caption: '<b>Language mapping</b>: a Supertext language per culture',
  during: async () => {
    await mapping.evaluate((el) => window.scrollBy({ top: el.getBoundingClientRect().top - 90, behavior: 'smooth' }));
    await kit.hold(900);
    await kit.moveTo(german);
  },
});
await kit.say('Pick another variant if you need one. We keep German, Switzerland.', {
  caption: 'German culture → <b>German (Switzerland) (de-CH)</b>',
  during: async () => {
    await kit.click(german);
    await german.selectOption('de-DE');
    await kit.hold(900);
    await german.selectOption('de-CH');
    await kit.hold(500);
  },
});
const review = mapping.locator('input[type=checkbox][name$=".Review"]').nth(1);
await kit.say('Translation review can be switched on per culture. We leave it off.', {
  caption: '<b>Translation review</b>: optional, per culture (left off)',
  during: async () => { await kit.hold(300); await kit.moveTo(review); },
});
await kit.say('Save the settings once more.', {
  caption: 'Click <b>Save</b>',
  during: async () => {
    await kit.moveTo(save);
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForNavigation(), save.click()]);
    await page.waitForLoadState('load');
    await kit.sync();
  },
});

// 5. Where editors find it.
kit.pause();
await page.goto(`${B}/Admin/Contents/ContentItems`);
await page.locator('a.supertext-translate').first().waitFor();
await kit.sync();
kit.resume();
await kit.say("That's it. Editors now find Translate with Supertext next to every item in the content list, and in the editor.", {
  caption: 'Done: <b>Translate with Supertext</b> next to every item',
  during: async () => {
    const button = page.locator('li.list-group-item', { hasText: 'Translating with Supertext' }).locator('a.supertext-translate');
    await kit.hold(400);
    await kit.moveTo(button);
    await button.hover();
  },
  after: 300,
});
await kit.caption('');

fs.mkdirSync(path.dirname(out), { recursive: true });
await kit.finish(out, { system: 'Orchard Core', repo: 'OrchardCore-Supertext-Translation', kind: 'install' });
await browser.close();
fs.rmSync(workDir, { recursive: true, force: true });
console.log(`Saved ${out}`);
