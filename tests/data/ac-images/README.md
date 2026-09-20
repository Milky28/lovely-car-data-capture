# Assetto Corsa morning capture regressions (2026-09-20)

Original captures: rss_gtm_macca_72_evo_v8 at 08:29, rss_gtm_bayer_i6_evo at 08:33,
 and rss_gtm_akuro_v6_evo2 at 08:36. Source folder:
C:/Users/jerky/OneDrive/Documents/SimHub/LovelyCarDataCapture/assettocorsa.

../ac-macca.csv and ../ac-bayer.csv are unmodified copies of the raw frames CSVs.
The PNGs are the first twelve current transition images, copied without alteration
from the corresponding transitions-20260920 folders. They are captured pixels,
not synthetic replacements for missing raw detections.

Image assertions cover the visible counts: Akuro 1,2,3,4 green, then 5,6,7,8 total,
dark, ten (four green, four yellow, two red), dark, ten. Bayer has five green lights
in images1-5 and ten lights in images6-12. Dark squares must not become lights.

These are image/processing regressions, not user-confirmed RPM calibration values.
The original Akuro CSV has lost green/red detections; the Bayer CSV has fragmented
centres. A new drive is required to validate their complete RPM profiles and blink
timing with the corrected detector. Sparse transition pictures cannot recover a
full capture. The Bayer raw replay currently calibrates a different corrupted
layout than the original export; its rows must not be published as a repaired car.

Adonis followup, 2026-09-20 09:20:38:
../ac-adonis.csv is an unchanged copy of rss-gtm-adonis-v8-evo.frames.csv.
adonis-transition-0005 through0008 are unchanged current PNGs from
rss-gtm-adonis-v8-evo.transitions-20260920-092038-d6d0d452.
All eight lights alternate red/blue/red/blue in those pictures. No dark phase is
visible. The raw capture has no empty detections above6800rpm. The former74ms
blink was inferred from filtered lower-RPM frames, not these limiter phases.
The format cannot encode alternating colours; regression expects an actual red
phase as a solid fallback, never an averaged purple or a guessed on/off cadence.

Bayer followup, 2026-09-20 09:09:10:
../ac-bayer-0909.csv is the untouched current raw CSV, separate from ../ac-bayer.csv.
bayer-0909-0014 previous/current/following PNGs are untouched transition pictures.
They show five green lights at5818rpm, then ten lights at5833rpm, including the cyan
bank. User confirmed those lights are cyan and redline is solid. The fixture
../ac-bayer-confirmed.overrides.json records those confirmations; no RPM overrides.
The first-gear cyan crossing is approximately5780rpm after the existing measured
52ms display-delay correction. Gear5 remains6665green/6705cyan.

Sixth-gear followup, 2026-09-20:
../ac-bayer-0944.csv and ../ac-adonis-0949.csv are unchanged copies of the09:44
and09:49 raw captures. Bayer sixth gear has1196raw-empty frames through6628rpm.
Adonis has sparse pale-blue detections in sixth which are NOT dark evidence;
independent sustained raw-empty observations reach6473rpm. Safeguards use only
those raw-empty runs, not filtered/removed detections. The provisional thresholds
above the tested range are placeholders, never measured onsets.
../ac-adonis-confirmed.overrides.json requests red on/off140ms as an APPROXIMATION
of actual red/blue colour alternation. Start-to-next-start phase medians in the
latest capture are142ms red(n60) and138ms blue(n61); the earlier fixture gives
140/138ms. First-to-last lit-sample duration omits one sampling interval and is
not the phase duration. The file still cannot reproduce two alternating colours.
