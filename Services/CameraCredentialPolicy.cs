namespace AIWeather.Services
{
    public static class CameraCredentialPolicy
    {
        // Do not trim a password: whitespace can be part of an intentional credential.
        public static string ForStart(string? entered, string? saved) =>
            string.IsNullOrEmpty(entered) ? saved ?? string.Empty : entered;
    }
}
