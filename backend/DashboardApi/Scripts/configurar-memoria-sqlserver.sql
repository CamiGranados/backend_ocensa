/*
    configurar-memoria-sqlserver.sql
    --------------------------------
    La máquina tiene ~7.7 GB de RAM y se queda sin memoria: Windows termina
    paginando a disco el buffer pool de SQL Server (baja a ~100 MB), y entonces
    hasta un INSERT de 42 filas tarda minutos → "Execution Timeout Expired" /
    "Login timeout".

    Por defecto 'max server memory' está en 2147483647 MB (sin límite): SQL Server
    y el resto del sistema se pelean por la RAM. Fijarle un tope estable lo evita.

    Ejecutar UNA vez (SSMS como administrador, o):
        sqlcmd -S localhost -E -C -i backend/DashboardApi/Scripts/configurar-memoria-sqlserver.sql

    Después REINICIAR el servicio para que tome un working set limpio:
        net stop MSSQLSERVER  &&  net start MSSQLSERVER
*/

EXEC sys.sp_configure N'show advanced options', 1;
RECONFIGURE;

-- Piso y techo iguales: SQL Server mantiene ~2.5 GB fijos y Windows no se los quita.
-- Ajusta si cierras/abres programas: con la caja tan justa, 2048-3072 es lo razonable.
EXEC sys.sp_configure N'min server memory (MB)', 1024;
EXEC sys.sp_configure N'max server memory (MB)', 2560;

-- Opcional: limitar el paralelismo ayuda en cajas chicas.
EXEC sys.sp_configure N'max degree of parallelism', 2;
EXEC sys.sp_configure N'cost threshold for parallelism', 50;

RECONFIGURE;

SELECT name, value_in_use
FROM sys.configurations
WHERE name IN (N'min server memory (MB)', N'max server memory (MB)',
               N'max degree of parallelism', N'cost threshold for parallelism');

PRINT 'Hecho. Ahora reinicia el servicio: net stop MSSQLSERVER && net start MSSQLSERVER';
