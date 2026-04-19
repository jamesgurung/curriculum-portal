using System.Text.Json;

namespace CurriculumPortal;

public static class JsonDefaults
{
  public static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
