#!/usr/bin/env node
/**
 * Records the demo movie (docs/images/demo.gif and an MP4) from a local demo whose module talks
 * to the stand-in API (node stand-in.mjs, without STAND_IN_PREFIX), like screenshots.mjs. The
 * editor opens the English article "Translating with Supertext", translates it with Supertext
 * into German and opens the German draft. See docs/DEVELOPER.md -> Demo movie.
 *
 *   BASE_URL=http://127.0.0.1:8096 DEMO_EDITOR_EMAIL=… DEMO_EDITOR_PASSWORD=… npm run movie
 *
 * An API key must be stored (Settings → Supertext, e.g. by movie-install.mjs or screenshots.mjs;
 * the stand-in accepts any key) and the article must not be translated yet (fresh demo).
 * Voice: Piper (pip install piper-tts), voice "alba" downloaded once into movie/voice-cache/
 * (git-ignored); MOVIE_VOICE=0 makes a silent movie. MOVIE_OUT: where the MP4 goes (default
 * Tests/Docs/movie/out/OrchardCore-Supertext-Translation.mp4, git-ignored; the MP4 is not
 * committed). Needs ffmpeg.
 */
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { MovieKit } from './movie/movie-kit.mjs';

const B = (process.env.BASE_URL || 'http://127.0.0.1:8096').replace(/\/$/, '');
const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const out = process.env.MOVIE_OUT ?? here('movie/out/OrchardCore-Supertext-Translation.mp4');
const gif = here('../../docs/images/demo.gif');
const need = (name) => process.env[name] ?? (() => { throw new Error(`Set ${name}`); })();

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1, locale: 'en-US' });

// Sign in as the demo editor (not recorded).
await page.goto(`${B}/Login`);
await page.fill('input[name$="UserName"]', need('DEMO_EDITOR_EMAIL'));
await page.fill('input[type=password]', need('DEMO_EDITOR_PASSWORD'));
await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
if (page.url().includes('/Login')) throw new Error('Login failed');

// The English article in the editor.
await page.goto(`${B}/Admin/Contents/ContentItems`);
const row = page.locator('li.list-group-item', { hasText: 'Translating with Supertext' });
const editUrl = await row.locator('a', { hasText: /^\s*Edit\s*$/ }).first().evaluate((a) => a.href);

const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'orchardcore-movie-'));
await page.goto(editUrl);
await page.locator('.ta-content').waitFor();
const kit = await MovieKit.start(page, { workDir, assets: here('movie/assets'), voiceCache: here('movie/voice-cache') });

// 1. The English article.
await kit.say("Here's an article in English, in the Orchard Core admin.", {
  caption: 'An article in English',
  during: async () => { await kit.hold(600); await kit.moveTo(page.locator('input[name="TitlePart.Title"]')); },
});

// 2. Translate with Supertext (bottom of the editor).
const translate = page.locator('a.supertext-translate', { hasText: 'Translate with Supertext' }).first();
await kit.say('At the bottom of the editor, click Translate with Supertext.', {
  caption: 'Click <b>Translate with Supertext</b>',
  during: async () => {
    // Room below the button bar, so the caption doesn't cover the button at the end of the page.
    await page.evaluate(() => { document.querySelector('.ta-content').style.paddingBottom = '260px'; });
    await translate.evaluate((el) => window.scrollBy({ top: el.getBoundingClientRect().top - 420, behavior: 'smooth' }));
    await kit.hold(700);
    await kit.moveTo(translate);
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForURL(/Supertext\/Translate/), translate.click()]);
    await page.locator('table.supertext-targets').waitFor();
    await kit.sync();
  },
});

// 3. Pick German and French.
const box = (culture) => page.locator(`input[name="cultures"][value="${culture}"]`);
await kit.say('Pick the language: tick German.', {
  caption: 'Tick <b>German (Switzerland)</b>',
  during: async () => {
    for (const [culture, want] of [['de-CH', true], ['fr-CH', false], ['it-CH', false]]) {
      if ((await box(culture).isChecked()) !== want) await kit.click(box(culture));
      else if (want) { await kit.moveTo(box(culture)); await kit.hold(200); }
    }
  },
});
const submit = page.locator('button.supertext-submit');
await kit.say('Then click Translate.', { caption: 'Click <b>Translate</b>', during: () => kit.moveTo(submit), after: 0 });

// The stand-in answers at once: send the request only after the waiting state has been seen.
let release;
const released = new Promise((resolve) => (release = resolve));
await page.route(/Supertext\/Translate\//, async (route) => {
  if (route.request().method() === 'POST') await released;
  await route.continue();
});
await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
const done = page.waitForURL(/Supertext\/Translate/, { waitUntil: 'load', timeout: 300_000 })
  .then(() => page.locator('table.supertext-targets tr', { hasText: 'de-CH' }).locator('a').first().waitFor({ timeout: 300_000 }));
await kit.say('Supertext translates the content.', {
  caption: 'Supertext translates the content…',
  during: async () => {
    await submit.click({ noWaitAfter: true });
    await kit.hold(1300);
    release();
  },
});
kit.pause();
await done;
await page.unroute(/Supertext\/Translate\//);
kit.resume();
await kit.sync();

// 4. The result: open the German draft.
const german = page.locator('table.supertext-targets tr', { hasText: 'de-CH' }).locator('a').first();
await kit.say('A few seconds later, the German version is there, saved as a draft.', {
  caption: 'Done: the German version, saved as a draft',
  during: async () => { await kit.hold(500); await kit.moveTo(german); },
});
await kit.say("Let's open the German draft and check it.", {
  caption: 'Open the German draft to check it',
  during: async () => {
    await page.evaluate(([x, y]) => window.__stk?.ripple(x, y), [kit.cursor.x, kit.cursor.y]);
    await Promise.all([page.waitForURL(/ContentItems\/.+\/Edit/), german.click()]);
    await page.locator('.ta-content').waitFor();
    await kit.sync();
    await kit.moveTo(page.locator('input[name="TitlePart.Title"]'));
  },
});
await kit.say('The German version is ready. Review it, and publish it as usual.', {
  caption: 'The German version is ready – review it and publish',
  during: async () => {
    await kit.hold(500);
    await page.evaluate(() => window.scrollBy({ top: 260, behavior: 'smooth' }));
  },
});
await kit.caption('');

fs.mkdirSync(path.dirname(out), { recursive: true });
await kit.finish(out, { system: 'Orchard Core', repo: 'OrchardCore-Supertext-Translation', gif });
await browser.close();
fs.rmSync(workDir, { recursive: true, force: true });
console.log(`Saved ${out} and ${gif} (${(fs.statSync(gif).size / 1e6).toFixed(2)} MB)`);
