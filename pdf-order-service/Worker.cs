using Microsoft.Extensions.Options;
using pdf_extractor.Configuration;
using pdf_extractor.Models;
using pdf_extractor.Services;
using System.Diagnostics;
using System.Text.Json;

namespace pdf_order_service
{
    public class Worker : BackgroundService
    {
        private readonly AppCredentialsOptions creds;
        private readonly TokenCache cache = new();
        private FileSystemWatcher? watcher;
        private string? _wsChannel;

        private readonly string inboundPdfFolder;
        private readonly string archivedPdfFolder;
        private readonly string failedPdfFolder;

        // Initializes configuration settings and folder paths for the PDF watcher service.
        public Worker(ILogger<Worker> logger, IOptions<AppCredentialsOptions> appCredentials)
        {
            creds = appCredentials.Value;
            inboundPdfFolder = creds.InboundPdfFolder;
            archivedPdfFolder = creds.ArchivedPdfFolder;
            failedPdfFolder = creds.FailedPdfFolder;
        }

        // Main entry point for the background service — starts the file watcher and begins monitoring for new PDFs.
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Debug.WriteLine("[STARTUP] PDF Watcher starting...");
            Directory.CreateDirectory(inboundPdfFolder);

            watcher = new FileSystemWatcher(inboundPdfFolder, "*.pdf");
            watcher.Created += OnNewFile;
            watcher.EnableRaisingEvents = true;

            Debug.WriteLine($"[WATCHING] Folder: {inboundPdfFolder}");
            return Task.CompletedTask;
        }

