namespace CurriculumPortal;

public static class Roles
{
  public const string Admin = nameof(Admin);
  public const string Teacher = nameof(Teacher);
  public const string Student = nameof(Student);
}

public class AssessmentBuildModel
{
  public string CourseId { get; set; } = string.Empty;
  public string CourseTitle { get; set; } = string.Empty;
  public string UnitId { get; set; } = string.Empty;
  public string UnitTitle { get; set; } = string.Empty;
  public KeyKnowledge KeyKnowledge { get; set; } = new();
  public Assessment Assessment { get; set; } = new();
  public bool Editable { get; set; }
  public bool KeyKnowledgeComplete { get; set; }
  public bool AssessmentComplete { get; set; }
}

public class UnitSortOrder
{
  public List<string> Order { get; set; } = [];
}

public class SingleValueModel
{
  public string Value { get; set; } = string.Empty;
}

public class NewUnitModel
{
  public string Title { get; set; } = string.Empty;
  public int YearGroup { get; set; }
}

public class PublicFacingUnit(UnitEntity unit)
{
  public string CourseId { get; set; } = unit.PartitionKey;
  public string Id { get; set; } = unit.RowKey;
  public string Title { get; set; } = unit.Title;
  public int YearGroup { get; set; } = unit.YearGroup;
  public string Term { get; set; } = unit.Term;
  public int Order { get; set; } = unit.Order;
  public string WhyThis { get; set; } = unit.WhyThis;
  public string WhyNow { get; set; } = unit.WhyNow;
  public bool HasKeyKnowledge { get; set; } = unit.KeyKnowledgeStatus == 2;
}

public class KeyKnowledgeRevisionQuiz
{
  public List<KeyKnowledgeRevisionQuestion> Questions { get; set; } = [];
}

public class MarkSchemeResponse
{
  public string MarkScheme { get; set; } = string.Empty;
}

public class GenerateQuestionsRequest
{
  public List<string> DeclarativeKnowledge { get; set; } = [];
  public int MultipleChoiceCount { get; set; }
  public int ShortAnswerCount { get; set; }
  public List<string> ExistingQuestions { get; set; } = [];
}

public class GenerateQuestionsResponse
{
  public List<MultipleChoiceQuestion> MultipleChoiceQuestions { get; set; } = [];
  public List<ShortAnswerQuestion> ShortAnswerQuestions { get; set; } = [];
}

public class MultipleChoiceQuestion
{
  public string Question { get; set; } = string.Empty;
  public string CorrectAnswer { get; set; } = string.Empty;
  public List<string> WrongAnswers { get; set; } = [];
}

public class ShortAnswerQuestion
{
  public string Question { get; set; } = string.Empty;
  public string Answer { get; set; } = string.Empty;
}

public class RecordSheets
{
  public string UnitTitle { get; set; } = string.Empty;
  public List<RecordSheetElement> Elements { get; set; } = [];
  public List<RecordSheetClass> Classes { get; set; } = [];
}

public class RecordSheetClass
{
  public string Name { get; set; } = string.Empty;
  public List<string> Students { get; set; } = [];
}

public class RecordSheetElement
{
  public string Title { get; set; } = string.Empty;
  public int MaxMarks { get; set; }
}

public class User
{
  public int Id { get; set; }
  public string Email { get; set; }
  public string FirstName { get; set; }
  public string LastName { get; set; }
  public string TutorGroup { get; set; }
  public List<string> Classes { get; set; }
  public bool IsTeacher { get; set; }
  public bool PupilPremium { get; set; }
  public bool IsAdmin { get; set; }
  public string DisplayName => $"{FirstName} {LastName}";
}

public class Holiday
{
  public DateOnly Start { get; set; }
  public DateOnly End { get; set; }
}

