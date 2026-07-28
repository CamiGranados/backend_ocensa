using System.Text.Json.Serialization;

namespace DashboardApi.DTOs
{
    public class ResultadoValidacionDto
    {
        public bool Valido { get; set; }
        public int TotalFilas { get; set; }
        public int FilasOmitidas { get; set; }
        public List<string> ColumnasFaltantes { get; set; } = new();
        public List<ErrorValidacionDto> Errores { get; set; } = new();

        // Filas que pasaron los filtros de omisión (identificadores vacíos, tanque no permitido, etc.).
        // Es lo que debe combinarse en el resultado final, no la lista cruda de filas del Excel.
        // Se ignora en la serialización: uso interno entre FileValidatorService y LoadFileController.Procesar.
        // Devolverla en /validar infla la respuesta a decenas de MB en archivos grandes y cuelga al cliente.
        [JsonIgnore]
        public List<Dictionary<string, string>> FilasValidas { get; set; } = new();
        public List<string> ColumnasFinales { get; set; } = new();
    }
}
