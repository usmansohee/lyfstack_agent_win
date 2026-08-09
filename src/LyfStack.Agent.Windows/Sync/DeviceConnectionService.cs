using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyfStack.Agent.Windows.Configuration;

namespace LyfStack.Agent.Windows.Sync;

/// <summary>
/// Outbound WebSocket to LyfStack so the website can command this PC
/// without the PC needing a public URL.
/// </summary>
public sealed class DeviceConnectionService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private AgentSettings _settings;
    private ClientWebSocket? _socket;

    public DeviceConnectionService(AgentSettings settings)
    {
        _settings = settings;
    }

    public string Status { get; private set; } = "Off";

    public event Action<string>? StatusChanged;
    public event Action<DeviceCommandMessage>? CommandReceived;

    public void ApplySettings(AgentSettings settings)
    {
        _settings = settings;
        Restart();
    }

    public void Start() => Restart();

    public void Restart()
    {
        StopLoop();
        if (!_settings.DeviceConnectionEnabled
            || string.IsNullOrWhiteSpace(_settings.DeviceConnectionUrl))
        {
            SetStatus("Off");
            return;
        }

        _loopCts = new CancellationTokenSource();
        CancellationToken token = _loopCts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(token), token);
    }

    public async ValueTask DisposeAsync()
    {
        StopLoop();
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
                }
            }
            catch
            {
            }

            socket.Dispose();
        }
    }

    private void StopLoop()
    {
        try
        {
            _loopCts?.Cancel();
        }
        catch
        {
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetStatus("Connecting");
                await ConnectAndListenAsync(cancellationToken);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                int delaySec = Math.Min(60, (int)Math.Pow(2, Math.Min(attempt, 5)));
                SetStatus($"Reconnecting in {delaySec}s ({Trim(ex.Message, 40)})");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            SetStatus("Offline");
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken cancellationToken)
    {
        Uri uri = BuildUri(_settings);
        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_settings.DeviceConnectionToken))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_settings.DeviceConnectionToken}");
        }

        lock (_gate)
        {
            _socket?.Dispose();
            _socket = socket;
        }

        await socket.ConnectAsync(uri, cancellationToken);
        SetStatus("Online");
        await SendHelloAsync(socket, cancellationToken);

        var buffer = new byte[8 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);
                    throw new InvalidOperationException("Server closed connection");
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            string json = Encoding.UTF8.GetString(ms.ToArray());
            await HandleMessageAsync(socket, json, cancellationToken);
        }

        throw new InvalidOperationException("Socket left Open state");
    }

    private async Task HandleMessageAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken)
    {
        DeviceCommandMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<DeviceCommandMessage>(json, JsonOptions);
        }
        catch
        {
            return;
        }

        if (msg is null || string.IsNullOrWhiteSpace(msg.Type))
        {
            return;
        }

        string type = msg.Type.Trim().ToUpperInvariant();
        if (type == "PING")
        {
            await SendJsonAsync(socket, new { type = "PONG", at = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        // Normalize type for handlers
        msg = msg with { Type = type };
        CommandReceived?.Invoke(msg);
    }

    private async Task SendHelloAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        DeviceProfile profile = DeviceProfileStore.LoadOrCreate();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        await SendJsonAsync(socket, new
        {
            type = "HELLO",
            deviceId = profile.DeviceId.ToString("D"),
            device = Environment.MachineName,
            platform = "windows",
            agentVersion = version,
            at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task SendAsync(object payload, CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
        }

        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        await SendJsonAsync(socket, payload, cancellationToken);
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private Uri BuildUri(AgentSettings settings)
    {
        string url = settings.DeviceConnectionUrl.Trim();
        DeviceProfile profile = DeviceProfileStore.LoadOrCreate();
        var builder = new UriBuilder(url);
        string query = builder.Query.TrimStart('?');
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(query))
        {
            parts.Add(query);
        }

        parts.Add("deviceId=" + Uri.EscapeDataString(profile.DeviceId.ToString("D")));
        parts.Add("platform=windows");
        if (!string.IsNullOrWhiteSpace(settings.DeviceConnectionToken))
        {
            parts.Add("token=" + Uri.EscapeDataString(settings.DeviceConnectionToken));
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

public sealed record DeviceCommandMessage(
    string Type,
    string? Range = null,
    string? From = null,
    string? To = null,
    string? RequestId = null);
