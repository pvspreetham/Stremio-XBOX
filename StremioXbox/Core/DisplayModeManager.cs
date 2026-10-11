// Adapted from Jellyfin for Xbox, Jellyfin/Core/FullScreenManager.cs
// https://github.com/jellyfin/jellyfin-xbox (GPL-2.0).
// Changes: standalone class (no DI or settings service), explicit HDR option passed to
// RequestSetCurrentDisplayModeAsync, and a simpler resolution / refresh-rate ranking.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Display.Core;

namespace StremioXbox.Core
{
    /// <summary>Dynamic range of the video being played.</summary>
    public enum VideoRange
    {
        Sdr,
        Hdr10,
        Hlg,
        DolbyVision,
    }

    /// <summary>Switches the Xbox HDMI output mode to suit the video, and back.</summary>
    public sealed class DisplayModeManager
    {
        /// <summary>Gets or sets a value indicating whether the output resolution follows the video (otherwise the highest is kept).</summary>
        public bool MatchResolution { get; set; }

        /// <summary>Gets or sets a value indicating whether the refresh rate follows the video frame rate.</summary>
        public bool MatchRefreshRate { get; set; } = true;

        /// <summary>Describes what the connected display reports, for the debug log.</summary>
        public static string DescribeDisplay()
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi == null)
            {
                return "No HDMI display information (not running on an Xbox?)";
            }

            var modes = hdmi.GetSupportedDisplayModes().Where(m => !m.StereoEnabled).ToList();
            var current = hdmi.GetCurrentDisplayMode();
            return string.Format(
                "Display: {0} modes, HDR10 {1}, Dolby Vision LL {2}, current {3}",
                modes.Count,
                modes.Any(m => m.IsSmpte2084Supported) ? "yes" : "no",
                modes.Any(m => m.IsDolbyVisionLowLatencySupported) ? "yes" : "no",
                current == null ? "unknown" : Describe(current));
        }

        /// <summary>Requests the best display mode for the video.</summary>
        /// <returns>A description of the outcome, for the debug log.</returns>
        public async Task<string> SwitchAsync(uint width, uint height, double frameRate, VideoRange range)
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi == null)
            {
                return "No HDMI display information; mode not changed";
            }

            var modes = hdmi.GetSupportedDisplayModes().Where(m => !m.StereoEnabled).ToList();
            var option = PickHdrOption(modes, range);
            var candidates = modes.Where(m => Supports(m, option)).ToList();
            if (candidates.Count == 0)
            {
                return "No display mode supports " + option + "; mode not changed";
            }

            var maxHeight = candidates.Max(m => m.ResolutionHeightInRawPixels);
            var targetHeight = MatchResolution && height > 0 ? height : maxHeight;
            var atOrAbove = candidates
                .Where(m => m.ResolutionHeightInRawPixels >= targetHeight)
                .Select(m => m.ResolutionHeightInRawPixels)
                .DefaultIfEmpty(maxHeight)
                .Min();
            var sameResolution = candidates.Where(m => m.ResolutionHeightInRawPixels == atOrAbove);

            IEnumerable<HdmiDisplayMode> ordered = MatchRefreshRate && frameRate > 1
                ? sameResolution.OrderBy(m => RefreshPenalty(m.RefreshRate, frameRate))
                : sameResolution.OrderBy(m => Math.Abs(m.RefreshRate - 60));

            foreach (var mode in ordered)
            {
                if (await hdmi.RequestSetCurrentDisplayModeAsync(mode, option))
                {
                    return "Display mode set: " + Describe(mode) + " " + option;
                }
            }

            return "Every display mode request was refused (" + option + ")";
        }

        /// <summary>Returns the display to the console's default mode.</summary>
        public async Task ResetAsync()
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi != null)
            {
                await hdmi.SetDefaultDisplayModeAsync();
            }
        }

        // From Jellyfin's GetHdmiDisplayHdrOption: use the richest mode the display supports.
        // Note from Jellyfin's code: "Xbox only supports DOVI profile 5". They send profile 8
        // (DV with HDR10 base layer) as HDR10. Here the caller decides via VideoRange.
        private static HdmiDisplayHdrOption PickHdrOption(IList<HdmiDisplayMode> modes, VideoRange range)
        {
            var supportsDolbyVision = modes.Any(m => m.IsDolbyVisionLowLatencySupported);
            var supportsHdr = modes.Any(m => m.IsSmpte2084Supported);
            var hdrOrSdr = supportsHdr ? HdmiDisplayHdrOption.Eotf2084 : HdmiDisplayHdrOption.None;

            switch (range)
            {
                case VideoRange.DolbyVision:
                    return supportsDolbyVision ? HdmiDisplayHdrOption.DolbyVisionLowLatency : hdrOrSdr;
                case VideoRange.Hdr10:
                case VideoRange.Hlg:
                    return hdrOrSdr;
                default:
                    return HdmiDisplayHdrOption.None;
            }
        }

        private static bool Supports(HdmiDisplayMode mode, HdmiDisplayHdrOption option)
        {
            switch (option)
            {
                case HdmiDisplayHdrOption.DolbyVisionLowLatency:
                    return mode.IsDolbyVisionLowLatencySupported;
                case HdmiDisplayHdrOption.Eotf2084:
                    return mode.IsSmpte2084Supported;
                default:
                    return true;
            }
        }

        // Exact multiples of the frame rate avoid judder; prefer 1x, then 2x, and so on.
        // The small difference term breaks ties, so a 24 fps file gets 24 Hz rather than 23.976 Hz.
        private static double RefreshPenalty(double refreshRate, double frameRate)
        {
            for (var multiple = 1; multiple <= 5; multiple++)
            {
                var difference = Math.Abs(refreshRate - (frameRate * multiple));
                if (difference < 0.05)
                {
                    return multiple - 1 + difference;
                }
            }

            return 100 + Math.Abs(refreshRate - frameRate);
        }

        private static string Describe(HdmiDisplayMode mode)
        {
            return string.Format("{0}x{1}@{2:0.###}", mode.ResolutionWidthInRawPixels, mode.ResolutionHeightInRawPixels, mode.RefreshRate);
        }
    }
}
