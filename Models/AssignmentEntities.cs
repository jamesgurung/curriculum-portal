using Azure;
using Azure.Data.Tables;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace CurriculumPortal;

public class AssignmentEntity : ITableEntity
{
  public string PartitionKey
  {
    get;
    set
    {
      field = value;
      if (value is null || value.Length != 4) return;
      YearGroup = int.Parse(value[..2], CultureInfo.InvariantCulture);
      SubjectCode = value[2..4];
    }
  }
  public string RowKey
  {
    get;
    set
    {
      field = value;
      if (value is null) return;
      DueDate = DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date) ? date : default;
    }
  }
  [JsonIgnore]
  public DateTimeOffset? Timestamp { get; set; }
  [JsonIgnore]
  public ETag ETag { get; set; }

  public int Length { get; set; }

  [IgnoreDataMember]
  public int YearGroup { get; private set; }
  [IgnoreDataMember]
  public string SubjectCode { get; private set; }
  [IgnoreDataMember]
  public DateOnly DueDate { get; private set; }
}

public class AssignmentQuestionEntity : ITableEntity
{
  public string PartitionKey
  {
    get;
    set
    {
      field = value;
      if (value is null || value.Length != 4) return;
      YearGroup = int.Parse(value[..2], CultureInfo.InvariantCulture);
      SubjectCode = value[2..4];
    }
  }
  public string RowKey
  {
    get;
    set
    {
      field = value;
      var parts = value?.Split('_', 2);
      if (parts is null || parts.Length < 2) return;
      DueDate = DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out var date) ? date : default;
      QuestionNumber = int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
  }
  [JsonIgnore]
  public DateTimeOffset? Timestamp { get; set; }
  [JsonIgnore]
  public ETag ETag { get; set; }

  [IgnoreDataMember]
  public int YearGroup { get; private set; }
  [IgnoreDataMember]
  public string SubjectCode { get; private set; }
  [IgnoreDataMember]
  public DateOnly DueDate { get; private set; }
  [IgnoreDataMember]
  public int QuestionNumber { get; private set; }

  public string Question { get; set; }
  public string CorrectAnswer { get; set; }
  public string IncorrectAnswer1 { get; set; }
  public string IncorrectAnswer2 { get; set; }
  public string IncorrectAnswer3 { get; set; }
  public string UnitId { get; set; }
  public string UnitTitle { get; set; }
}

public class AssignmentSubmissionEntity : ITableEntity
{
  public string PartitionKey
  {
    get;
    set
    {
      field = value;
      if (value is null || value.Length != 4) return;
      YearGroup = int.Parse(value[..2], CultureInfo.InvariantCulture);
      SubjectCode = value[2..4];
    }
  }
  public string RowKey
  {
    get;
    set
    {
      field = value;
      var parts = value?.Split('_', 2);
      if (parts is null || parts.Length < 2) return;
      DueDate = DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out var date) ? date : default;
      StudentId = int.Parse(parts[1], CultureInfo.InvariantCulture);
    }
  }
  [JsonIgnore]
  public DateTimeOffset? Timestamp { get; set; }
  [JsonIgnore]
  public ETag ETag { get; set; }

  [IgnoreDataMember]
  public int YearGroup { get; private set; }
  [IgnoreDataMember]
  public string SubjectCode { get; private set; }
  [IgnoreDataMember]
  public DateOnly DueDate { get; private set; }
  [IgnoreDataMember]
  public int StudentId { get; private set; }

  public string ClassName { get; set; }
  public string Progress { get; set; }
  public int Completed { get; set; }
  public DateTimeOffset LockedUntil { get; set; }
}
