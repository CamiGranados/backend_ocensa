using System.Security.Cryptography;
using System.Text;

namespace DashboardApi.Services
{
    // Huella SHA-256 de una fila de origen ya normalizada. Dos filas que quedan iguales
    // tras la limpieza (guiones → vacío, texto no numérico → vacío, trim) producen la
    // misma huella, así que se detectan como "fila completa repetida".
    public static class RowFingerprint
    {
        // Clave interna del controlador, no es un dato real de la fila.
        private const string ClaveArchivoOrigen = "archivoOrigen";

        public static byte[] Compute(IReadOnlyDictionary<string, string> fila)
        {
            var sb = new StringBuilder();
            foreach (var clave in fila.Keys.Where(k => k != ClaveArchivoOrigen).OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append(clave).Append('=').Append(fila[clave]?.Trim() ?? string.Empty).Append('\n');
            }
            return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // Los byte[] no sirven como clave de HashSet/Dictionary (comparan por referencia):
        // se usa la representación Base64 como clave.
        public static string Key(byte[] hash) => Convert.ToBase64String(hash);
    }
}
