using pdf_extractor.Configuration;
using pdf_extractor.Models;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;


namespace pdf_extractor.Services
{
    public static class MsApiCalls
    {
        private const string BaseUrl = "https://opticat2.net/OpticatApi/api/v1/";
        private static readonly HttpClient Http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        private static string? _lastWsResponse;
        public static string? GetLastWsResponse() => _lastWsResponse;

        private sealed class LoginDto
        {
            public string? Token { get; set; }
            public string? Status { get; set; }
            public string? StatusMessage { get; set; }
        }

        // Login: fetch + cache + save token (skips if already cached)
        public static async Task RunAsync(AppCredentialsOptions creds, TokenCache cache)
        {
            if (cache.TryGet(creds.Username, out _))
            {
                System.Diagnostics.Debug.WriteLine("[Opticat login] Reusing cached token.");
                return;
            }

            var url = $"Login?UserName={Uri.EscapeDataString(creds.Username)}&Password={Uri.EscapeDataString(creds.Password)}";

            try
            {
                var response = await Http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                var dto = JsonSerializer.Deserialize<LoginDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(dto?.Token))
                {
                    System.Diagnostics.Debug.WriteLine($"[Opticat login] FAILED. Status: {(int)response.StatusCode}");
                    System.Diagnostics.Debug.WriteLine($"[Opticat login] Body:\n{body}");
                    return;
                }

                cache.Set(creds.Username, dto.Token);
                TokenPersistence.Save(creds.Username, dto.Token);
                System.Diagnostics.Debug.WriteLine("[Opticat login] Token cached + saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Opticat login] FAILED: {ex.Message}");
            }
        }

        // --- WebSocket state ---
        private static ClientWebSocket? _ws;
        private static CancellationTokenSource? _wsCts;

        // Connect to wss://opticat2.net:46429/?userid=...&channel=...&token=...
        // Connect to: wss://opticat2.net:46429/?userid=<user>&channel=<user>clientHHmmss&token=<token>
        public static async Task WsConnectAsync(string userName, string token, string? channel = null)
        {
            channel ??= $"{userName}client{DateTime.Now:HHmmss}";

            // clean up any previous connection
            if (_ws is not null)
            {
                try { await WsDisconnectAsync("Reconnecting"); } catch { /* ignore */ }
            }

            var uri = new Uri(
                $"wss://opticat2.net:46429/?" +
                $"userid={Uri.EscapeDataString(userName)}&" +
                $"channel={Uri.EscapeDataString(channel)}&" +
                $"token={Uri.EscapeDataString(token)}");

            _wsCts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            System.Diagnostics.Debug.WriteLine($"[WS] Connecting {uri} …");
            await _ws.ConnectAsync(uri, _wsCts.Token);
            System.Diagnostics.Debug.WriteLine("[WS] Connected");

            _ = Task.Run(ReceiveLoopAsync);  // start simple background receiver
        }

        public static async Task<(List<string> available, List<string> unavailable, string? rawResponse)> CheckPriceAvailabilityAsync(
                AppCredentialsOptions creds,
                string channel,
                IEnumerable<LineItem> items,
                string zipCode = "49311"  // default—you can wire to config later
            )
        {
            // Build the WebSocket payload (PriceCheck)
            var messageObject = new
            {
                channel,
                data = new
                {
                    Locationname = creds.DefaultLocation,
                    RequestType = "PriceCheck",
                    PartBrand = (items ?? Enumerable.Empty<LineItem>())
                        .Where(i => !string.IsNullOrWhiteSpace(i.PartNumber))
                        .Select(i => new
                        {
                            Brand = "HBWN",
                            Part = i.PartNumber,
                            MFG = "LF"
                        })
                        .ToArray(),
                    ZipCode = zipCode
                }
            };

            string messageJson = JsonSerializer.Serialize(messageObject, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = true
            });

            // Send over WS
            Debug.WriteLine($"[P&A] WS PriceCheck payload:\n{messageJson}");
            await WsSendAsync(messageJson);

            // Give the server a moment to respond and let ReceiveLoop stash it.
            await Task.Delay(1500);

