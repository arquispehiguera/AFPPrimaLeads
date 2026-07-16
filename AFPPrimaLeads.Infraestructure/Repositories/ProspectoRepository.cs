using AFPPrimaLeads.Core.Entities;
using AFPPrimaLeads.Core.Interfaces;
using AFPPrimaLeads.Infraestructure.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using System.Linq;

namespace AFPPrimaLeads.Infraestructure.Repositories
{
    public class ProspectoRepository : IProspectoRepository
    {
        private readonly DbContextApp _db;
        private readonly ILogger<ProspectoRepository> _logger;
        private readonly int _maxIntentos;
        private readonly int _cooldownHours;
        private readonly IAsyncPolicy _dbRetryPolicy;

        // Números de error transitorios de SQL Server (timeout, deadlock, conexión) —
        // a diferencia de la capa HTTP (Polly ya cubre Prima/InConcert), acá un blip de
        // red o un deadlock se propagaba directo como fallo duro. Mismo nivel de
        // simplicidad que Resiliencia:Http:InConcert/Prima: reintentos fijos, sin backoff
        // exponencial, porque estas llamadas ya son rápidas de por sí.
        private static readonly HashSet<int> TransientSqlErrorNumbers = new()
        {
            -2,     // Timeout de comando/conexión
            64,     // Conexión cerrada forzosamente
            233,    // No hay proceso del otro lado del pipe
            1205,   // Deadlock — esta transacción fue elegida como víctima
            4060,   // No se pudo abrir la base (failover transitorio)
            40197,  // Error transitorio de Azure SQL
            40501,  // Azure SQL ocupado
            40613,  // Base de Azure SQL no disponible momentáneamente
        };

