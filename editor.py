"""Annotation editor. Edits are kept as a list of vector ops on top of a base bitmap, so strokes can be
erased one at a time and undo/redo is just restoring an earlier (base, ops) state."""

import math
import tkinter as tk

from PIL import Image, ImageDraw, ImageFont, ImageTk

HIGHLIGHT_ALPHA = 110
TOOLS = ("pen", "highlighter", "eraser", "rect", "oval", "line", "arrow", "text", "crop")


def _font(size):
    for name in ("segoeui.ttf", "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def _arrow_head(x0, y0, x1, y1, width):
    ang = math.atan2(y1 - y0, x1 - x0)
    size = max(10, width * 4)
    return [(x1, y1),
            (x1 - size * math.cos(ang - 0.45), y1 - size * math.sin(ang - 0.45)),
            (x1 - size * math.cos(ang + 0.45), y1 - size * math.sin(ang + 0.45))]


def draw_op(draw, op):
    kind, color, w, pts = op["kind"], op["color"], op["width"], op["points"]
    if kind in ("pen", "highlighter"):
        if len(pts) == 1:
            pts = pts * 2
        draw.line(pts, fill=color, width=w, joint="curve")
        r = w / 2
        for x, y in (pts[0], pts[-1]):
            draw.ellipse((x - r, y - r, x + r, y + r), fill=color)
    elif kind in ("rect", "oval"):
        (x0, y0), (x1, y1) = pts
        box = (min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))
        (draw.rectangle if kind == "rect" else draw.ellipse)(box, outline=color, width=w)
    elif kind in ("line", "arrow"):
        draw.line(pts, fill=color, width=w)
        if kind == "arrow":
            draw.polygon(_arrow_head(*pts[0], *pts[1], w), fill=color)
    elif kind == "text":
        draw.text(pts[0], op["text"], fill=color, font=_font(op["size"]))


def render(base, ops):
    img = base.convert("RGBA")
    for op in ops:
        if op["kind"] == "highlighter":
            layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
            draw_op(ImageDraw.Draw(layer), dict(op, color=op["color"] + (255,)))
            alpha = layer.getchannel("A").point(lambda a: HIGHLIGHT_ALPHA if a else 0)
            layer.putalpha(alpha)
            img = Image.alpha_composite(img, layer)
        else:
            draw_op(ImageDraw.Draw(img), op)
    return img.convert("RGB")


def _dist_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    if dx == dy == 0:
        return math.hypot(px - ax, py - ay)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)


def hit_test(op, x, y, tol):
    pts = op["points"]
    if op["kind"] in ("pen", "highlighter", "line", "arrow"):
        if len(pts) == 1:
            return math.hypot(x - pts[0][0], y - pts[0][1]) <= tol + op["width"] / 2
        return any(_dist_to_segment(x, y, *a, *b) <= tol + op["width"] / 2 for a, b in zip(pts, pts[1:]))
    if op["kind"] in ("rect", "oval"):
        (x0, y0), (x1, y1) = pts
        l, r, t, b = min(x0, x1), max(x0, x1), min(y0, y1), max(y0, y1)
        inside_outer = l - tol <= x <= r + tol and t - tol <= y <= b + tol
        inside_inner = l + tol + op["width"] < x < r - tol - op["width"] and t + tol + op["width"] < y < b - tol - op["width"]
        return inside_outer and not inside_inner
    if op["kind"] == "text":
        x0, y0 = pts[0]
        lines = op["text"].split("\n")
        w = max(len(s) for s in lines) * op["size"] * 0.6
        h = len(lines) * op["size"] * 1.3
        return x0 - tol <= x <= x0 + w + tol and y0 - tol <= y <= y0 + h + tol
    return False


