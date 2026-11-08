# PR Walker Chrome Extension

1. Open Chrome → `chrome://extensions`
2. Enable **Developer mode**
3. Click **Load unpacked**
4. Select this folder: `xscribe-desktop/extension/pr-walker`
5. Open XScribe Desktop → **PR Walker** tab
6. Click the **PR Walker** extension icon → **Connect now** (badge shows **ON** when linked)
7. Click **Choose PR tab** and bind your GitHub PR Files/Changes page

If the badge shows **OFF**, the service worker may be asleep — open the popup and click **Connect now** again.

## Optional: Anthropic API for live call steps

Set environment variable `ANTHROPIC_API_KEY` or it will be read from `%APPDATA%\XScribeDesktop\settings.json`.

Without API key, ripgrep (`rg`) still resolves many "show me where" questions locally.
