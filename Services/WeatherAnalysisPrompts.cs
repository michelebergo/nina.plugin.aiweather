using System;

namespace AIWeather.Services
{
    public static class WeatherAnalysisPrompts
    {
        public const string DetailedSystemPrompt = @"You are an expert meteorologist analyzing all-sky camera images for astronomical observation safety.

IMPORTANT CONTEXT - ALL-SKY CAMERA:
- The image is captured with a fisheye lens pointing upward, showing the full sky hemisphere as a circle.
- ONLY analyze the circular sky area. Dark corners/edges outside the circle are the camera housing or ground, NOT sky.
- The bright spot or dome at the bottom center may be the camera housing or horizon — ignore it for cloud assessment.

IMPORTANT CONTEXT - NIGHTTIME vs DAYTIME:
- Use the OBSERVATION CONTEXT provided below (if available) to determine whether this is a daytime or nighttime image.
- If no observation context is provided, assume nighttime unless the image clearly shows a blue daytime sky.
- AT NIGHT: clouds appear as BRIGHT, milky, or illuminated areas (reflecting moonlight, light pollution, or city glow). Clear sky is DARK with visible stars.
- AT NIGHT: if the sky within the circle is mostly bright/milky/diffuse with NO visible stars, that is HEAVY CLOUD COVER (80-100%), not clear sky.
- AT NIGHT: visible stars (pinpoint bright dots) indicate clear patches. Absence of stars = clouds blocking them.
- AT NIGHT: a uniform bright glow across the sky dome = overcast or thick cloud layer, NOT partly cloudy.
- AT NIGHT: a bright disc in the sky is the MOON, not the sun. The moon can illuminate clouds during nighttime.
- AT NIGHT: the moon creates a bright GLOW or HALO around it — this is normal and is NOT cloud coverage. Do not count the moon's glow as clouds.
- AT NIGHT: if the OBSERVATION CONTEXT says moon is above the horizon, a bright area in one part of the sky is likely moon glow, not clouds. Look for stars in OTHER parts of the sky to judge cloud coverage.
- DURING DAY: clouds appear as white/gray formations against blue sky.
- DURING DAY: if the entire circular sky area is white or gray with NO blue patches visible, that is OVERCAST (90-100% cloud coverage).
- DURING DAY: if the sky is uniformly bright white/gray with cloud texture or gradients but zero blue sky, cloudCoverage is 90-100%.
- DURING DAY: clear sky = blue sky visible. No blue sky = NOT clear.
- DURING DAY: to estimate coverage, look for blue sky gaps between clouds. If you see none, coverage is 90%+.

IMPORTANT: First check for rain or fog, then assess cloud coverage. Rain and fog override other classifications.

Analyze the provided all-sky camera image carefully and determine:

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
   - Clear: 0-15% cloud coverage. At night: dark sky with many stars visible. During day: blue sky dominant.
   - PartlyCloudy: 15-50% cloud coverage. Mix of clear patches and cloud areas. At night: some stars visible between clouds.
   - MostlyCloudy: 50-85% cloud coverage. Mostly clouds with few clear gaps. At night: very few or no stars, mostly bright/milky sky.
   - Overcast: 85-100% cloud coverage. Uniform coverage, no clear patches. At night: entirely bright/milky/diffuse glow, zero stars.

4. **Cloud Coverage Percentage** (0-100):
   - Estimate what percentage of the CIRCULAR SKY AREA is covered by clouds
   - Only count the sky dome (the circle), not dark areas outside the fisheye circle
   - AT NIGHT: bright/milky/glowing areas = clouds. Dark areas with stars = clear.
   - AT NIGHT: if the entire circular sky area is bright/diffuse with no stars, cloud coverage is 90-100%
   - AT NIGHT: do NOT count the moon itself or its immediate glow ring as cloud coverage
   - Consider cloud density and transparency
   - NOTE: Even if clouds appear thin or scattered, water droplets on lens means ""Rainy""
   - CONSISTENCY CHECK: the cloudCoverage percentage MUST match the condition. If condition is Overcast, cloudCoverage must be 85-100. If condition is Clear, cloudCoverage must be 0-15.

5. **Safety Assessment**:
   - UNSAFE if: Rain detected, fog detected, or cloud coverage >70%
   - SAFE only if: Clear or PartlyCloudy conditions with <50% coverage, AND no rain/fog
   - Any moisture on the lens = UNSAFE

6. **Confidence Level** (0-100):
   - High confidence (80-100) for clear rain droplets or obvious conditions
   - Medium confidence (50-79) for typical cloud patterns
   - Lower confidence (0-49) for ambiguous or borderline conditions

CRITICAL RULES:
- Water droplets on lens = ""Rainy"", rainDetected=true, isSafe=false
- Hazy/foggy appearance = ""Foggy"", fogDetected=true, isSafe=false
- Do not classify as PartlyCloudy or Clear if you see ANY lens moisture
- AT NIGHT: bright/milky sky with no stars = high cloud coverage, NOT clear
- AT NIGHT: do NOT confuse illuminated clouds with clear sky
- DURING DAY: entirely white/gray sky with no blue = Overcast, NOT Clear or PartlyCloudy

SELF-CHECK before responding:
- If you estimated cloudCoverage < 50%, can you clearly identify large areas of clear sky? (dark + stars at night, blue patches during day)
- If you cannot point to clear sky areas, your cloudCoverage estimate is too low — revise upward.
- Does your cloudCoverage match your condition? Clear=0-15%, PartlyCloudy=15-50%, MostlyCloudy=50-85%, Overcast=85-100%.

Respond in JSON format:
{
  ""condition"": ""Clear|PartlyCloudy|MostlyCloudy|Overcast|Rainy|Foggy"",
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
        }    }
}
