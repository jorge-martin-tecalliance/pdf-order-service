using pdf_extractor.Services;
using pdf_extractor.Models;
using pdf_extractor.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;


namespace pdf_order_service
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly AppCredentialsOptions _creds;
        private readonly TokenCache _cache = new();
        private FileSystemWatcher? _watcher;
        private readonly string _watchFolder = @"C:\InboundPDFs";

        public Worker(ILogger<Worker> logger, IOptions<AppCredentialsOptions> creds)
        {
            _logger = logger;
            _creds = creds.Value;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[ALERT] Starting PDF watcher...");

            _logger.LogInformation("[ALERT] Loaded credentials for user: {user}", _creds.Username);

            // Ensure the folder exists
            Directory.CreateDirectory(_watchFolder);

            // Create a watcher for *.pdf files
            _watcher = new FileSystemWatcher(_watchFolder, "*.pdf");
            _watcher.Created += OnNewFile;       // fired when a file is created
            _watcher.EnableRaisingEvents = true; // start listening

            _logger.LogInformation("[ALERT] Watching folder: {folder}", _watchFolder);

            return Task.CompletedTask;
        }

        private async void OnNewFile(object sender, FileSystemEventArgs e)
        {
            _logger.LogInformation("[ALERT] New PDF detected: {file}", e.FullPath);

            bool isReady = await WaitUntilFileIsReadyAsync(e.FullPath);

            if (!isReady)
            {
                _logger.LogWarning("[ALERT] File did not become ready in time: {file}", e.FullPath);
                return;
            }

            try
            {
                using var stream = File.OpenRead(e.FullPath);

                // ✅ Use the same extractor you used in your web app
                var extractor = new PdfExtractor();
                OrderDocument order = extractor.ExtractOrder(stream);

                // ✅ Log extraction results
                _logger.LogInformation("[ALERT] Extraction complete for {file}. Found {count} line items.",
                    e.FullPath, order.Items?.Count ?? 0);



                // (Optional) You can save extracted data to a JSON file to inspect it
                string jsonPath = Path.ChangeExtension(e.FullPath, ".json");
                string json = System.Text.Json.JsonSerializer.Serialize(order,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(jsonPath, json);
                _logger.LogInformation("[ALERT] Saved extracted data to {jsonPath}", jsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ALERT] Error extracting data from {file}", e.FullPath);
            }
        }

        private static async Task<bool> WaitUntilFileIsReadyAsync(string filePath, int maxRetries = 20, int delayMilliseconds = 500)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // Try to open the file exclusively (no one else can be writing)
                    using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true; // File opened successfully → it's ready
                    }
                }
                catch (IOException)
                {
                    // File is still locked — wait and retry
                    await Task.Delay(delayMilliseconds);
                }
                catch (UnauthorizedAccessException)
                {
                    // File might still be in use by another process
                    await Task.Delay(delayMilliseconds);
                }
            }

            return false; // File never became ready within the retry limit
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping watcher...");
            _watcher?.Dispose();
            return base.StopAsync(cancellationToken);
        }

    }
}
