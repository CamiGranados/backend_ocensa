/*
    limpiar-todo.sql
    ----------------
    BORRA TODO: cargas + catálogo (empresas, tanques, escenarios).
    Deja la BD como recién migrada, con los Id reiniciados en 1.
    Los TargetScenarios los vuelve a crear el controlador en la siguiente carga.

    Uso (SSMS con F5, o):
        sqlcmd -S localhost -d DashboardDb -E -C -i backend/DashboardApi/Scripts/limpiar-todo.sql

    Todo va en una transacción: si algo falla, no borra nada.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;   -- requerido: PhysicalChemistries tiene una columna computada

PRINT '=== ANTES ===';
SELECT 'Measurements' t, COUNT(*) n FROM dbo.Measurements
UNION ALL SELECT 'TankDailyOperations',  COUNT(*) FROM dbo.TankDailyOperations
UNION ALL SELECT 'PhysicalChemistries', COUNT(*) FROM dbo.PhysicalChemistries
UNION ALL SELECT 'TankTargetPeriods',   COUNT(*) FROM dbo.TankTargetPeriods
UNION ALL SELECT 'Uploads',             COUNT(*) FROM dbo.Uploads
UNION ALL SELECT 'Companies',            COUNT(*) FROM dbo.Companies
UNION ALL SELECT 'Tanks',                COUNT(*) FROM dbo.Tanks
UNION ALL SELECT 'TargetScenarios',      COUNT(*) FROM dbo.TargetScenarios;

BEGIN TRAN;

    -- hijos -> padres
    DELETE FROM dbo.PhysicalChemistries;
    DELETE FROM dbo.TankTargetPeriods;
    DELETE FROM dbo.Measurements;
    DELETE FROM dbo.TankDailyOperations;
    DELETE FROM dbo.Uploads;
    DELETE FROM dbo.Companies;
    DELETE FROM dbo.Tanks;
    DELETE FROM dbo.TargetScenarios;

    DBCC CHECKIDENT ('dbo.PhysicalChemistries', RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.TankTargetPeriods',   RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Measurements',        RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.TankDailyOperations', RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Uploads',             RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Companies',           RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.Tanks',               RESEED, 0) WITH NO_INFOMSGS;
    DBCC CHECKIDENT ('dbo.TargetScenarios',     RESEED, 0) WITH NO_INFOMSGS;

COMMIT TRAN;

PRINT '=== DESPUES ===';
SELECT 'Measurements' t, COUNT(*) n FROM dbo.Measurements
UNION ALL SELECT 'TankDailyOperations',  COUNT(*) FROM dbo.TankDailyOperations
UNION ALL SELECT 'PhysicalChemistries', COUNT(*) FROM dbo.PhysicalChemistries
UNION ALL SELECT 'TankTargetPeriods',   COUNT(*) FROM dbo.TankTargetPeriods
UNION ALL SELECT 'Uploads',             COUNT(*) FROM dbo.Uploads
UNION ALL SELECT 'Companies',            COUNT(*) FROM dbo.Companies
UNION ALL SELECT 'Tanks',                COUNT(*) FROM dbo.Tanks
UNION ALL SELECT 'TargetScenarios',      COUNT(*) FROM dbo.TargetScenarios;

PRINT 'BD vacia. Lista para la prueba de carga.';
