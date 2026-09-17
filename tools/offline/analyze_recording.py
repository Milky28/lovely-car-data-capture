"""Turn a screen recording of a rev sweep into the CSV the screen-capture tests use.

The plugin can't read a car's rev lights from games that don't report them, so it reads them off
the screen instead. This script is the offline half of that: it runs the same detection over a
recorded clip, which is how the detector gets tuned without sitting in a car.

It needs ffmpeg on PATH and numpy, plus Pillow for the glyph montage.

Two steps, because the RPM has to be read out of the picture as well:

  1. python analyze_recording.py glyphs clip.mkv --rpm-box 1426,620,144,60
     Writes glyphs.png: every distinct digit shape found in the RPM overlay, in one row.

  2. Read the digits off that image yourself and pass them back in order:
     python analyze_recording.py run clip.mkv --rpm-box 1426,620,144,60 \
         --led-box 2320,1100,440,100 --labels 7195642813342431663596277889980850587080 \
         --gear N --out car.csv

The CSV has one row per frame: frame, timeMs, gear, rpm, and the lit lights as "x:r:g:b" each.
Feed it to the tests in tests/data, or read it back with tests/ScreenTests.cs.

Recording notes: borderless windowed, a fixed cockpit camera with head movement off, and an RPM
readout on screen. The game's own HUD is a better source than a SimHub overlay, which lags the
game by a frame or two and so reads about 15 rpm low while the revs climb.
"""
import argparse
import csv
import subprocess
import sys

import numpy as np

MIN_BRIGHTNESS = 90      # same thresholds as StripDetector.cs
MIN_SATURATION = 55
MIN_COLUMN_PIXELS = 3
MERGE_GAP = 4
MIN_WIDTH, MAX_WIDTH = 6, 40
DIGIT_THRESHOLD = 110    # the RPM text is light on a dark box


