using System.Globalization;

namespace CurriculumPortal;

public partial class AssignmentService
{
  private static AssignmentsStudentCard CreateStudentCard(AssignmentEntity assignment, PartitionAssignmentData data, int studentId, CourseEntity course)
  {
    var progress = GetAssignmentProgress(assignment, data, studentId);
    var courseId = course?.RowKey ?? assignment.CourseId;
    var courseName = course?.Name ?? assignment.CourseId;

    return new AssignmentsStudentCard
    {
      CourseId = courseId,
      CourseName = courseName,
      YearGroup = assignment.YearGroup,
      DueDate = assignment.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      DueDateLabel = FormatShortDate(assignment.DueDate),
      Completed = progress.Completed,
      TotalQuestions = progress.Total,
      AwardsXp = XpService.IsXpEligible(assignment.DueDate, assignment.YearGroup),
      IsComplete = progress.Total > 0 && progress.Completed >= progress.Total,
      Href = $"/assignments/{Uri.EscapeDataString(courseId)}/year-{assignment.YearGroup}/{assignment.DueDate:yyyy-MM-dd}"
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(PartitionAssignmentData data, IEnumerable<int> studentIds, string dueDate, IEnumerable<int> pupilPremiumStudentIds = null)
  {
    if (string.IsNullOrWhiteSpace(dueDate)) return new AssignmentsProgressCell();
    var date = DateOnly.ParseExact(dueDate, "yyyy-MM-dd");
    if (!data.AssignmentsByDate.TryGetValue(date, out var assignment)) return new AssignmentsProgressCell { DueDate = dueDate };

    var completed = 0;
    var total = 0;
    foreach (var studentId in studentIds)
    {
      var progress = GetAssignmentProgress(assignment, data, studentId);
      completed += progress.Completed;
      total += progress.Total;
    }

    var pupilPremiumCompleted = 0;
    var pupilPremiumTotal = 0;
    if (pupilPremiumStudentIds is not null)
    {
      foreach (var studentId in pupilPremiumStudentIds)
      {
        var progress = GetAssignmentProgress(assignment, data, studentId);
        pupilPremiumCompleted += progress.Completed;
        pupilPremiumTotal += progress.Total;
      }
    }

    return new AssignmentsProgressCell
    {
      DueDate = dueDate,
      HasAssignment = true,
      Completed = completed,
      Total = total,
      PupilPremiumCompleted = pupilPremiumCompleted,
      PupilPremiumTotal = pupilPremiumTotal
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    PartitionAssignmentData data,
    IReadOnlyList<int> studentIds,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    IReadOnlyList<int> pupilPremiumStudentIds = null)
  {
    if (!data.AssignmentsByDate.TryGetValue(date.DueDate, out var assignment)) return new AssignmentsProgressCell { DueDate = date.Value };

    var completed = 0;
    var total = 0;
    foreach (var studentId in studentIds)
    {
      var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);
      completed += progress.Completed;
      total += progress.Total;
    }

    var pupilPremiumCompleted = 0;
    var pupilPremiumTotal = 0;
    if (pupilPremiumStudentIds is not null)
    {
      foreach (var studentId in pupilPremiumStudentIds)
      {
        var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);
        pupilPremiumCompleted += progress.Completed;
        pupilPremiumTotal += progress.Total;
      }
    }

    return new AssignmentsProgressCell
    {
      DueDate = date.Value,
      HasAssignment = true,
      Completed = completed,
      Total = total,
      PupilPremiumCompleted = pupilPremiumCompleted,
      PupilPremiumTotal = pupilPremiumTotal
    };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    string assignmentKey,
    IReadOnlyList<int> studentIds,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    IReadOnlyList<int> pupilPremiumStudentIds = null)
  {
    return assignmentKey is not null && partitionData.TryGetValue(assignmentKey, out var data)
      ? BuildAggregateCell(data, studentIds, date, progressCache, pupilPremiumStudentIds)
      : new AssignmentsProgressCell { DueDate = date.Value };
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<string, List<int>> studentIdsByPartition,
    string dueDate,
    IReadOnlyDictionary<string, List<int>> pupilPremiumStudentIdsByPartition = null)
  {
    var cell = new AssignmentsProgressCell { DueDate = dueDate };
    foreach (var entry in studentIdsByPartition)
    {
      if (!partitionData.TryGetValue(entry.Key, out var data)) continue;

      var partitionCell = BuildAggregateCell(data, entry.Value, dueDate, pupilPremiumStudentIdsByPartition?.GetValueOrDefault(entry.Key));
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
      cell.PupilPremiumCompleted += partitionCell.PupilPremiumCompleted;
      cell.PupilPremiumTotal += partitionCell.PupilPremiumTotal;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildAggregateCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<string, List<int>> studentIdsByPartition,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    IReadOnlyDictionary<string, List<int>> pupilPremiumStudentIdsByPartition = null)
  {
    var cell = new AssignmentsProgressCell { DueDate = date.Value };
    foreach (var entry in studentIdsByPartition)
    {
      if (!partitionData.TryGetValue(entry.Key, out var data)) continue;

      var partitionCell = BuildAggregateCell(data, entry.Value, date, progressCache, pupilPremiumStudentIdsByPartition?.GetValueOrDefault(entry.Key));
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
      cell.PupilPremiumCompleted += partitionCell.PupilPremiumCompleted;
      cell.PupilPremiumTotal += partitionCell.PupilPremiumTotal;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildStudentCell(PartitionAssignmentData data, int studentId, string dueDate)
  {
    if (string.IsNullOrWhiteSpace(dueDate)) return new AssignmentsProgressCell();
    var date = DateOnly.ParseExact(dueDate, "yyyy-MM-dd");
    if (!data.AssignmentsByDate.TryGetValue(date, out var assignment)) return new AssignmentsProgressCell { DueDate = dueDate };
    var progress = GetAssignmentProgress(assignment, data, studentId);

    return new AssignmentsProgressCell
    {
      DueDate = dueDate,
      HasAssignment = true,
      Completed = progress.Completed,
      Total = progress.Total
    };
  }

  private static AssignmentsProgressCell BuildStudentCell(
    PartitionAssignmentData data,
    int studentId,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    if (!data.AssignmentsByDate.TryGetValue(date.DueDate, out var assignment)) return new AssignmentsProgressCell { DueDate = date.Value };
    var progress = GetAssignmentProgress(assignment, data, studentId, progressCache);

    return new AssignmentsProgressCell
    {
      DueDate = date.Value,
      HasAssignment = true,
      Completed = progress.Completed,
      Total = progress.Total
    };
  }

  private static AssignmentsProgressCell BuildAggregateStudentCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IEnumerable<string> partitionKeys,
    int studentId,
    string dueDate)
  {
    var cell = new AssignmentsProgressCell { DueDate = dueDate };
    foreach (var partitionKey in partitionKeys)
    {
      if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

      var partitionCell = BuildStudentCell(data, studentId, dueDate);
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
    }

    return cell;
  }

  private static AssignmentsProgressCell BuildAggregateStudentCell(
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IEnumerable<string> partitionKeys,
    int studentId,
    StaffDateColumn date,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    var cell = new AssignmentsProgressCell { DueDate = date.Value };
    foreach (var partitionKey in partitionKeys)
    {
      if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

      var partitionCell = BuildStudentCell(data, studentId, date, progressCache);
      if (!partitionCell.HasAssignment) continue;

      cell.HasAssignment = true;
      cell.Completed += partitionCell.Completed;
      cell.Total += partitionCell.Total;
    }

    return cell;
  }

  private static List<AssignmentsStaffRow> BuildClassStudentRows(
    IEnumerable<User> students,
    SubjectClass cls,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyList<StaffDateColumn> dateColumns,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student => new AssignmentsStaffRow
      {
        Title = $"{student.LastName}, {student.FirstName}",
        PupilPremium = student.PupilPremium,
        Cells = dateColumns.Select(date =>
        {
          var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, date.YearGroupOffset);
          return assignmentKey is not null && partitionData.TryGetValue(assignmentKey, out var data)
            ? BuildStudentCell(data, student.Id, date, progressCache)
            : new AssignmentsProgressCell { DueDate = date.Value };
        }).ToList()
      })
      .ToList();
  }

  private static List<AssignmentsStaffRow> BuildAggregateStudentRows(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyList<StaffDateColumn> dateColumns,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<int, List<SubjectClass>> studentClassesById,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache,
    int yearGroup = 0)
  {
    return students
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(student =>
      {
        return new AssignmentsStaffRow
        {
          Title = $"{student.LastName}, {student.FirstName}",
          PupilPremium = student.PupilPremium,
          Cells = dateColumns.Select(date =>
          {
            var partitionKeys = GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup, date.YearGroupOffset);
            return BuildAggregateStudentCell(partitionData, partitionKeys, student.Id, date, progressCache);
          }).ToList()
        };
      })
      .Where(o => o.Cells.Any(cell => cell.HasAssignment))
      .ToList();
  }

