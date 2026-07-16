using AFPPrimaLeads.Core.Entities;
using AFPPrimaLeads.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;

namespace AFPPrimaLeads.Infraestructure.Services
{
    public class LeadUploadService : ILeadUploadService
    {
        private readonly ILogger<LeadUploadService> _logger;
        private readonly IInConcertApiService _inConcertApiService;
        private readonly IPrimaApiService _primaApiService;
        private readonly IProspectoRepository _gssRepo;
        private readonly IHeartbeatMonitor _producerHeartbeat;
        private readonly IHeartbeatMonitor _consumersHeartbeat;
        private readonly string _baseBatchId;
        private readonly string _campaignId;
        private readonly bool _skillsEnabled;
        private readonly bool _skillsMix;
        private readonly List<SkillItem> _skillItems;
        private readonly int _workerCount;
        private readonly int _channelCapacity;
        private readonly int _maxRetryBatchSize;

        public LeadUploadService(
            ILogger<LeadUploadService> logger,
            IInConcertApiService inConcertApiService,
            IPrimaApiService primaApiService,
            IProspectoRepository gssRepo,
            [FromKeyedServices("Producer")] IHeartbeatMonitor producerHeartbeat,
            [FromKeyedServices("Consumers")] IHeartbeatMonitor consumersHeartbeat,
            IConfiguration configuration)
        {
            _logger = logger;
            _inConcertApiService = inConcertApiService;
            _primaApiService = primaApiService;
            _gssRepo = gssRepo;
            _producerHeartbeat = producerHeartbeat;
            _consumersHeartbeat = consumersHeartbeat;
            // yyyyMM se recalcula en cada corrida (UploadLeadsAsync), no acá — este
            // servicio ahora vive dentro de un host de larga duración, y calcularlo
            // una sola vez en el constructor lo dejaría congelado en el mes de arranque.
            _baseBatchId = configuration["ApiPrima:BatchId"] ?? throw new ArgumentNullException("ApiPrima:BatchId");
            _campaignId = configuration["ApiPrima:CampaignId"] ?? throw new ArgumentNullException("ApiPrima:CampaignId");
            _skillsEnabled = configuration.GetValue<bool>("SkillsIC:Enabled", false);
            _skillsMix = configuration.GetValue<bool>("SkillsIC:Mix", true);
            _skillItems = configuration.GetSection("SkillsIC:Skills").Get<List<SkillItem>>() ?? new();
            _workerCount = configuration.GetValue<int>("InConcert:MaxParallelUploads", 5);
            _channelCapacity = configuration.GetValue<int>("InConcert:ChannelCapacity", 200);
            _maxRetryBatchSize = configuration.GetValue<int>("InConcert:MaxRetryBatchSize", 200);
        }

        private sealed record UploadItem(int GssId, string ContactId, string BatchId, Prospecto Prospecto);

