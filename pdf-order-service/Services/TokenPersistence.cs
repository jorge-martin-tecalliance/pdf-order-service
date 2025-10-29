using System;
using System.IO;

namespace pdf_extractor.Services
{
    public class TokenPersistence
    {
        private static string PathFor(string userName)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pdf-extractor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"token-{userName}.txt");
        }

        public static bool TryLoad(string userName, out string token)
        {
            var path = PathFor(userName);
            token = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            return !string.IsNullOrEmpty(token);
        }

        public static void Save(string userName, string token)
        {
            File.WriteAllText(PathFor(userName), token);
        }

        public static void Clear(string userName)
        {
            var path = PathFor(userName);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}