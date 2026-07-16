# Checklist de deploy — AFPPrimaLeads Worker Service

Solo documentación. Ninguno de estos pasos se ejecutó — son acciones sobre el
server de producción, requieren admin de dominio/servidor y confirmación
explícita antes de correrlas.

1. Dar de baja la Scheduled Task vieja de Task Scheduler **antes** de instalar
   el servicio — si conviven, procesan el mismo lote duplicado.

2. `dotnet publish -c Release` y copiar el resultado a
   `C:\JobsDeployment\AFPPrimaLeads`.

3. Confirmar que `appsettings.json` con los valores reales de producción está
   en `C:\JobsDeployment\AFPPrimaLeads\appsettings.json` (el publish ya lo
   copia solo, viene con `CopyToOutputDirectory` en el csproj).

4. Crear el servicio:
   ```
   sc.exe create AFPPrimaLeadsWorker binPath= "C:\JobsDeployment\AFPPrimaLeads\AFPPrimaLeads.Process.exe" start= auto obj= "DOMINIO\usuario" password= "..."
   ```
   (espacio después de cada `=` obligatorio). Si la cuenta es de dominio con
   GPO propia, el derecho "Log on as a service" puede pisarse solo en el
   próximo `gpupdate` — si aparece el Error 1069 después de que andaba bien,
   ese es el motivo.

5. Permisos sobre la carpeta:
   ```
   icacls "C:\JobsDeployment\AFPPrimaLeads" /grant "DOMINIO\usuario:(OI)(CI)RX"
   ```
   y además permiso de escritura sobre `C:\JobsDeployment\AFPPrimaLeads\logs`
   (el sink de archivo de Serilog escribe ahí).

6. Política de recovery — sin esto, el Watchdog mata el proceso para nada
   porque nadie lo revive:
   ```
   sc.exe failure AFPPrimaLeadsWorker reset= 86400 actions= restart/60000/restart/60000/restart/60000
   ```

7. `sc.exe start AFPPrimaLeadsWorker`.

## Validar después del deploy (no asumir que anda por las dudas)

- Confirmar en el log ("Watchdog iniciado...") que arrancó con los umbrales
  esperados (`StaleThreshold`, `MaxNoProgress` de `appsettings.json`).
- Provocar un hang real (cortar la conectividad a InConcert o a la BD un rato
  más largo que `Watchdog:StaleThresholdSeconds`) y confirmar que el Watchdog
  dispara y el SCM levanta el proceso solo.
- Dejarlo sin actividad (fuera de horario, sin prospectos nuevos) más tiempo
  que `Watchdog:CheckIntervalSeconds` y confirmar que NO se reinicia — eso
  validaría que el patrón idle-safe del heartbeat de Consumers está bien.
- Confirmar el rate limit real de InConcert contra `InConcert:MaxParallelUploads`
  antes de subirlo — sigue pendiente ese dato (ver memoria del proyecto).
- Correr el script `sql/2026-07-14_add-intentos-ic.sql` contra la base ANTES
  de arrancar el servicio — si no, `GetPendingRetryAsync`/`RegisterFailedAttemptAsync`
  fallan por columna inexistente.
- Correr también `sql/2026-07-16_add-fecha-ultimo-intento-ic.sql` (misma base,
  mismo motivo) — soporta la reactivación por enfriamiento de prospectos
  descartados tras una caída larga de InConcert.