def box(text):
    parts = [int(v) for v in text.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("a box is x,y,width,height")
    if parts[2] % 2 or parts[3] % 2:
        raise argparse.ArgumentTypeError("width and height must be even, or ffmpeg shifts the crop by a pixel")
    return parts


def frames(video, rpm_box, led_box):
    """Yields (rpm_pixels, led_pixels) per frame, decoding the clip once."""
    rx, ry, rw, rh = rpm_box
    lx, ly, lw, lh = led_box
    height = max(rh, lh)
    width = rw + lw
    vf = (f"[0:v]crop={rw}:{rh}:{rx}:{ry},pad={rw}:{height}[a];"
          f"[0:v]crop={lw}:{lh}:{lx}:{ly},pad={lw}:{height}[b];[a][b]hstack=inputs=2[out]")
    cmd = ["ffmpeg", "-v", "error", "-i", video, "-filter_complex", vf, "-map", "[out]",
           "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
    size = width * height * 3
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, bufsize=size * 4)
    try:
        while True:
            buf = proc.stdout.read(size)
            if len(buf) < size:
                break
            frame = np.frombuffer(buf, np.uint8).reshape(height, width, 3)
            yield frame[:rh, :rw], frame[:lh, rw:]
    finally:
        proc.stdout.close()
        proc.wait()


def find_lights(led):
    """The lit lights in one frame: bright, saturated runs of columns. Mirrors StripDetector.cs."""
    pixels = led.astype(np.int16)
    mx = pixels.max(2)
    mn = pixels.min(2)
    lit = (mx >= MIN_BRIGHTNESS) & ((mx - mn) >= MIN_SATURATION)

    columns = lit.sum(0) >= MIN_COLUMN_PIXELS
    runs, start = [], None
    for i, on in enumerate(columns):
        if on and start is None:
            start = i
        if not on and start is not None:
            runs.append([start, i - 1])
            start = None
    if start is not None:
        runs.append([start, len(columns) - 1])

    merged = []
    for run in runs:
        if merged and run[0] - merged[-1][1] <= MERGE_GAP:
            merged[-1][1] = run[1]
        else:
            merged.append(run)

    lights = []
    for left, right in merged:
        if not MIN_WIDTH <= right - left + 1 <= MAX_WIDTH:
            continue
        mask = lit[:, left:right + 1]
        seg = pixels[:, left:right + 1]
        values = seg.max(2)[mask]
        if values.size == 0:
            continue
        # The brightest pixels only: a light's halo is a blend with the dark cockpit behind it.
        chosen = mask & (seg.max(2) >= np.percentile(values, 70))
        colour = seg[chosen].mean(0)
        lights.append(((left + right) // 2, int(colour[0]), int(colour[1]), int(colour[2])))
    return lights


def glyph_shapes(rpm):
    """The digit shapes in one frame of the RPM overlay, left to right."""
    mask = rpm.mean(2) > DIGIT_THRESHOLD
    columns = mask.sum(0)
    runs, start = [], None
    for i, on in enumerate(columns):
        if on and start is None:
            start = i
        if not on and start is not None:
            runs.append((start, i - 1))
            start = None
    if start is not None:
        runs.append((start, len(columns) - 1))

    shapes = []
    for left, right in runs:
        if right - left < 4:
            continue
        sub = mask[:, left:right + 1]
        rows = np.nonzero(sub.sum(1))[0]
        if len(rows):
            shapes.append(sub[rows[0]:rows[-1] + 1])
    return shapes


def normalise(shape, width=12, height=18):
    from PIL import Image
    image = Image.fromarray((shape * 255).astype(np.uint8)).resize((width, height))
    return (np.asarray(image) > 127).astype(np.int8)


def collect_glyphs(video, rpm_box, led_box, step):
    """Distinct digit shapes over the clip, most common first."""
    seen = {}
    for index, (rpm, _) in enumerate(frames(video, rpm_box, led_box)):
        if index % step:
            continue
        for shape in glyph_shapes(rpm):
            key = normalise(shape).tobytes()
            if key not in seen:
                seen[key] = [0, shape]
            seen[key][0] += 1
    return sorted(seen.values(), key=lambda v: -v[0])


def cmd_glyphs(args):
    from PIL import Image
    found = collect_glyphs(args.video, args.rpm_box, args.led_box, args.step)[:args.count]
    if not found:
        sys.exit("No digits found. Check --rpm-box covers the RPM readout.")
    height = max(s.shape[0] for _, s in found)
    width = max(s.shape[1] for _, s in found)
    sheet = Image.new("L", ((width + 6) * len(found), height + 6), 0)
    for i, (_, shape) in enumerate(found):
        sheet.paste(Image.fromarray((shape * 255).astype(np.uint8)), (i * (width + 6) + 3, 3))
    sheet = sheet.resize((sheet.width * 3, sheet.height * 3), Image.NEAREST)
    sheet.save(args.out)
    print(f"{len(found)} shapes written to {args.out}.")
    print("Read them left to right and pass them to 'run' as --labels, e.g. --labels 7195642813...")


def cmd_run(args):
    templates, labels = [], []
    shapes = collect_glyphs(args.video, args.rpm_box, args.led_box, args.step)[:len(args.labels)]
    if len(shapes) < len(args.labels):
        sys.exit(f"--labels has {len(args.labels)} digits but only {len(shapes)} shapes were found.")
    for (_, shape), label in zip(shapes, args.labels):
        templates.append(normalise(shape).reshape(-1))
        labels.append(int(label))
    table = np.array(templates)

    rows, unreadable = [], 0
    for index, (rpm_pixels, led_pixels) in enumerate(frames(args.video, args.rpm_box, args.led_box)):
        digits = glyph_shapes(rpm_pixels)
        if not 3 <= len(digits) <= 5:
            unreadable += 1
            continue
        value = 0
        for shape in digits:
            distance = np.abs(table - normalise(shape).reshape(1, -1)).sum(1)
            value = value * 10 + labels[int(distance.argmin())]
        lights = find_lights(led_pixels)
        rows.append([index, round(index * 1000 / args.fps), args.gear, value,
                     " ".join(f"{x + args.led_box[0]}:{r}:{g}:{b}" for x, r, g, b in lights)])

    if args.min_rpm:
        rows = [r for r in rows if r[3] >= args.min_rpm]
    with open(args.out, "w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(["frame", "timeMs", "gear", "rpm", "blobs"])
        writer.writerows(rows)
    print(f"{len(rows)} frames written to {args.out}; {unreadable} had no readable RPM.")
    if rows:
        print(f"RPM {min(r[3] for r in rows)}-{max(r[3] for r in rows)}, "
              f"up to {max(len(r[4].split()) for r in rows)} lights lit at once.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("glyphs", "run"):
        p = sub.add_parser(name)
        p.add_argument("video")
        p.add_argument("--rpm-box", type=box, required=True, help="x,y,width,height of the RPM readout")
        p.add_argument("--led-box", type=box, required=True, help="x,y,width,height around the rev lights")
        p.add_argument("--step", type=int, default=3, help="sample every Nth frame when collecting digit shapes")
    sub.choices["glyphs"].add_argument("--count", type=int, default=40)
    sub.choices["glyphs"].add_argument("--out", default="glyphs.png")
    sub.choices["run"].add_argument("--labels", required=True, help="the digits in glyphs.png, left to right")
    sub.choices["run"].add_argument("--gear", default="N")
    sub.choices["run"].add_argument("--fps", type=float, default=60.0)
    sub.choices["run"].add_argument("--min-rpm", type=int, default=0, help="drop frames below this to keep the file small")
    sub.choices["run"].add_argument("--out", default="capture.csv")
    args = parser.parse_args()
    (cmd_glyphs if args.command == "glyphs" else cmd_run)(args)


if __name__ == "__main__":
    main()
