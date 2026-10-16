# Precision Snip

A Windows desktop utility that turns a selected screen region into editable text on the clipboard. Launch it once and press **Ctrl+Alt+S**, drag over the text, then paste with **Ctrl+V**. The app is labeled **Precision Text** in its window and tray UI.

## What it does

- Displays a dimmed, multi-monitor selection overlay with a crosshair.
- Captures only the selected pixels and runs the built-in offline Windows OCR engine.
- Copies recognized Unicode plain text-not the screenshot-to the clipboard.
- Preserves reading order and line breaks; normalizes common bullet and radio-choice markers.
- Keeps the OCR engine ready in the notification area for one hour after launch, then exits automatically.
- Uses a single-instance signal so another launch brings up the existing app.
- Includes self-tests (`--self-test` and `--resident-self-test`).

The capture stays in memory only while OCR runs. Nothing is uploaded. Because OCR reads pixels rather than document metadata, it cannot preserve exact fonts, colors, bold styling, or hidden page structure.

## Requirements

- Windows 10 or later with the Windows OCR language pack for the text being recognized
- .NET Framework 4.8 development tools / C# compiler to build from source

## Build from source

The application is a single-file Windows Forms project. The source is in `src/PrecisionSnip.cs`, with its DPI-aware application manifest in `src/PrecisionSnip.manifest`. Compile it as a Windows application with `System.Windows.Forms`, `System.Drawing`, and `System.Runtime` references; use the manifest as the application manifest. No downloaded OCR service or API key is needed.

The source retains its original assembly/product naming (`Precision Text`) and version `2.1.3.0`. The executable and desktop shortcut are intentionally not committed. If you build an executable named `PrecisionSnip.exe`, place it beside `scripts/Create-Desktop-Shortcut.ps1` before running that helper.

## Usage

1. Run the built executable.
2. Press **Ctrl+Alt+S** (or launch the executable again) and drag a rectangle over text.
3. Paste the copied result with **Ctrl+V**.

Press **Esc** or right-click to cancel. A click without dragging does not scan. The tray icon has an **Exit** action. The app does not start automatically with Windows.

## Architecture

The Windows Forms overlay measures the selection in physical pixels under per-monitor DPI awareness. `Graphics.CopyFromScreen` captures the region; an in-memory bitmap is passed to `Windows.Media.Ocr` through WinRT interop. The formatter uses OCR line geometry for reading order and choice-marker grouping before placing Unicode text on the clipboard with retries.

## Privacy

The selected image is processed locally and discarded. This repository does not contain captured images, clipboard contents, or credentials.