            var raw = GetLastWsResponse();
            var available = new List<string>();
            var unavailable = new List<string>();

            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataArray.EnumerateArray())
                        {
                            // responseStatus could be "success" / "Success" / etc.
                            // Prefer inspecting responseDetails.
                            if (item.TryGetProperty("responseDetails", out var details) && details.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var detail in details.EnumerateArray())
                                {
                                    var part = detail.TryGetProperty("Part", out var partEl) ? (partEl.GetString() ?? "") : "";

                                    // Heuristic: if sellerdetails exists and has at least one entry → available
                                    bool hasSeller =
                                        detail.TryGetProperty("sellerdetails", out var sellers) &&
                                        sellers.ValueKind == JsonValueKind.Array &&
                                        sellers.GetArrayLength() > 0;

                                    bool hasLocationWithQty =
                                        detail.TryGetProperty("location", out var locations) &&
                                        locations.ValueKind == JsonValueKind.Array &&
                                        locations.EnumerateArray().Any(loc =>
                                            loc.TryGetProperty("qty", out var qtyEl) &&
                                            int.TryParse(qtyEl.GetString(), out var qtyVal) &&
                                            qtyVal > 0);

                                    bool hasValidPriceFields =
                                        detail.TryGetProperty("price_Qty", out var priceQtyProp) &&
                                        !string.IsNullOrWhiteSpace(priceQtyProp.GetString()) &&
                                        detail.TryGetProperty("Price", out var priceProp) &&
                                        !string.IsNullOrWhiteSpace(priceProp.GetString());

                                    if (!string.IsNullOrWhiteSpace(part))
                                    {
                                        if (hasSeller && hasLocationWithQty && hasValidPriceFields)
                                            available.Add(part);
                                        else
                                            unavailable.Add(part);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // If the schema differs, we’ll keep raw and empty lists.
                }
            }

            // NEW: Write unavailable parts to a log file
            if (unavailable.Count > 0)
            {
                try
                {
                    string failedLogFolder = creds.FailedPdfFolder ?? "C:\\FailedPDF";
                    Directory.CreateDirectory(failedLogFolder);

                    string logFileName = $"PriceCheck_Failed_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                    string logFilePath = Path.Combine(failedLogFolder, logFileName);

                    string logText =
                        "[PRICE & AVAILABILITY CHECK]\n" +
                        $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Location: {creds.DefaultLocation}\n" +
                        $"User: {creds.Username}\n" +
                        "----------------------------------------------\n" +
                        "Unavailable Parts:\n" +
                        string.Join(Environment.NewLine, unavailable) +
                        "\n----------------------------------------------\n";

                    await File.WriteAllTextAsync(logFilePath, logText);
                    Debug.WriteLine($"[P&A] Saved unavailable parts log: {logFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[P&A] Failed to write unavailable parts log: {ex.Message}");
                }
            }

            return (available, unavailable, raw);
        }

        public static async Task WsSendAsync(string text)
        {
            if (_ws is null || _ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket not connected.");
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _wsCts?.Token ?? CancellationToken.None);
        }

        // Optional: cleanly close
        public static async Task WsDisconnectAsync(string reason = "Client closing")
        {
            if (_ws is { State: WebSocketState.Open })
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, _wsCts?.Token ?? CancellationToken.None);

            _wsCts?.Cancel();
            _ws?.Dispose();
            _ws = null;
            _wsCts?.Dispose();
            _wsCts = null;

            System.Diagnostics.Debug.WriteLine("[WS] Disconnected");
        }

        // Minimal receiver that just logs text frames
        private static async Task ReceiveLoopAsync()
        {
            if (_ws is null) return;

            var buffer = new byte[8192];
            var sb = new StringBuilder();

            try
            {
                while (_ws.State == WebSocketState.Open && !(_wsCts?.IsCancellationRequested ?? false))
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, _wsCts?.Token ?? CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WS] Closed by server ({result.CloseStatus})");
                            await WsDisconnectAsync("Server closed");
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    string message = sb.ToString();
                    System.Diagnostics.Debug.WriteLine("[WS msg] " + message);

                    // Save the last meaningful message for the worker to inspect
                    if (!string.Equals(message, "completed", StringComparison.OrdinalIgnoreCase) &&
                        !message.Contains("\"data\":\"completed\"", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastWsResponse = message;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WS error] " + ex.Message);
            }
        }

    }
}