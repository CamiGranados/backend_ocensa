using ClosedXML.Excel;

namespace DashboardApi.Services
{
    public class FileReaderService
    {
        public (List<string> encabezados, List<Dictionary<string, string>> filas) LeerExcel(Stream archivoStream)
        {
            using var workbook = new XLWorkbook(archivoStream);
            var hoja = workbook.Worksheets.First(); // por ahora tomamos la primera hoja

            // XLCellsUsedOptions.Contents ignora celdas que solo tienen formato aplicado
            // (columnas/filas enteras formateadas en Excel sin datos reales), que si no
            // se excluyen hacen que ClosedXML recorra rangos enormes y sea extremadamente lento.
            var filaEncabezados = hoja.FirstRowUsed(XLCellsUsedOptions.Contents)
                ?? throw new InvalidOperationException("La hoja de Excel está vacía.");
            var encabezados = filaEncabezados
                .Cells()
                .Select(c => c.GetString().Trim())
                .ToList();

            var filas = new List<Dictionary<string, string>>();

            var filasDatos = hoja.RowsUsed(XLCellsUsedOptions.Contents).Skip(1); // saltamos la fila de encabezados

            foreach (var fila in filasDatos)
            {
                var diccionarioFila = new Dictionary<string, string>();

                for (int i = 0; i < encabezados.Count; i++)
                {
                    var celda = fila.Cell(i + 1); // ClosedXML es 1-indexado, no 0-indexado
                    diccionarioFila[encabezados[i]] = celda.GetString().Trim();
                }

                filas.Add(diccionarioFila);
            }

            return (encabezados, filas);
        }
    }
}
