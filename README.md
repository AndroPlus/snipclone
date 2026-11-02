# SnipClone

A clean-room screen snipping tool for Windows, modelled on Snipping Tool.

> **Built with [Claude Code](https://claude.com/claude-code) and the [REA MCP](https://github.com/morluto/rea).**
> Claude used REA (Reverse Engineer Anything), an MCP server for reverse engineering, to study the
> Snipping Tool installed on this PC. It then wrote SnipClone from scratch based on what REA found.
> [FINDINGS.md](FINDINGS.md) has the evidence, the original architecture, and how each feature maps to
> this clone.

## How it was made

This repo follows the "see a feature, understand it, rebuild it" workflow from the REA project:

1. **Install REA.** The [REA MCP server](https://github.com/morluto/rea) and its
   `reverse-engineer-anything` skill were connected to Claude Code.
2. **Find the target.** Claude located the installed `Microsoft.ScreenSketch` package
   (Snipping Tool 11.2608.41.0) and listed its binaries.
3. **Inspect it with REA.** Claude ran REA's `inspect_managed_members` on the package's WinRT metadata
   files (`SnippingTool.Capture.winmd` and `SnippingToolUI.winmd`). This revealed 330 UI types, the
   capture modes, view models, tool enums and settings. Claude also read the app manifest for its
   protocols and capabilities. The results were exported with `export_evidence_bundle`.
4. **Write the findings.** Each observation went into [FINDINGS.md](FINDINGS.md) with its REA evidence
   ID, and the notes separate what was observed from the design choices made for the clone.
5. **Rebuild it clean-room.** Claude wrote new Python code (tkinter + Pillow) for each feature. No
   Microsoft code, assets or strings were copied.
6. **Test it.** A scripted run fed synthetic mouse events through every capture mode and editing tool,
   and the overlay and editor were checked visually.

SnipClone is an independent project and is not affiliated with or endorsed by Microsoft.

Requirements: Python 3.10 or newer with Pillow (`pip install pillow`). Tkinter ships with Python.

## Install as an app

Double-click `build.bat`. It builds `dist\SnipClone\SnipClone.exe` with PyInstaller (in a private
`.build-venv`) and installs it to `%LOCALAPPDATA%\Programs\SnipClone`, with Start Menu and Desktop
shortcuts. To uninstall:

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

To run from source instead, use `python main.py` (or `run.bat`). Add `--snip` to open the capture
overlay straight away.

## Use

- **New** (Ctrl+N) or the **global hotkey** from anywhere: the screen freezes and dims, and you pick an
  area. The hotkey is the first free one of Ctrl+Alt+S, Ctrl+Shift+Alt+S, Ctrl+Alt+Q and Ctrl+Print
  Screen, and the status bar shows which one you got.
  You can switch modes on the overlay toolbar. **Esc** cancels.
  - **Rectangle**: drag a box. Its size is shown while you drag.
  - **Window**: hover to highlight a window, then click it.
  - **Full screen**: captures every monitor at once.
  - **Freeform**: draw any shape. Everything outside it becomes white.
  - **Color picker**: click a pixel to copy its color as HEX, RGB or HSL (change the format in ⚙).
- **Delay**: 3, 5 or 10 seconds, with a countdown on screen.
- After a capture the image is copied to the clipboard and saved to `Pictures\Screenshots`. You can turn
  both off in ⚙.
- **Editing tools**: pen, highlighter, stroke eraser (removes a whole stroke), rectangle, oval, line,
  arrow, text and crop (drag, then Enter to apply).
- **Keys**: Ctrl+Z / Ctrl+Y undo and redo. Ctrl+C copies the edited image. Ctrl+S saves as PNG, JPG,
  GIF or BMP.

Settings are stored in `%APPDATA%\SnipClone\settings.json`.

## Files

| File | What it does |
|---|---|
| `main.py` | Window, toolbars, settings, delay countdown, auto-copy and auto-save |
| `overlay.py` | Frozen-snapshot overlay with dimming and all capture modes |
| `editor.py` | Vector annotation model, PIL rendering, stroke eraser, crop, undo and redo |
| `winapi.py` | DPI awareness, window enumeration, image clipboard (CF_DIB), global hotkey |

## Not included

Text extraction (OCR), AI smart crop, screen recording and QR scanning. The original builds these on
Microsoft's own OCR and segmentation models (see FINDINGS.md). Window mode captures what is visible on
screen, so any window covering the chosen one appears in the snip. The original captures the window
itself through Windows.Graphics.Capture.
