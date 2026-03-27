using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Astronomical context computed from observer location and current time.
    /// Uses Meeus algorithms for sun/moon position and lunar phase.
    /// </summary>
    public class AstroContext
    {
        public DateTime UtcTime { get; init; }
        public DateTime LocalTime { get; init; }
        public string TimeZone { get; init; } = "";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double Elevation { get; init; }
        public double SunAltitude { get; init; }
        public string SunState { get; init; } = "";
        public double MoonAltitude { get; init; }
        public double MoonIllumination { get; init; }
        public string MoonPhase { get; init; } = "";

        /// <summary>
        /// Compute full astronomical context for a given observer position and time.
        /// </summary>
        public static AstroContext Compute(double latitude, double longitude, double elevation, DateTime utcNow)
        {
            var jd = ToJulianDay(utcNow);
            var sunAlt = ComputeSunAltitude(jd, latitude, longitude);
            var (moonAlt, moonIllum, moonPhase) = ComputeMoonInfo(jd, latitude, longitude);

            var localTime = utcNow.ToLocalTime();
            var tz = TimeZoneInfo.Local;
            var offset = tz.GetUtcOffset(utcNow);
            var tzName = $"UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset.Hours:D2}:{offset.Minutes:D2}";

            return new AstroContext
            {
                UtcTime = utcNow,
                LocalTime = localTime,
                TimeZone = tzName,
                Latitude = latitude,
                Longitude = longitude,
                Elevation = elevation,
                SunAltitude = sunAlt,
                SunState = ClassifySunState(sunAlt),
                MoonAltitude = moonAlt,
                MoonIllumination = moonIllum,
                MoonPhase = moonPhase
            };
        }

        private static double ToJulianDay(DateTime utc)
        {
            int y = utc.Year, m = utc.Month, d = utc.Day;
            double dayFraction = (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;
            if (m <= 2) { y--; m += 12; }
            int a = y / 100;
            int b = 2 - a + a / 4;
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + dayFraction + b - 1524.5;
        }

        private static double ComputeSunAltitude(double jd, double lat, double lon)
        {
            // Meeus solar position (low-accuracy, ~1 arcmin)
            double T = (jd - 2451545.0) / 36525.0;
            double L0 = Normalize(280.46646 + 36000.76983 * T + 0.0003032 * T * T); // mean longitude
            double M = Normalize(357.52911 + 35999.05029 * T - 0.0001537 * T * T);  // mean anomaly
            double Mrad = M * Math.PI / 180.0;

            // equation of center
            double C = (1.9146 - 0.004817 * T) * Math.Sin(Mrad)
                      + 0.019993 * Math.Sin(2 * Mrad)
                      + 0.00029 * Math.Sin(3 * Mrad);

            double sunLon = (L0 + C) * Math.PI / 180.0;

            // obliquity of ecliptic
            double eps = (23.439291 - 0.0130042 * T) * Math.PI / 180.0;

            // right ascension and declination
            double sinDec = Math.Sin(eps) * Math.Sin(sunLon);
            double dec = Math.Asin(sinDec);
            double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(sunLon), Math.Cos(sunLon));

            // Greenwich mean sidereal time
            double GMST = Normalize(280.46061837 + 360.98564736629 * (jd - 2451545.0));
            double lst = (GMST + lon) * Math.PI / 180.0; // local sidereal time

            // hour angle
            double ha = lst - ra;

            // altitude
            double latRad = lat * Math.PI / 180.0;
            double sinAlt = Math.Sin(latRad) * Math.Sin(dec) + Math.Cos(latRad) * Math.Cos(dec) * Math.Cos(ha);
            return Math.Asin(sinAlt) * 180.0 / Math.PI;
        }

        private static (double altitude, double illumination, string phase) ComputeMoonInfo(double jd, double lat, double lon)
        {
            double T = (jd - 2451545.0) / 36525.0;

            // Moon mean elements (Meeus Ch. 47, simplified)
            double Lp = Normalize(218.3165 + 481267.8813 * T);   // mean longitude
            double D  = Normalize(297.8502 + 445267.1115 * T);   // mean elongation
            double M  = Normalize(357.5291 + 35999.0503 * T);    // sun mean anomaly
            double Mp = Normalize(134.9634 + 477198.8676 * T);   // moon mean anomaly
            double F  = Normalize(93.2720 + 483202.0175 * T);    // argument of latitude

            double LpR = Lp * Math.PI / 180.0;
            double DR  = D  * Math.PI / 180.0;
            double MR  = M  * Math.PI / 180.0;
            double MpR = Mp * Math.PI / 180.0;
            double FR  = F  * Math.PI / 180.0;

            // Ecliptic longitude (simplified — largest terms)
            double moonLon = Lp
                + 6.289 * Math.Sin(MpR)
                - 1.274 * Math.Sin(2 * DR - MpR)
                + 0.658 * Math.Sin(2 * DR)
                - 0.214 * Math.Sin(2 * MpR)
                - 0.186 * Math.Sin(MR);
            double moonLonR = moonLon * Math.PI / 180.0;

            // Ecliptic latitude (simplified)
            double moonLat = 5.128 * Math.Sin(FR)
                + 0.281 * Math.Sin(MpR + FR)
                - 0.278 * Math.Sin(FR - MpR);
            double moonLatR = moonLat * Math.PI / 180.0;

            // Obliquity
            double eps = (23.439291 - 0.0130042 * T) * Math.PI / 180.0;

            // Ecliptic to equatorial
            double sinMoonLon = Math.Sin(moonLonR);
            double cosMoonLon = Math.Cos(moonLonR);
            double cosMoonLat = Math.Cos(moonLatR);
            double sinMoonLat = Math.Sin(moonLatR);

            double ra = Math.Atan2(
                sinMoonLon * Math.Cos(eps) - Math.Tan(moonLatR) * Math.Sin(eps),
                cosMoonLon);
            double dec = Math.Asin(sinMoonLat * Math.Cos(eps) + cosMoonLat * Math.Sin(eps) * sinMoonLon);

            // Local hour angle → altitude
            double GMST = Normalize(280.46061837 + 360.98564736629 * (jd - 2451545.0));
            double lst = (GMST + lon) * Math.PI / 180.0;
            double ha = lst - ra;

            double latRad = lat * Math.PI / 180.0;
            double sinAlt = Math.Sin(latRad) * Math.Sin(dec) + Math.Cos(latRad) * Math.Cos(dec) * Math.Cos(ha);
            double altitude = Math.Asin(sinAlt) * 180.0 / Math.PI;

            // Phase angle and illumination
            // Phase angle i ≈ 180° - D (simplified)
            double phaseAngle = 180.0 - D;
            // Normalize to [0, 360)
            phaseAngle = Normalize(phaseAngle);
            double illumination = (1.0 + Math.Cos(phaseAngle * Math.PI / 180.0)) / 2.0 * 100.0;

            // Phase name from elongation D
            double normD = Normalize(D);
            string phaseName = normD switch
            {
                < 22.5 => "New Moon",
                < 67.5 => "Waxing Crescent",
                < 112.5 => "First Quarter",
                < 157.5 => "Waxing Gibbous",
                < 202.5 => "Full Moon",
                < 247.5 => "Waning Gibbous",
                < 292.5 => "Last Quarter",
                < 337.5 => "Waning Crescent",
                _ => "New Moon"
            };

            return (altitude, illumination, phaseName);
        }

        private static string ClassifySunState(double altitude)
        {
            return altitude switch
            {
                > -0.833 => "Day",
                > -6.0   => "Civil Twilight",
                > -12.0  => "Nautical Twilight",
                > -18.0  => "Astronomical Twilight",
                _        => "Night"
            };
        }

        private static double Normalize(double degrees)
        {
            double result = degrees % 360.0;
            if (result < 0) result += 360.0;
            return result;
        }
    }
}
