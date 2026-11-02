"""SnipClone: a clean-room screen snipping tool for Windows (tkinter + Pillow).

Built with Claude Code. Snipping Tool's design was studied with the REA MCP (github.com/morluto/rea)
before this code was written from scratch; see FINDINGS.md.

Run: python main.py          (add --snip to open the capture overlay immediately)
Global hotkey: Ctrl+Alt+S
"""

import json
import os
import queue
import sys
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

import winapi

winapi.enable_dpi_awareness()  # must happen before Tk creates any window

from PIL import ImageOps  # noqa: E402

from editor import Editor  # noqa: E402
from overlay import MODES, CaptureOverlay  # noqa: E402

APP = "SnipClone"
BASE = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))  # bundle dir when built as an exe
ICON = BASE / "assets" / "icon.ico"
CONFIG_PATH = Path(os.environ.get("APPDATA", Path.home())) / APP / "settings.json"
DEFAULTS = {
    "mode": "rectangle",
    "delay": 0,
    "color_format": "HEX",
    "auto_copy": True,
    "auto_save": True,
    "save_dir": str(Path.home() / "Pictures" / "Screenshots"),
    "outline": False,
    "outline_color": "#7a7a7a",
    "confirm_unsaved": True,
}
DELAYS = [(0, "No delay"), (3, "3 seconds"), (5, "5 seconds"), (10, "10 seconds")]
PALETTE = [(0, 0, 0), (255, 255, 255), (232, 17, 35), (255, 140, 0), (255, 241, 0),
           (0, 204, 106), (0, 120, 215), (136, 23, 152)]
TOOL_BUTTONS = [("pen", "✒ Pen"), ("highlighter", "▮ Highlight"), ("eraser", "⌫ Eraser"),
                ("rect", "▭"), ("oval", "◯"), ("line", "╱"), ("arrow", "➚"), ("text", "T Text"), ("crop", "⛶ Crop")]


def load_settings():
    try:
        return {**DEFAULTS, **json.loads(CONFIG_PATH.read_text("utf-8"))}
    except (OSError, ValueError):
        return dict(DEFAULTS)


def save_settings(cfg):
    try:
        CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        CONFIG_PATH.write_text(json.dumps(cfg, indent=2), "utf-8")
    except OSError:
        pass


