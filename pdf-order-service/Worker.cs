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
        private readonly string inboundPdfFolder;
        private readonly string archivedPdfFolder;

        public Worker(ILogger<Worker> logger, IOptions<AppCredentialsOptions> creds)
        {
            _logger = logger;
            _creds = creds.Value;
            inboundPdfFolder = _creds.InboundPdfFolder;
            archivedPdfFolder = _creds.ArchivedPdfFolder;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[ALERT] Starting PDF watcher...");

            _logger.LogInformation("[ALERT] Loaded credentials for user: {user}", _creds.Username);

            // Ensure the folder exists
            Directory.CreateDirectory(inboundPdfFolder);

            // Create a watcher for *.pdf files
            _watcher = new FileSystemWatcher(inboundPdfFolder, "*.pdf");
            _watcher.Created += OnNewFile;       // fired when a file is created
            _watcher.EnableRaisingEvents = true; // start listening

            _logger.LogInformation("[ALERT] Watching folder: {folder}", inboundPdfFolder);

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
                // ✅ Read and extract order safely while ensuring file handle release
                OrderDocument order;
                using (var stream = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var extractor = new PdfExtractor();
                    order = extractor.ExtractOrder(stream);
                }

                // 🔓 Ensure .NET finalizes any internal PDF handles before we try to move
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(500);
                _logger.LogInformation("[ALERT] File handle released for: {file}", e.FullPath);

                // ✅ Log extraction results
                _logger.LogInformation("[ALERT] Extraction complete for {file}. Found {count} line items.",
                    e.FullPath, order.Items?.Count ?? 0);

                foreach (var item in order.Items)
                {
                    _logger.LogInformation("Extracted item: {part} x{qty}", item.PartNumber, item.Quantity);
                }

                // ✅ Authentication + WebSocket sending
                try
                {
                    if (TokenPersistence.TryLoad(_creds.Username, out var savedToken))
                    {
                        _cache.Set(_creds.Username, savedToken);
                        _logger.LogInformation("[ALERT] Loaded token from disk.");
                    }

                    await MsApiCalls.RunAsync(_creds, _cache);

                    if (!_cache.TryGet(_creds.Username, out var token))
                    {
                        _logger.LogError("[ALERT] No token available. Login must succeed before sending order.");
                        return;
                    }

                    var channel = $"{_creds.Username}client{DateTime.Now:HHmmss}";
                    await MsApiCalls.WsConnectAsync(_creds.Username, token, channel);

                    var placeOrder = new
                    {
                        channel,
                        data = new
                        {
                            Locationname = _creds.DefaultLocation,
                            RequestType = "PlaceOrder",
                            Order = new[]
                            {
                        new
                        {
                            Sellerid = _creds.SellerId,
                            PONumber = order.OrderInfo?.OrderNumber,
                            ShipCost = "10",
                            Comments = "Automated order from Windows Service",
                            ShipVia = "UPSN",
                            DeliveryMethod = order.OrderInfo?.DeliveryWay,
                            Paymentmethod = order.OrderInfo?.PaymentMethod,
                            estimatedtaxes = "2.6",
                            OrderItems = (order.Items ?? new List<LineItem>())
                                .Select(item => new
                                {
                                    Brand = "HBWN",
                                    Part = item.PartNumber,
                                    Mfg = "LF",
                                    Quantity = (item.Quantity ?? 0).ToString(),
                                    LocationId = "1"
                                })
                                .ToArray(),
                            Shipto = new[]
                            {
                                new
                                {
                                    ShipToName = order.CustomerInfo?.Customer,
                                    ShipToAddress1 = order.DeliveryAddress?.Street,
                                    ShipToAddress2 = "",
                                    ShipToCity = order.DeliveryAddress?.City,
                                    ShipToState = order.DeliveryAddress?.State,
                                    ShipToZipCode = order.DeliveryAddress?.ZipCode,
                                    ShipToCountry = order.DeliveryAddress?.Country,
                                    ShipToContact = order.CustomerInfo?.DmsNumber,
                                    ShipToPhone = order.CustomerInfo?.Phone,
                                    EmailAddress = order.CustomerInfo?.Email,
                                    ShipToCompanyName = order.CustomerInfo?.Company
                                }
                            }
                        }
                    }
                        }
                    };

                    var orderJson = JsonSerializer.Serialize(placeOrder, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = null,
                        WriteIndented = true
                    });

                    _logger.LogInformation("[ALERT] Sending order payload for {file}", e.FullPath);
                    await MsApiCalls.WsSendAsync(orderJson);
                    _logger.LogInformation("[ALERT] Order sent successfully for {file}", e.FullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALERT] Error sending order for {file}", e.FullPath);
                }

                // ✅ Save extracted data to JSON file
                string jsonPath = Path.ChangeExtension(e.FullPath, ".json");
                string json = JsonSerializer.Serialize(order, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(jsonPath, json);
                _logger.LogInformation("[ALERT] Saved extracted data to {jsonPath}", jsonPath);

                // ✅ Move both PDF and JSON to archive safely
                try
                {
                    string archivedPdfFolder = _creds.ArchivedPdfFolder ?? "C:\\ArchivedPDF";
                    Directory.CreateDirectory(archivedPdfFolder);

                    string archivedPdfPath = Path.Combine(archivedPdfFolder, Path.GetFileName(e.FullPath));
                    string archivedJsonPath = Path.Combine(archivedPdfFolder, Path.GetFileName(jsonPath));

                    const int maxRetries = 10;
                    const int delayMs = 1000;

                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            // 🕐 Ensure both files are ready to move
                            if (await WaitUntilFileIsReadyAsync(e.FullPath) && await WaitUntilFileIsReadyAsync(jsonPath))
                            {
                                File.Move(e.FullPath, archivedPdfPath, true);
                                File.Move(jsonPath, archivedJsonPath, true);

                                _logger.LogInformation("[ALERT] Moved PDF and JSON to archive: {path}", archivedPdfFolder);
                                break;
                            }
                        }
                        catch (IOException)
                        {
                            _logger.LogWarning("[ALERT] File still locked, retrying move attempt {try}/{max}...", i + 1, maxRetries);
                            await Task.Delay(delayMs);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            _logger.LogWarning("[ALERT] Access denied, retrying move attempt {try}/{max}...", i + 1, maxRetries);
                            await Task.Delay(delayMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ALERT] Failed to move files to archive for {file}", e.FullPath);
                }
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
                    using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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
