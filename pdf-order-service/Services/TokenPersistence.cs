using System;
using System.IO;

namespace pdf_extractor.Services
{
    public class TokenPersistence
    {
        private const int ExpirationHours = 24;

        private static string PathFor(string userName)
        {
            var dir = Path.Combine(@"C:\OrderSubmissionServiceData", "token");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"token-{userName}.txt");
        }

        public static bool TryLoad(string userName, int expirationHours, out string token)
        {
            token = "";
            var path = PathFor(userName);

            if (!File.Exists(path))
            {
                return false;
            }
                
            var lines = File.ReadAllLines(path);

            // No timestamp means "old style token" → treat as expired
            if (lines.Length < 2)
            {
                File.Delete(path);
                return false;
            }

            token = lines[0].Trim();

            var timestampString = lines[1].Trim();
            if (!DateTime.TryParse(timestampString, out var timestamp))
            {
                File.Delete(path);
                token = "";
                return false;
            }

            // Expiration controlled by appsettings.json
            if (DateTime.UtcNow - timestamp > TimeSpan.FromHours(ExpirationHours))
            {
                File.Delete(path);
                token = "";
                return false;
            }

            return !string.IsNullOrEmpty(token);
        }

        public static void Save(string userName, string token)
        {
            var path = PathFor(userName);

            File.WriteAllLines(path, new[]
            {
                token,
                DateTime.UtcNow.ToString("o")
            });
        }

        public static void Clear(string userName)
        {
            var path = PathFor(userName);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}