class App:
    def __init__(self, root, snip_now=False):
        self.root = root
        self.cfg = load_settings()
        self.hotkey_events = queue.Queue()
        self.capturing = False
        root.title(APP)
        if ICON.exists():
            root.iconbitmap(default=str(ICON))
        root.geometry("1000x680")
        root.minsize(640, 360)
        root.configure(bg="#f3f3f3")
        self._style()
        self._build_toolbar()
        self.editor = Editor(root, on_change=self._update_buttons)
        self.editor.pack(fill="both", expand=True)
        self.status = tk.Label(root, anchor="w", bg="#f3f3f3", fg="#555", font=("Segoe UI", 9), padx=10)
        self.status.pack(fill="x", side="bottom")
        self._bind_keys()
        self._select_tool("pen")
        self._update_buttons()
        root.protocol("WM_DELETE_WINDOW", self.quit)

        ctrl, alt, shift = winapi.MOD_CONTROL, winapi.MOD_ALT, winapi.MOD_SHIFT
        ready = winapi.start_hotkey_thread([("Ctrl+Alt+S", ctrl | alt, ord("S")),
                                            ("Ctrl+Shift+Alt+S", ctrl | shift | alt, ord("S")),
                                            ("Ctrl+Alt+Q", ctrl | alt, ord("Q")),
                                            ("Ctrl+Print Screen", ctrl, 0x2C)],
                                           lambda: self.hotkey_events.put("snip"))
        self.hotkey = ready.label
        self.set_status(f"Ready. {self.hotkey} starts a snip from anywhere." if self.hotkey
                        else "Ready. (All SnipClone hotkeys are taken by other apps, so use the New button.)")
        self._poll_hotkey()
        if snip_now:
            root.after(300, self.new_snip)

    # ---------------------------------------------------------------- UI construction

    def _style(self):
        s = ttk.Style()
        s.theme_use("vista" if "vista" in s.theme_names() else s.theme_use())
        s.configure("TCombobox", padding=4)

    def _build_toolbar(self):
        bar = tk.Frame(self.root, bg="#f3f3f3", padx=8, pady=6)
        bar.pack(fill="x")
        btn = dict(relief="flat", bd=0, bg="#f3f3f3", activebackground="#e0e0e0", font=("Segoe UI", 10), padx=8, pady=4)

        tk.Button(bar, text="＋ New", command=self.new_snip, **dict(btn, bg="#0067c0", fg="white",
                                                                    activebackground="#1975c5", activeforeground="white")).pack(side="left")
        self.mode_var = tk.StringVar(value=dict(MODES)[self.cfg["mode"]])
        mode_box = ttk.Combobox(bar, textvariable=self.mode_var, values=[m[1] for m in MODES], state="readonly", width=14)
        mode_box.pack(side="left", padx=(6, 2))
        mode_box.bind("<<ComboboxSelected>>", lambda e: self._set_cfg("mode", {v: k for k, v in MODES}[self.mode_var.get()]))
        self.delay_var = tk.StringVar(value=dict(DELAYS)[self.cfg["delay"]])
        delay_box = ttk.Combobox(bar, textvariable=self.delay_var, values=[d[1] for d in DELAYS], state="readonly", width=10)
        delay_box.pack(side="left", padx=2)
        delay_box.bind("<<ComboboxSelected>>", lambda e: self._set_cfg("delay", {v: k for k, v in DELAYS}[self.delay_var.get()]))

        # Second row: drawing tools, colors and stroke width.
        tools = self.tools_row = tk.Frame(self.root, bg="#f3f3f3", padx=8)
        tools.pack(fill="x", pady=(0, 6))
        self.tool_buttons = {}
        for key, label in TOOL_BUTTONS:
            b = tk.Button(tools, text=label, command=lambda k=key: self._select_tool(k), **btn)
            b.pack(side="left", padx=1)
            self.tool_buttons[key] = b

        ttk.Separator(tools, orient="vertical").pack(side="left", fill="y", padx=8)
        self.swatches = []
        for rgb in PALETTE:
            hexc = "#%02x%02x%02x" % rgb
            sw = tk.Frame(tools, width=18, height=18, bg=hexc, highlightthickness=2, highlightbackground="#c8c8c8", cursor="hand2")
            sw.pack(side="left", padx=1)
            sw.bind("<Button-1>", lambda e, c=rgb: self._select_color(c))
            self.swatches.append((rgb, sw))
        tk.Label(tools, text="Size", bg="#f3f3f3", fg="#555", font=("Segoe UI", 9)).pack(side="left", padx=(10, 2))
        self.width_var = tk.IntVar(value=4)
        ttk.Scale(tools, from_=1, to=20, variable=self.width_var, length=90,
                  command=lambda v: setattr(self.editor, "width", int(float(v)))).pack(side="left", padx=2)

        right = tk.Frame(bar, bg="#f3f3f3")
        right.pack(side="right")
        self.btn_undo = tk.Button(right, text="↶", command=lambda: self.editor.undo(), **btn)
        self.btn_redo = tk.Button(right, text="↷", command=lambda: self.editor.redo(), **btn)
        self.btn_copy = tk.Button(right, text="⧉ Copy", command=self.copy, **btn)
        self.btn_save = tk.Button(right, text="💾 Save", command=self.save_as, **btn)
        settings = tk.Menubutton(right, text="⚙", relief="flat", bg="#f3f3f3", font=("Segoe UI", 11), padx=8)
        for w in (self.btn_undo, self.btn_redo, self.btn_copy, self.btn_save, settings):
            w.pack(side="left", padx=1)
        self._settings_menu(settings)

        # Crop confirmation bar, shown only while a crop rectangle is pending.
        self.crop_bar = tk.Frame(self.root, bg="#fff4ce", padx=10, pady=4)
        tk.Label(self.crop_bar, text="Drag to choose the area to keep.", bg="#fff4ce", font=("Segoe UI", 9)).pack(side="left")
        tk.Button(self.crop_bar, text="✓ Apply (Enter)", command=self._apply_crop, **dict(btn, bg="#fff4ce")).pack(side="right")
        tk.Button(self.crop_bar, text="✕ Cancel (Esc)", command=self._cancel_crop, **dict(btn, bg="#fff4ce")).pack(side="right")

    def _settings_menu(self, mb):
        menu = tk.Menu(mb, tearoff=False)
        mb["menu"] = menu
        self.setting_vars = {}
        for key, label in (("auto_copy", "Automatically copy to clipboard"),
                           ("auto_save", "Automatically save screenshots"),
                           ("outline", "Add outline to screenshots"),
                           ("confirm_unsaved", "Ask to save edited screenshots")):
            v = tk.BooleanVar(value=self.cfg[key])
            self.setting_vars[key] = v
            menu.add_checkbutton(label=label, variable=v, command=lambda k=key, var=v: self._set_cfg(k, var.get()))
        menu.add_separator()
        fmt = tk.Menu(menu, tearoff=False)
        self.fmt_var = tk.StringVar(value=self.cfg["color_format"])
        for f in ("HEX", "RGB", "HSL"):
            fmt.add_radiobutton(label=f, variable=self.fmt_var, value=f, command=lambda: self._set_cfg("color_format", self.fmt_var.get()))
        menu.add_cascade(label="Color picker format", menu=fmt)
        menu.add_command(label="Change screenshot folder…", command=self._choose_folder)
        menu.add_command(label="Open screenshot folder", command=lambda: os.startfile(self._save_dir()))
        menu.add_separator()
        menu.add_command(label=f"About {APP}", command=lambda: messagebox.showinfo(
            f"About {APP}",
            f"{APP}: a clean-room screen snipping tool.\n\n"
            "Built with Claude Code. Snipping Tool was first studied with the REA MCP "
            "(Reverse Engineer Anything, github.com/morluto/rea), then SnipClone was written from scratch.\n\n"
            "Not affiliated with Microsoft.\n\ngithub.com/AndroPlus/snipclone"))

    def _bind_keys(self):
        r = self.root
        r.bind("<Control-n>", lambda e: self.new_snip())
        r.bind("<Control-c>", lambda e: self.copy())
        r.bind("<Control-s>", lambda e: self.save_as())
        r.bind("<Control-z>", lambda e: self.editor.undo())
        r.bind("<Control-y>", lambda e: self.editor.redo())
        r.bind("<Return>", lambda e: self._apply_crop() if self.editor.pending_crop else None)
        r.bind("<Escape>", lambda e: self._cancel_crop())

    # ---------------------------------------------------------------- state helpers

    def set_status(self, text):
        self.status.configure(text=text)

    def _set_cfg(self, key, value):
        self.cfg[key] = value
        save_settings(self.cfg)

    def _save_dir(self):
        d = Path(self.cfg["save_dir"])
        d.mkdir(parents=True, exist_ok=True)
        return str(d)

    def _choose_folder(self):
        d = filedialog.askdirectory(initialdir=self._save_dir(), title="Screenshot folder")
        if d:
            self._set_cfg("save_dir", d)

    def _select_tool(self, tool):
        if self.editor.pending_crop and tool != "crop":
            self._cancel_crop()
        self.editor.tool = tool
        for k, b in self.tool_buttons.items():
            b.configure(bg="#cfe4f7" if k == tool else "#f3f3f3")
        self.editor.canvas.configure(cursor={"eraser": "dotbox", "text": "xterm", "crop": "crosshair"}.get(tool, "pencil"))
        self._update_buttons()

    def _select_color(self, rgb):
        self.editor.color = rgb
        for c, sw in self.swatches:
            sw.configure(highlightbackground="#0067c0" if c == rgb else "#c8c8c8")

    def _update_buttons(self):
        has = self.editor.has_image
        for b, on in ((self.btn_copy, has), (self.btn_save, has),
                      (self.btn_undo, bool(self.editor.undo_stack)), (self.btn_redo, bool(self.editor.redo_stack))):
            b.configure(state="normal" if on else "disabled")
        if self.editor.pending_crop or (self.editor.tool == "crop" and has):
            if not self.crop_bar.winfo_ismapped():
                self.crop_bar.pack(fill="x", after=self.tools_row)
        elif self.crop_bar.winfo_ismapped():
            self.crop_bar.pack_forget()

    def _apply_crop(self):
        self.editor.apply_crop()
        self._select_tool("pen")

    def _cancel_crop(self):
        self.editor.cancel_crop()
        if self.editor.tool == "crop":
            self._select_tool("pen")

    def _confirm_discard(self):
        if not (self.editor.has_image and self.editor.dirty and self.cfg["confirm_unsaved"]):
            return True
        answer = messagebox.askyesnocancel(APP, "Save changes to this screenshot?")
        if answer is None:
            return False
        return self.save_as() if answer else True

    # ---------------------------------------------------------------- capture flow

    def _poll_hotkey(self):
        try:
            while True:
                self.hotkey_events.get_nowait()
                self.new_snip()
        except queue.Empty:
            pass
        self.root.after(100, self._poll_hotkey)

    def new_snip(self):
        if self.capturing or not self._confirm_discard():
            return
        self.capturing = True
        self._was_visible = self.root.state() != "withdrawn"
        self.root.withdraw()
        mode, delay = self.cfg["mode"], self.cfg["delay"]
        if mode == "fullscreen" or mode == "color":
            delay = 0
        self._countdown(delay, mode)

    def _countdown(self, remaining, mode):
        if remaining > 0:
            self._show_toast(f"Snip in {remaining}…")
            self.root.after(1000, lambda: self._countdown(remaining - 1, mode))
            return
        self._hide_toast()
        # Give the compositor a moment to remove our window before grabbing the screen.
        self.root.after(250, lambda: CaptureOverlay(self.root, mode, self.cfg["color_format"], self._on_captured,
                                                    own_hwnds=(int(self.root.wm_frame(), 16),)))

    def _show_toast(self, text):
        if not getattr(self, "toast", None):
            self.toast = tk.Toplevel(self.root)
            self.toast.overrideredirect(True)
            self.toast.attributes("-topmost", True)
            self.toast_label = tk.Label(self.toast, bg="#202020", fg="white", font=("Segoe UI", 14), padx=18, pady=10)
            self.toast_label.pack()
            sw = self.root.winfo_screenwidth()
            self.toast.geometry(f"+{sw // 2 - 80}+40")
        self.toast_label.configure(text=text)

    def _hide_toast(self):
        if getattr(self, "toast", None):
            self.toast.destroy()
            self.toast = None

    def _on_captured(self, image, info):
        self.capturing = False
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()
        if info.get("mode") == "color" and info.get("color"):
            self.root.clipboard_clear()
            self.root.clipboard_append(info["color"])
            self.set_status(f"Copied color {info['color']} to the clipboard.")
            return
        if image is None:
            self.set_status("Snip canceled.")
            return
        if self.cfg["outline"]:
            image = ImageOps.expand(image, border=1, fill=self.cfg["outline_color"])
        self.editor.load(image)
        self._select_tool("pen")
        notes = [f"{image.width} × {image.height}"]
        if self.cfg["auto_copy"]:
            try:
                winapi.copy_image_to_clipboard(image)
                notes.append("copied to clipboard")
            except OSError as e:
                notes.append(f"copy failed: {e}")
        if self.cfg["auto_save"]:
            path = Path(self._save_dir()) / time.strftime("Screenshot %Y-%m-%d %H%M%S.png")
            image.save(path)
            notes.append(f"saved to {path}")
        self.set_status(" · ".join(notes))

    # ---------------------------------------------------------------- actions

    def copy(self):
        if isinstance(self.root.focus_get(), tk.Text):
            return  # Ctrl+C inside a text box copies text, not the image
        if self.editor.has_image:
            winapi.copy_image_to_clipboard(self.editor.image())
            self.set_status("Copied to clipboard.")

    def save_as(self):
        if not self.editor.has_image:
            return False
        path = filedialog.asksaveasfilename(
            title="Save as", initialdir=self._save_dir(), defaultextension=".png",
            initialfile=time.strftime("Screenshot %Y-%m-%d %H%M%S"),
            filetypes=[("PNG", "*.png"), ("JPEG", "*.jpg"), ("GIF", "*.gif"), ("BMP", "*.bmp")])
        if not path:
            return False
        img = self.editor.image()
        if path.lower().endswith((".jpg", ".jpeg")):
            img.save(path, quality=92)
        else:
            img.save(path)
        self.editor.dirty = False
        self.set_status(f"Saved {path}")
        return True

    def quit(self):
        if self._confirm_discard():
            self.root.destroy()


def main():
    root = tk.Tk()
    App(root, snip_now="--snip" in sys.argv)
    root.mainloop()


if __name__ == "__main__":
    main()