        public async Task UploadLeadsAsync(CancellationToken ct = default)
        {
            var batchId = _baseBatchId + DateTime.Now.ToString("yyyyMM");
            var outboundProcessId = _campaignId;

            var channel = Channel.CreateBounded<UploadItem>(new BoundedChannelOptions(_channelCapacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            // Ya no se pide/pasa un token acá: cada worker resuelve su propio token
            // vigente por llamada (cacheado adentro de InConcertApiService) y se
            // autorrecupera con uno nuevo si le rebota un 401 — no dependen de un
            // único token capturado al principio de la corrida.
            var workers = Enumerable.Range(0, _workerCount)
                .Select(_ => ConsumeAsync(channel.Reader, ct))
                .ToList();

            try
            {
                _producerHeartbeat.ReportAlive();

                // Reintentos pendientes de corridas anteriores primero — los consumers ya
                // arrancan a subirlos mientras todavía esperamos la respuesta de Prima.
                var pendientes = await _gssRepo.GetPendingRetryAsync(_maxRetryBatchSize, ct);
                _producerHeartbeat.ReportAlive();
                foreach (var p in pendientes)
                    await channel.Writer.WriteAsync(new UploadItem(p.Id, p.ContactId, p.BatchId, p.Prospecto), ct);

                var prospectosNuevos = await _primaApiService.GetProspectosAsync(ct);
                _logger.LogInformation("Se descargaron {Count} prospectos nuevos de Prima.", prospectosNuevos.Count);
                _producerHeartbeat.ReportAlive();

                foreach (var prospecto in prospectosNuevos)
                {
                    var contactId = Guid.NewGuid().ToString("N");
                    var gssId = await _gssRepo.InsertAsync(prospecto, batchId, outboundProcessId, contactId, ct);
                    await channel.Writer.WriteAsync(new UploadItem(gssId, contactId, batchId, prospecto), ct);
                    _producerHeartbeat.ReportAlive();
                }

                _producerHeartbeat.ReportProgress();
            }
            finally
            {
                // SIEMPRE completar el channel, incluso si el productor tiró una
                // excepción a mitad de camino — si no, los consumers que ya arrancaron
                // quedan esperando para siempre (no cuentan como hang porque el patrón
                // idle-safe sigue reportando alive, pero tampoco terminan nunca).
                channel.Writer.Complete();
            }

            await Task.WhenAll(workers);
        }

        private async Task ConsumeAsync(ChannelReader<UploadItem> reader, CancellationToken ct)
        {
            while (true)
            {
                bool hasData;
                try
                {
                    var waitTask = reader.WaitToReadAsync(ct).AsTask();
                    var idleTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
                    if (await Task.WhenAny(waitTask, idleTask) == idleTask)
                    {
                        // Sin trabajo pendiente hace un rato — no es un hang, no hay
                        // nada que hacer. El worker sigue vivo y al día.
                        _consumersHeartbeat.ReportAlive();
                        _consumersHeartbeat.ReportProgress();
                        continue;
                    }
                    hasData = await waitTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!hasData)
                {
                    // El channel se cerró vacío — no es un hang, es que no había nada
                    // pendiente en este tick (0 reintentos + 0 prospectos nuevos de Prima).
                    // Sin este ReportAlive/ReportProgress, una racha de ticks sin trabajo
                    // real (horarios de bajo tráfico) deja el heartbeat de Consumers sin
                    // tocar por varios minutos seguidos, y el Watchdog termina reiniciando
                    // un servicio sano.
                    _consumersHeartbeat.ReportAlive();
                    _consumersHeartbeat.ReportProgress();
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    await ProcessItemAsync(item, ct);
                    _consumersHeartbeat.ReportAlive();
                    _consumersHeartbeat.ReportProgress();
                }
            }
        }

        private async Task ProcessItemAsync(UploadItem item, CancellationToken ct)
        {
            using (LogContext.PushProperty("ContactId", item.ContactId))
            {
                try
                {
                    var priority = CalculatePriority(item.Prospecto);
                    var lead = MapToLead(item.Prospecto, item.ContactId, item.BatchId, priority);
                    var request = BuildRequest(lead, item.Prospecto);

                    var sw = Stopwatch.StartNew();
                    var result = await _inConcertApiService.AddContactAsync(request, ct);
                    sw.Stop();

                    if (result.Success)
                    {
                        await _gssRepo.MarkUploadedAsync(item.GssId, (int)sw.Elapsed.TotalSeconds, item.ContactId, ct);

                        if (_skillsEnabled && _skillItems.Count > 0)
                        {
                            var skillsRequest = new SetSkillsRequest
                            {
                                mix = _skillsMix,
                                contactId = item.ContactId,
                                skills = _skillItems
                            };
                            var skillsOk = await _inConcertApiService.SetSkillsAsync(skillsRequest, ct);
                            if (!skillsOk)
                                _logger.LogWarning(
                                    "El contacto se subió pero falló la asignación de skills. ContactId: {ContactId}.",
                                    item.ContactId);
                        }
                    }
                    else
                    {
                        await _gssRepo.RegisterFailedAttemptAsync(item.GssId, result.FailureKind, ct);
                        _logger.LogWarning(
                            "Fallo al enviar prospecto {Dni}. GssId: {GssId}. Motivo: {FailureKind}.",
                            item.Prospecto.dni, item.GssId, result.FailureKind);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown en curso — el item no se marcó ni sumó intento fallido,
                    // así que GetPendingRetryAsync lo va a recoger solo en el próximo
                    // arranque. No es un error real, no se loguea como tal.
                    _logger.LogInformation(
                        "Prospecto no se llegó a procesar antes del shutdown — se reintentará al próximo arranque. GssId: {GssId}.",
                        item.GssId);
                }
                catch (Exception ex)
                {
                    // Excepción fuera de AddContactAsync (mapeo, prioridad, o el propio
                    // MarkUploadedAsync/SetSkillsAsync) — no es un problema de infraestructura
                    // de InConcert, se trata como Permanent (mismo comportamiento que antes).
                    await _gssRepo.RegisterFailedAttemptAsync(item.GssId, UploadFailureKind.Permanent, ct);
                    _logger.LogError(ex, "Error procesando prospecto {Dni}. GssId: {GssId}.", item.Prospecto.dni, item.GssId);
                }
            }
        }

        private static readonly TimeZoneInfo PeruTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "SA Pacific Standard Time" : "America/Lima");

        private static int CalculatePriority(Prospecto prospecto)
        {
            var nowPeru = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, PeruTimeZone);
            var timeOfDay = nowPeru.TimeOfDay;
            var minutes = (int)timeOfDay.TotalMinutes;
            var isHorarioPico = timeOfDay.Hours is >= 8 and < 19;
            var basePriority = isHorarioPico ? 10_000 : 8_000;
            var priority = basePriority + minutes;

            if (prospecto.canal?.ToUpper() == "AMP")
            {
                var bonusPct = isHorarioPico ? 0.05 : 0.10;
                priority = (int)Math.Round(priority * (1 + bonusPct));
            }

            return priority;
        }

        private Lead MapToLead(Prospecto prospecto, string contactId, string batchId, int priority) => new()
        {
            ContactId = contactId,
            Phone = prospecto.celular,
            Name = prospecto.NombreCompleto(),
            BatchId = batchId,
            CampaignId = _campaignId,
            Priority = priority,
        };

        private static OutboundRequest BuildRequest(Lead lead, Prospecto prospecto) => new()
        {
            scope = new Scope
            {
                contacts = new List<Contact>
                {
                    new() { id = $"{lead.ContactId}@afpprima" }
                }
            },
            batchId = lead.BatchId,
            campaignId = lead.CampaignId,
            configuration = new OutboundConfiguration
            {
                address = new Address
                {
                    Type = "Phone",
                    Kind = "CELLULAR",
                    Channels = new List<string> { "CALL" },
                    Number = lead.Phone
                },
                agent = string.Empty,
                scheduleDate = string.Empty,
                priority = lead.Priority,
                contactData = new ContactData
                {
                    Name = lead.Name,
                    ImportId = lead.BatchId,
                    NameValuesSearchText = new List<NameValue>
                    {
                        new() { Name = "NOMBRE_COMPLETO", Value = lead.Name },
                        new() { Name = "DNI",             Value = prospecto.dni },
                        new() { Name = "Correo",          Value = prospecto.email??"" },
                        new() { Name = "UltimoPaso",      Value = prospecto.ultimoPaso??"" },
                        new() { Name = "FechaUltimoPaso", Value = prospecto.fechaUltimoPaso ??""},
                        new() { Name = "Edad", Value = prospecto.edad??"" },
                        new() { Name = "FechaNacimiento", Value = prospecto.fechaNacimiento ?? "" },
                        new() { Name = "TipodeComision", Value = prospecto.tipoComision ?? "" },
                        new() { Name = "AFPOrigen", Value = prospecto.afpOrigen ?? "" },
                        new() { Name = "EstuvoenPrima", Value = prospecto.indicadorEnPrima ?? "" },
                        new() { Name = "TipodeCliente", Value = prospecto.tipoCliente ?? "" },
                        new() { Name = "DatosBCP", Value = prospecto.ramBcp ?? "" },
                        new() { Name = "IndicadorPrima", Value = prospecto.indicadorEnPrima ?? "" },
                        new() { Name = "ErrordeValidacionReniec", Value = prospecto.errorValidacionReniec.ToString()},
                        new() { Name = "Genero", Value = prospecto.genero??""  },
                        new() { Name = "Canal", Value = prospecto.canal??""  },

                        //Nuevos campos
                        new() { Name = "UtmSource", Value = prospecto.utmSource??""  },
                        new() { Name = "UtmMedium", Value = prospecto.utmMedium??""  },
                        new() { Name = "UtmCampaign", Value = prospecto.utmCampaign??""  },
                        new() { Name = "UtmContent", Value = prospecto.utmContent??""  }
                    }
                }
            }
        };
    }
}