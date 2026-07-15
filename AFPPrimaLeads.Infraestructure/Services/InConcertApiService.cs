using AFPPrimaLeads.Core.Entities;
using AFPPrimaLeads.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.CircuitBreaker;
using System.Text;

namespace AFPPrimaLeads.Infraestructure.Services
{
    public class InConcertApiService : IInConcertApiService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<InConcertApiService> _logger;
        private readonly string _baseUrl;
        private readonly string _user;
        private readonly string _password;
        private readonly TimeSpan _tokenLifetime;

        // Dos circuitos independientes, cada uno compartido entre TODAS las llamadas
        // concurrentes de su grupo (el estado tiene que acumularse entre workers, no
        // reiniciarse por llamada). _criticalPolicy cubre login + add_contacts — si el
        // login falla, add_contacts tampoco puede andar, tiene sentido que compartan
        // destino. _skillsPolicy está aislada a propósito: un blip en set_skills (falla
        // tolerada, no bloqueante) no debe poder tumbar la subida de contactos, que es
        // lo crítico. Antes compartían un solo circuito y un par de 500 en skills
        // bloqueaba todo el pipeline por 15s.
        private readonly IAsyncPolicy<HttpResponseMessage> _criticalPolicy;
        private readonly IAsyncPolicy<HttpResponseMessage> _skillsPolicy;

        // Cache del token con doble-check lock (Cambio 6 del doc): el token de InConcert
        // dura ~1h, no tiene sentido re-loguear en cada tick de 1 min. La instancia es
        // Singleton (ver Program.cs) justamente para que este cache persista entre ticks.
        private string? _cachedToken;
        private DateTime _tokenExpiresAtUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private static readonly TimeSpan TokenLockTimeout = TimeSpan.FromSeconds(10);

