-- Migración manual: soporte de reactivación por enfriamiento tras InConcert caído.
-- Ejecutar contra la base de dbo.GSS_Prospectos (B2503_WEBRTC_CLS32_AFP_PRIMA).
--
-- FechaUltimoIntentoIC se actualiza en cada intento de subida (éxito o falla,
-- Transient o Permanent) y habilita que GetPendingRetryAsync le dé una segunda
-- oportunidad a un prospecto descartado (ICUpload = 3) después de
-- ReintentosGss:CooldownHours, por si esa 3ra falla fue una caída de InConcert
-- mal clasificada como Permanent.

ALTER TABLE dbo.GSS_Prospectos
    ADD FechaUltimoIntentoIC DATETIME NULL;

-- Backfill puntual (una sola vez, en este deploy): los prospectos ya descartados
-- (ICUpload = 3) antes de este fix quedarían con FechaUltimoIntentoIC = NULL para
-- siempre, y NULL < DATEADD(...) nunca es verdadero en SQL Server — nunca
-- calificarían para la reactivación por enfriamiento. Se les da una fecha
-- suficientemente atrás para que sean elegibles de inmediato en el próximo tick
-- del Worker. No toca ninguna fila ICUpload = 0 (prospectos activos/en línea).
UPDATE dbo.GSS_Prospectos
SET FechaUltimoIntentoIC = DATEADD(DAY, -1, GETUTCDATE())
WHERE ICUpload = 3;
