namespace CurriculumPortal;

internal static class ClassNameParser
{
  public static int GetLeadingNumber(string value)
    => TryGetLeadingNumber(value, out var number) ? number : 0;

  public static string GetAssignmentPartitionKey(string className)
    => TryParseSubjectClass(className, out var parsed) ? parsed.PartitionKey : null;

  public static bool TryParseSubjectClass(string className, out SubjectClass parsed)
  {
    parsed = null;
    if (string.IsNullOrWhiteSpace(className)) return false;

    var trimmed = className.Trim();
    var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
    if (!TryGetLeadingNumber(trimmed, out var yearGroup) || slashIndex <= 0 || slashIndex + 2 >= trimmed.Length) return false;

    parsed = new SubjectClass(trimmed, yearGroup, trimmed.Substring(slashIndex + 1, 2));
    return true;
  }

  private static bool TryGetLeadingNumber(string value, out int number)
  {
    number = 0;
    if (string.IsNullOrWhiteSpace(value)) return false;

    var digits = new string(value.Trim().TakeWhile(char.IsDigit).ToArray());
    return int.TryParse(digits, out number);
  }
}

internal sealed record SubjectClass(string Name, int YearGroup, string SubjectCode)
{
  public string PartitionKey => $"{YearGroup:D2}{SubjectCode}";
}
