# RPM LED Builder: capture override editor handoff

Prepared 2026-09-19 for `C:\Users\jerky\Documents\rpm-led-builder`.
This is the historical implementation handoff for that project, not the current builder status.

## Purpose

Let users keep confirmed LED colors and blink timing across SimHub captures without editing JSON by hand. Editing the generated car file alone does not preserve these adjustments on the next export. The capture plugin now reads a separate, sparse `<car>.overrides.json` file.

The override contract was introduced in plugin version 0.2.0, commit `f6acba4b4e7379a1887d8864b2ed2045dba19731`. That version is published. The requirements below describe the original builder handoff; use the current plugin code and tests to verify the contract.

## Required builder changes

1. Add a small **Capture overrides** section to the existing editor. Reuse the current color and blink controls. Allow explicit selection of individual colors and the blink interval to preserve across captures. Defaults must select nothing. Do not automatically freeze every imported color or infer confirmed values from differences against the repository.
2. Offer **Download capture overrides** and **Import capture overrides**. Show the selected fields and destination filename before download. Keep the normal car JSON download, diff and GitHub submission flows separate. Override metadata must never enter a Lovely Car Data submission.
3. Import an override alongside an already loaded car profile. Validate the entire override before changing either the profile or override selection. Apply its selected colors and blink timing to the preview while retaining unlisted fields and every RPM row. The override is not a complete car profile and must not go through the normal car importer.
4. Include override selections in existing undo/redo and local draft persistence. Loading another car, changing the game or raw car ID, or changing LED count must clear or invalidate the selection. Do not silently rebind a previous car's overrides. Old saved drafts without these fields must still load.
5. Removing a selection excludes that field from the next override download. If nothing remains selected, explain that the existing sidecar must be removed to resume normal capture behavior; do not generate an empty file that would block plugin exports. A browser cannot silently remove or replace the installed sidecar.
6. Add short placement and application instructions. The browser downloads a file; the user puts it beside the capture export, replacing an older sidecar if necessary. The plugin reads it on the next export. Downloading an override or pressing ATSR's reload alone does not apply it to an already generated profile.

Keep the app single-file and dependency-free. Reuse its existing import, download, preview, history and validation patterns. Read the builder's own `AGENTS.md` first. Do not push or deploy these changes without the owner's approval: pushing its `main` publishes GitHub Pages.

## Exact plugin file contract

| Field | Requirement |
| --- | --- |
| `game` | Required string: the capture export's game folder, such as `lmu` or `assettocorsacompetizione`. Use the existing sim field, but require the correct folder rather than guessing from a car ID. |
| `carId` | Required string: preserve the game's raw ID, including spaces or underscores. Do not slugify or silently trim it. Plugin identity comparison ignores case. |
| `ledNumber` | Required JSON integer matching the loaded profile's LED count. Count includes physical gap slots and excludes redline slot 0. |
| `ledColor` | Optional object, not an array. Keys are LED indices from `0` through `ledNumber`; emit canonical decimal strings. Values are exactly `#AARRGGBB`, preferably uppercase. |
| `redlineBlinkInterval` | Optional JSON integer from 0 through 2147483647, in milliseconds. Zero disables blinking. Absence means no override. |

At least one color entry or a blink interval is required. Unknown top-level fields are rejected. Do not include `ledRpm`, RPM thresholds, car names, classes, schema versions or notes. Colors apply across all gears; per-gear overrides are unsupported.

Validate invalid types, fractional or non-finite numbers, negative intervals, out-of-range indices, malformed colors and mismatched identity/count before mutation. Avoid the normal importer's repair/default behavior here. Detect repeated numeric indices such as `1` and `01` if accepted by the importer; always emit canonical keys. A user-entered six-digit color can use the existing color control's conversion, but the serialized sidecar must contain eight hex digits.

Unlisted fields continue using the plugin's normal capture/repository logic. Overrides take precedence over measured and repository colors or blink timing. They do not lock any RPM thresholds. Undriven gears retain previous RPM rows separately, according to the plugin's existing rules.

### Confirmed LMU SC63 example

Filename: `lamborghini-iron-lynx-2024.overrides.json`

```json
{
  "game": "lmu",
  "carId": "Lamborghini Iron Lynx 2024",
  "ledNumber": 10,
  "ledColor": { "6": "#FFFFFF00", "7": "#FFFFFF00", "8": "#FFFFFF00" }
}
```

This preserves the three yellow LEDs that the repository had as red.

### Confirmed ACC McLaren example

Filename: `mclaren-720s-gt3-evo.overrides.json`

```json
{
  "game": "assettocorsacompetizione",
  "carId": "mclaren_720s_gt3_evo",
  "ledNumber": 12,
  "redlineBlinkInterval": 200
}
```

The repository used 250 ms; the user's capture and wheel comparison confirmed 200 ms. Independent game and wheel timers can remain out of phase even when the interval is correct. Do not promise phase synchronization or introduce a phase-offset feature.

## Filenames and placement

The sidecar filename is the normal capture export basename plus `.overrides.json`: lowercase raw car ID, accents removed, non-alphanumeric runs replaced with a hyphen, and leading/trailing hyphens removed. Match the plugin's `Slug.Make` and verify the builder's existing `slugify` against the examples.

