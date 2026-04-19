using Azure;
using Azure.Data.Tables;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace CurriculumPortal;

public class CourseEntity : ITableEntity
{
  [JsonIgnore]
  public string PartitionKey { get; set; }

  [JsonPropertyName("id")]
  public string RowKey { get; set; }

  [JsonIgnore]
  public DateTimeOffset? Timestamp { get; set; }

  [JsonIgnore]
  public ETag ETag { get; set; }

  public int KeyStage { get; set; }
  public string Name { get; set; }
  public string SubjectCode { get; set; }
  public string Intent { get; set; }
  public string Specification { get; set; }
  public int AssignmentLength { get; set; }

  [JsonIgnore]
  public string Leaders
  {
    get;
    set
    {
      LeadersList = value?.Split(',') ?? [];
      field = value;
    }
  }

  [JsonIgnore, IgnoreDataMember]
  public IList<string> LeadersList { get; private set; }

  [IgnoreDataMember]
  public string LeaderNames { get; set; }
}

public class UnitEntity : ITableEntity
{
  [JsonPropertyName("courseId")]
  public string PartitionKey { get; set; }

  [JsonPropertyName("id")]
  public string RowKey { get; set; }

  [JsonIgnore]
  public DateTimeOffset? Timestamp { get; set; }

  [JsonIgnore]
  public ETag ETag { get; set; }

  public string Title { get; set; }
  public int YearGroup { get; set; }
  public string Term { get; set; }
  public int Order { get; set; }
  public string WhyThis { get; set; }
  public string WhyNow { get; set; }
  public string SchemeUrl { get; set; }
  public string AssessmentUrl { get; set; }
  public string MarkSchemeUrl { get; set; }
  public string Checklist { get; set; }
  public int KeyKnowledgeStatus { get; set; }
  public int AssessmentStatus { get; set; }
  public int RevisionQuizStatus { get; set; }
}

public interface ICurriculumBlob { }

public class Assessment : ICurriculumBlob
{
  public List<AssessmentSection> Sections { get; set; } = [];
}

public class AssessmentSection
{
  public string Title { get; set; } = string.Empty;
  public List<AssessmentQuestion> Questions { get; set; } = [];
}

public class AssessmentQuestion
{
  public string Question { get; set; } = string.Empty;
  public int Marks { get; set; }
  public string MarkScheme { get; set; } = string.Empty;

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public List<string> Answers { get; set; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public int? Lines { get; set; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public int? AnswerSpaceFormat { get; set; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public List<string> SuccessCriteria { get; set; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public string Image { get; set; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public int? ImageWidth { get; set; }
}

public class KeyKnowledge : ICurriculumBlob
{
  public List<string> DeclarativeKnowledge { get; set; } = [];
  public List<string> ProceduralKnowledge { get; set; } = [];

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public List<KeyKnowledgeImage> Images { get; set; }

  public List<KeyKnowledgeRevisionQuestion> RevisionQuiz { get; set; } = [];
}

public class KeyKnowledgeImage
{
  public int Index { get; set; }
  public string Content { get; set; }
  public int Width { get; set; }
}

public class KeyKnowledgeRevisionQuestion
{
  public string Question { get; set; }
  public string CorrectAnswer { get; set; }
  public string IncorrectAnswer { get; set; }
}

public class QuestionBank : ICurriculumBlob
{
  public List<QuestionBankQuestion> Questions { get; set; } = [];
}

public class QuestionBankQuestion
{
  public string Question { get; set; }
  public string CorrectAnswer { get; set; }
  public string IncorrectAnswer1 { get; set; }
  public string IncorrectAnswer2 { get; set; }
  public string IncorrectAnswer3 { get; set; }
}

public class QuestionBankQuestionWithUnit : QuestionBankQuestion
{
  public QuestionBankQuestionWithUnit(QuestionBankQuestion question, string unitId, string unitTitle)
  {
    ArgumentNullException.ThrowIfNull(question);
    Question = question.Question;
    CorrectAnswer = question.CorrectAnswer;
    IncorrectAnswer1 = question.IncorrectAnswer1;
    IncorrectAnswer2 = question.IncorrectAnswer2;
    IncorrectAnswer3 = question.IncorrectAnswer3;
    UnitId = unitId;
    UnitTitle = unitTitle;
  }
  public string UnitId { get; set; }
  public string UnitTitle { get; set; }
}