        public ProspectoRepository(DbContextApp db, ILogger<ProspectoRepository> logger, IConfiguration configuration)
        {
            _db = db;
            _logger = logger;
            _maxIntentos = configuration.GetValue<int>("ReintentosGss:MaxIntentos", 3);
            _cooldownHours = configuration.GetValue<int>("ReintentosGss:CooldownHours", 1);

            var retryCount = configuration.GetValue<int>("Resiliencia:Db:RetryCount", 3);
            _dbRetryPolicy = Policy
                .Handle<SqlException>(ex => TransientSqlErrorNumbers.Contains(ex.Number))
                .WaitAndRetryAsync(retryCount, _ => TimeSpan.FromSeconds(2));
        }
        public async Task<int> InsertAsync(Prospecto prospecto, string batchId, string outboundProcessId, string contactId, CancellationToken ct = default)
        {
            const string sql = """
                INSERT INTO dbo.GSS_Prospectos (
                    Dni, PrimerNombre, SegundoNombre, PrimerApellido, SegundoApellido,
                    Genero, Email, Celular, UltimoPaso, FechaUltimoPaso, Canal, Edad,
                    FechaNacimiento, TipoComision, AfpOrigen, IndicadorEnPrima, TipoCliente,
                    CelularBcp, RamBcp, RamPrima, FechaAfiliacionPrima, ErrorValidacionReniec,
                    ParametrosUtm, OutboundProcessID, BatchId,JsonClient,UtmSource,UtmMedium,UtmCampaign,UtmContent,
                    ContactId
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @Dni, @PrimerNombre, @SegundoNombre, @PrimerApellido, @SegundoApellido,
                    @Genero, @Email, @Celular, @UltimoPaso, @FechaUltimoPaso, @Canal, @Edad,
                    @FechaNacimiento, @TipoComision, @AfpOrigen, @IndicadorEnPrima, @TipoCliente,
                    @CelularBcp, @RamBcp, @RamPrima, @FechaAfiliacionPrima, @ErrorValidacionReniec,
                    @ParametrosUtm, @OutboundProcessID, @BatchId,@JsonClient,@UtmSource,@UtmMedium,@UtmCampaign,@UtmContent,
                    @ContactId
                );
                """;

            try
            {
                return await _dbRetryPolicy.ExecuteAsync(async innerCt =>
                {
                    using var conn = _db.CreateConnection();
                    var command = new CommandDefinition(sql, new
                    {
                        Dni                   = prospecto.dni,
                        PrimerNombre          = prospecto.primerNombre,
                        SegundoNombre         = prospecto.segundoNombre,
                        PrimerApellido        = prospecto.primerApellido,
                        SegundoApellido       = prospecto.segundoApellido,
                        Genero                = prospecto.genero,
                        Email                 = prospecto.email,
                        Celular               = prospecto.celular,
                        UltimoPaso            = prospecto.ultimoPaso,
                        FechaUltimoPaso       = prospecto.fechaUltimoPaso,
                        Canal                 = prospecto.canal,
                        Edad                  = prospecto.edad,
                        FechaNacimiento       = prospecto.fechaNacimiento,
                        TipoComision          = prospecto.tipoComision,
                        AfpOrigen             = prospecto.afpOrigen,
                        IndicadorEnPrima      = prospecto.indicadorEnPrima,
                        TipoCliente           = prospecto.tipoCliente,
                        CelularBcp            = prospecto.celularBcp,
                        RamBcp                = prospecto.ramBcp,
                        RamPrima              = prospecto.ramPrima,
                        FechaAfiliacionPrima  = prospecto.fechaAfiliacionPrima,
                        ErrorValidacionReniec = prospecto.errorValidacionReniec.ToString(),
                        ParametrosUtm         = prospecto.parametrosUtm,
                        OutboundProcessID     = outboundProcessId,
                        BatchId               = batchId,
                        JsonClient            = prospecto.jsonClient,
                        UtmSource             = prospecto.utmSource,
                        UtmMedium             = prospecto.utmMedium,
                        UtmCampaign           = prospecto.utmCampaign,
                        UtmContent            = prospecto.utmContent,
                        ContactId             = contactId
                    }, cancellationToken: innerCt);
                    return await conn.ExecuteScalarAsync<int>(command);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso — no es una falla de inserción real.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error al insertar prospecto en GSS_Prospectos. Dni: {Dni}, BatchId: {BatchId}, ProcessId: {ProcessId}.",
                    prospecto.dni, batchId, outboundProcessId);
                throw;
            }
        }

        public async Task<IReadOnlyList<ProspectoPendiente>> GetPendingRetryAsync(int maxBatchSize, CancellationToken ct = default)
        {
            // TOP (@MaxBatchSize) es a propósito: sin tope, un backlog acumulado por una
            // caída larga de InConcert (30+ min) podría devolver miles de filas en una
            // sola corrida y volar el presupuesto de 1 min. El resto queda para la
            // próxima vuelta — no se pierde nada, solo se pospone.
            const string sql = """
                SELECT TOP (@MaxBatchSize)
                       Id, ContactId, BatchId, Dni, PrimerNombre, SegundoNombre, PrimerApellido, SegundoApellido,
                       Genero, Email, Celular, UltimoPaso, FechaUltimoPaso, Canal, Edad, FechaNacimiento,
                       TipoComision, AfpOrigen, IndicadorEnPrima, TipoCliente, CelularBcp, RamBcp, RamPrima,
                       FechaAfiliacionPrima, ErrorValidacionReniec, ParametrosUtm, UtmSource, UtmMedium,
                       UtmCampaign, UtmContent
                FROM dbo.GSS_Prospectos
                WHERE (ICUpload = 0 AND IntentosIC < @MaxIntentos)
                   OR (ICUpload = 3 AND IntentosIC = @MaxIntentos
                       AND FechaUltimoIntentoIC < DATEADD(HOUR, -@CooldownHours, GETUTCDATE()))
                ORDER BY Id;
                """;

            try
            {
                var rows = await _dbRetryPolicy.ExecuteAsync(async innerCt =>
                {
                    using var conn = _db.CreateConnection();
                    var command = new CommandDefinition(sql,
                        new { MaxBatchSize = maxBatchSize, MaxIntentos = _maxIntentos, CooldownHours = _cooldownHours },
                        cancellationToken: innerCt);
                    return await conn.QueryAsync<RetryRow>(command);
                }, ct);

                return rows.Select(r => new ProspectoPendiente
                {
                    Id = r.Id,
                    ContactId = r.ContactId,
                    BatchId = r.BatchId,
                    Prospecto = new Prospecto
                    {
                        dni = r.Dni,
                        primerNombre = r.PrimerNombre,
                        segundoNombre = r.SegundoNombre,
                        primerApellido = r.PrimerApellido,
                        segundoApellido = r.SegundoApellido,
                        genero = r.Genero,
                        email = r.Email,
                        celular = r.Celular,
                        ultimoPaso = r.UltimoPaso,
                        fechaUltimoPaso = r.FechaUltimoPaso,
                        canal = r.Canal,
                        edad = r.Edad,
                        fechaNacimiento = r.FechaNacimiento,
                        tipoComision = r.TipoComision,
                        afpOrigen = r.AfpOrigen,
                        indicadorEnPrima = r.IndicadorEnPrima,
                        tipoCliente = r.TipoCliente,
                        celularBcp = r.CelularBcp,
                        ramBcp = r.RamBcp,
                        ramPrima = r.RamPrima,
                        fechaAfiliacionPrima = r.FechaAfiliacionPrima,
                        errorValidacionReniec = bool.TryParse(r.ErrorValidacionReniec, out var errorReniec) && errorReniec,
                        parametrosUtm = r.ParametrosUtm,
                        utmSource = r.UtmSource,
                        utmMedium = r.UtmMedium,
                        utmCampaign = r.UtmCampaign,
                        utmContent = r.UtmContent
                    }
                }).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso — no es una falla real de lectura.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener prospectos pendientes de reintento.");
                throw;
            }
        }

        public async Task MarkUploadedAsync(int id, int elapsedSeconds, string contactId, CancellationToken ct = default)
        {
            const string sql = """
                UPDATE dbo.GSS_Prospectos
                SET ICUpload = 1, ICTimeUpload = @ElapsedSeconds
                WHERE Id = @Id;
                """;

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async innerCt =>
                {
                    using var conn = _db.CreateConnection();
                    var command = new CommandDefinition(sql,
                        new { Id = id, ElapsedSeconds = elapsedSeconds },
                        cancellationToken: innerCt);
                    return await conn.ExecuteAsync(command);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown en curso justo después de subir el contacto — se propaga tal
                // cual; el contacto ya se subió a IC, pero acá se pierde el marcado.
                // Riesgo aceptado por ahora: el próximo GetPendingRetryAsync lo va a
                // reintentar y volverá a subirlo (mismo riesgo de duplicado que ya existe
                // en AddContactAsync ante un cancel a mitad de respuesta).
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error al marcar prospecto como subido en GSS_Prospectos. Id: {Id}, ContactId: {ContactId}.",
                    id, contactId);
                throw;
            }
        }