  private static List<CompletionStudentRow> BuildCompletionStudentRows(IEnumerable<User> students, Func<User, AssignmentsProgressCell> buildCell)
  {
    return students
      .Select(student => new { Student = student, Cell = buildCell(student) })
      .Where(o => o.Cell.HasAssignment)
      .Select(o => new CompletionStudentRow
      {
        Name = o.Student.DisplayName,
        FirstName = o.Student.FirstName,
        LastName = o.Student.LastName,
        CompletedQuestions = o.Cell.Completed,
        TotalQuestions = o.Cell.Total,
        CompletionPercentage = GetCompletionPercentage(o.Cell.Completed, o.Cell.Total)
      })
      .OrderByDescending(o => o.CompletionPercentage)
      .ThenBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static Dictionary<string, List<int>> BuildStudentIdsByPartition(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    int yearGroup = 0,
    int yearGroupOffset = 0)
  {
    return students
      .SelectMany(student => GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, yearGroup, yearGroupOffset)
        .Select(partitionKey => new { partitionKey, student.Id }))
      .GroupBy(o => o.partitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Id).Distinct().ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static Dictionary<string, List<int>> BuildStudentIdsByPartition(
    IEnumerable<User> students,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<int, List<SubjectClass>> studentClassesById,
    int yearGroup = 0,
    int yearGroupOffset = 0)
  {
    return students
      .SelectMany(student => GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup, yearGroupOffset)
        .Select(partitionKey => new { partitionKey, student.Id }))
      .GroupBy(o => o.partitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.Id).Distinct().ToList(),
        StringComparer.OrdinalIgnoreCase);
  }

  private static List<string> GetStudentPartitionKeys(
    User student,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    int yearGroup = 0,
    int yearGroupOffset = 0)
  {
    return ParseClasses(student.Classes)
      .Where(o => yearGroup <= 0 || o.YearGroup == yearGroup)
      .Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode, yearGroupOffset))
      .Where(o => o is not null && partitionData.ContainsKey(o))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static List<string> GetStudentPartitionKeys(
    User student,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    IReadOnlyDictionary<int, List<SubjectClass>> studentClassesById,
    int yearGroup = 0,
    int yearGroupOffset = 0)
  {
    if (!studentClassesById.TryGetValue(student.Id, out var classes)) return [];

    return classes
      .Where(o => yearGroup <= 0 || o.YearGroup == yearGroup)
      .Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode, yearGroupOffset))
      .Where(o => o is not null && partitionData.ContainsKey(o))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static List<AssignmentsStaffQuestion> BuildQuestionSummaries(
    PartitionAssignmentData data,
    IReadOnlyList<int> studentIds,
    DateOnly dueDate,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    if (!data.QuestionsByDate.TryGetValue(dueDate, out var questions) || questions.Count == 0) return [];

    return questions
      .Select((question, index) => BuildQuestionSummary(data, question, index + 1, studentIds, progressCache))
      .ToList();
  }

  private static AssignmentsStaffQuestion BuildQuestionSummary(
    PartitionAssignmentData data,
    AssignmentQuestionEntity question,
    int questionNumber,
    IReadOnlyList<int> studentIds,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    var attempted = 0;
    var firstTimeCorrect = 0;
    var totalQuestions = data.QuestionsByDate.TryGetValue(question.DueDate, out var questions) ? questions.Count : 0;

    foreach (var studentId in studentIds)
    {
      if (!data.SubmissionsByStudentAndDate.TryGetValue((question.DueDate, studentId), out var submission)) continue;

      var progress = GetAssignmentQuestionProgress(submission, totalQuestions, progressCache);
      var entry = progress.FirstOrDefault(o => o.QuestionNumber == questionNumber);
      if (entry is null || entry.Attempts <= 0) continue;

      attempted++;
      if (entry.IsCorrect && entry.Attempts == 1)
      {
        firstTimeCorrect++;
      }
    }

    return new AssignmentsStaffQuestion
    {
      QuestionNumber = questionNumber,
      UnitTitle = question.UnitTitle ?? string.Empty,
      QuestionText = question.Question ?? string.Empty,
      CorrectAnswer = question.CorrectAnswer ?? string.Empty,
      IncorrectAnswers = [question.IncorrectAnswer1 ?? string.Empty, question.IncorrectAnswer2 ?? string.Empty, question.IncorrectAnswer3 ?? string.Empty],
      Attempted = attempted,
      FirstTimeCorrect = firstTimeCorrect,
      Percentage = GetCompletionPercentage(firstTimeCorrect, attempted)
    };
  }

  private static List<AssignmentProgressEntry> GetAssignmentQuestionProgress(
    AssignmentSubmissionEntity submission,
    int questionCount,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>> progressCache)
  {
    var key = (BuildAssignmentRowKey(submission.YearGroup, submission.CourseId), submission.DueDate, submission.StudentId);
    if (!progressCache.TryGetValue(key, out var progress))
    {
      progress = ParseProgress(submission.Progress, questionCount);
      progressCache[key] = progress;
    }

    return progress;
  }

  private static int GetAssignmentTotal(AssignmentEntity assignment) => Math.Max(assignment.Length, 0);

  private static AssignmentProgressTotals GetAssignmentProgress(AssignmentEntity assignment, PartitionAssignmentData data, int studentId)
  {
    var total = GetAssignmentTotal(assignment);
    data.SubmissionsByStudentAndDate.TryGetValue((assignment.DueDate, studentId), out var submission);
    return new AssignmentProgressTotals(Math.Min(submission?.Completed ?? 0, total), total);
  }

  private static AssignmentProgressTotals GetAssignmentProgress(
    AssignmentEntity assignment,
    PartitionAssignmentData data,
    int studentId,
    Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals> progressCache)
  {
    var key = (assignment.RowKey, assignment.DueDate, studentId);
    if (!progressCache.TryGetValue(key, out var progress))
    {
      progress = GetAssignmentProgress(assignment, data, studentId);
      progressCache[key] = progress;
    }

    return progress;
  }

  private static int GetCompletionPercentage(int completed, int total)
  {
    return total <= 0 ? 0 : (int)Math.Round(completed * 100d / total);
  }

}