On this machine, the SC63 sidecar belongs at:

```text
C:\Users\jerky\OneDrive\Documents\SimHub\LovelyCarDataCapture\lmu\lamborghini-iron-lynx-2024.overrides.json
```

For general users, describe the configured capture output folder, not this machine's hardcoded OneDrive path. Place sidecars beside capture exports, not in the Lovely Car Data repository or ATSR development folder.

If a sidecar is invalid, the plugin stops export before replacing the existing car and ATSR files. Correct it and retry **Stop and export** while the capture session remains loaded. This makes accurate builder validation important.

## Related compatibility changes to include

### Distinguish export names from ATSR development names

The current builder's filename guidance assumes ATSR always loads the slug of the raw car ID. There is one verified exception to reflect in its guidance:

| Context | LMU raw ID `Lamborghini Iron Lynx 2024` |
| --- | --- |
| Capture export | `lamborghini-iron-lynx-2024.json` |
| Capture override | `lamborghini-iron-lynx-2024.overrides.json` |
| ATSR development copy | `_ATSR_DevelopmentData/rpm_data/lamborghini-sc63.json` |

The plugin recognizes this exception only for game names whose slug is `lmu` or `le-mans-ultimate`, with a case-insensitive match of that raw car ID. A sidecar's `game` still must match its actual export folder; do not normalize these two folder names into one when validating identity.

Update the builder's filename and repository-origin mismatch guidance so this known case does not incorrectly tell the user to rename/remove repository files just because ATSR uses another name. Preserve the raw ID and ordinary car download name. Do not implement a general LMU alias resolver, rename repository records automatically, or change its existing LMU template contribution workflow. If offering an ATSR-specific filename, label it separately.

### Preserve gap and redline semantics

- Slot 0 is the redline color, not a physical LED. `#00000000` lets ATSR keep individual strip colors at the redline. It must not zero redline RPM.
- For physical slots, RGB black at any alpha is a gap in ATSR; RPM 0 with a non-black color is not a reliable way to turn a slot off. Keep the builder's existing gap control and preview behavior.
- The capture plugin makes detected physical gaps black with RPM 0 in every gear after applying overrides. A color override cannot force a detected gap to light. Explain this when selecting overrides for a gap. The builder only knows the loaded profile's gaps; it must not claim access to the detector's original observations.
- Importing or exporting an override must not silently rewrite RPM rows. Structural gap corrections remain ordinary profile edits, outside the sidecar's color/blink contract.

## Acceptance checks

Extend the existing `node test.cjs` runner with focused checks:

1. Export the two confirmed examples above; assert identity, sparse fields and exact filenames. Round-trip them through the new importer.
2. Import a partial color override without changing unlisted colors, blink interval or any RPM row. A blink-only import must preserve all colors and RPMs.
3. Reject malformed or mismatched overrides without changing profile, selections or undo history. Cover unsupported fields, wrong game/car/count, invalid indices/colors/intervals, and an empty override.
4. Preserve an explicit zero interval and transparent slot-0 color; omit unselected fields. Do not confuse zero with absence.
5. Verify selection persistence, undo/redo, older draft loading, identity/count changes and switching cars. Ordinary profile JSON and GitHub submission content must contain no override metadata.
6. Check SC63 development-name guidance only for the exact LMU case; other cars and games retain existing behavior. Verify physical gaps remain off and slot 0 keeps its distinct meaning.

Run `node test.cjs` and `git diff --check`. Also use a local browser to exercise import, checkbox selection, preview, removal, download and re-import. Verify that ordinary profile export still works. Report any browser checks that could not be performed rather than treating mocked tests as visual validation.

## Source pointers

Capture plugin root: `C:\Users\jerky\Documents\simhub-capture-plugin`

- `src/Profile/LocalProfileOverrides.cs`: authoritative parser, validation and precedence.
- `src/Profile/ProfileComposer.cs`: overrides, previous gear restoration and final physical-gap normalization.
- `src/Profile/AtsrCompatibility.cs`: `DevelopmentFileName` and the exact SC63 exception.
- `src/Util/Slug.cs`: game and car filename normalization.
- `src/Plugin/CapturePlugin.cs`: sidecar path and export timing.
- `docs/reference.md`: user-facing override, backup and retained-gear instructions.
- `tests/data/lmu-sc63.overrides.json` and `tests/data/acc-mclaren.overrides.json`: confirmed examples.
- `tests/LocalOverrideTests.cs`, `tests/AtsrPathTests.cs`, `tests/BmwEvoGapTests.cs`: compatibility regressions.

Builder root: `C:\Users\jerky\Documents\rpm-led-builder`

- `index.html`: existing state, `importFromText`, `buildJsonText`, `slugify`, color controls, downloads, validation, `snapshot`/`restoreSnapshot` and local draft handling.
- `test.cjs`: existing Node regression harness with a mocked DOM and deferred fetches.

## Deferred work

No report parser, capture replay viewer, automatic backup browser, direct SimHub filesystem integration, RPM override format, broad ATSR identity mapping or detector changes are needed for this editor. The plugin already handles capture backups and retained gear RPMs. Keep this implementation focused on explicit color/blink overrides and the compatibility guidance above.
