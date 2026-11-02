# Snipping Tool: REA findings (clean-room notes)

This investigation was done by Claude (in [Claude Code](https://claude.com/claude-code)) using the
[REA MCP server](https://github.com/morluto/rea) and its `reverse-engineer-anything` skill. The REA tools
used were `inspect_managed_members`, `inspect_analysis_view` and `export_evidence_bundle`.

Target: `Microsoft.ScreenSketch` 11.2608.41.0, installed at
`C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2608.41.0_x64__8wekyb3d8bbwe`.

These notes come only from static metadata (WinRT `.winmd` interface files and the package manifest).
No Microsoft code, assets or strings were copied into SnipClone. They record **what** the
app does and how it is split up. The clone's code is written from scratch.

## Evidence

| ID | Source | What it covers |
|---|---|---|
| `ev_38be6b48…bc3cb` | REA `inspect_managed_members` on `SnippingTool.Capture.winmd` | Capture interop surface |
| `ev_636c4888…b298446` | REA `inspect_managed_members` on `SnippingToolUI.winmd` (330 types, 3,365 methods, coverage complete) | UI types, enums, view models |
| `AppxManifest.xml` | Read directly | Protocols, capabilities, file types |

The full REA evidence bundle (`export_evidence_bundle`, about 4 MB) isn't committed. You can recreate it by
running the same REA calls against your own installed Snipping Tool.

## Architecture

| Layer | File | Observation |
|---|---|---|
| Host | `SnippingTool.exe` (C++/WinRT, `Windows.FullTrustApplication`) | Entry point, also registered as the `SnippingTool.exe` execution alias |
| UI | `SnippingToolUI.dll` + `.winmd` | XAML views and view models (MVVM): `MainPage`, `SnipOverlayView`, `AnnotateCanvas`, `SettingsView` |
| Capture | `SnippingTool.Capture.dll` | `CaptureInterop.CreateCaptureItemForDesktop()`, `CreateCaptureItemForMonitor(HMONITOR)` and `ShowPrintUIForWindowAsync`, which wrap Windows.Graphics.Capture |
| OCR | `oneocr.dll` + `oneocr.onemodel` (58 MB) | Text extractor (`OcrResult`, `OcrLine`, `OcrWord`, `OcrTable`) |
| AI crop | `seg*.onnxe`, `inseg_*.onnxe` + `onnxruntime.dll` + OpenCV | `CropModeManager.DetectEdges/SnapToEdges/SAMMask`: segmentation-based smart crop |
| Protocols | Manifest | `ms-screenclip:` (the Win+Shift+S overlay) and `ms-screensketch:` |
| Capabilities | Manifest | `graphicsCaptureWithoutBorder` (no yellow capture border), `picturesLibrary` (auto-save), `microphone` (recording) |

## Features, as stated by the metadata

**Capture modes** (`SnippingMode` enum): Rectangle, Window, Fullscreen, Freeform, plus Recording,
TextExtractor, ColorPicker and RecordingWindow.

**Overlay** (`SnipOverlayViewModel`):
- It shows a frozen snapshot of the screen (`SnapshotOpacity`) dimmed by a "smoke" layer
  (`IsSmokeVisible`, `SmokeFadeIn/OutRequested`). The selected area is shown undimmed.
- `SelectionRectangle` has resize handles (`SelectionRectangleHandle`) and keyboard control
  (`KeyboardClipping`: `EscapePressed`, pointer events).
- Window mode highlights the window under the cursor (`WindowSelection.HighlightWindow`,
  `WindowHighlightRequested`) and then selects it.
- Freeform mode builds a path from stroke points (`FreeformSelection.BeginStroke/AppendStroke/EndStroke`,
  `ClippingHelper.UpdateFreeformPath/CutoutPath`).
- `CompleteFullScreenCapture` captures with no selection step.
- The color picker offers HEX, RGB and HSL formats (`ColorFormat`).

**Delay** (`SnipDelayOption`: Delay and DisplayValue). `DoesSnippingModeSupportDelays` means not every
mode allows a delay.

**After capture** (`SettingsViewModel`): `IsAutoCopyScreenshotToClipboardOn`,
`IsAutoSaveScreenshotsOn` with `AutoSaveScreenshotsLocation`, `IsOutlineOn` with
`OutlineStrokeColor/Thickness`, and `ShowUnsavedScreenshotChangesConfirmations`.

**Editor** (`MainPageViewModel` and `AnnotateCanvasViewModel`):
- Tools (`SelectedToolButton`): BallpointPen, Highlighter, StrokeEraser, Shapes and Text.
- Pen and highlighter each have a width and a brush index (`PenWidth`, `PenBrushIndex`, …).
- Undo and redo (`HasUndoOperations`, `HasRedoOperations`).
- Shapes (`ShapeType`): Square, Oval, Line, Arrow and Emoji, with outline and fill color and transparency.
- Crop (`MoveCroppingRectangle`, `ResizeCroppingRectangle`, `GetMinCropSize`).
- Actions (`ActionType`): Copy, Save, Share, VisualSearch and Copilot.
- View states: Initial, Loading, LoadingDelayed, Annotation and Crop.

## What SnipClone builds, and what it leaves out

SnipClone 2.0 is written in C# with WPF on .NET 9, the native Windows stack closest to the original's
C++/WinRT + XAML. Version 1 was a Python/tkinter prototype; it is kept under the `v1-python` tag.

| Feature | Original | SnipClone 2.0 |
|---|---|---|
| Rectangle, Fullscreen and Freeform | Windows.Graphics.Capture + XAML overlay | GDI copy of the virtual screen shown in a WPF overlay with the same "frozen snapshot + smoke" design. The selection is revealed through a clip geometry |
| Window snip | Windows.Graphics.Capture (`CaptureInterop.CreateCaptureItemForDesktop/ForMonitor` were observed) + the `graphicsCaptureWithoutBorder` capability | **The same API family:** `IGraphicsCaptureItemInterop.CreateForWindow` → `Direct3D11CaptureFramePool` with `IsBorderRequired = false`. It captures the window's own surface, so windows on top of it don't appear |
| Window highlight | `WindowSelection.HighlightWindow` | `EnumWindows` in z-order + `DwmGetWindowAttribute` frame bounds |
| Color picker (HEX/RGB/HSL) | Auxiliary mode + `ColorFormat` | Included |
| Delay | `SnipDelayOption` | None, 3, 5 or 10 s, for the modes that support it |
| Auto-copy and auto-save | `IsAutoCopyScreenshotToClipboardOn`, `IsAutoSaveScreenshotsOn` | WPF clipboard + PNG in `Pictures\Screenshots` |
| Pen, highlighter, stroke eraser | AnnotateCanvas (`BallpointPen`, `Highlighter`, `StrokeEraser`) | WPF `InkCanvas` with highlighter drawing attributes and `EraseByStroke` |
| Shapes, text, crop, undo/redo | `ShapeType`, `TextEdit`, `CropRectangle`, undo/redo operations | Shapes are stored as ink strokes, so the eraser handles them. Text labels, crop, and snapshot-based undo/redo |
| Per-monitor DPI | — | PerMonitorV2 manifest. 1 overlay unit = 1 physical pixel, and snips open at 1:1 size |
| Global hotkey | `ms-screenclip` via Win+Shift+S (owned by Windows) | The first free one of Ctrl+Alt+S, Ctrl+Shift+Alt+S, Ctrl+Alt+Q and Ctrl+Print Screen, using `RegisterHotKey` |
| Text extractor (OCR), AI smart crop, recording, QR codes, Copilot/visual search | OneOCR, ONNX models, MediaCapture | **Not included**: they depend on proprietary models or need a large separate project |
