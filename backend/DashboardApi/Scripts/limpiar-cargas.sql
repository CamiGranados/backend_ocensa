/*
    limpiar-cargas.sql
    -------------------
    Borra todos los datos cargados desde Excel (Uploads y lo que cuelga de ellos)
    para poder re-cargar desde cero. NO toca el catálogo reutilizable
    (Companies, Tanks, TargetScenarios).

    Uso:
        sqlcmd -S localhost -d DashboardDb -E -C -i backend/DashboardApi/Scripts/limpiar-cargas.sql
        -- o abrirlo en SSMS y ejecutar (F5)

    Todo va dentro de una transacción: si algo falla, no se borra nada.
    Para vaciar TAMBIÉN el catálogo, descomenta el bloque marcado "CATÁLOGO".
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;   -- cualquier error aborta y hace rollback
SET QUOTED_IDENTIFIER ON;  -- requerido por la columna calculada de Tanks (CategoryNace)

PRINT '=== Conteos ANTES ===';
SELECT 'Measurements'        AS tabla, COUNT(*) AS filas FROM dbo.Measurements
UNION ALL SELECT 'TankDailyOperations',  COUNT(*) FROM dbo.TankDailyOperations
UNION ALL SELECT 'PhysicalChemistries', COUNT(*) FROM dbo.PhysicalChemistries
UNION ALL SELECT 'TankTargetPeriods',   COUNT(*) FROM dbo.TankTargetPeriods
UNION ALL SELECT 'Uploads',             COUNT(*) FROM dbo.Uploads
UNION ALL SELECT 'Companies (se conserva)',       COUNT(*) FROM dbo.Companies
UNION ALL SELECT 'Tanks (se conserva)',           COUNT(*) FROM dbo.Tanks
UNION ALL SELECT 'TargetScenarios (se conserva)', COUNT(*) FROM dbo.TargetScenarios;

BEGIN TRAN;

    -- Orden respetando las FK: hijos -> padres
    DELETE FROM dbo.PhysicalChemistries;
    DELETE FROM dbo.TankTargetPeriods;
    DELETE FROM dbo.Measurements;
    DELETE FROM dbo.TankDailyOperations;
    DELETE FROM dbo.Uploads;

    /* ---------- CATÁLOGO (opcional) ----------
       Descomenta si también quieres borrar empresas / tanques / escenarios.
       Los escenarios los vuelve a crear el controlador en la siguiente carga.
    DELETE FROM dbo.Companies;
    DELETE FROM dbo.Tanks;
    DELETE FROM dbo.TargetScenarios;
    ------------------------------------------- */

    -- Reiniciar los contadores IDENTITY para que los Id vuelvan a empezar en 1.
    DBCC CHECKIDENT ('dbo.PhysicalChemistries', RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.TankTargetPeriods',   RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Measurements',        RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.TankDailyOperations', RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Uploads',             RESEED, 0) WITH NO_INFOMSGS;

COMMIT TRAN;

PRINT '=== Conteos DESPUÉS ===';
SELECT 'Measurements'        AS tabla, COUNT(*) AS filas FROM dbo.Measurements
UNION ALL SELECT 'TankDailyOperations',  COUNT(*) FROM dbo.TankDailyOperations
UNION ALL SELECT 'PhysicalChemistries', COUNT(*) FROM dbo.PhysicalChemistries
UNION ALL SELECT 'TankTargetPeriods',   COUNT(*) FROM dbo.TankTargetPeriods
UNION ALL SELECT 'Uploads',             COUNT(*) FROM dbo.Uploads
UNION ALL SELECT 'Companies (se conserva)',       COUNT(*) FROM dbo.Companies
UNION ALL SELECT 'Tanks (se conserva)',           COUNT(*) FROM dbo.Tanks
UNION ALL SELECT 'TargetScenarios (se conserva)', COUNT(*) FROM dbo.TargetScenarios;

PRINT 'Listo. Base lista para volver a cargar.';
