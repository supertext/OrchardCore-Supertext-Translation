#!/usr/bin/env node
/**
 * End-to-end test against the REAL Supertext API: a fresh demo (nothing translated yet) with
 * a real SUPERTEXT_API_KEY. As the demo's editor account, through Orchard Core's admin:
 *
 * 1. creates one more English article whose HTML body contains Liquid ({{ … }}, {% … %}),
 *    because the demo's sample content has none and the module must keep it untouched;
 * 2. translates every English content item (editor → "Translate with Supertext") into every
 *    other culture of the site;
 * 3. checks the SQLite database: every item has exactly one localization per culture, none of
 *    them is published, the texts are filled and translated, no protocol leftovers, formatting,
 *    links and Liquid kept, the text is in the target language (de-CH without "ß");
 * 4. translates everything again (overwrite confirmed) and checks the same items were updated.
 *
 * See "End-to-end tests" in docs/DEVELOPER.md.
 *
 *   ORCHARD_URL=http://127.0.0.1:8081 ORCHARD_DATA=/path/mounted/as/data \
 *   EDITOR_EMAIL=… EDITOR_PASSWORD=… node Tests/E2E/live.mjs
 */
import { chromium } from 'playwright';
import { DatabaseSync } from 'node:sqlite';
import { mkdirSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const B = (process.env.ORCHARD_URL ?? 'http://127.0.0.1:8081').replace(/\/$/, '');
const need = (name) => process.env[name] || (() => { throw new Error(`Set ${name}`); })();
const artifacts = fileURLToPath(new URL('./artifacts/', import.meta.url));
mkdirSync(artifacts, { recursive: true });

const failures = [];
function check(name, ok, detail = '') {
	console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` — ${detail}` : ''}`);
	if (!ok) failures.push(`${name}${detail ? ` — ${detail}` : ''}`);
	return ok;
}
const note = (text) => console.log(`note ${text}`);

// Common words, to tell the target languages apart. Lower case, whole words.
const STOPWORDS = {
	en: ['the', 'and', 'is', 'are', 'with', 'for', 'you', 'your', 'our', 'of', 'to', 'we', 'who', 'what'],
	de: ['der', 'die', 'das', 'und', 'ist', 'sind', 'mit', 'für', 'sie', 'ihre', 'ihr', 'unser', 'unsere', 'wir', 'eine', 'ein', 'den', 'zu', 'von', 'was'],
	fr: ['le', 'les', 'et', 'est', 'sont', 'avec', 'pour', 'vous', 'votre', 'vos', 'notre', 'nos', 'nous', 'des', 'une', 'un', 'du', 'de', 'qui', 'que'],
	it: ['il', 'lo', 'gli', 'e', 'è', 'sono', 'con', 'per', 'lei', 'sua', 'suo', 'nostro', 'nostra', 'noi', 'una', 'un', 'del', 'della', 'di', 'che', 'chi'],
};
const words = (text) => text.toLowerCase().match(/[\p{L}]+/gu) ?? [];
const score = (text, lang) => words(text).filter((w) => STOPWORDS[lang].includes(w)).length;
const LIQUID = /\{\{[\s\S]*?\}\}|\{%[\s\S]*?%\}/g;
/** Visible text of a field: without tags and Liquid. */
const plain = (v) => String(v ?? '').replace(LIQUID, ' ').replace(/<[^>]+>/g, ' ');
const count = (haystack, needle) => String(haystack ?? '').split(needle).length - 1;

const LEFTOVERS = ['data-st-id', 'data-st-ph', 'st-ph-', 'data-st-keep'];
const KEPT = [['Liquid output tags', '{{'], ['Liquid logic tags', '{%'], ['links', '<a '], ['link targets', 'href="https://www.supertext.com"'], ['bold', '<strong'], ['italics', '<em'], ['list items', '<li'], ['headings', '<h2'], ['line breaks', '<br']];

// The extra article with Liquid (the demo's sample content has none).
const LIQUID_TITLE = 'Liquid in translated content';
const LIQUID_SUBTITLE = 'A short check that template tags stay as they are';
const LIQUID_HTML = '<p>Welcome to {{ Site.SiteName }}. This article checks that <strong>template tags</strong> survive the translation.</p>'
	+ '<p>{% if Site.SiteName %}Our team answers your questions within one working day.{% endif %}</p>';

// --- Database (YesSql on SQLite, App_Data in the mounted /data) -----------------------------
const sitesDir = join(need('ORCHARD_DATA'), 'App_Data', 'Sites', 'Default');
function openDb() {
	const file = readdirSync(sitesDir).find((f) => f.endsWith('.db'));
	if (!file) throw new Error(`No SQLite database in ${sitesDir}`);
	return new DatabaseSync(join(sitesDir, file), { readOnly: true });
}

/** All versions of all content items: { id, latest, published, type, json }. */
function versions() {
	for (let attempt = 1; ; attempt++) {
		try {
			const db = openDb();
			try {
				const tables = db.prepare("SELECT name FROM sqlite_master WHERE type = 'table'").all().map((r) => r.name);
				const index = tables.find((t) => /(^|_)ContentItemIndex$/.test(t));
				const document = tables.find((t) => /(^|_)Document$/.test(t));
				if (!index || !document) throw new Error(`YesSql tables not found: ${tables.join(', ')}`);
				return db.prepare(`SELECT i.ContentItemId AS id, i.Latest AS latest, i.Published AS published, i.ContentType AS type, d.Content AS content FROM "${index}" i JOIN "${document}" d ON d.Id = i.DocumentId`).all()
					.map((r) => ({ id: r.id, latest: !!r.latest, published: !!r.published, type: r.type, json: JSON.parse(r.content) }));
			} finally {
				db.close();
			}
		} catch (e) {
			if (attempt >= 5 || !/busy|locked/i.test(e.message)) throw e;
		}
	}
}

/** Latest version of each localized item, by culture (lower case) and localization set. */
function localized() {
	return versions().filter((v) => v.latest && v.json.LocalizationPart?.Culture).map((v) => ({
		...v,
		culture: v.json.LocalizationPart.Culture.toLowerCase(),
		set: v.json.LocalizationPart.LocalizationSet,
	}));
}

// What the module translates (ContentWalker): TitlePart.Title, HtmlBodyPart/HtmlField .Html,
// TextField .Text, MarkdownBodyPart/MarkdownField .Markdown, LinkField .Text, MediaField .MediaTexts.
// Parts the module doesn't copy (target-owned) are skipped.
const TARGET_OWNED = new Set(['LocalizationPart', 'AutoroutePart', 'AliasPart', 'ContainedPart', 'FlowMetadata']);
const TEXT_KEYS = new Set(['Title', 'Html', 'Text', 'Markdown']);
function fields(json, path = '', out = new Map()) {
	if (Array.isArray(json)) {
		json.forEach((v, i) => fields(v, `${path}[${i}]`, out));
	} else if (json && typeof json === 'object') {
		for (const [k, v] of Object.entries(json)) {
			if (TARGET_OWNED.has(k)) continue;
			const p = path ? `${path}.${k}` : k;
			if (typeof v === 'string' && TEXT_KEYS.has(k) && (k !== 'Title' || path.endsWith('TitlePart'))) out.set(p, v);
			else if (k === 'MediaTexts' && Array.isArray(v)) v.forEach((t, i) => typeof t === 'string' && out.set(`${p}[${i}]`, t));
			else fields(v, p, out);
		}
	}
	return out;
}
// Values without words (URLs, e-mails, numbers) aren't translated.
const translatable = (v) => /\p{L}{2,}/u.test(plain(v)) && !/^\s*(https?:\/\/|mailto:|\/|~\/)\S*\s*$/.test(v) && !/^\s*\S+@\S+\s*$/.test(v);

// --- Browser ------------------------------------------------------------------------------
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
const page = await browser.newPage({ viewport: { width: 1280, height: 1000 }, locale: 'en-US' });
page.setDefaultTimeout(30_000);

/** Orchard Core's notifications (toasts) on the current page. */
async function messages() {
	const read = async (type) => (await page.locator(`.toast.message-${type}`).allTextContents()).map((t) => t.replace(/\s+/g, ' ').trim());
	return { success: await read('success'), warning: await read('warning'), error: await read('error') };
}

// --- Log in as the editor -------------------------------------------------------------------
await page.goto(`${B}/Login`);
await page.fill('input[name$="UserName"]', need('EDITOR_EMAIL'));
await page.fill('input[type=password]', need('EDITOR_PASSWORD'));
await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
if (!check('editor can log in (DEMO_EDITOR_*)', !page.url().includes('/Login'))) {
	await page.screenshot({ path: `${artifacts}login.png` });
	throw new Error(failures.join('\n'));
}

check('fresh demo (nothing translated yet)', localized().every((v) => v.culture === 'en'));

// --- 1. An English article with Liquid, created like an editor would ---------------------------
await page.goto(`${B}/Admin/Contents/ContentTypes/Article/Create`);
await page.fill('input[name="TitlePart.Title"]', LIQUID_TITLE);
await page.fill('input[name="Article.Subtitle.Text"]', LIQUID_SUBTITLE);
await page.locator('.trumbowyg-editor').first().waitFor({ timeout: 15_000 }).catch(() => note('no Trumbowyg editor found, filling the text area'));
await page.evaluate((html) => {
	const area = document.querySelector('textarea[name="HtmlBodyPart.Html"]');
	const $ = window.jQuery;
	if ($ && $(area).data('trumbowyg')) $(area).trumbowyg('html', html);
	area.value = html;
}, LIQUID_HTML);
await Promise.all([page.waitForNavigation(), page.click('button.publish')]);
const liquidSource = localized().find((v) => v.culture === 'en' && v.json.TitlePart?.Title === LIQUID_TITLE);
if (check('editor created the English article with Liquid', !!liquidSource)) {
	const html = String(liquidSource.json.HtmlBodyPart?.Html ?? '');
	check('the article\'s HTML body has its Liquid tags', count(html, '{{') === 1 && count(html, '{%') === 2, html);
} else {
	await page.screenshot({ path: `${artifacts}liquid-article.png`, fullPage: true });
}

// --- 2. Translate every English item into every other culture ---------------------------------
const sources = localized().filter((v) => v.culture === 'en');
console.log(`English items: ${sources.map((s) => `${s.type} "${s.json.DisplayText}"`).join(', ')}`);
check('demo has English content', sources.length >= 2, `${sources.length}`);
let cultures = [];

async function translateAll(label) {
	for (const source of sources) {
		const name = source.json.DisplayText;
		// The editor's "Translate with Supertext" button (the content list's "Supertext" button opens the same page).
		await page.goto(`${B}/Admin/Contents/ContentItems/${source.id}/Edit`);
		const button = page.locator('a.supertext-translate');
		if (!check(`${label}: "${name}" has the Translate with Supertext button`, (await button.count()) === 1)) continue;
		await Promise.all([page.waitForNavigation(), button.click()]);

		const boxes = page.locator('table.supertext-targets input[name="cultures"]');
		const values = await boxes.evaluateAll((list) => list.map((b) => b.value));
		if (!cultures.length) cultures = values;
		check(`${label}: "${name}" offers every culture`, values.join(',') === cultures.join(',') && values.length > 0, values.join(', '));
		const before = await messages();
		check(`${label}: "${name}" translate page without errors`, before.error.length === 0 && !(await page.locator('.alert-warning', { hasText: 'No Supertext API key' }).count()), before.error.join(' | '));
		for (const value of values) await page.check(`table.supertext-targets input[value="${value}"]`);

		const started = Date.now();
		await Promise.all([page.waitForNavigation({ timeout: 900_000 }), page.click('button.supertext-submit')]);
		const overwrite = page.locator('.supertext-overwrite #confirmOverwrite');
		if (label !== 'first-run') {
			if (check(`${label}: "${name}" asks before overwriting`, (await overwrite.count()) === 1)) {
				for (const value of values) await page.check(`table.supertext-targets input[value="${value}"]`);
				await overwrite.check();
				await Promise.all([page.waitForNavigation({ timeout: 900_000 }), page.click('button.supertext-submit')]);
			}
		}
		const file = `${artifacts}${label}-${source.type}-${name.replace(/\W+/g, '-')}.png`;
		await page.screenshot({ path: file, fullPage: true });
		const after = await messages();
		console.log(`${label}: "${name}" ${Math.round((Date.now() - started) / 1000)} s`);
		check(`${label}: "${name}" translated into every culture`, after.success.length === values.length, `${after.success.length}/${values.length}`);
		check(`${label}: "${name}" no texts kept the source wording`, after.warning.length === 0, after.warning.join(' | '));
		check(`${label}: "${name}" no errors shown`, after.error.length === 0, after.error.join(' | '));
	}
}

/** The localizations of each source item: Map(source id → Map(culture → [items])). */
function snapshot() {
	const all = localized();
	return new Map(sources.map((s) => [s.id, new Map(cultures.map((c) => [c, all.filter((v) => v.set === s.set && v.culture === c.toLowerCase())]))]));
}

await translateAll('first-run');
const first = snapshot();
const everything = versions();

for (const culture of cultures) {
	const lang = culture.split(/[-_]/)[0].toLowerCase();
	let fieldCount = 0;
	let changed = 0;
	let text = '';
	for (const source of sources) {
		const tag = `${culture}: ${source.type} "${source.json.DisplayText}"`;
		const copies = first.get(source.id).get(culture);
		if (!check(`${tag} has exactly one localization`, copies.length === 1, `${copies.length}`)) continue;
		const target = copies[0];
		check(`${tag} localization is not published (draft)`, !everything.some((v) => v.id === target.id && v.published));
		check(`${tag} localization has the culture`, target.json.LocalizationPart.Culture === culture, target.json.LocalizationPart.Culture);

		const sourceFields = fields(source.json);
		const targetFields = fields(target.json);
		for (const [path, s] of sourceFields) {
			if (!translatable(s)) continue;
			fieldCount++;
			const v = targetFields.get(path) ?? '';
			if (!check(`${tag} ${path} filled`, plain(v).trim() !== '')) continue;
			// A single word can be the same in the target language; unchanged fields count against the ratio below.
			if (v !== s) changed++;
			else note(`${tag} ${path} unchanged: ${s}`);
			text += ` ${plain(v)}`;
			const left = LEFTOVERS.filter((l) => v.includes(l));
			check(`${tag} ${path} has no protocol leftovers`, left.length === 0, left.join(', '));
			for (const [what, needle] of KEPT) {
				if (count(s, needle)) check(`${tag} ${path} keeps its ${what}`, count(v, needle) === count(s, needle), `${count(v, needle)}/${count(s, needle)}`);
			}
			const liquid = (x) => (x.match(LIQUID) ?? []).join(' ');
			if (liquid(s)) check(`${tag} ${path} keeps its Liquid exactly`, liquid(v) === liquid(s), liquid(v));
		}
		check(`${tag} display text translated`, String(target.json.DisplayText ?? '').trim() !== '', target.json.DisplayText);
	}
	check(`${culture}: texts were changed by the translation`, fieldCount > 0 && changed / fieldCount >= 0.8, `${changed}/${fieldCount} fields`);
	if (STOPWORDS[lang]) {
		const scores = Object.fromEntries(Object.keys(STOPWORDS).map((l) => [l, score(text, l)]));
		const best = Object.entries(scores).sort((a, b) => b[1] - a[1])[0][0];
		check(`${culture}: text is in the target language`, best === lang, JSON.stringify(scores));
	}
	if (/^de-ch$/i.test(culture)) check(`${culture}: Swiss German spelling (no "ß")`, !text.includes('ß'));
}

// --- 3. Translating again updates the same items ----------------------------------------------
await translateAll('second-run');
const second = snapshot();
for (const source of sources) {
	for (const culture of cultures) {
		const before = first.get(source.id).get(culture).map((v) => v.id).sort().join(',');
		const after = second.get(source.id).get(culture).map((v) => v.id).sort().join(',');
		check(`${culture}: ${source.type} "${source.json.DisplayText}" translated again in place (no duplicates)`, before !== '' && before === after, `${before || '-'} → ${after || '-'}`);
	}
}
const published = versions().filter((v) => v.published && [...second.values()].some((m) => [...m.values()].flat().some((t) => t.id === v.id)));
check('translating again published nothing', published.length === 0, published.map((v) => v.id).join(', '));

await browser.close();

if (failures.length) {
	console.error(`\n${failures.length} check(s) failed:\n- ${failures.join('\n- ')}`);
	process.exit(1);
}
console.log('\nAll end-to-end checks passed.');
