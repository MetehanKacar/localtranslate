# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Project documentation: architecture, troubleshooting, contributing guide.
- GitHub issue templates and a build workflow.
- English and Turkish interface; follows the Windows display language by default and can be changed in Settings.
- Screenshots in the README.
- `--settings` command-line option to open the settings window at start.

### Changed
- Errors from background tasks are now written to `error.log`.

### Removed
- The early browser-extension prototype (replaced by the desktop app).
- Unused code and options.

## [1.0.0]

### Added
- Hover translation: the sentence under the cursor is highlighted and translated below it.
- Selection translation, with a Copy button.
- Whole-screen translation (`Ctrl+Alt+F10`) that repaints translations in place and follows screen changes.
- Screen text selection (`Ctrl+Alt+F11`) for text that cannot be selected normally, including text inside images.
- OCR fallback through Windows' built-in engine.
- Per-sentence language detection in whole-screen mode.
- Tray icon and menu, settings window, optional start with Windows.
- Single-file, self-contained Windows build.
- Turkish and English README.
