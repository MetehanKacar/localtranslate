# Contributing

Thanks for looking at LocalTranslate. This is a small project, so the process is simple.

## Requirements

- Windows 10 (19041) or later
- .NET 10 SDK
- Docker Desktop with LibreTranslate, if you want to run the app against a real server

## Build

```bash
dotnet build LocalTranslate.csproj -c Release
```

Publish the single-file exe the same way the release is built:

```bash
dotnet publish LocalTranslate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

## Test

The self-test program uses the real source files and needs an interactive desktop with a display and a running LibreTranslate server:

```bash
dotnet build tools/SelfTest -c Release
tools/SelfTest/bin/Release/net10.0-windows10.0.19041.0/SelfTest.exe
```

Before opening a pull request, run it and describe in the PR which checks you ran. Screen-mode and hover changes should include a screenshot, since they are visual.

## Guidelines

- **Keep the mouse hook trivial.** The hook callback runs inside the system input pipeline. Post work to the dispatcher; never do UIA or OCR there.
- **UI Automation and OCR run on the `TextLocator` thread.** Do not call them from the UI thread.
- **Every overlay window must be excluded from capture** (`Native.ExcludeFromCapture`), otherwise OCR reads its own output.
- **Language detection is per request.** Send sentences separately if you need per-sentence languages.
- **Settings changes need a migration** if you rename or change the default of a field: bump `CurrentVersion` in `Settings.cs` and handle the old value in `Load`.
- **Turkish and English.** User-facing text is Turkish. The README is bilingual; keep both sections in sync.
- Match the surrounding style: nullable enabled, file-scoped namespaces, small focused classes.

## Pull requests

1. Open an issue first for larger changes, so the approach can be agreed.
2. Keep each PR to one topic.
3. Update `CHANGELOG.md` under *Unreleased*.
4. Describe how you tested it.

## Reporting bugs

Use the bug report template. Include your Windows version, the app version, the app you were using, and the relevant part of `%APPDATA%\LocalTranslate\error.log`.
