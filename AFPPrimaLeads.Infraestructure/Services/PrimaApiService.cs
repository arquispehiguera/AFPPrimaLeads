using AFPPrimaLeads.Core.Entities;
using AFPPrimaLeads.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;

namespace AFPPrimaLeads.Infraestructure.Services
{
    public class PrimaApiService : IPrimaApiService
    {
        private class PrimaTokenResponse
        {
            public string? access_token { get; set; }
            public int? expires_in { get; set; }
        }


        private readonly HttpClient _httpClient;
        private readonly ILogger<PrimaApiService> _logger;
        private readonly string _baseUrl;
        private readonly string _subscriptionKey;
        private readonly string _prospectosSubscriptionKey;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _scope;

        private const string OAuthPath = "/ux/oauth-manager-spa/token/v1/generation";
        private const string ProspectosPath = "/fc-gestionfonoprima/v1/prospectos";

        // Sin circuit breaker acá a propósito: a diferencia de InConcert, no tenemos
        // confirmado que Prima tenga rate limit — solo retry para blips transitorios.
        private readonly IAsyncPolicy<HttpResponseMessage> _requestPolicy;

        private readonly TimeSpan _defaultTokenLifetime;

        // Cache del token con doble-check lock, igual que InConcert — el token de Prima
        // dura ~1h, no tiene sentido re-loguear en cada tick de 1 min. La instancia es
        // Singleton (ver Program.cs) para que este cache persista entre ticks.
        private string? _cachedToken;
        private DateTime _tokenExpiresAtUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private static readonly TimeSpan TokenLockTimeout = TimeSpan.FromSeconds(10);

        public PrimaApiService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<PrimaApiService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _baseUrl = configuration["ApiPrima:BaseUrl"] ?? throw new ArgumentNullException("ApiPrima:BaseUrl");
            _subscriptionKey = configuration["ApiPrima:SubscriptionKey"] ?? throw new ArgumentNullException("ApiPrima:SubscriptionKey");
            _prospectosSubscriptionKey = configuration["ApiPrima:ProspectosSubscriptionKey"] ?? throw new ArgumentNullException("ApiPrima:ProspectosSubscriptionKey");
            _clientId = configuration["ApiPrima:ClientId"] ?? throw new ArgumentNullException("ApiPrima:ClientId");
            _clientSecret = configuration["ApiPrima:ClientSecret"] ?? throw new ArgumentNullException("ApiPrima:ClientSecret");
            _scope = configuration["ApiPrima:Scope"] ?? throw new ArgumentNullException("ApiPrima:Scope");
            // Margen de seguridad bajo el TTL real (~1h, sin confirmar) — se usa solo si
            // la respuesta OAuth no trae 'expires_in'; si lo trae, ese manda.
            _defaultTokenLifetime = TimeSpan.FromMinutes(configuration.GetValue<int>("ApiPrima:TokenLifetimeMinutes", 50));

            var retryCount = configuration.GetValue<int>("Resiliencia:Http:Prima:RetryCount", 2);
            _requestPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                .WaitAndRetryAsync(retryCount, retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)));
        }

        public async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiresAtUtc)
                return _cachedToken;

            if (!await _tokenLock.WaitAsync(TokenLockTimeout, ct))
                throw new TimeoutException("No se pudo obtener el lock del token de Prima a tiempo.");

            try
            {
                if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiresAtUtc)
                    return _cachedToken;

                try
                {
                    var body = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("client_id", _clientId),
                        new KeyValuePair<string, string>("client_secret", _clientSecret),
                        new KeyValuePair<string, string>("scope", _scope)
                    });

                    var response = await _requestPolicy.ExecuteAsync(async innerCt =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{OAuthPath}")
                        {
                            Content = body
                        };
                        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _subscriptionKey);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        return await _httpClient.SendAsync(request, innerCt);
                    }, ct);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(ct);
                    var result = JsonConvert.DeserializeObject<PrimaTokenResponse>(json)
                        ?? throw new InvalidOperationException("Respuesta vacía al obtener token Prima.");

                    string token = result.access_token
                        ?? throw new InvalidOperationException("La respuesta no contiene 'access_token'.");

                    // Preferir el expires_in real de la respuesta OAuth sobre el valor de
                    // config, con margen de seguridad del 10% para no pisar el borde exacto.
                    TimeSpan lifetime = _defaultTokenLifetime;
                    if (result.expires_in is > 0)
                        lifetime = TimeSpan.FromSeconds(result.expires_in.Value * 0.9);

                    _cachedToken = token;
                    _tokenExpiresAtUtc = DateTime.UtcNow + lifetime;
                    return token;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown en curso — no es una falla de Prima, no hace falta loguear
                    // como error ni contaminar el flujo con un log ruidoso por corrida.
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error al obtener token de la API Prima.");
                    throw;
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public async Task InvalidateTokenAsync()
        {
            // Mismo lock que GetTokenAsync — evita la race de invalidar sin sincronizar
            // que el doc marca como el bug real encontrado en producción.
            if (!await _tokenLock.WaitAsync(TokenLockTimeout))
                return;

            try
            {
                _cachedToken = null;
                _tokenExpiresAtUtc = DateTime.MinValue;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private HttpRequestMessage BuildProspectosRequest(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{ProspectosPath}");
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _prospectosSubscriptionKey);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        public async Task<List<Prospecto>> GetProspectosAsync(CancellationToken ct = default)
        {
            try
            {
                // Igual que InConcert: token vigente por su cuenta, y si el server
                // responde 401 (venció en medio de la corrida, o el TTL asumido está
                // mal), se genera uno nuevo YA MISMO y se reintenta una sola vez.
                var token = await GetTokenAsync(ct);
                var response = await _requestPolicy.ExecuteAsync(
                    innerCt => _httpClient.SendAsync(BuildProspectosRequest(token), innerCt), ct);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Prima devolvió 401 — generando token nuevo y reintentando una vez.");
                    await InvalidateTokenAsync();
                    token = await GetTokenAsync(ct);
                    response = await _requestPolicy.ExecuteAsync(
                        innerCt => _httpClient.SendAsync(BuildProspectosRequest(token), innerCt), ct);
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonConvert.DeserializeObject<ProspectosResponse>(json)
                    ?? throw new InvalidOperationException("Respuesta vacía al obtener prospectos.");
                foreach (var p in result.prospectos)
                {
                    p.jsonClient = JsonConvert.SerializeObject(p);
                }
                return result.prospectos;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso — se propaga tal cual para que UploadLeadsAsync lo
                // trate como corte normal, no como falla de Prima.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener prospectos de la API Prima.");
                throw;
            }
        }
    }
}