        public async Task RegisterFailedAttemptAsync(int id, UploadFailureKind kind, CancellationToken ct = default)
        {
            // Una falla Transient (InConcert caído, no un rechazo real del dato) no debe
            // quemar IntentosIC ni tocar ICUpload — si no, un outage de InConcert abandona
            // prospectos sanos antes de que InConcert siquiera vuelva a responder.
            // FechaUltimoIntentoIC sí se actualiza siempre: es lo que habilita la
            // reactivación por enfriamiento en GetPendingRetryAsync.
            const string sql = """
                UPDATE dbo.GSS_Prospectos
                SET IntentosIC = CASE WHEN @Kind = 'Permanent' THEN IntentosIC + 1 ELSE IntentosIC END,
                    ICUpload = CASE WHEN @Kind = 'Permanent' AND IntentosIC + 1 >= @MaxIntentos THEN 3 ELSE ICUpload END,
                    FechaUltimoIntentoIC = GETUTCDATE()
                WHERE Id = @Id;
                """;

            try
            {
                await _dbRetryPolicy.ExecuteAsync(async innerCt =>
                {
                    using var conn = _db.CreateConnection();
                    var command = new CommandDefinition(sql,
                        new { Id = id, MaxIntentos = _maxIntentos, Kind = kind.ToString() },
                        cancellationToken: innerCt);
                    return await conn.ExecuteAsync(command);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al registrar intento fallido en GSS_Prospectos. Id: {Id}.", id);
                throw;
            }
        }

        private sealed class RetryRow
        {
            public int Id { get; set; }
            public string ContactId { get; set; } = string.Empty;
            public string BatchId { get; set; } = string.Empty;
            public string Dni { get; set; } = string.Empty;
            public string PrimerNombre { get; set; } = string.Empty;
            public string? SegundoNombre { get; set; }
            public string PrimerApellido { get; set; } = string.Empty;
            public string? SegundoApellido { get; set; }
            public string? Genero { get; set; }
            public string? Email { get; set; }
            public string Celular { get; set; } = string.Empty;
            public string? UltimoPaso { get; set; }
            public string? FechaUltimoPaso { get; set; }
            public string? Canal { get; set; }
            public string? Edad { get; set; }
            public string? FechaNacimiento { get; set; }
            public string? TipoComision { get; set; }
            public string? AfpOrigen { get; set; }
            public string? IndicadorEnPrima { get; set; }
            public string? TipoCliente { get; set; }
            public string? CelularBcp { get; set; }
            public string? RamBcp { get; set; }
            public string? RamPrima { get; set; }
            public string? FechaAfiliacionPrima { get; set; }
            public string? ErrorValidacionReniec { get; set; }
            public string? ParametrosUtm { get; set; }
            public string? UtmSource { get; set; }
            public string? UtmMedium { get; set; }
            public string? UtmCampaign { get; set; }
            public string? UtmContent { get; set; }
        }
    }
}
