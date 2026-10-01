#!/usr/bin/env python3
"""Draws the README diagrams as SVG, then renders PNGs (rsvg-convert) next to them.

Hexium strips inline SVG and raw.githubusercontent.com serves .svg as text/plain, so the README
links the PNGs. Run from anywhere: python3 src/Feint/docs/diagrams.py

The numbers mirror Cancel.cs: DelayOfElapsed = 0.5, PeakProgress = 1 / 1.5, PenaltyAtHit = 0.5,
CancelledHitDamage = 0.5 (default).
"""
import os
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))

DELAY = 0.5
PEAK = 1 / (1 + DELAY)
PENALTY_AT_HIT = 0.5
MIN_DAMAGE = 0.5

BG = "#1f2328"
GRID = "#3a4048"
TEXT = "#e6e8eb"
MUTED = "#9aa3ad"
SWING = "#d9a441"
WAIT = "#7d8590"
DEFEND = "#4fa3e0"
HIT = "#e5534b"
STAMINA = "#57ab5a"
FONT = "font-family='DejaVu Sans, Helvetica, Arial, sans-serif'"


def pulled_back(t):
    """How far into the wait the hit lands, for a cancel at t (fraction of the way to the hit)."""
    return (1 - t) / (DELAY * t)


def stamina(t):
    if t <= PEAK:
        x = t / PEAK
        return 1 - (1 - x) ** 2
    return PENALTY_AT_HIT + (1 - PENALTY_AT_HIT) * pulled_back(t)


def damage(t):
    if t < PEAK:
        return None
    return 1 + (MIN_DAMAGE - 1) * pulled_back(t)


def text(x, y, s, size=14, fill=TEXT, anchor="start", weight="normal"):
    return (f"<text x='{x:.1f}' y='{y:.1f}' {FONT} font-size='{size}' fill='{fill}' "
            f"text-anchor='{anchor}' font-weight='{weight}'>{s}</text>")


def svg(w, h, body):
    return (f"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}' viewBox='0 0 {w} {h}'>"
            f"<rect width='{w}' height='{h}' rx='10' fill='{BG}'/>{''.join(body)}</svg>")


def timeline():
    w, h = 760, 320
    x0, x1 = 150, 720
    scale = (x1 - x0) * 0.6  # the hit sits 60% along the axis

    def px(t):  # t in seconds; the example hit is at 1.0 s
        return x0 + t * scale

    b = [text(24, 34, "When does the block or dodge start?", 17, weight="bold")]
    lx = 24
    for color, s in [(SWING, "swing"), (WAIT, "wait"), (DEFEND, "block or dodge")]:
        b.append(f"<rect x='{lx}' y='52' width='14' height='14' rx='3' fill='{color}'/>")
        b.append(text(lx + 20, 64, s, 12, MUTED))
        lx += 40 + len(s) * 7
    b.append(f"<circle cx='{lx + 7}' cy='59' r='6' fill='{HIT}'/>")
    b.append(text(lx + 20, 64, "the swing's hit", 12, MUTED))

    rows = [
        (115, "Early cancel", 0.4,
         "Cancel at 0.4 s, block or dodge at 0.6 s. That's before the hit, so the hit is stopped."),
        (205, "Late cancel", 0.8,
         "Cancel at 0.8 s, block or dodge at 1.2 s. The hit at 1.0 s lands during the wait, at 75% damage."),
    ]
    for y, label, c, note in rows:
        start = c * (1 + DELAY)
        b.append(text(24, y + 5, label, 14, MUTED))
        b.append(f"<rect x='{px(0):.1f}' y='{y - 11}' width='{px(c) - px(0):.1f}' height='22' rx='4' fill='{SWING}'/>")
        b.append(f"<rect x='{px(c):.1f}' y='{y - 11}' width='{px(start) - px(c):.1f}' height='22' rx='4' fill='{WAIT}'/>")
        b.append(f"<rect x='{px(start):.1f}' y='{y - 11}' width='{x1 - px(start):.1f}' height='22' rx='4' fill='{DEFEND}'/>")
        if start > 1:
            b.append(f"<circle cx='{px(1):.1f}' cy='{y}' r='7' fill='{HIT}' stroke='{BG}' stroke-width='2'/>")
        else:
            b.append(f"<circle cx='{px(1):.1f}' cy='{y}' r='7' fill='none' stroke='{HIT}' stroke-width='2' "
                     f"stroke-dasharray='3 2'/>")
        b.append(text(px(0), y + 36, note, 12, TEXT))

    ay = 275
    b.append(f"<line x1='{x0}' y1='{ay}' x2='{x1}' y2='{ay}' stroke='{GRID}' stroke-width='1.5'/>")
    for t in [0, 0.4, 0.6, 0.8, 1.0, 1.2]:
        b.append(f"<line x1='{px(t):.1f}' y1='{ay - 5}' x2='{px(t):.1f}' y2='{ay + 5}' stroke='{MUTED}'/>")
        b.append(text(px(t), ay + 22, f"{t:.1f} s", 12, HIT if t == 1.0 else MUTED, "middle"))
    return svg(w, h, b)


