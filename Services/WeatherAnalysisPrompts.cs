using System;
using System.Text;

namespace AIWeather.Services
{
    /// <summary>How the camera sees the sky. Decides which part of the image is sky at all.</summary>
    public enum CameraView
    {
        /// <summary>A fisheye pointing up: the whole hemisphere as a circle. The default, and what the prompt was written for.</summary>
        AllSky = 0,

        /// <summary>A fixed camera looking at part of the sky above a horizon, trees or buildings.</summary>
        FixedView = 1,
    }

    /// <summary>
    /// How dark the site is. Decides what a cloud looks like at night: lit from below by a
    /// city it is bright and milky; at a dark site nothing lights it and it is a black hole
    /// in the star field. The same prompt cannot be right for both.
    /// </summary>
    public enum SiteDarkness
    {
        /// <summary>Light pollution lights the clouds. The default, and what the prompt was written for.</summary>
        LightPolluted = 0,

        /// <summary>No light pollution: clouds are darker than the sky, stars are the only evidence.</summary>
        DarkSite = 1,
    }

    /// <summary>
    /// The analysis prompt, composed from what the owner told us about the camera and the
    /// site. It used to be one constant written for a fisheye at a light-polluted site; a
    /// user at a Bortle 3 site with a fixed security camera got "Clear 0%" on an overcast
    /// night, because the prompt said dark means clear and told the model to look for a
    /// circle that was not there (issue #17).
    /// </summary>
    public static class WeatherAnalysisPrompts
    {
        /// <summary>The prompt for the camera and site the options describe.</summary>
        public static string BuildSystemPrompt()
        {
            return BuildSystemPrompt(
                (CameraView)Properties.Settings.Default.CameraView,
                (SiteDarkness)Properties.Settings.Default.SiteDarkness);
        }

        /// <summary>The prompt for a given camera view and site darkness.</summary>
        public static string BuildSystemPrompt(CameraView view, SiteDarkness site)
        {
            var sb = new StringBuilder();

            sb.Append(view == CameraView.AllSky
                ? "You are an expert meteorologist analyzing all-sky camera images for astronomical observation safety.\n\n"
                : "You are an expert meteorologist analyzing images from a fixed sky camera for astronomical observation safety.\n\n");

            sb.Append(view == CameraView.AllSky ? AllSkyViewBlock : FixedViewBlock);
            sb.Append('\n');
            sb.Append(DayNightCommonBlock);
            sb.Append(site == SiteDarkness.DarkSite ? DarkSiteNightBlock : LightPollutedNightBlock);
            sb.Append(DayBlock);
            sb.Append('\n');
            sb.Append(NoiseAndUnreadableBlock);
            sb.Append('\n');
            sb.Append(AnalysisStepsBlock);
            sb.Append(site == SiteDarkness.DarkSite ? DarkSiteCriticalRules : LightPollutedCriticalRules);
            sb.Append(SelfCheckAndJsonBlock);

            return sb.ToString();
        }

        private const string AllSkyViewBlock =
@"IMPORTANT CONTEXT - ALL-SKY CAMERA:
- The image is captured with a fisheye lens pointing upward, showing the full sky hemisphere as a circle.
- The SKY REGION is the circular area. Dark corners/edges outside the circle are the camera housing or ground, NOT sky.
- The bright spot or dome at the bottom center may be the camera housing or horizon — ignore it for cloud assessment.
";

        private const string FixedViewBlock =
@"IMPORTANT CONTEXT - FIXED CAMERA:
- The image comes from a fixed camera looking at PART of the sky, not a fisheye. There is no circle to look for.
- The SKY REGION is the area above the horizon line — above the treeline, buildings, or ground that occupy the lower part of the frame. Only that region is sky. It may be a strip along the top of the image.
- Trees, buildings, ground, lamps, reflections and anything below the horizon line are NOT weather. Do not count them as clouds, and do not count a lit window or lamp as the moon.
- Judge cloud coverage as the fraction of the SKY REGION covered, not of the whole image.
";

