using Azure.Data.Tables;
using MatthiWare.FinancialModelingPrep.Abstractions.Http;
using MatthiWare.FinancialModelingPrep.Model;
using MatthiWare.FinancialModelingPrep.Model.Error;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MatthiWare.FinancialModelingPrep.Core.Http
{
    public class FinancialModelingPrepHttpClient
    {
        private readonly HttpClient client;
        private readonly FinancialModelingPrepOptions options;
        private readonly IRequestRateLimiter rateLimiter;
        private readonly ILogger<FinancialModelingPrepHttpClient> logger;
        private readonly JsonSerializerOptions jsonSerializerOptions;
        private const string EmptyArrayResponse = "[ ]";
        private const string EmptyArrayResponse2 = "[]";
        private const string ErrorMessageResponse = "Error Message";

        public FinancialModelingPrepHttpClient(HttpClient client, FinancialModelingPrepOptions options,
                                               IRequestRateLimiter rateLimiter,
                                               ILogger<FinancialModelingPrepHttpClient> logger)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
            };

            if (string.IsNullOrWhiteSpace(this.options.ApiKey))
            {
                throw new ArgumentException("'ApiKey' can not be null or empty");
            }
        }

        public async Task<ApiResponse<string>> GetStringAsync(string urlPattern, NameValueCollection pathParams, QueryStringBuilder queryString)
        {
            try
            {
                var (wasThrottled, totalDelay) = await rateLimiter.ThrottleAsync();

                var response = await CallApiAsync(urlPattern, pathParams, queryString);

                if (wasThrottled)
                {
                    logger.LogDebug("FMP API Call was throttled by {throttle} ms", totalDelay.TotalMilliseconds);
                }

                if (response.HasError)
                {
                    return ApiResponse.FromError<string>(response.Error);
                }

                if (response.Data.Contains(ErrorMessageResponse))
                {
                    var errorData = JsonSerializer.Deserialize<ErrorResponse>(response.Data);

                    return ApiResponse.FromError<string>(errorData.ErrorMessage);
                }

                if (response.Data.Equals(EmptyArrayResponse, StringComparison.OrdinalIgnoreCase) ||
                    response.Data.Equals(EmptyArrayResponse2, StringComparison.OrdinalIgnoreCase))
                {
                    return ApiResponse.FromError<string>("Invalid parameters");
                }

                return ApiResponse.FromSucces(response.Data);
            }
            finally
            {
                rateLimiter.ReleaseThrottle();
            }
        }

        public async Task<ApiResponse<T>> GetJsonAsync<T>(string urlPattern, NameValueCollection pathParams, QueryStringBuilder queryString)
            where T : class
        {
            try
            {
                var response = await GetStringAsync(urlPattern, pathParams, queryString);

                if (response.HasError)
                {
                    return ApiResponse.FromError<T>(response.Error);
                }

                var data = JsonSerializer.Deserialize<T>(response.Data, jsonSerializerOptions);

                return ApiResponse.FromSucces(data);
            }
            catch (JsonException ex)
            {
                return ApiResponse.FromError<T>(ex.ToString());
            }
        }

        private async Task<ApiResponse<string>> CallApiAsync(string urlPattern, NameValueCollection pathParams, QueryStringBuilder queryString)
        {
            PreProcessUrl(ref urlPattern, ref pathParams, ref queryString);

            queryString.Add("apikey", options.ApiKey);

            var requestUrl = $"{urlPattern}{queryString}";
            var timestampUtc = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            string content;

            try
            {
                response = await client.GetAsync(requestUrl);
                content = await response.Content.ReadAsStringAsync();
                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await LogApiCallAsync(timestampUtc, stopwatch.ElapsedMilliseconds, requestUrl, null, false, ex.Message, 0);
                throw;
            }

            await LogApiCallAsync(
                timestampUtc,
                stopwatch.ElapsedMilliseconds,
                requestUrl,
                (int)response.StatusCode,
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? string.Empty : content,
                content?.Length ?? 0);

            if (!response.IsSuccessStatusCode)
            {
                return ApiResponse.FromError<string>($"{response.StatusCode} - {content}");
            }

            return ApiResponse.FromSucces(content);
        }

        private async Task LogApiCallAsync(
            DateTime timestampUtc,
            long durationMs,
            string requestUrl,
            int? statusCode,
            bool success,
            string errorMessage,
            int responseLength)
        {
            if (!options.EnableApiCallLogging || string.IsNullOrWhiteSpace(options.ApiCallLogStorageConnectionString))
            {
                return;
            }

            try
            {
                var table = new TableClient(options.ApiCallLogStorageConnectionString, "apiLogs");
                await table.CreateIfNotExistsAsync();

                var endpoint = RedactApiKey(requestUrl);
                var operation = GetOperation(endpoint);
                var entity = new TableEntity(timestampUtc.ToString("yyyyMMdd"), BuildRowKey(timestampUtc, operation))
                {
                    ["Provider"] = "FMP",
                    ["Operation"] = operation,
                    ["Method"] = "GET",
                    ["Endpoint"] = endpoint,
                    ["Symbol"] = GetSymbol(endpoint),
                    ["StatusCode"] = statusCode ?? 0,
                    ["DurationMs"] = durationMs,
                    ["Success"] = success,
                    ["ErrorMessage"] = Truncate(errorMessage, 1024),
                    ["ResponseLength"] = responseLength,
                    ["TimestampUtc"] = timestampUtc
                };

                await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to write FMP API call log.");
            }
        }

        private static string BuildRowKey(DateTime timestampUtc, string operation)
        {
            var safeOperation = new string((operation ?? string.Empty)
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(safeOperation))
                safeOperation = "FMP";
            return $"{timestampUtc:HHmmssfff}-{safeOperation}-{Guid.NewGuid():N}";
        }

        private static string GetOperation(string endpoint)
        {
            var withoutQuery = endpoint.Split('?', 2)[0].Trim('/');
            if (string.IsNullOrWhiteSpace(withoutQuery))
                return "FMP";

            var segments = withoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0 ? "FMP" : segments[^1];
        }

        private static string GetSymbol(string endpoint)
        {
            var withoutQuery = endpoint.Split('?', 2)[0].Trim('/');
            var segments = withoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0 ? string.Empty : segments[^1].ToUpperInvariant();
        }

        private static string RedactApiKey(string requestUrl)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                requestUrl,
                "([?&]apikey=)[^&]+",
                "$1REDACTED",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static void PreProcessUrl(ref string url, ref NameValueCollection pathParams, ref QueryStringBuilder qsb)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentNullException(nameof(url));
            }

            if (pathParams == null)
            {
                throw new ArgumentNullException(nameof(pathParams));
            }

            qsb ??= new QueryStringBuilder();

            if (pathParams.Count == 0)
            {
                return;
            }

            foreach (string key in pathParams.Keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("Provided path parameter was null or empty");
                }
                else if (string.IsNullOrWhiteSpace(pathParams[key]))
                {
                    throw new ArgumentException($"Provided path parameter value for {key} was null or empty");
                }
                else if (url.IndexOf($"[{key}]") < 0)
                {
                    throw new ArgumentException($"Url pattern doesn't contain [{key}]");
                }

                url = url.Replace($"[{key}]", pathParams[key]);
            }
        }
    }
}