class Editor(tk.Frame):
    """Canvas that shows the snip fitted to the window and turns mouse input into ops."""

    def __init__(self, master, on_change=None, **kw):
        super().__init__(master, bg="#f3f3f3", **kw)
        self.on_change = on_change or (lambda: None)
        self.canvas = tk.Canvas(self, bg="#e9e9e9", highlightthickness=0)
        self.canvas.pack(fill="both", expand=True)
        self.base = None
        self.ops = []
        self.undo_stack, self.redo_stack = [], []
        self.tool, self.color, self.width = "pen", (0, 0, 0), 4
        self.scale, self.offset = 1.0, (0, 0)
        self.rendered = None
        self._photo = None
        self._live = None
        self._crop_rect = None
        self.dirty = False
        self._placeholder()
        self.canvas.bind("<Configure>", lambda e: self.after_idle(self.refresh_view))
        self.canvas.bind("<ButtonPress-1>", self._down)
        self.canvas.bind("<B1-Motion>", self._drag)
        self.canvas.bind("<ButtonRelease-1>", self._up)

    # ---------------------------------------------------------------- document

    def _placeholder(self):
        self.canvas.delete("all")
        self.canvas.create_text(20, 20, anchor="nw", fill="#666", font=("Segoe UI", 12),
                                text="Press New (or Ctrl+N) to take a snip.\n"
                                     "The global hotkey shown in the status bar works from anywhere.")

    def load(self, image):
        self.base, self.ops = image.convert("RGB"), []
        self.undo_stack.clear()
        self.redo_stack.clear()
        self.dirty = False
        self._rerender()

    @property
    def has_image(self):
        return self.base is not None

    def image(self):
        return self.rendered

    def _push_undo(self):
        self.undo_stack.append((self.base, list(self.ops)))
        self.redo_stack.clear()
        self.dirty = True

    def undo(self):
        if self.undo_stack:
            self.redo_stack.append((self.base, list(self.ops)))
            self.base, self.ops = self.undo_stack.pop()
            self.dirty = True
            self._rerender()

    def redo(self):
        if self.redo_stack:
            self.undo_stack.append((self.base, list(self.ops)))
            self.base, self.ops = self.redo_stack.pop()
            self.dirty = True
            self._rerender()

    def add_op(self, op):
        self._push_undo()
        self.ops.append(op)
        self._rerender()

    def clear_ink(self):
        if self.ops:
            self._push_undo()
            self.ops = []
            self._rerender()

    def _rerender(self):
        self.rendered = render(self.base, self.ops) if self.base else None
        self.refresh_view()
        self.on_change()

    # ---------------------------------------------------------------- view

    def refresh_view(self):
        if self.rendered is None:
            return
        cw, ch = max(self.canvas.winfo_width(), 50), max(self.canvas.winfo_height(), 50)
        iw, ih = self.rendered.size
        self.scale = min(1.0, (cw - 24) / iw, (ch - 24) / ih)
        dw, dh = max(1, int(iw * self.scale)), max(1, int(ih * self.scale))
        self.offset = ((cw - dw) // 2, (ch - dh) // 2)
        shown = self.rendered if self.scale == 1.0 else self.rendered.resize((dw, dh), Image.LANCZOS)
        self._photo = ImageTk.PhotoImage(shown)
        self.canvas.delete("all")
        ox, oy = self.offset
        self.canvas.create_rectangle(ox - 1, oy - 1, ox + dw, oy + dh, outline="#c8c8c8")
        self.canvas.create_image(ox, oy, anchor="nw", image=self._photo)
        self._crop_rect = None

    def to_image(self, x, y):
        return ((x - self.offset[0]) / self.scale, (y - self.offset[1]) / self.scale)

    def to_view(self, x, y):
        return (x * self.scale + self.offset[0], y * self.scale + self.offset[1])

    def _clamp(self, x, y):
        w, h = self.base.size
        return (min(max(x, 0), w - 1), min(max(y, 0), h - 1))

    # ---------------------------------------------------------------- input

    def _down(self, e):
        if not self.has_image:
            return
        p = self._clamp(*self.to_image(e.x, e.y))
        self._start = p
        self._points = [p]
        if self.tool == "eraser":
            self._erase_at(p, push=True)
        elif self.tool == "text":
            self._place_text(e, p)
        elif self.tool == "crop":
            self.canvas.delete("crop")

    def _drag(self, e):
        if not self.has_image or not hasattr(self, "_start") or self._start is None:
            return
        p = self._clamp(*self.to_image(e.x, e.y))
        hex_color = "#%02x%02x%02x" % self.color
        vw = max(1, self.width * self.scale)
        if self.tool in ("pen", "highlighter"):
            a = self.to_view(*self._points[-1])
            self._points.append(p)
            self.canvas.create_line(*a, e.x, e.y, fill=hex_color, width=vw, capstyle="round", tags="live",
                                    stipple="gray50" if self.tool == "highlighter" else "")
        elif self.tool == "eraser":
            self._erase_at(p, push=False)
        elif self.tool in ("rect", "oval", "line", "arrow", "crop"):
            self.canvas.delete("live")
            a = self.to_view(*self._start)
            if self.tool == "rect":
                self.canvas.create_rectangle(*a, e.x, e.y, outline=hex_color, width=vw, tags="live")
            elif self.tool == "oval":
                self.canvas.create_oval(*a, e.x, e.y, outline=hex_color, width=vw, tags="live")
            elif self.tool in ("line", "arrow"):
                self.canvas.create_line(*a, e.x, e.y, fill=hex_color, width=vw, tags="live",
                                        arrow="last" if self.tool == "arrow" else "none",
                                        arrowshape=(vw * 4, vw * 4, vw * 1.6))
            else:
                self.canvas.delete("crop")
                self.canvas.create_rectangle(*a, e.x, e.y, outline="white", width=2, dash=(6, 4), tags="crop")
            self._points = [self._start, p]

    def _up(self, e):
        if not self.has_image or getattr(self, "_start", None) is None:
            return
        self.canvas.delete("live")
        pts = [tuple(round(c) for c in pt) for pt in self._points]
        if self.tool in ("pen", "highlighter"):
            self.add_op({"kind": self.tool, "color": self.color, "width": self.width * (3 if self.tool == "highlighter" else 1),
                         "points": pts})
        elif self.tool in ("rect", "oval", "line", "arrow") and len(pts) == 2 and pts[0] != pts[1]:
            self.add_op({"kind": self.tool, "color": self.color, "width": self.width, "points": pts})
        elif self.tool == "crop" and len(pts) == 2:
            (x0, y0), (x1, y1) = pts
            self.pending_crop = (min(x0, x1), min(y0, y1), max(x0, x1) + 1, max(y0, y1) + 1)
            self.on_change()
        self._start = None

    def _erase_at(self, p, push):
        tol = 6 / self.scale
        for i in range(len(self.ops) - 1, -1, -1):
            if hit_test(self.ops[i], *p, tol):
                if push or not getattr(self, "_erased_this_drag", False):
                    self._push_undo()
                self._erased_this_drag = True
                del self.ops[i]
                self._rerender()
                return
        if push:
            self._erased_this_drag = False

    def _place_text(self, e, p):
        size = max(14, self.width * 5)
        entry = tk.Text(self.canvas, width=24, height=2, font=("Segoe UI", -max(8, int(size * self.scale))),
                        fg="#%02x%02x%02x" % self.color, relief="solid", bd=1, wrap="none")
        win = self.canvas.create_window(e.x, e.y, anchor="nw", window=entry)
        entry.focus_set()
        color = self.color

        def commit(_e=None):
            text = entry.get("1.0", "end").rstrip("\n")
            self.canvas.delete(win)
            entry.destroy()
            if text.strip():
                self.add_op({"kind": "text", "color": color, "width": 1, "size": size, "text": text,
                             "points": [tuple(round(c) for c in p)]})
            return "break"

        entry.bind("<Escape>", commit)
        entry.bind("<FocusOut>", commit)
        entry.bind("<Control-Return>", commit)
        self._start = None

    # ---------------------------------------------------------------- crop

    pending_crop = None

    def apply_crop(self):
        if not self.pending_crop:
            return
        l, t, r, b = self.pending_crop
        if r - l > 2 and b - t > 2:
            self._push_undo()
            self.base, self.ops = self.rendered.crop((l, t, r, b)), []
            self._rerender()
        self.pending_crop = None

    def cancel_crop(self):
        self.pending_crop = None
        self.canvas.delete("crop")
        self.on_change()
