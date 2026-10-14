using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    // A failed UniFi call, carrying UniFi's own reason (meta.msg, e.g. api.err.PortForwardOverlap)
    // instead of a bare "400 Bad Request".
    internal class UnifiApiException : Exception
    {
        public int StatusCode { get; }
        public UnifiApiException(int status, string message) : base(message) { StatusCode = status; }
    }

    // The port-forward operations the reconciler needs. Lets tests swap in a fake controller.
    internal interface IUnifiClient : IDisposable
    {
        Task<List<PortForward>> GetPortForwardsAsync();
        Task CreatePortForwardAsync(PortForward rule);
        Task UpdatePortForwardAsync(PortForward rule);
        Task DeletePortForwardAsync(string id);
    }

    // Talks to the UniFi Network API with an API key.
    internal class UnifiClient : IUnifiClient
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _site;

        private static readonly JsonSerializerOptions J = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // SHA-256 fingerprint of the certificate the controller presented on the last call.
        // Shown by Test Connection so it can be pasted into the pin setting.
        public string LastSeenFingerprint { get; private set; }

        // True when the last certificate was only accepted because nothing is pinned and it
        // failed normal validation (self-signed). Used to nudge the user towards pinning.
        public bool AcceptedUntrustedCert { get; private set; }

        public UnifiClient(string host, int port, string apiKey, string site, string pinnedFingerprint = null)
        {
            _baseUrl = $"https://{host}:{port}";
            _site = string.IsNullOrWhiteSpace(site) ? "default" : site;
            var pin = NormalizeFingerprint(pinnedFingerprint);
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, __, errors) =>
                {
                    if (cert == null) return false;
                    var fp = Fingerprint(cert);
                    LastSeenFingerprint = fp;
                    // A pinned fingerprint is authoritative: it must match, whatever the chain says.
                    if (pin != null) return string.Equals(fp, pin, StringComparison.OrdinalIgnoreCase);
                    if (errors == SslPolicyErrors.None) return true;
                    // UniFi OS uses a self-signed cert out of the box. Without a pin we accept it
                    // (as earlier versions did) but flag it so the caller can warn.
                    AcceptedUntrustedCert = true;
                    return true;
                }
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey ?? "");
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        internal static string Fingerprint(X509Certificate2 cert)
            => Convert.ToHexString(SHA256.HashData(cert.RawData));

        // Accepts "AA:BB:..", "aa bb ..", plain hex, with or without a leading "SHA256:" or
        // "SHA-256 Fingerprint" label. Returns null when empty and Invalid when the value isn't a
        // SHA-256 fingerprint (e.g. a SHA-1 one copied from a browser), so a bad paste can never
        // be confused with "no pin".
        internal const string Invalid = "INVALID";

        internal static string NormalizeFingerprint(string fp)
        {
            if (string.IsNullOrWhiteSpace(fp)) return null;
            var v = fp.Trim();
            var label = System.Text.RegularExpressions.Regex.Match(v, @"^\s*sha-?256(\s*fingerprint)?\s*[:=]?\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (label.Success) v = v.Substring(label.Length);
            var hex = new StringBuilder();
            foreach (var ch in v)
            {
                if (Uri.IsHexDigit(ch)) hex.Append(char.ToUpperInvariant(ch));
                else if (ch != ':' && ch != ' ' && ch != '-') return Invalid;
            }
            return hex.Length == 64 ? hex.ToString() : Invalid;
        }

        // Colon-separated form for display.
        internal static string Pretty(string hex)
            => hex == null || hex == Invalid ? hex : string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

        private static async Task<string> ReadOrThrow(HttpResponseMessage resp)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return body;
            string reason = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("msg", out var msg))
                    reason = msg.GetString();
                else if (doc.RootElement.TryGetProperty("message", out var m))
                    reason = m.GetString();
            }
            catch { /* not JSON */ }
            var status = (int)resp.StatusCode;
            throw new UnifiApiException(status, string.IsNullOrEmpty(reason)
                ? $"UniFi returned {status} {resp.ReasonPhrase}"
                : $"UniFi returned {status}: {reason}");
        }

        private static List<T> Data<T>(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            return JsonSerializer.Deserialize<List<T>>(data.GetRawText(), J) ?? new List<T>();
        }

        // Doubles as a connection / API-key check.
        public async Task<List<UnifiSite>> ListSitesAsync()
            => Data<UnifiSite>(await ReadOrThrow(await _http.GetAsync($"{_baseUrl}/proxy/network/integration/v1/sites")));

        public async Task<List<PortForward>> GetPortForwardsAsync()
            => Data<PortForward>(await ReadOrThrow(await _http.GetAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward")));

        public async Task CreatePortForwardAsync(PortForward rule)
        {
            var payload = new StringContent(JsonSerializer.Serialize(rule, J), Encoding.UTF8, "application/json");
            await ReadOrThrow(await _http.PostAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward", payload));
        }

        public async Task UpdatePortForwardAsync(PortForward rule)
        {
            var payload = new StringContent(JsonSerializer.Serialize(rule, J), Encoding.UTF8, "application/json");
            await ReadOrThrow(await _http.PutAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward/{rule.Id}", payload));
        }

        public async Task DeletePortForwardAsync(string id)
            => await ReadOrThrow(await _http.DeleteAsync($"{_baseUrl}/proxy/network/api/s/{_site}/rest/portforward/{id}"));

        public void Dispose() => _http?.Dispose();
    }
}
