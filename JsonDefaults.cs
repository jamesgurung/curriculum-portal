using System.Text.Json;

namespace CurriculumPortal;

public static class JsonDefaults
{
  public static JsonSerializerOptions CamelCase { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
