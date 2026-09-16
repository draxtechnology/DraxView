using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace DraxView.Services;

public sealed class MqttOptions
{
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "drax";
}

// Subscriber for the service's MQTT mirror (MqttTransfer in DraxTechService)
// and the sender for controls. Mirrors the service's own shape: one client,
// a connect/reconnect loop, announce-once logging.
public sealed class MqttService : BackgroundService
{
    private readonly MqttOptions _opt;
    private readonly EventStore _store;
    private readonly ILogger<MqttService> _log;
    private readonly IMqttClient _client;
    private readonly HashSet<string> _panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public MqttService(IOptions<MqttOptions> opt, EventStore store, ILogger<MqttService> log)
    {
        _opt = opt.Value;
        _store = store;
        _log = log;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessage;
    }

    public bool IsConnected => _client.IsConnected;
    public string Broker => $"{_opt.Broker}:{_opt.Port}";
    public string TopicFilter => $"{_opt.TopicPrefix}/#";
    public string? LastError { get; private set; }
    public DateTime? LastMessageLocal { get; private set; }
    public long LogLines { get; private set; }

    public event Action? StateChanged;

    public IReadOnlyList<string> Panels
    {
        get { lock (_lock) return _panels.OrderBy(p => p).ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_opt.Broker, _opt.Port)
            .WithClientId($"DraxView-{Environment.ProcessId}")
            .WithCleanSession()
            .Build();

        bool outageAnnounced = false;
        while (!ct.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await _client.ConnectAsync(options, ct);
                    await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(TopicFilter).WithAtMostOnceQoS())
                        .Build(), ct);
                    LastError = null;
                    outageAnnounced = false;
                    _log.LogInformation("MQTT connected to {Broker}, subscribed to {Filter}", Broker, TopicFilter);
                    StateChanged?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (!outageAnnounced)
                    {
                        _log.LogWarning("MQTT connect to {Broker} failed: {Error} (retrying)", Broker, ex.Message);
                        outageAnnounced = true;
                        StateChanged?.Invoke();
                    }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            string topic = e.ApplicationMessage.Topic;
            string payload = e.ApplicationMessage.ConvertPayloadToString();
            LastMessageLocal = DateTime.Now;

            if (topic.EndsWith("/event", StringComparison.OrdinalIgnoreCase))
            {
                RememberPanel(topic);
                _store.Add(payload);
            }
            else if (topic.EndsWith("/log", StringComparison.OrdinalIgnoreCase))
            {
                RememberPanel(topic);
                LogLines++;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("MQTT message handling failed: {Error}", ex.Message);
        }
        return Task.CompletedTask;
    }

    private void RememberPanel(string topic)
    {
        // drax/<panel>/event -> <panel>
        var parts = topic.Split('/');
        if (parts.Length < 3) return;
        bool added;
        lock (_lock) added = _panels.Add(parts[^2]);
        if (added) StateChanged?.Invoke();
    }

    // Controls ride the same pipe-command strings the WinForms client sends
    // (handlepiperesponse), e.g. "SILENCE|0,0,0,0". The service subscribes to
    // drax/<panel>/cmd and hands the payload straight to that handler.
    public async Task<bool> SendCommandAsync(string panel, string command)
    {
        if (!_client.IsConnected || string.IsNullOrWhiteSpace(panel)) return false;
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"{_opt.TopicPrefix}/{panel.Trim().ToLowerInvariant()}/cmd")
                .WithPayload(command)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await _client.PublishAsync(msg);
            _log.LogInformation("Sent {Command} to {Panel}", command, panel);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning("Command {Command} to {Panel} failed: {Error}", command, panel, ex.Message);
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
        await base.StopAsync(ct);
    }
}
