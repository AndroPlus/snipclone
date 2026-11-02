# SnipClone

A clean-room screen snipping tool for Windows, modelled on Snipping Tool and written in **C# / WPF on
.NET 9**: a native Windows app with the Windows 11 Fluent theme.

> **Built with [Claude Code](https://claude.com/claude-code) and the [REA MCP](https://github.com/morluto/rea).**
> Claude used REA (Reverse Engineer Anything), an MCP server for reverse engineering, to study the
> Snipping Tool installed on this PC. It then wrote SnipClone from scratch based on what REA found.
> [FINDINGS.md](FINDINGS.md) has the evidence, the original architecture, and how each feature maps to
> this clone.

## Features

- **Rectangle, Window, Full screen, Freeform and Color picker** snips, on a frozen, dimmed overlay of every
  monitor.
- **Window snips use Windows.Graphics.Capture**, the API the real Snipping Tool wraps. They capture the
  window itself, so windows on top of it don't appear in the snip, and there's no yellow capture border.
- **Delay** of 3, 5 or 10 seconds with an on-screen countdown.
- **Automatic copy** to the clipboard and **automatic save** to `Pictures\Screenshots`, each switchable.
- **Editor:** pen, highlighter, stroke eraser, rectangle, oval, line, arrow, text, crop, and undo/redo.
  Snips open at true 1:1 size on high-DPI screens.
- **Global hotkey:** the first free one of Ctrl+Alt+S, Ctrl+Shift+Alt+S, Ctrl+Alt+Q and Ctrl+Print Screen.
  The status bar shows which one you got.
- **Single instance:** launching it again brings the running copy forward. `SnipClone.exe --snip` starts a
  snip straight away.

## Install

Requirements: Windows 10 2004 or later (Windows 11 for borderless window capture) and the
[.NET 9 SDK](https://dotnet.microsoft.com/download) to build it.

Double-click `build.bat`. It publishes a single-file `SnipClone.exe` and installs it to
`%LOCALAPPDATA%\Programs\SnipClone`, with Start Menu and Desktop shortcuts. To uninstall:

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

To run it from source:

```bash
dotnet run --project src
```

## Use

| Action | How |
|---|---|
| New snip | **New**, Ctrl+N, or the global hotkey |
| Switch mode | On the overlay toolbar, or in the main window's mode list |
| Cancel a snip | Esc |
| Draw | Pen, highlighter, shapes and text in the second toolbar row. Pick a color and size there too |
| Erase | The eraser removes a whole stroke, shape or text label |
| Crop | The crop tool: drag, then Enter (or **Apply**) |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Copy / save | Ctrl+C / Ctrl+S (PNG, JPEG, BMP or GIF) |

Settings (⚙) are stored in `%APPDATA%\SnipClone\settings.json`.

## How it was made

This repo follows the "see a feature, understand it, rebuild it" workflow from the REA project:

1. **Install REA.** The [REA MCP server](https://github.com/morluto/rea) and its
   `reverse-engineer-anything` skill were connected to Claude Code.
2. **Find the target.** Claude located the installed `Microsoft.ScreenSketch` package
   (Snipping Tool 11.2608.41.0).
3. **Inspect it with REA.** `inspect_managed_members` on the package's WinRT metadata
   (`SnippingTool.Capture.winmd` and `SnippingToolUI.winmd`) revealed 330 UI types, the capture modes, view
   models, tool enums, settings and the Windows.Graphics.Capture interop. Claude also read the manifest.
4. **Write the findings.** Each observation went into [FINDINGS.md](FINDINGS.md) with its REA evidence ID.
5. **Rebuild it clean-room in the platform's native stack.** The original is C++/WinRT + XAML, so SnipClone
   2.0 is C#/WPF and uses the same capture API for window snips. No Microsoft code, assets or strings were
   copied. (Version 1 was a Python prototype; it's kept under the
   [`v1-python`](https://github.com/AndroPlus/snipclone/tree/v1-python) tag.)
6. **Test it.** Every capture mode and editor tool was tested with real OS mouse and keyboard input
   at 125% display scaling.

## Code

| File | What it does |
|---|---|
| `src/App.xaml(.cs)` | Fluent theme, single-instance check, `--snip` |
| `src/MainWindow.xaml(.cs)` | Toolbars, editor (InkCanvas, shapes, text, crop, undo/redo), output, settings, hotkey |
| `src/OverlayWindow.cs` | Frozen-snapshot overlay and all capture modes |
| `src/ScreenCapture.cs` | GDI virtual-screen snapshot and Windows.Graphics.Capture window capture |
| `src/Native.cs` | Win32/DWM declarations and the window list |
| `src/Settings.cs` | JSON settings |

## Not included

Text extraction (OCR), AI smart crop, screen recording and QR scanning. The original builds these on
Microsoft's own OCR and segmentation models (see FINDINGS.md).

SnipClone is an independent project and is not affiliated with or endorsed by Microsoft. MIT licensed.
