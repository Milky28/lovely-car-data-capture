# Project Motor Racing R8 LMS GT4 Evo detection regressions (2026-09-20)

Original capture: `r8-lms-gt4-evo` at 18:13. Source folder:
C:/Users/jerky/OneDrive/Documents/SimHub/LovelyCarDataCapture/projectmotorracing.

The PNGs are current transition images 0045, 0046, 0050, 0053 and 0056, copied without
alteration from the `transitions-20260920-181328` folder. They are captured pixels.

This car's lights blow out to white through the middle and keep colour only at their
edges. The detector read each one as two, so the export had 19 LEDs instead of 10 and
named the washed green edges cyan. 0046 is the limiter flash, where the whole strip is
red and the ten lights are unambiguous; 0045 is the dark half of that same flash.

These are image/detection regressions, not user-confirmed RPM calibration values. The
raw frames CSV from this drive was recorded through the old detector and still holds the
split lights, so it cannot validate the RPM profile or the blink. A new drive is needed
for that.
