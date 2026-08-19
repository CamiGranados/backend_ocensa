using System.ComponentModel.DataAnnotations;

namespace DashboardApi.DTOs;

public class MicroRequestDto : IValidatableObject
{
    [Required(ErrorMessage = "tankId es obligatorio.")]
    public long? TankId { get; set; }

    public int[]? Years { get; set; }

    public int[]? Months { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Months != null && Months.Any(m => m is < 1 or > 12))
        {
            yield return new ValidationResult(
                "months debe contener valores entre 1 y 12.",
                new[] { nameof(Months) });
        }

        if (Years != null && Years.Any(y => y is < 2000 or > 2100))
        {
            yield return new ValidationResult(
                "years debe contener años entre 2000 y 2100.",
                new[] { nameof(Years) });
        }
    }
}
