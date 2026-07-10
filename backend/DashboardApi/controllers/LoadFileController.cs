using Microsoft.AspNetCore.Mvc;
using DashboardApi.Services;
using DashboardApi.DTOs;

namespace DashboardApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LoadFileController : ControllerBase
    {
        private readonly FileReaderService _fileReaderService;
        private readonly FileValidatorService _validadorService;

        public LoadFileController(FileReaderService fileReaderService, FileValidatorService validadorService)
        {
            _fileReaderService = fileReaderService;
            _validadorService = validadorService;
        }

        [HttpPost("validar")]
        public IActionResult Validar(IFormFile archivo)
        {
            if (archivo == null || archivo.Length == 0)
            {
                return BadRequest(new { mensaje = "No se recibió ningún archivo." });
            }

            var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            if (extension != ".xlsx")
            {
                return BadRequest(new { mensaje = "El archivo debe ser un Excel (.xlsx)." });
            }

            try
            {
                using var stream = archivo.OpenReadStream();
                var (encabezados, filas) = _fileReaderService.LeerExcel(stream);

                var resultado = _validadorService.Validar(encabezados, filas);

                return Ok(resultado);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al procesar el archivo.", detalle = ex.Message });
            }
        }
    }
}