        public InConcertApiService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<InConcertApiService> logger)
        {
            _httpClient = httpClient;
            _logger     = logger;
            _baseUrl    = configuration["ApiKeyIC:BaseUrl"]   ?? throw new ArgumentNullException("ApiKeyIC:BaseUrl");
            _user       = configuration["ApiKeyIC:User"]      ?? throw new ArgumentNullException("ApiKeyIC:User");
            _password   = configuration["ApiKeyIC:Password"]  ?? throw new ArgumentNullException("ApiKeyIC:Password");
            var retryCount = configuration.GetValue<int>("Resiliencia:Http:InConcert:RetryCount", 3);
            // Margen de seguridad bajo el TTL real (~1h, sin confirmar con documentación
            // de InConcert) — si el supuesto está mal, se ajusta acá sin tocar código.
            _tokenLifetime = TimeSpan.FromMinutes(configuration.GetValue<int>("ApiKeyIC:TokenLifetimeMinutes", 50));

            // Subido de 2 a un default de 5: el circuit breaker cuenta CADA intento
            // individual (incluidos los reintentos internos de una misma llamada, por
            // cómo están encadenadas retry+circuitBreaker), así que con 2 alcanzaba con
            // un par de 500 sueltos y esporádicos para tumbar todo el pipeline 15s por
            // vez, repetidas veces. Con 5 workers concurrentes, una caída real sigue
            // detectándose casi al instante igual — la diferencia es que ya no reacciona
            // a ruido puntual.
            var circuitBreakerThreshold = configuration.GetValue<int>("InConcert:CircuitBreakerThreshold", 5);

            _criticalPolicy = BuildResiliencePolicy(retryCount, circuitBreakerThreshold);
            _skillsPolicy = BuildResiliencePolicy(retryCount, circuitBreakerThreshold);
        }

        private static IAsyncPolicy<HttpResponseMessage> BuildResiliencePolicy(int retryCount, int circuitBreakerThreshold)
        {
            var retry = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(IsTransientFailure)
                .WaitAndRetryAsync(
                    retryCount,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)),
                    onRetry: (outcome, delay, retryAttempt, context) => context["attempts"] = retryAttempt);

            // Antes solo trippeaba con 429 — ante una caída sostenida de InConcert (5xx),
            // los workers seguían machacando la API caída en paralelo, cada uno con su
            // propio ciclo completo de reintentos. Ahora comparte el mismo criterio de
            // falla transitoria que el retry, así el circuito corta ANTES de que cada
            // worker agote sus reintentos contra un backend que ya sabemos que está mal.
            var circuitBreaker = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(IsTransientFailure)
                .CircuitBreakerAsync(handledEventsAllowedBeforeBreaking: circuitBreakerThreshold, durationOfBreak: TimeSpan.FromSeconds(15));

            return Policy.WrapAsync(retry, circuitBreaker);
        }

        private static bool IsTransientFailure(HttpResponseMessage r) =>
            (int)r.StatusCode >= 500
            || r.StatusCode == System.Net.HttpStatusCode.RequestTimeout
            || r.StatusCode == System.Net.HttpStatusCode.TooManyRequests;

        public async Task<string> LoginAsync(CancellationToken ct = default)
        {
            if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiresAtUtc)
                return _cachedToken;

            if (!await _tokenLock.WaitAsync(TokenLockTimeout, ct))
                throw new TimeoutException("No se pudo obtener el lock del token de InConcert a tiempo.");

            try
            {
                // Doble check: otro tick pudo haber refrescado el token mientras
                // esperábamos el lock.
                if (_cachedToken is not null && DateTime.UtcNow < _tokenExpiresAtUtc)
                    return _cachedToken;

                // Sin retry acá, un solo blip transitorio de InConcert mataba TODA la
                // vuelta del Worker — incluyendo los reintentos pendientes, que no
                // dependen de esto.
                var context = new Context();
                try
                {
                    var response = await _criticalPolicy.ExecuteAsync(async (_, innerCt) =>
                    {
                        var payload = JsonConvert.SerializeObject(new { user = _user, password = _password });
                        var content = new StringContent(payload, Encoding.UTF8, "application/json");
                        return await _httpClient.PostAsync($"{_baseUrl}/login/", content, innerCt);
                    }, context, ct);

                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var obj  = JObject.Parse(body);
                    var token = obj["token"]?.Value<string>()
                        ?? throw new InvalidOperationException($"La respuesta de login no contiene 'token'. Respuesta: {body}");

                    _cachedToken = token;
                    _tokenExpiresAtUtc = DateTime.UtcNow + _tokenLifetime;
                    return token;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown en curso — se propaga tal cual (sin envolver en
                    // InvalidOperationException) para que quien llama pueda distinguirlo
                    // de una falla real de login y no lo trate ni loguee como error.
                    throw;
                }
                catch (Exception ex)
                {
                    var attempts = context.TryGetValue("attempts", out var a) ? a : 0;
                    _logger.LogError(ex, "Error al realizar login en InConcert tras {Attempts} reintento(s).", attempts);
                    throw new InvalidOperationException("Error al realizar login en InConcert.", ex);
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        public async Task InvalidateTokenAsync()
        {
            // Toma el MISMO lock que LoginAsync — el doc marca esto como el bug real
            // encontrado en producción: invalidar sin tomar el lock que sí toma la
            // lectura es una race de datos real, no teórica.
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

        /// <summary>
        /// Cada llamada obtiene el token vigente por su cuenta (cacheado, sin re-login
        /// de más) y lo usa para armar el request. Si el server responde 401 — el token
        /// venció en medio de la corrida, o el supuesto de TTL de config está mal — este
        /// worker en particular invalida el cache, genera uno nuevo YA MISMO y reintenta
        /// una sola vez con el token fresco. No hace falta esperar al próximo tick del
        /// Worker ni depender de que otro worker lo haga.
        /// </summary>
        private async Task<HttpResponseMessage> SendAuthenticatedAsync(
            Func<string, HttpRequestMessage> buildRequest, Context context, IAsyncPolicy<HttpResponseMessage> policy, CancellationToken ct)
        {
            var token = await LoginAsync(ct);
            var response = await policy.ExecuteAsync(
                (_, innerCt) => _httpClient.SendAsync(buildRequest(token), innerCt), context, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("InConcert devolvió 401 — generando token nuevo y reintentando una vez.");
                await InvalidateTokenAsync();
                token = await LoginAsync(ct);
                response = await policy.ExecuteAsync(
                    (_, innerCt) => _httpClient.SendAsync(buildRequest(token), innerCt), context, ct);
            }

            return response;
        }

        public async Task<bool> SetSkillsAsync(SetSkillsRequest request, CancellationToken ct = default)
        {
            var settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            var json     = JsonConvert.SerializeObject(request, settings);
            var context  = new Context();

            try
            {
                var response = await SendAuthenticatedAsync(token =>
                {
                    var msg = new HttpRequestMessage(HttpMethod.Post,
                        $"{_baseUrl}/outbound_engine/contacts/set_skills/")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    return msg;
                }, context, _skillsPolicy, ct);

                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso — no es una falla de skills, se propaga para que el
                // caller lo distinga de un fallo real (no debe loguearse como error).
                throw;
            }
            catch (Exception ex)
            {
                var attempts = context.TryGetValue("attempts", out var a) ? a : 0;
                _logger.LogError(ex,
                    "Error al asignar skills al contacto {ContactId} en InConcert tras {Attempts} reintento(s).",
                    request.contactId, attempts);
                return false;
            }
        }

        /// <returns>actionId devuelto por IC, o null si falló tras los reintentos.</returns>
        public async Task<string?> AddContactAsync(OutboundRequest request, CancellationToken ct = default)
        {
            var json    = JsonConvert.SerializeObject(request);
            var context = new Context();

            try
            {
                var response = await SendAuthenticatedAsync(token =>
                {
                    var msg = new HttpRequestMessage(HttpMethod.Post,
                        $"{_baseUrl}/outbound_engine/batches/add_contacts/")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    return msg;
                }, context, _criticalPolicy, ct);

                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(ct);
                var obj  = JObject.Parse(body);

                return obj["actionId"]?.Value<string>()
                    ?? throw new InvalidOperationException($"La respuesta de IC no contiene 'actionId'. Respuesta: {body}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso — se propaga tal cual. Si esto pasó mientras se
                // esperaba la respuesta de IC, existe la posibilidad de que el contacto
                // ya se haya creado del lado de IC sin que lleguemos a leer el actionId;
                // el reintento en el próximo arranque puede duplicar en ese caso puntual
                // (riesgo preexistente a un kill duro del proceso, no nuevo).
                throw;
            }
            catch (Exception ex)
            {
                var attempts = context.TryGetValue("attempts", out var a) ? a : 0;
                _logger.LogError(ex,
                    "Error al agregar contacto en InConcert tras {Attempts} reintento(s).",
                    attempts);
                return null;
            }
        }
    }
}