def chart(title, ylabel, curves, yticks, notes):
    w, h = 760, 370
    x0, x1, y0, y1 = 80, 720, 310, 100
    b = [text(24, 34, title, 17, weight="bold")]

    def px(t):
        return x0 + t * (x1 - x0)

    def py(v):
        return y0 - v * (y0 - y1)

    for v, s in yticks:
        b.append(f"<line x1='{x0}' y1='{py(v):.1f}' x2='{x1}' y2='{py(v):.1f}' stroke='{GRID}' stroke-width='1'/>")
        b.append(text(x0 - 10, py(v) + 4, s, 12, MUTED, "end"))
    for t in [0, 0.25, 0.5, 0.75, 1]:
        b.append(text(px(t), y0 + 22, f"{t:.0%}", 12, MUTED, "middle"))
    b.append(text((x0 + x1) / 2, y0 + 46, "When you cancel (how far the swing is toward its hit)", 13, TEXT, "middle"))
    b.append(text(24, 56, ylabel, 13, MUTED))

    b.append(f"<line x1='{px(PEAK):.1f}' y1='{y1 - 30}' x2='{px(PEAK):.1f}' y2='{y0}' stroke='{MUTED}' "
             f"stroke-width='1' stroke-dasharray='4 4'/>")
    b.append(text(px(PEAK) - 8, y1 - 18, "hit stopped", 12, MUTED, "end"))
    b.append(text(px(PEAK) + 8, y1 - 18, "hit lands anyway", 12, MUTED))

    for f, color, start, end in curves:
        n = 200
        pts = []
        for i in range(n + 1):
            t = start + (end - start) * i / n
            v = f(t)
            pts.append(f"{px(t):.1f},{py(v):.1f}")
        b.append(f"<polyline points='{' '.join(pts)}' fill='none' stroke='{color}' stroke-width='3' "
                 f"stroke-linejoin='round'/>")
    for t, v, s, anchor, dy in notes:
        b.append(f"<circle cx='{px(t):.1f}' cy='{py(v):.1f}' r='4' fill='{TEXT}'/>")
        b.append(text(px(t) + (8 if anchor == 'start' else -8), py(v) + dy, s, 12, TEXT, anchor))
    return svg(w, h, b)


def stamina_chart():
    return chart(
        "Extra stamina for a cancel",
        "Share of the full penalty (by default, the swing's own stamina cost)",
        [(stamina, STAMINA, 0.0, 1.0)],
        [(0, "0%"), (0.5, "50%"), (1, "100%")],
        [(0.0, 0.0, "free at the start", "start", -10),
         (PEAK, 1.0, "full cost", "end", 18),
         (1.0, PENALTY_AT_HIT, "half at the hit", "end", 20)],
    )


def damage_chart():
    return chart(
        "Damage of a cancelled swing",
        "Damage, compared with a normal hit",
        [(lambda t: 0.0, HIT, 0.0, PEAK), (damage, HIT, PEAK, 1.0)],
        [(0, "0%"), (0.5, "50%"), (1, "100%")],
        [(PEAK / 2, 0.0, "no hit", "start", -10),
         (PEAK, MIN_DAMAGE, "half damage", "start", -10),
         (1.0, 1.0, "full damage", "end", 20)],
    )


def main():
    for name, content in [("timeline", timeline()), ("stamina", stamina_chart()), ("damage", damage_chart())]:
        path = os.path.join(HERE, name + ".svg")
        with open(path, "w") as f:
            f.write(content)
        subprocess.run(["rsvg-convert", "-z", "2", "-o", os.path.join(HERE, name + ".png"), path], check=True)
        print(path)


if __name__ == "__main__":
    main()
