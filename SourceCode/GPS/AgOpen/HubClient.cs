using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgOpenGPS.AgOpen
{
    /// <summary>What came back from one request to the hub.</summary>
    public class HubResponse
    {
        /// <summary>The hub answered and said yes (HTTP 2xx).</summary>
        public bool Ok;

        /// <summary>The hub answered at all (false: not reachable, or no answer in time).</summary>
        public bool Reached;

        /// <summary>HTTP status, 0 if the hub wasn't reached.</summary>
        public int Status;

        public string Body = "";

        /// <summary>What went wrong, in plain words ("" when Ok).</summary>
        public string Error = "";

        public JToken Json()
        {
            try
            {
                return string.IsNullOrEmpty(Body) ? null : JToken.Parse(Body);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Talks to the AgOpen hub's tablet API (/api/v1). Every call returns a
    /// HubResponse instead of throwing, so callers can simply check Ok.
    /// </summary>
    public static class HubClient
    {
        public const int DefaultPort = 8642;
        private const string ApiPrefix = "/api/v1";

        // One shared HttpClient for the whole program, as Microsoft recommends.
        // Each request sets its own time limit.
        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }

        /// <summary>
        /// Turn what someone typed ("192.168.1.20", "192.168.1.20:8642",
        /// "http://hub.local:8642/") into a clean base address, or "" if it isn't one.
        /// With no "http://" and no port, the hub's usual port 8642 is used.
        /// </summary>
        public static string NormalizeUrl(string text)
        {
            text = (text ?? "").Trim().TrimEnd('/');
            if (text.Length == 0) return "";

            bool hasScheme = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!hasScheme)
            {
                string authority = text.Split('/')[0];
                bool hasPort = authority.LastIndexOf(':') > authority.LastIndexOf(']');
                text = "http://" + (hasPort ? text : authority + ":" + DefaultPort + text.Substring(authority.Length));
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host)) return "";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        public static Task<HubResponse> HealthAsync(string baseUrl)
        {
            return SendAsync(baseUrl, null, HttpMethod.Get, "/health", null, 10);
        }

        public static Task<HubResponse> PairAsync(string baseUrl, string code)
        {
            var body = new JObject();
            body["code"] = code;
            return SendAsync(baseUrl, null, HttpMethod.Post, "/pair", body.ToString(Newtonsoft.Json.Formatting.None), 15);
        }

        public static Task<HubResponse> MeAsync(string baseUrl, string key)
        {
            return SendAsync(baseUrl, key, HttpMethod.Get, "/me", null, 10);
        }

        /// <summary>The same as SendAsync, for code that runs on a background thread.</summary>
        public static HubResponse Send(string baseUrl, string key, HttpMethod method, string path, string jsonBody, int timeoutSeconds)
        {
            return SendAsync(baseUrl, key, method, path, jsonBody, timeoutSeconds).GetAwaiter().GetResult();
        }

        /// <summary>
        /// One request to the hub. path is the part after /api/v1, e.g. "/fields".
        /// key is the tablet key (null for /health and /pair).
        /// </summary>
        public static async Task<HubResponse> SendAsync(string baseUrl, string key, HttpMethod method, string path, string jsonBody, int timeoutSeconds)
        {
            var result = new HubResponse();
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Error = "No hub address set.";
                return result;
            }

            try
            {
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var request = new HttpRequestMessage(method, baseUrl + ApiPrefix + path))
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (!string.IsNullOrEmpty(key))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    }
                    if (jsonBody != null)
                    {
                        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    }

                    using (HttpResponseMessage response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false))
                    {
                        result.Reached = true;
                        result.Status = (int)response.StatusCode;
                        result.Body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        result.Ok = response.IsSuccessStatusCode;
                        if (!result.Ok)
                        {
                            result.Error = ErrorFrom(result);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "The hub didn't answer in time.";
            }
            catch (HttpRequestException ex)
            {
                string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                result.Error = "Can't reach the hub (" + detail + ").";
            }
            catch (Exception ex)
            {
                result.Error = "Problem talking to the hub: " + ex.Message;
            }
            return result;
        }

        private static string ErrorFrom(HubResponse response)
        {
            // The hub explains its refusals as {"error": "..."}.
            JObject json = response.Json() as JObject;
            JToken message = json != null ? json["error"] : null;
            string text = message != null ? message.ToString() : "";
            if (text.Length == 0)
            {
                text = response.Body.Length > 200 ? response.Body.Substring(0, 200) : response.Body;
            }
            return "Hub said " + response.Status + ": " + text;
        }
    }
}
