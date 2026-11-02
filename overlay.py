"""Full-screen capture overlay: a frozen snapshot of every monitor, dimmed ("smoke"), with the selected
area shown at full brightness. Modes: rectangle, window, fullscreen, freeform, color picker."""

import colorsys
import tkinter as tk

from PIL import Image, ImageDraw, ImageEnhance, ImageGrab, ImageTk

import winapi

MODES = [("rectangle", "▭ Rectangle"), ("window", "▢ Window"), ("fullscreen", "⛶ Full screen"),
         ("freeform", "✎ Freeform"), ("color", "◉ Color picker")]
SMOKE = 0.45  # brightness of the dimmed snapshot


def format_color(rgb, fmt):
    r, g, b = rgb
    if fmt == "RGB":
        return f"rgb({r}, {g}, {b})"
    if fmt == "HSL":
        h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
        return f"hsl({round(h * 360)}, {round(s * 100)}%, {round(l * 100)}%)"
    return f"#{r:02X}{g:02X}{b:02X}"


class CaptureOverlay:
    """Shows the overlay and calls on_done(image_or_None, info). info is a dict:
    {"mode": ..., "color": text} for the color picker."""

    def __init__(self, root, mode, color_format, on_done, own_hwnds=()):
        self.root, self.mode, self.color_format, self.on_done = root, mode, color_format, on_done
        self.vx, self.vy, vw, vh = winapi.virtual_screen()
        # Enumerate windows before our overlay exists so it can't pick itself.
        self.windows = [w for w in winapi.visible_windows(own_hwnds)]
        self.shot = ImageGrab.grab(all_screens=True).convert("RGB")
        self.dim = ImageEnhance.Brightness(self.shot).enhance(SMOKE)

        self.win = tk.Toplevel(root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.geometry(f"{self.shot.width}x{self.shot.height}+{self.vx}+{self.vy}")
        self.canvas = tk.Canvas(self.win, highlightthickness=0, cursor="crosshair",
                                width=self.shot.width, height=self.shot.height)
        self.canvas.pack(fill="both", expand=True)
        self._bg = ImageTk.PhotoImage(self.dim)
        self.canvas.create_image(0, 0, anchor="nw", image=self._bg)
        self._hi = None
        self._hi_item = self.canvas.create_image(0, 0, anchor="nw")
        self._outline = self.canvas.create_rectangle(0, 0, 0, 0, outline="#4cc2ff", width=2, state="hidden")
        self._path_item = self.canvas.create_line(0, 0, 0, 0, fill="#ffffff", width=2, state="hidden")
        self._badge = self.canvas.create_text(0, 0, anchor="nw", fill="white", font=("Segoe UI", 10),
                                              state="hidden")
        self._badge_bg = self.canvas.create_rectangle(0, 0, 0, 0, fill="#202020", outline="#555", state="hidden")
        self.canvas.tag_raise(self._badge)
        self._pending = None
        self.start = None
        self.points = []
        self.selection = None

        self._build_toolbar()
        for seq, fn in (("<ButtonPress-1>", self._down), ("<B1-Motion>", self._drag),
                        ("<ButtonRelease-1>", self._up), ("<Motion>", self._move)):
            self.canvas.bind(seq, fn)
        self.win.bind("<Escape>", lambda e: self._finish(None))
        self.win.bind("<Return>", lambda e: self._confirm_selection())
        self.win.focus_force()
        self.set_mode(mode)

    # ---------------------------------------------------------------- toolbar

    def _build_toolbar(self):
        bar = tk.Frame(self.canvas, bg="#2b2b2b", padx=4, pady=4)
        self._mode_buttons = {}
        for key, label in MODES:
            b = tk.Button(bar, text=label, relief="flat", bg="#2b2b2b", fg="white", activebackground="#3d3d3d",
                          activeforeground="white", bd=0, padx=10, pady=4, font=("Segoe UI", 10),
                          command=lambda k=key: self.set_mode(k))
            b.pack(side="left", padx=1)
            self._mode_buttons[key] = b
        tk.Button(bar, text="✕", relief="flat", bg="#2b2b2b", fg="white", activebackground="#c42b1c", bd=0,
                  padx=10, pady=4, font=("Segoe UI", 10), command=lambda: self._finish(None)).pack(side="left", padx=(8, 0))
        # Centre the toolbar on the primary monitor (screen origin 0,0 in virtual coordinates).
        primary_w = self.win.winfo_screenwidth()
        self.canvas.create_window(-self.vx + primary_w // 2, -self.vy + 12, anchor="n", window=bar)

    def set_mode(self, mode):
        self.mode = mode
        for k, b in self._mode_buttons.items():
            b.configure(bg="#0067c0" if k == mode else "#2b2b2b")
        self._clear_selection()
        if mode == "fullscreen":
            # Brief flash of the bright snapshot, then capture everything.
            self._show_bright((0, 0, self.shot.width, self.shot.height))
            self.win.after(120, lambda: self._finish(self.shot.copy()))

    # ---------------------------------------------------------------- drawing helpers

    def _clear_selection(self):
        self.selection, self.start, self.points = None, None, []
        self.canvas.itemconfigure(self._hi_item, image="")
        for item in (self._outline, self._path_item, self._badge, self._badge_bg):
            self.canvas.itemconfigure(item, state="hidden")

    def _show_bright(self, box, mask=None):
        """Show the snapshot at full brightness inside box (optionally limited to a mask)."""
        l, t, r, b = box
        if r - l < 1 or b - t < 1:
            self.canvas.itemconfigure(self._hi_item, image="")
            return
        crop = self.shot.crop(box)
        if mask is not None:
            crop = Image.composite(crop, self.dim.crop(box), mask)
        self._hi = ImageTk.PhotoImage(crop)
        self.canvas.itemconfigure(self._hi_item, image=self._hi)
        self.canvas.coords(self._hi_item, l, t)
        self.canvas.coords(self._outline, l, t, r, b)
        self.canvas.itemconfigure(self._outline, state="normal" if mask is None else "hidden")

    def _show_badge(self, x, y, text):
        self.canvas.itemconfigure(self._badge, text=text, state="normal")
        self.canvas.coords(self._badge, x + 16, y + 18)
        bx = self.canvas.bbox(self._badge)
        self.canvas.coords(self._badge_bg, bx[0] - 6, bx[1] - 3, bx[2] + 6, bx[3] + 3)
        self.canvas.itemconfigure(self._badge_bg, state="normal")
        self.canvas.tag_raise(self._badge_bg)
        self.canvas.tag_raise(self._badge)

    def _throttled(self, fn):
        """Coalesce rapid mouse events: only the latest pending update runs."""
        self._pending = fn
        if not getattr(self, "_scheduled", False):
            self._scheduled = True

            def run():
                self._scheduled = False
                if self._pending:
                    p, self._pending = self._pending, None
                    p()
            self.win.after(8, run)

    # ---------------------------------------------------------------- mouse

    def _move(self, e):
        if self.mode == "window" and self.start is None:
            hit = self._window_at(e.x + self.vx, e.y + self.vy)
            self._throttled(lambda: self._highlight_window(hit))
        elif self.mode == "color":
            rgb = self.shot.getpixel((min(max(e.x, 0), self.shot.width - 1), min(max(e.y, 0), self.shot.height - 1)))
            self._show_badge(e.x, e.y, f"■ {format_color(rgb, self.color_format)}")
            self.canvas.itemconfigure(self._badge, fill=format_color(rgb, "HEX"))

    def _window_at(self, sx, sy):
        for title, (l, t, r, b) in self.windows:
            if l <= sx < r and t <= sy < b:
                return title, (l, t, r, b)
        return None

    def _highlight_window(self, hit):
        if not hit:
            self._clear_selection()
            return
        l, t, r, b = hit[1]
        box = (max(l - self.vx, 0), max(t - self.vy, 0),
               min(r - self.vx, self.shot.width), min(b - self.vy, self.shot.height))
        self.selection = box
        self._show_bright(box)

    def _down(self, e):
        if self.mode == "window":
            if self.selection:
                self._confirm_selection()
            return
        if self.mode == "color":
            rgb = self.shot.getpixel((e.x, e.y))
            self._finish(None, {"mode": "color", "color": format_color(rgb, self.color_format)})
            return
        self.start = (e.x, e.y)
        self.points = [(e.x, e.y)]
        if self.mode == "freeform":
            self.canvas.itemconfigure(self._path_item, state="normal")

    def _drag(self, e):
        if self.start is None:
            return
        x = min(max(e.x, 0), self.shot.width)
        y = min(max(e.y, 0), self.shot.height)
        if self.mode == "rectangle":
            x0, y0 = self.start
            box = (min(x0, x), min(y0, y), max(x0, x), max(y0, y))
            self.selection = box
            self._throttled(lambda: (self._show_bright(box),
                                     self._show_badge(box[2] - 90, box[3], f"{box[2] - box[0]} × {box[3] - box[1]}")))
        elif self.mode == "freeform":
            self.points.append((x, y))
            flat = [c for p in self.points for c in p]
            if len(flat) >= 4:
                self.canvas.coords(self._path_item, *flat)

    def _up(self, e):
        if self.start is None:
            return
        if self.mode == "rectangle":
            self._confirm_selection()
        elif self.mode == "freeform":
            self._finish_freeform()

    # ---------------------------------------------------------------- results

    def _confirm_selection(self):
        if not self.selection:
            return
        l, t, r, b = self.selection
        if r - l < 3 or b - t < 3:
            self._clear_selection()
            return
        self._finish(self.shot.crop(self.selection))

    def _finish_freeform(self):
        if len(self.points) < 3:
            self._clear_selection()
            return
        xs, ys = [p[0] for p in self.points], [p[1] for p in self.points]
        box = (min(xs), min(ys), max(xs) + 1, max(ys) + 1)
        if box[2] - box[0] < 3 or box[3] - box[1] < 3:
            self._clear_selection()
            return
        mask = Image.new("L", (box[2] - box[0], box[3] - box[1]), 0)
        ImageDraw.Draw(mask).polygon([(x - box[0], y - box[1]) for x, y in self.points], fill=255)
        # Outside the drawn shape becomes white, since a clipboard bitmap has no transparency.
        out = Image.new("RGB", mask.size, "white")
        out.paste(self.shot.crop(box), (0, 0), mask)
        self._finish(out)

    def _finish(self, image, info=None):
        if self.win is None:
            return
        self.win.destroy()
        self.win = None
        self.on_done(image, info or {"mode": self.mode})
