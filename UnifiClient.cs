using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace UnifiPortSync
{
    // Just the port-forward fields we actually touch.
    internal class PortForward
    {
        [JsonPropertyName("_id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("pfwd_interface")] public string Interface { get; set; } = "wan";
        [JsonPropertyName("src")] public string Src { get; set; } = "any";
        [JsonPropertyName("proto")] public string Proto { get; set; } = "tcp_udp";
        [JsonPropertyName("dst_port")] public string DstPort { get; set; }
        [JsonPropertyName("fwd")] public string Fwd { get; set; }
        [JsonPropertyName("fwd_port")] public string FwdPort { get; set; }
        [JsonPropertyName("destination_ip")] public string DestinationIp { get; set; } = "any";
    }

    internal class UnifiSite
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("internalReference")] public string InternalReference { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
    }

    // Talks to the UniFi Network API with an API key.
    internal class UnifiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _site;

        private static readonly JsonSerializerOptions J = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        public UnifiClient(string host, int port, string apiKey, string site)
        {
            _baseUrl = $"https://{host}:{port}";
            _site = string.IsNullOrWhiteSpace(site) ? "default" : site;
            var handler = new HttpClientHandler
            {
                // UniFi OS uses a self-signed cert out of the box.
                ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey ?? "");
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Doubles as a connection / API-key check.
        public async Task<List<UnifiSite>> ListSitesAsync()
        {
            var resp = await _http.GetAsync($"{_baseUrl}/proxy/network/integration/v1/sites");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            return JsonSerializer.Deserialize<List<UnifiSite>>(data.GetRawText(), J) ?? new List<UnifiSite>();
        }

        public async Task<List<PortForward>> GetPortForwardsAsync()
        {
            var resp = await _http.GetAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            return JsonSerializer.Deserialize<List<PortForward>>(data.GetRawText(), J) ?? new List<PortForward>();
        }

        public async Task CreatePortForwardAsync(PortForward rule)
        {
            var payload = new StringContent(JsonSerializer.Serialize(rule, J), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward", payload);
            resp.EnsureSuccessStatusCode();
        }

        public async Task UpdatePortForwardAsync(PortForward rule)
        {
            var payload = new StringContent(JsonSerializer.Serialize(rule, J), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward/{rule.Id}", payload);
            resp.EnsureSuccessStatusCode();
        }

        public async Task DeletePortForwardAsync(string id)
        {
            var resp = await _http.DeleteAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward/{id}");
            resp.EnsureSuccessStatusCode();
        }

        public void Dispose() => _http?.Dispose();
    }
}