        private const string DayNightCommonBlock =
@"IMPORTANT CONTEXT - NIGHTTIME vs DAYTIME:
- Use the OBSERVATION CONTEXT provided below (if available) to determine whether this is a daytime or nighttime image.
- If no observation context is provided, assume nighttime unless the image clearly shows a blue daytime sky.
- AT NIGHT: visible stars (small, slightly soft bright points that vary in brightness and only appear in the sky region) indicate clear sky. Absence of stars where the sky is = clouds blocking them.
- AT NIGHT: a bright disc in the sky is the MOON, not the sun. The moon can illuminate clouds during nighttime.
- AT NIGHT: the moon creates a bright GLOW or HALO around it — this is normal and is NOT cloud coverage. Do not count the moon's glow as clouds.
- AT NIGHT: if the OBSERVATION CONTEXT says moon is above the horizon, a bright area in one part of the sky is likely moon glow, not clouds. Look for stars in OTHER parts of the sky to judge cloud coverage.
";

        private const string LightPollutedNightBlock =
@"- AT NIGHT AT THIS SITE (light pollution present): clouds appear as BRIGHT, milky, or illuminated areas (reflecting moonlight, light pollution, or city glow). Clear sky is DARK with visible stars.
- AT NIGHT: if the sky region is mostly bright/milky/diffuse with NO visible stars, that is HEAVY CLOUD COVER (80-100%), not clear sky.
- AT NIGHT: a uniform bright glow across the sky region = overcast or thick cloud layer, NOT partly cloudy.
";

        private const string DarkSiteNightBlock =
@"- AT NIGHT AT THIS SITE (dark site, no light pollution): clouds are NOT lit from below. They appear as DARK, featureless patches with no stars — darker than the star-filled sky around them, or simply as uniform blackness. Do NOT read a dark sky as clear.
- AT NIGHT: the ONLY reliable evidence of clear sky is STARS. Where there are stars the sky is clear; where the sky region shows no stars, it is covered.
- AT NIGHT: a sky region that is uniformly black with no stars at all is OVERCAST (90-100%), unless the whole frame is unreadable (see below).
- AT NIGHT: clouds can look bright only when the moon is up. Without the moon, brightness is not the clue — the star count is.
";

        private const string DayBlock =
@"- DURING DAY: clouds appear as white/gray formations against blue sky.
- DURING DAY: if the entire sky region is white or gray with NO blue patches visible, that is OVERCAST (90-100% cloud coverage).
- DURING DAY: if the sky is uniformly bright white/gray with cloud texture or gradients but zero blue sky, cloudCoverage is 90-100%.
- DURING DAY: clear sky = blue sky visible. No blue sky = NOT clear.
- DURING DAY: to estimate coverage, look for blue sky gaps between clouds. If you see none, coverage is 90%+.
";

        private const string NoiseAndUnreadableBlock =
@"IMPORTANT - SENSOR NOISE AND UNREADABLE FRAMES:
- Camera noise: isolated single-pixel specks spread evenly over the WHOLE frame — including over the housing, trees, buildings or ground where no star can be — are sensor noise, NOT stars. Real stars are slightly soft, vary in brightness, and appear only in the sky region.
- If the sky region carries NO readable information at all — uniformly black or uniformly flat, no stars, no cloud structure, no moon, no gradient — do NOT guess a clear sky. Set condition=""Unknown"", cloudCoverage=100, isSafe=false, confidence 30 or lower, and say in the description that the sky is not visible. An unreadable frame is never evidence of clear sky.
";

