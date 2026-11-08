# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Changed

- **Supertext SDK 0.4.0: default languages.** A culture you haven't mapped now uses its obvious Supertext language instead of failing: `de` → `de-DE`, `en` → `en-US`, `fr` → `fr-FR`, `it` → `it-IT`, `pt` → `pt-PT`, `zh` → `zh-Hans`, `zh-TW` → `zh-Hant`, `es-ES` → `es`, `nl-NL` → `nl`. The **Language mapping** selects on Settings → Supertext are pre-selected with that default (*Suggested by Supertext*); only cultures without one (`en-IE`, `de-LI`, `gsw` …) show *— choose —* and *Not mapped*. Language names in the selects follow the back-end language. Saved codes Supertext doesn't accept are updated where the replacement is obvious (`es-ES` → `es`); other saved values are kept. Only Supertext's codes can be saved.

### Added

- **Translation review** per culture (Settings → Supertext → Language mapping, checkbox next to each culture's Supertext language): translations into a culture with *Translation review* ticked go through Supertext's review service (Supertext Localization Review), where customers and Supertext linguists correct them on the website with a Chrome extension; corrections come back into Orchard Core with the next translation, and texts translated before keep their (reviewed) wording. Same API key, billing, languages and messages; other cultures keep going directly to Supertext. New setting `SUPERTEXT_REVIEW_URL` / `Supertext:ReviewUrl` (defaults to Supertext's review service; empty turns review off everywhere). Existing sites start with review off. All four interface languages.
- Requests to Supertext name the module's version in the User-Agent (`OrchardCore-Supertext-Translation/<version>`, as in the version reports).

- Export for translation (XLIFF 1.2 or HTML) and import of translated files; Supertext SDK 0.5.0. On the *Translate with Supertext* page: download an item's texts for one or more cultures (HTML: one), have them translated elsewhere, upload the file again; the texts are saved as drafts like translations with Supertext, with a summary (imported, skipped because the source changed, untranslated, formatting changed, not in the file). Texts whose source changed since the export are only imported on request.

- Automatic API key check: when Settings → Supertext is saved, when a key from `SUPERTEXT_API_KEY` is new or changed, and when Supertext rejects the key during a translation. Settings → Supertext shows the latest result with its time (*the connection works*, *rejected*, *no key*, *Supertext can't be reached*, *error*; with the account and API key links where they help) in English, German, French and Italian; it replaces the old "Connected to Supertext" notice.
- *Send version information to Supertext* (Settings → Supertext → Support, on by default; `SUPERTEXT_VERSION_REPORTS=0` turns it off): when the settings are saved and after an update, the module sends the site URL, the module, Orchard Core, .NET and SDK versions, whether a key is set, the key check result and the ping address to Supertext. No content, no API key and no personal data. Reports are sent after the page is delivered and never break saving or translating.
- Public ping endpoint `GET /supertext/ping` that only echoes Supertext's challenge, so Supertext can check the site is reachable (404 with version reports off).
- **Language mapping** table on Settings → Supertext: one row per culture with a list of Supertext's languages, pre-selected where the culture is a Supertext code; unmapped cultures are marked *Not mapped*, codes Supertext doesn't offer and languages without a formal/informal choice are pointed out.
- Messages for a culture without a Supertext language (*has no Supertext language yet*) and for a form of address Supertext doesn't offer for a language, in all four languages.

- Its own message for an unsupported language pair, naming the codes ("Supertext does not support this language pair (en → xx-YY)"), in English, German, French and Italian.
- French and Italian interface (and German where it was missing): buttons, the Translate with Supertext page, Settings → Supertext, notifications and Supertext error messages follow the admin's interface language (`Localization/de.po`, `fr.po`, `it.po`). The "Authentication failed" and missing-key errors now also link to Supertext account signup and API key generation.
- Settings → Supertext shows the installed module version, linked to its GitHub release notes.
- Support: the Supertext support form is linked in the README, the user guide and GitHub's "New issue" page.
- Demo movie in the README (`docs/images/demo.gif`): an editor translates an article into German with *Translate with Supertext* and opens the German draft. Recorded by `Tests/Docs/movie.mjs` against the demo and the stand-in API; the MP4 version has an English voice (Piper voice “alba”, CC BY 4.0). Install and configuration movie (MP4, not committed): `Tests/Docs/movie-install.mjs` shows an administrator installing the package, enabling the feature and setting the API key and the language mapping.

### Changed

- A culture nobody mapped now uses Supertext's obvious default language (Supertext SDK 0.4.0): `de` → `de-DE`, `en` → `en-US`, `fr` → `fr-FR`, `it` → `it-IT`, `pt` → `pt-PT`, `zh-CN` → `zh-Hans`, `es-ES` → `es`; the language mapping pre-selects it. Cultures without an obvious default (`en-IE`, `de-LI`, `gsw` …) still need a mapping.

- All Supertext API calls now go through the official Supertext .NET SDK (0.3.0, commit `f7bd414`, `V1` namespace), compiled into the module (nothing extra to install). The HtmlAgilityPack dependency is gone.
- **Languages are no longer guessed.** A culture that is a Supertext code (`de-CH`, `fr-CH`, `es`, `zh-Hans`, apart from case and `_`/`-`) is used as it is; every other culture (`de`, `en`, `zh-CN`, `es-ES` …) needs a Supertext language in the **Language mapping** (formerly *Language codes*; existing `culture=code` entries are kept). Supertext only accepts its own codes: regional for German, English, French, Italian and Portuguese, bare for most others (`es`, `nl`, `sl`). An unmapped culture is not sent to Supertext.
- The form of address is only sent for languages Supertext offers it for; English, Swiss German, Romansh, Albanian and Serbian are translated in the default tone.
- Version reports, the API key check result and the reachability ping now use Supertext Plugin Health 0.1.0 (bundled in `lib/supertext-plugin-health`); Supertext SDK updated to 0.3.0. No change for sites: settings and stored state stay as they are.

## 0.1.0 — 2026-10-07

### Added

- *Translate with Supertext* page (content list and editor buttons): translate an item into several cultures, create missing localizations, overwrite existing ones after confirmation; results are saved as drafts.
- Automatic translation of new localizations created with Orchard Core's *Localizations* menu (can be turned off).
- Settings → Supertext: encrypted API key (with connection check), endpoint (https only, except localhost), form of address, language code mapping, excluded fields, timeout. `SUPERTEXT_API_KEY` / `SUPERTEXT_API_ENDPOINT` override the settings.
- Permissions *Translate content with Supertext* (Administrator, Editor) and *Manage Supertext settings* (Administrator).
- Translates TitlePart, HtmlBodyPart, MarkdownBodyPart, TextField (prose editors), HtmlField, MarkdownField, LinkField text, MediaField alt texts, and widgets in FlowPart, BagPart and WidgetsListPart; Liquid in HTML is protected.
- Settings → Supertext and the "no API key" warning link to Supertext account signup and API key generation (Integrations → API, Admin role); same links in the installation guide, README and demo `.env.example`.
- Railway demo (Orchard Core 3.0.1, TheBlogTheme, English → German, French, Italian) with demo accounts from `DEMO_*` variables.
