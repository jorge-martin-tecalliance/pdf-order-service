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

        public static async Task<(List<string> available, List<string> unavailable, string? rawResponse)> CheckPriceAvailabilityAsync (AppCredentialsOptions creds,string channel, IEnumerable<LineItem> items, string zipCode = "49311")
        {
            var messageObject = new
            {
                channel,
                data = new
                {
                    Locationname = creds.DefaultLocation,
                    RequestType = "PriceCheck",
                    PartBrand = (items ?? Enumerable.Empty<LineItem>())
                        .Where(i => !string.IsNullOrWhiteSpace(i.PartNumber))
                        .Select(i => new { Brand = "HBWN", Part = i.PartNumber, MFG = "LF" })
                        .ToArray(),
                    ZipCode = zipCode
                }
            };

            string messageJson = JsonSerializer.Serialize(messageObject, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = true
            });

            Debug.WriteLine($"[P&A] WS PriceCheck payload:\n{messageJson}");

            // 🔴 IMPORTANT: clear last response before sending
            _lastWsResponse = null;

            await Task.Delay(200);

            await WsSendAsync(messageJson);

            // 🔴 Wait up to ~5 seconds for a new response
            var sw = Stopwatch.StartNew();
            while (_lastWsResponse == null && sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(200);
            }

            var raw = _lastWsResponse;
            var available = new List<string>();
            var unavailable = new List<string>();

            if (string.IsNullOrEmpty(raw))
            {
                Debug.WriteLine("[P&A] No WS response received for PriceCheck.");
                return (available, unavailable, raw);
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("responseDetails", out var details) &&
                            details.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var detail in details.EnumerateArray())
                            {
                                var part = detail.TryGetProperty("Part", out var partEl)
                                    ? (partEl.GetString() ?? "")
                                    : "";

                                if (string.IsNullOrWhiteSpace(part))
                                    continue;

                                bool hasValidPrice =
                                    detail.TryGetProperty("Price", out var priceProp) &&
                                    !string.IsNullOrWhiteSpace(priceProp.GetString());

                                var location = detail.TryGetProperty("location", out var locArray) &&
                                               locArray.ValueKind == JsonValueKind.Array
                                    ? locArray.EnumerateArray().FirstOrDefault(loc =>
                                        loc.TryGetProperty("locationid", out var locIdEl) &&
                                        locIdEl.GetString() == creds.LocationId)
                                    : default;

                                bool matchedLocation = location.ValueKind == JsonValueKind.Object;

                                bool qtyGreaterThanZero = false;
                                bool backorderAllowed = false;

                                if (matchedLocation)
                                {
                                    if (location.TryGetProperty("qty", out var qtyEl) &&
                                        int.TryParse(qtyEl.GetString(), out var qtyVal))
                                    {
                                        qtyGreaterThanZero = qtyVal > 0;
                                    }

                                    if (location.TryGetProperty("backorder", out var backorderEl))
                                    {
                                        backorderAllowed = backorderEl.GetString() == "1";
                                    }
                                }

                                bool isAvailable =
                                    hasValidPrice &&
                                    matchedLocation &&
                                    (qtyGreaterThanZero || backorderAllowed);

                                if (isAvailable)
                                {
                                    Debug.WriteLine($"[P&A] Part {part} available at location {creds.LocationId}.");
                                    available.Add(part);
                                }
                                else
                                {
                                    Debug.WriteLine($"[P&A] Part {part} unavailable.");
                                    unavailable.Add(part);
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                Debug.WriteLine("[P&A] JSON parse error: unexpected schema");
            }

            // Write unavailable parts to a log file
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
                Debug.WriteLine("[WS error] " + ex.Message);
            }
        }

    }
}