        // Triggered when a new PDF is detected — handles extraction, validation, and order processing.
        private async void OnNewFile(object sender, FileSystemEventArgs e)
        {
            Debug.WriteLine($"[DETECTED] New PDF: {e.FullPath}");

            if (!await WaitUntilFileIsReadyAsync(e.FullPath))
            {
                Debug.WriteLine($"[WARN] File not ready: {e.FullPath}");
                return;
            }

            try
            {
                var order = await ExtractOrderAsync(e.FullPath);
                await EnsureConnectedAsync();

                await ProcessOrderAsync(e.FullPath, order);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] Unexpected failure for {e.FullPath}: {ex.Message}");
            }
        }

        // Extracts order details from the specified PDF and returns an OrderDocument object.
        private async Task<OrderDocument> ExtractOrderAsync(string pdfPath)
        {
            using var stream = File.Open(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var extractor = new PdfExtractor();
            var order = extractor.ExtractOrder(stream);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(500);

            Debug.WriteLine($"[EXTRACT] {order.Items?.Count ?? 0} items extracted from {pdfPath}");
            return order;
        }

        // Ensure valid token and Web Socket connection
        private async Task EnsureConnectedAsync()
        {
            if (TokenPersistence.TryLoad(creds.Username, creds.TokenExpirationHours, out var savedToken))
            {
                cache.Set(creds.Username, savedToken);
                Debug.WriteLine("[TOKEN] Loaded from disk.");
            }

            await MsApiCalls.RunAsync(creds, cache);

            if (!cache.TryGet(creds.Username, out var token))
                throw new InvalidOperationException("No token available. Please check credentials.");

            if (_wsChannel == null)
                _wsChannel = $"{creds.Username}client{DateTime.Now:HHmmss}";

            await MsApiCalls.WsConnectAsync(creds.Username, token, _wsChannel);
        }

        // Processes the extracted order — checks price/availability, sends the order, and handles file archiving.
        private async Task ProcessOrderAsync(string pdfPath, OrderDocument order)
        {
            if (!cache.TryGet(creds.Username, out var token))
                throw new InvalidOperationException("Token missing before order processing.");

            var channel = _wsChannel!;

            var (available, unavailable, _) =
                await MsApiCalls.CheckPriceAvailabilityAsync(creds, channel, order.Items ?? new List<LineItem>());

            if (!available.Any())
            {
                Debug.WriteLine("[ORDER] No available parts — skipping order send.");

                try
                {
                    string failedLogFolder = failedPdfFolder;
                    Directory.CreateDirectory(failedLogFolder);

                    // Build error log
                    string logFileName = Path.GetFileNameWithoutExtension(pdfPath) + "_ErrorLog.txt";
                    string logFilePath = Path.Combine(failedLogFolder, logFileName);
                    string unavailableList = unavailable.Any()
                        ? string.Join(", ", unavailable)
                        : "All parts unavailable (quantity = 0 or missing price)";

                    string logMessage =
                        "[FAILED ORDER LOG]\n" +
                        $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"PDF File: {pdfPath}\n" +
                        $"Order Failed: {unavailableList}\n" +
                        "------------------------------------------------------------\n";

                    await File.WriteAllTextAsync(logFilePath, logMessage);

                    // Move files to FailedPDF folder
                    Directory.CreateDirectory(failedPdfFolder);
                    string targetPdfPath = Path.Combine(failedPdfFolder, Path.GetFileName(pdfPath));
                    string jsonPath = Path.ChangeExtension(pdfPath, ".json");
                    string targetJsonPath = Path.Combine(failedPdfFolder, Path.GetFileName(jsonPath));

                    if (File.Exists(pdfPath)) File.Move(pdfPath, targetPdfPath, true);
                    if (File.Exists(jsonPath)) File.Move(jsonPath, targetJsonPath, true);

                    Debug.WriteLine($"[ORDER] Moved PDF/JSON to FailedPDF: {failedPdfFolder}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ORDER ERROR] Failed to move or log failed order: {ex.Message}");
                }

                return;
            }

            // Build order payload
            var placeOrder = new
            {
                channel,
                data = new
                {
                    Locationname = creds.DefaultLocation,
                    RequestType = "PlaceOrder",
                    Order = new[]
                    {
                        new
                        {
                            Sellerid = creds.SellerId,
                            PONumber = order.OrderInfo?.OrderNumber,
                            Comments = "Automated order from service",
                            ShipVia = "UPSN",
                            DeliveryMethod = order.OrderInfo?.DeliveryWay,
                            Paymentmethod = order.OrderInfo?.PaymentMethod,
                            OrderItems = order.Items!
                                .Where(i => available.Contains(i.PartNumber))
                                .Select(i => new
                                {
                                    Brand = "HBWN",
                                    Part = i.PartNumber,
                                    Mfg = "LF",
                                    Quantity = (i.Quantity ?? 0).ToString(),
                                    LocationId = "1"
                                }).ToArray(),
                            Shipto = new[]
                            {
                                new
                                {
                                    ShipToName = order.CustomerInfo?.Customer,
                                    ShipToAddress1 = order.DeliveryAddress?.Street,
                                    ShipToCity = order.DeliveryAddress?.City,
                                    ShipToState = order.DeliveryAddress?.State,
                                    ShipToZipCode = order.DeliveryAddress?.ZipCode,
                                    EmailAddress = order.CustomerInfo?.Email
                                }
                            }
                        }
                    }
                }
            };

            string orderJson = JsonSerializer.Serialize(placeOrder, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = true
            });

            Debug.WriteLine($"[ORDER] Sending order with {available.Count} items.");
            await MsApiCalls.WsSendAsync(orderJson);

            // Await response & archive
            await Task.Delay(2000);
            await FinalizeOrderAsync(pdfPath, order, MsApiCalls.GetLastWsResponse());
        }

        // Finalizes the order by saving logs, generating JSON output, and moving files to the appropriate folder.
        private async Task FinalizeOrderAsync(string pdfPath, OrderDocument order, string? wsResponse)
        {
            bool failed = false;
            if (!string.IsNullOrEmpty(wsResponse) &&
                (wsResponse.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                 wsResponse.Contains("Invalid", StringComparison.OrdinalIgnoreCase)))
            {
                failed = true;
                var failedLogPath = Path.Combine(failedPdfFolder,
                    $"{Path.GetFileNameWithoutExtension(pdfPath)}_ErrorLog.txt");
                await File.WriteAllTextAsync(failedLogPath, wsResponse);
                Debug.WriteLine($"[FAIL] Logged to {failedLogPath}");
            }

            string targetFolder = failed ? failedPdfFolder : archivedPdfFolder;
            Directory.CreateDirectory(targetFolder);

            string jsonPath = Path.ChangeExtension(pdfPath, ".json");
            await File.WriteAllTextAsync(jsonPath,
                JsonSerializer.Serialize(order, new JsonSerializerOptions { WriteIndented = true }));

            File.Move(pdfPath, Path.Combine(targetFolder, Path.GetFileName(pdfPath)), true);
            File.Move(jsonPath, Path.Combine(targetFolder, Path.GetFileName(jsonPath)), true);

            Debug.WriteLine($"[COMPLETE] Moved to {targetFolder}");
        }

        // Waits until the specified file is fully written and unlocked before processing.
        private static async Task<bool> WaitUntilFileIsReadyAsync(string filePath, int retries = 20, int delayMs = 500)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }
                catch (IOException)
                {
                    await Task.Delay(delayMs);
                }
            }
            return false;
        }

        // Stops the background service and disposes of the file watcher when the application is shutting down.
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            watcher?.Dispose();
            Debug.WriteLine("[STOPPED] Watcher stopped.");
            return base.StopAsync(cancellationToken);
        }
    }
}