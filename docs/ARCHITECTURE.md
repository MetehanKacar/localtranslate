# Architecture

This document describes how LocalTranslate is put together and why. For usage, see the [README](../README.md).

## Overview

LocalTranslate is a single-process Windows desktop app (WPF + a little WinForms for the tray icon). It has no main window. Everything runs from a `Controller` that owns the input hooks, the text readers, the translator and the UI windows.

```
                   ┌──────────────── Controller ────────────────┐
 mouse hook ─────▶ │ hover / selection state machine            │ ──▶ OverlayWindow (bubble)
 hotkeys ────────▶ │                                             │ ──▶ HighlightWindow
 tray menu ──────▶ │                                             │ ──▶ RegionSelectWindow
                   │                                             │ ──▶ ScreenTranslator ──▶ ScreenOverlayWindow
                   └──────┬───────────────────────┬──────────────┘
                          │                       │
                  TextLocator (MTA thread)    Translator ──HTTP──▶ LibreTranslate (localhost:5000)
                   ├── UiaReader   (UI Automation)
                   └── OcrReader   (Windows.Media.Ocr + screen capture)
```

## Threads

| Thread | Work |
| --- | --- |
| UI (WPF dispatcher) | Windows, the hook callbacks (queued, never blocking), the 50 ms cursor poll |
| `LocalTranslate.TextLocator` (MTA) | All UI Automation calls and OCR. These are cross-process and can block for seconds, so they never run on the UI thread |
| Thread pool | Screen capture and tile signatures in screen mode; HTTP requests |

The mouse hook (`WH_MOUSE_LL`) is the only code that runs inside the system input pipeline. Its callback only copies the event and posts it to the dispatcher. A slow hook would make the mouse lag in every application, so the callback must stay trivial.

## Components

### Input (`Input/`)

- **`MouseHook`** — low-level mouse hook. Emits left-down/up, other buttons and wheel events.
- **`Hotkey`** — global hotkeys via `RegisterHotKey` on a message-only window. Parses strings such as `Ctrl+Alt+F9`. Plain letters are rejected so they cannot be swallowed system-wide.
- **`ClipboardGrabber`** — last-resort selection reader: sends Ctrl+C, reads the clipboard, restores the previous clipboard contents. Skipped for terminals and Excel-like apps where Ctrl+C has other meanings.

### Text (`Text/`)

- **`UiaReader`** — reads text through UI Automation (`IUIAutomationTextPattern`). For a point it finds the element, walks up to the nearest element with a text pattern, expands the caret to the enclosing word, paragraph and sentence, and returns the sentence with its screen rectangles. Images are reported as `Unsupported` so OCR takes over.
- **`OcrReader`** — captures the window band around the cursor (or a whole monitor), upscales it, runs `Windows.Media.Ocr`, and maps word boxes back to screen pixels. It also groups lines into paragraphs and finds the sentence under a word.
- **`TextLocator`** — owns the MTA thread and exposes `SentenceAtAsync` (UIA first, OCR when UIA has nothing) and `SelectionAsync`.
- **`Sentence`** — sentence boundaries. Handles abbreviations and decimals (`3.14`, `example.com`), closing quotes, CJK punctuation and wrapped lines.
- **`TextHit`** — text plus its screen rectangles and source (`uia`, `ocr`, `clipboard`).

### Translation (`Translator.cs`, `Settings.cs`)

- Calls `POST /translate` with `source: "auto"` and reads `detectedLanguage`. Detection is per request, so each sentence must be sent separately; batching produces one language for the whole batch.
- An in-memory cache of 500 entries, keyed by target language + text. When full, the oldest entry is evicted.
- In-flight requests for the same text are shared.
- At most 4 concurrent requests.
- Settings are JSON in `%APPDATA%\LocalTranslate\settings.json`. A `Version` field allows migrations (e.g. the move from `Ctrl+Alt+T/E/S` to F-keys).

### Screen mode (`Screen/`)

`ScreenTranslator` runs a loop every 200 ms, per monitor:

1. Capture the monitor at reduced cost and compute a **tile signature**: the mean brightness of each 24×24 tile, sampled sparsely.
2. Compare with the previous signature. Changed tiles immediately remove any translation overlay that covers them, so nothing stays over moved text.
3. Tiles that change in each of four consecutive checks are treated as animation and ignored. Otherwise a video would never let the screen settle.
4. When the change count is small (a blinking caret, a clock) nothing is re-read. When the screen has been still for 250 ms and something changed, the monitor is OCR'd again.
5. `BlockBuilder` groups OCR lines into blocks (menu items and table columns are split by large horizontal gaps; wrapped paragraphs are merged). Code-like text is skipped.
6. Each block is split into sentences, each translated separately, and `ScreenOverlayWindow` draws the translation over the original box, with background and text colour sampled from the screenshot (`PixelSampler`).

`ScreenOverlayWindow` is excluded from screen capture (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`). Without this, the next OCR pass would read the translations and loop forever.

### UI (`UI/`)

- **`OverlayWindow`** — the translation bubble. Non-activating; the hover variant is click-through (`WS_EX_TRANSPARENT`), the selection variant is interactive for the Copy button. Positioned below the text, flipped above when there is no room, clamped to the monitor's work area.
- **`HighlightWindow`** — click-through rectangles over the translated sentence.
- **`RegionSelectWindow`** — frozen screenshot with OCR word boxes for Ctrl+Alt+F11.
- **`Tray`** — notification-area icon and menu (WinForms `NotifyIcon`).
- **`SettingsWindow`** — XAML settings window.
- **`Theme`** — follows the Windows light/dark app setting.

## Key decisions

- **UIA before OCR.** UIA is exact and takes about 10 ms. OCR takes about 100 ms and can misread characters. OCR is only used when UIA reports nothing usable.
- **Per-sentence language detection.** Batched requests share one detected language. Mixed paragraphs need one request per sentence.
- **Shortcuts use F-keys.** On Turkish and other AltGr layouts, `Ctrl+Alt+letter` types characters (₺, €, @).
- **Native APIs through P/Invoke and COM interop.** UI Automation comes from the `Interop.UIAutomationClient` package; OCR uses `Windows.Media.Ocr` through the Windows SDK projection; window and input APIs are declared in `Native.cs`.
- **Single-file, self-contained publish.** No runtime installation needed on the user's machine.

## Testing

`tools/SelfTest` is a separate console app that references the real source files (not copies), so the tests exercise the actual code.

- `SelfTest` — sentence splitting, LibreTranslate connectivity, UIA and OCR reads against a helper WinForms window.
- `SelfTest e2e <exe>` — runs the published app, moves the mouse, presses the hotkeys, saves screenshots.
- `SelfTest screen <exe>` — runs only the whole-screen mode and dumps the composite PNG and per-sentence translations.

Debug environment variables:

| Variable | Effect |
| --- | --- |
| `LOCALTRANSLATE_DEBUG_CAPTURE=1` | Lets screenshots include our own windows (disables capture exclusion) |
| `LOCALTRANSLATE_DEBUG_DUMP=<dir>` | Writes screen-mode composites and sentence translations to `<dir>` |

The desktop tests need an interactive session and a real display, so they are not run in CI.
