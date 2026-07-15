-- Migración manual: soporte de reintentos para subida a InConcert.
-- Ejecutar contra la base de dbo.GSS_Prospectos (B2503_WEBRTC_CLS32_AFP_PRIMA).
--
-- ICUpload pasa a tener 3 estados:
--   0 = pendiente (nunca subido, o subido con < 3 intentos fallidos)
--   1 = subido OK
--   3 = fallo definitivo (agotó los 3 intentos)

ALTER TABLE dbo.GSS_Prospectos
    ADD IntentosIC INT NOT NULL DEFAULT 0;