        private const string AnalysisStepsBlock =
@"IMPORTANT: First check for rain or fog, then assess cloud coverage. Rain and fog override other classifications.

Analyze the provided image carefully and determine:

1. **PRIORITY: Rain Detection** (Check FIRST before other classifications):
   - Water droplets on the camera lens (appear as bright spots, circles, or reflections)
   - Rain streaks, distortion, or wet appearance
   - Condensation or moisture visible on the lens
   - If ANY water droplets are visible on the lens, classify as ""Rainy"" regardless of cloud coverage
   - Dark heavy storm clouds with precipitation
   - If rain is detected, set rainDetected=true and condition=""Rainy""

2. **Fog Detection** (Check SECOND):
   - Fog means ground-level moisture obscuring the sky — the entire image looks washed out, uniformly hazy
   - Key fog indicators: you CANNOT distinguish ANY sky features — no stars, no cloud edges, no clear/dark patches, no moon
   - The image has severely reduced contrast across the ENTIRE frame, not just parts of it
   - Fog produces a flat, featureless, uniformly bright or gray image with NO texture variation
   - DO NOT confuse fog with overcast: overcast clouds still show texture, gradients, or brightness variations across the sky
   - DO NOT confuse fog with moon glow: if the moon is above the horizon, a bright hazy area around it is moon glow, NOT fog
   - If fog is detected, set fogDetected=true and condition=""Foggy""

3. **Weather Condition Classification** (Only if no rain or fog detected):
   - Clear: 0-15% cloud coverage. At night: many stars visible across the sky region. During day: blue sky dominant.
   - PartlyCloudy: 15-50% cloud coverage. Mix of clear patches and cloud areas. At night: stars visible in parts of the sky region, missing in others.
   - MostlyCloudy: 50-85% cloud coverage. Mostly clouds with few clear gaps. At night: very few stars, most of the sky region without any.
   - Overcast: 85-100% cloud coverage. Uniform coverage, no clear patches. At night: zero stars across the sky region.
   - Unknown: the sky region cannot be read at all (see the unreadable-frame rule). Treated as unsafe.

4. **Cloud Coverage Percentage** (0-100):
   - Estimate what percentage of the SKY REGION is covered by clouds
   - Only count the sky region, not housing, ground, trees or buildings
   - AT NIGHT: the fraction of the sky region where stars are missing is the cloud coverage
   - AT NIGHT: do NOT count the moon itself or its immediate glow ring as cloud coverage
   - Consider cloud density and transparency
   - NOTE: Even if clouds appear thin or scattered, water droplets on lens means ""Rainy""
   - CONSISTENCY CHECK: the cloudCoverage percentage MUST match the condition. If condition is Overcast, cloudCoverage must be 85-100. If condition is Clear, cloudCoverage must be 0-15.

5. **Safety Assessment**:
   - UNSAFE if: Rain detected, fog detected, cloud coverage >70%, or the sky cannot be read
   - SAFE only if: Clear or PartlyCloudy conditions with <50% coverage, AND no rain/fog
   - Any moisture on the lens = UNSAFE

6. **Confidence Level** (0-100):
   - High confidence (80-100) for clear rain droplets or obvious conditions
   - Medium confidence (50-79) for typical cloud patterns
   - Lower confidence (0-49) for ambiguous, borderline or barely readable frames

CRITICAL RULES:
- Water droplets on lens = ""Rainy"", rainDetected=true, isSafe=false
- Hazy/foggy appearance = ""Foggy"", fogDetected=true, isSafe=false
- Do not classify as PartlyCloudy or Clear if you see ANY lens moisture
- DURING DAY: entirely white/gray sky with no blue = Overcast, NOT Clear or PartlyCloudy
- Noise specks are not stars; an unreadable frame is not a clear sky
";

        private const string LightPollutedCriticalRules =
@"- AT NIGHT: bright/milky sky with no stars = high cloud coverage, NOT clear
- AT NIGHT: do NOT confuse illuminated clouds with clear sky
";

        private const string DarkSiteCriticalRules =
@"- AT NIGHT: a dark sky without stars = high cloud coverage, NOT clear
- AT NIGHT: do NOT confuse a black, starless sky with clear sky
";

        private const string SelfCheckAndJsonBlock =
@"
SELF-CHECK before responding:
- If you estimated cloudCoverage < 50%, can you clearly identify large areas of clear sky? (stars at night, blue patches during day)
- If you cannot point to clear sky areas, your cloudCoverage estimate is too low — revise upward.
- Are the ""stars"" you counted only in the sky region, and not spread evenly over the whole frame like noise?
- Does your cloudCoverage match your condition? Clear=0-15%, PartlyCloudy=15-50%, MostlyCloudy=50-85%, Overcast=85-100%.

Respond in JSON format:
{
  ""condition"": ""Clear|PartlyCloudy|MostlyCloudy|Overcast|Rainy|Foggy|Unknown"",
  ""cloudCoverage"": 0-100,
  ""rainDetected"": true|false,
  ""fogDetected"": true|false,
  ""isSafe"": true|false,
  ""description"": ""brief description of observed conditions"",
  ""confidence"": 0-100
}";

