using System;
using System.Collections.Generic;
using LovelyCarDataCapture.Screen;

namespace LovelyCarDataCapture.Tests
{
    internal static partial class Program
    {
        private static void DarkFirstFlashUsesEachGearsInitialEdge()
        {
            var result = SyntheticColourFlash(true).Result();
            Check(result.BlinkSeen, "repeated dark phases confirm blinking");
            Check(Math.Abs(result.RedlineByGear["2"].Rpm - 6000) <= 10, "gear 2 uses its initial dark edge");
            Check(Math.Abs(result.RedlineByGear["3"].Rpm - 6200) <= 10, "gear 3 keeps its different flash onset");
            Equal("#FF0000FF", result.RedlineColor, "dark-first flash keeps its blue colour");
            Check(!result.RedlineFromBlink, "colour remains known rather than becoming transparent");
        }

        private static void MissedFramesDoNotAdvanceColourFlash()
        {
            var result = SyntheticColourFlash(false).Result();
            Check(Math.Abs(result.RedlineByGear["2"].Rpm - 6000) <= 10, "a missed frame or dark gap returning to own colours is not the redline");
            Check(Math.Abs(result.RedlineByGear["3"].Rpm - 6200) <= 10, "blue-first gear keeps its own threshold");
            Equal("#FF0000FF", result.RedlineColor, "ordinary flashing still uses blue");
        }

        private static void DarkFirstFlashCanSpanAFastClimb()
        {
            var result = SyntheticColourFlash(true, 50).Result();
            Check(result.RedlineByGear.ContainsKey("2"), "first-dark crossing survives a wide normal-to-blue RPM span");
            Check(Math.Abs(result.RedlineByGear["2"].Rpm - 5975) <= 10, "fast climb uses the 5950-6000 boundary");
            Equal("#FF0000FF", result.RedlineColor, "a fast dark-first transition still measures blue");
        }

        private static void DarkFirstFlashDoesNotBridgeGears()
        {
            var result = SyntheticColourFlash(false, 10, true).Result();
            Check(Math.Abs(result.RedlineByGear["2"].Rpm - 6000) <= 10,
                  "blue in the next gear cannot confirm a preceding gear's dark gap");
        }

        private static ScreenLedCapture SyntheticColourFlash(bool darkFirst, int rpmStep = 10, bool crossGearGap = false)
        {
            var capture = new ScreenLedCapture();
            var colours = new[] { new LedColor(0, 255, 0), new LedColor(255, 255, 0),
                                  new LedColor(255, 128, 0), new LedColor(255, 0, 0) };
            long time = 0;
            void Record(string gear, int rpm, bool gap, bool flash)
            {
                var blobs = new List<LitBlob>();
                if (!gap)
                    for (int led = 0; led < colours.Length; led++)
                        if (rpm >= 5000 + led * 100)
                            blobs.Add(new LitBlob
                            {
                                Left = 91 + led * 30,
                                Right = 109 + led * 30,
                                Color = flash ? new LedColor(0, 0, 255) : colours[led],
                            });
                capture.Record(gear, rpm, time += 20, blobs);
            }
            if (crossGearGap)
            {
                for (int rpm = 4600; rpm < 5800; rpm += 10) Record("2", rpm, false, false);
                for (int rpm = 5800; rpm < 5850; rpm += 10) Record("2", rpm, true, false);
                Record("3", 6500, false, true);
            }
            foreach (var gear in new[] { "2", "3" })
            {
                int redline = gear == "2" ? 6000 : 6200;
                for (int sweep = 0; sweep < 3; sweep++)
                    for (int rpm = 4600; rpm <= redline + 400; rpm += rpmStep)
                    {
                        // One missed frame and a longer dark gap below the redline both return to
                        // the normal colours. Neither should be confused with the flash onset.
                        bool gap = rpm == 5500 || (rpm >= 5700 && rpm < 5750);
                        if (rpm >= redline)
                        {
                            int phase = (rpm - redline) / rpmStep % 10;
                            gap = darkFirst ? phase < 5 : phase >= 5;
                        }
                        Record(gear, rpm, gap, rpm >= redline);
                    }
            }
            return capture;
        }
    }
}
