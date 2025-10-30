using pdf_extractor.Configuration;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;


namespace pdf_extractor.Services
{
    public static class MsApiCalls
    {
        private const string BaseUrl = "https://opticat2.net/OpticatApi/api/v1/";
        private static readonly HttpClient Http = new HttpClient { BaseAddress = new Uri(BaseUrl) };

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

                    System.Diagnostics.Debug.WriteLine("[WS msg] " + sb.ToString());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WS error] " + ex.Message);
            }
        }
    }
}