        /// <summary>
        /// Upper bound for the user's site notes, in characters. Every analysis cycle pays
        /// for this text, and small local models (Gemma, Qwen) lose the output format when
        /// the prompt grows: a few sentences of local knowledge help, an essay does not.
        /// </summary>
        public const int MaxSiteNotesLength = 1200;

        /// <summary>
        /// Build the site-specific notes block from what the observatory owner wrote in the
        /// options. Every site has visual quirks no general prompt can anticipate — clouds
        /// lit by a nearby city that read as overcast, a dome reflection that reads as haze —
        /// and this is where that knowledge enters the analysis.
        ///
        /// The notes steer *interpretation* only: the classification thresholds, the safety
        /// rules and the JSON contract stay with the system prompt, so a note can explain
        /// what a feature is without being able to redefine what "safe" means.
        /// Returns an empty string when nothing was written, so the prompt is unchanged for
        /// everyone who does not use the field.
        /// </summary>
        public static string BuildSiteNotesBlock(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) { return string.Empty; }

            var trimmed = notes.Trim();
            if (trimmed.Length > MaxSiteNotesLength)
            {
                trimmed = trimmed.Substring(0, MaxSiteNotesLength).TrimEnd() + "...";
            }

            return $@"SITE-SPECIFIC NOTES (written by the owner of this observatory, about this location and camera):
{trimmed}

Use these notes to interpret what you see — they describe known local artifacts of this site.
They do not change the classification thresholds, the safety rules, or the JSON response format.
";
        }

        /// <summary>
        /// The full prefix prepended to the user message: the observation context (when
        /// astronomical data is available) followed by the site notes (when the user wrote
        /// any). Kept in one place so every provider sends the same thing, and so the notes
        /// still reach the model when no astronomical context could be computed.
        /// </summary>
        public static string BuildPromptPrefix(AstroContext ctx)
        {
            var context = ctx != null ? BuildContextBlock(ctx) : string.Empty;
            return context + BuildSiteNotesBlock(Properties.Settings.Default.SiteNotes);
        }

        /// <summary>
        /// Build a dynamic observation context block from astronomical data.
        /// This is prepended to the user message so the AI knows day/night state, moon, etc.
        /// </summary>
        public static string BuildContextBlock(AstroContext ctx)
        {
            var lat = ctx.Latitude >= 0 ? $"{ctx.Latitude:F2}°N" : $"{Math.Abs(ctx.Latitude):F2}°S";
            var lon = ctx.Longitude >= 0 ? $"{ctx.Longitude:F2}°E" : $"{Math.Abs(ctx.Longitude):F2}°W";

            var expectation = ctx.SunState switch
            {
                "Day" => "Expect a bright daytime sky.",
                "Civil Twilight" => "Expect a dim sky near sunrise/sunset.",
                _ when ctx.MoonAltitude > 10 && ctx.MoonIllumination > 40
                    => $"Expect a dark sky with the MOON visible (moon is {ctx.MoonIllumination:F0}% illuminated at {ctx.MoonAltitude:F1}° altitude). The bright glow around the moon is NORMAL — do NOT count it as cloud coverage.",
                _ when ctx.MoonAltitude > 0 && ctx.MoonIllumination > 10
                    => $"The moon is low on the horizon ({ctx.MoonAltitude:F1}° altitude, {ctx.MoonIllumination:F0}% illuminated). It may cause a bright area near the horizon — this is NOT clouds.",
                _ => "Expect a dark sky. Any bright disc is NOT the sun."
            };

            return $@"OBSERVATION CONTEXT:
- Date/Time: {ctx.UtcTime:yyyy-MM-dd HH:mm} UTC ({ctx.LocalTime:HH:mm} {ctx.TimeZone})
- Location: {lat}, {lon}, {ctx.Elevation:F0}m elevation
- Sun: {ctx.SunAltitude:F1}° altitude ({ctx.SunState})
- Moon: {ctx.MoonIllumination:F0}% illuminated ({ctx.MoonPhase}), {ctx.MoonAltitude:F1}° altitude
- {expectation}
";
        }
    }
}