public class ChecklistItemConfig
{
  public string Id { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
}

public class AssignmentsPageData
{
  public bool IsStaff { get; set; }
  public AssignmentsStudentData Student { get; set; } = new();
  public AssignmentsStaffData Staff { get; set; } = new();
}

public class AssignmentsStudentData
{
  public List<AssignmentsStudentCard> ToDo { get; set; } = [];
  public List<AssignmentsStudentCard> Past { get; set; } = [];
}

public class StudentWithCompletion
{
  public User Student { get; set; }
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public double CompletionRate { get; set; }
}

public class WeeklyCompletionReports
{
  public DateOnly DueDate { get; set; }
  public string DueDateLabel { get; set; } = string.Empty;
  public List<TutorCompletionReport> Tutors { get; set; } = [];
  public List<TeacherCompletionReport> Teachers { get; set; } = [];
}

public class TutorCompletionReport
{
  public string DueDateLabel { get; set; } = string.Empty;
  public User Tutor { get; set; }
  public string TutorGroup { get; set; } = string.Empty;
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public int CompletionPercentage { get; set; }
  public List<CompletionStudentRow> Students { get; set; } = [];
  public List<TutorGroupCompletionRow> TutorGroups { get; set; } = [];
}

public class TeacherCompletionReport
{
  public string DueDateLabel { get; set; } = string.Empty;
  public User Teacher { get; set; }
  public List<ClassCompletionReport> Classes { get; set; } = [];
}

public class ClassCompletionReport
{
  public string ClassName { get; set; } = string.Empty;
  public string CourseName { get; set; } = string.Empty;
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public int CompletionPercentage { get; set; }
  public List<CompletionStudentRow> Students { get; set; } = [];
}

public class CompletionStudentRow
{
  public string Name { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public int CompletionPercentage { get; set; }
}

public class TutorGroupCompletionRow
{
  public int Rank { get; set; }
  public string TutorGroup { get; set; } = string.Empty;
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public int CompletionPercentage { get; set; }
  public bool IsCurrentTutorGroup { get; set; }
}

public class AssignmentsStudentCard
{
  public string CourseId { get; set; } = string.Empty;
  public string CourseName { get; set; } = string.Empty;
  public int YearGroup { get; set; }
  public string DueDate { get; set; } = string.Empty;
  public string DueDateLabel { get; set; } = string.Empty;
  public int Completed { get; set; }
  public int TotalQuestions { get; set; }
  public bool IsComplete { get; set; }
  public string Href { get; set; } = string.Empty;
}

public class AssignmentDetailPageData
{
  public string CourseId { get; set; } = string.Empty;
  public string CourseName { get; set; } = string.Empty;
  public int YearGroup { get; set; }
  public string DueDate { get; set; } = string.Empty;
  public string DueDateLabel { get; set; } = string.Empty;
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public bool IsComplete { get; set; }
  public AssignmentQuestionDto CurrentQuestion { get; set; }
}

public class AssignmentQuestionDto
{
  public int QuestionNumber { get; set; }
  public string UnitId { get; set; } = string.Empty;
  public string UnitTitle { get; set; } = string.Empty;
  public string QuestionText { get; set; } = string.Empty;
  public string[] Answers { get; set; } = [];
}

public class AssignmentAnswerRequest
{
  public int QuestionNumber { get; set; }
  public int Answer { get; set; }
}

public class AssignmentAnswerResponse
{
  public int CorrectAnswer { get; set; }
  public int CompletedQuestions { get; set; }
  public int TotalQuestions { get; set; }
  public AssignmentQuestionDto NextQuestion { get; set; }
}

public class AssignmentsStaffData
{
  public List<AssignmentsDateColumn> Dates { get; set; } = [];
  public List<AssignmentsStaffRow> Classes { get; set; } = [];
  public List<AssignmentsStaffRow> YearGroups { get; set; } = [];
  public List<AssignmentsStaffRow> Courses { get; set; } = [];
  public List<AssignmentsStaffDetail> Details { get; set; } = [];
}

public class AssignmentsDateColumn
{
  public string Value { get; set; } = string.Empty;
  public string Label { get; set; } = string.Empty;
}

public class AssignmentsStaffRow
{
  public string Title { get; set; } = string.Empty;
  public string DetailId { get; set; } = string.Empty;
  public bool PupilPremium { get; set; }
  public List<AssignmentsProgressCell> Cells { get; set; } = [];
}

public class AssignmentsStaffDetail
{
  public string Id { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string FirstColumnTitle { get; set; } = string.Empty;
  public bool ClickableRows { get; set; }
  public List<AssignmentsStaffRow> Rows { get; set; } = [];
}

public class AssignmentsProgressCell
{
  public string DueDate { get; set; } = string.Empty;
  public bool HasAssignment { get; set; }
  public int Completed { get; set; }
  public int Total { get; set; }
  public int PupilPremiumCompleted { get; set; }
  public int PupilPremiumTotal { get; set; }
}
