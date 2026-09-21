# Project Motor Racing Corvette C7.R detection regressions (2026-09-21)

Original capture: `corvette-c7-r` at 09:05. Source folder:
C:/Users/jerky/OneDrive/Documents/SimHub/LovelyCarDataCapture/projectmotorracing.

The PNGs are current transition images 0010, 0013 and 0014, copied without alteration
from the `transitions-20260921-090525` folder. They are captured pixels.

This strip's backing runs as a gradient, green at the low end through red to violet and
blue at the top, and its lights sit on it. Where the backing is violet it filled the gaps
between lights 8, 9 and 10, joining them into a run too wide to keep, so a frame with nine
lights lit was read as seven. The violet lights also blow out to rgb(255,216,255), white in
two channels only, so the white-centre fallback skipped them as well.

The counts are what the game shows: 8 lit at 6790 rpm, 9 lit at 6965, and all 10 in the
limiter flash. These are image/detection regressions, not confirmed RPM values.
