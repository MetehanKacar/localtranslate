# Troubleshooting

## "LibreTranslate'e ulaşılamıyor" / "Cannot reach LibreTranslate"

The app talks to `http://localhost:5000` by default.

1. Check that Docker Desktop is running.
2. Start the server:
   ```bash
   docker start libretranslate
   ```
   If the container does not exist yet:
   ```bash
   docker run -d --name libretranslate -p 5000:5000 -v lt-local:/home/libretranslate/.local libretranslate/libretranslate --host 0.0.0.0
   ```
3. Open `http://localhost:5000/languages` in a browser. You should see a JSON list.
4. The first start downloads language models (several GB). Until that finishes the server does not answer. Watch progress with `docker logs -f libretranslate`.

If the server runs somewhere else, change the address in Settings → LibreTranslate server.

## Hotkey does nothing / "Kısayol kaydedilemedi"

Another program has registered the same combination. Change it in Settings. Shortcuts are F-keys by default to avoid AltGr clashes on Turkish keyboards.

## Typing `₺`, `€`, `@` or other AltGr characters stops working

You have a shortcut that uses `Ctrl+Alt+letter` (or digit). Those combinations are AltGr on many layouts. Use an F-key or a `Win+` combination instead.

## Hover translation does not appear over some app

- The app may expose no text to Windows accessibility (UI Automation). The app then falls back to OCR for the area under the cursor. OCR needs the text to be visible on screen and reasonably large.
- Enable **OCR fallback** in Settings if it is off.
- For apps that draw their own text in a way OCR cannot read (very small or stylised fonts), use **Ekrandan metin seç** (`Ctrl+Alt+F11`) and select the text there.

## OCR reads the wrong text or nothing

- OCR uses the Windows language packs. Check **Settings → Time & language → Language & region** and make sure the display language you read (for example English or Turkish) has its OCR feature installed. Without it, the app shows "Windows OCR bulunamadı".
- Very small text may be missed. Zooming the page in usually fixes it.

## Whole-screen mode keeps showing old translations

Translations are removed as soon as the area under them changes. If the screen is animated (a video or a moving background), those tiles are ignored on purpose, so translations of nearby text may stay until the screen is still.

## Whole-screen mode translates code or terminal text

Code-like blocks (symbols, paths, `::`, `->`) are skipped, but some text still gets through. Turn the mode off with `Ctrl+Alt+F10` while you work in a terminal.

## Selection translation picks up the wrong text

Selections are read with UI Automation first, then Ctrl+C (if enabled). Ctrl+C is not used in terminals and in Excel, File Explorer and Paint. If an app ignores UI Automation and a clipboard copy is not wanted, disable **Clipboard fallback** in Settings.

## The tray icon disappeared

Windows sometimes hides new tray icons. Open the arrow next to the clock and drag the LocalTranslate icon onto the taskbar, or pin it in Settings → Personalization → Taskbar → Other system tray icons.

## "LocalTranslate zaten çalışıyor"

Only one instance runs at a time. Find it in the tray (or in Task Manager under `LocalTranslate.exe`), quit it, then start again.

## Where are the logs and settings?

| File | Contents |
| --- | --- |
| `%APPDATA%\LocalTranslate\settings.json` | All settings |
| `%APPDATA%\LocalTranslate\error.log` | Unexpected errors and connection problems |

Paste the relevant part of `error.log` into a [bug report](https://github.com/MetehanKacar/localtranslate/issues/new/choose).
