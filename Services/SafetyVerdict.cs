namespace AIWeather.Services
{
    /// <summary>How the external ASCOM safety monitor's verdict is combined with the sky's.</summary>
    public enum ExternalMonitorMode
    {
        /// <summary>Safe only when both say safe. The default, and all there was before #19.</summary>
        And = 0,

        /// <summary>
        /// Unsafe only when both say unsafe: the external device confirms the sky verdict
        /// rather than adding to it. For a narrow-field cloud sensor that cries cloudy for
        /// clouds far from the target while the AI judges the sky around the pointing (#19).
        /// </summary>
        Or = 1,
    }

    /// <summary>
    /// Pure local input composition. The monitor applies connection, daylight and
    /// replica authority gates separately before consulting this truth table.
    /// </summary>
    public static class SafetyVerdict
    {
        /// <param name="mode">How the external monitor is combined, when one is enabled.</param>
        /// <param name="skySafe">The sky verdict from the latest analysis (hysteresis applied).</param>
        /// <param name="fresh">Whether that analysis is recent enough to describe the current sky.</param>
        /// <param name="hazard">Rain, fog or explicit unreadable sky in the latest analysis. Water on the lens is not a
        /// cloud far from the target: in OR mode a clear-reading cloud sensor does not dry it.</param>
        /// <param name="externalEnabled">Whether an external monitor is configured at all.</param>
        /// <param name="externalSafe">Its verdict; unreadable counts as unsafe.</param>
        public static bool Combine(ExternalMonitorMode mode, bool skySafe, bool fresh, bool hazard, bool externalEnabled, bool externalSafe)
        {
            // Stale data makes the sky unknown, and an unknown sky is an unsafe one - also in OR
            // mode, where it hands the decision to the external device alone. A hazard seen in
            // a stale analysis says nothing about now, so it is not carried past the freshness check.
            var sky = skySafe && fresh;

            if (!externalEnabled) return sky;

            if (mode == ExternalMonitorMode.Or)
            {
                if (fresh && hazard) return false;
                return sky || externalSafe;
            }

            return sky && externalSafe;
        }
    }
}
