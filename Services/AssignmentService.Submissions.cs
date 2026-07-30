using Azure;

namespace CurriculumPortal;

public partial class AssignmentService
{
  private async Task<StudentAssignmentContext> LoadStudentAssignmentContextAsync(User student, CourseEntity course, int yearGroup, DateOnly dueDate, string className)
  {
    if (course.KeyStage != GetKeyStage(yearGroup)) return null;

    var dueDateText = FormatDate(dueDate);
    var assignmentKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
    var assignment = await _assignmentsClient.GetEntityIfExistsAsync<AssignmentEntity>(dueDateText, assignmentKey);
    if (!assignment.HasValue) return null;

    var questionPrefix = BuildQuestionRowKeyPrefix(yearGroup, course.RowKey);
    var questions = (await _questionsClient.QueryAsync<AssignmentQuestionEntity>(
      filter: $"{BuildPartitionKeyFilter(dueDateText)} and {BuildRowKeyPrefixFilter(questionPrefix)}").ToListAsync())
      .OrderBy(question => question.QuestionNumber).ToList();

    if (questions.Count == 0) return null;

    return new StudentAssignmentContext
    {
      DueDateText = dueDateText,
      Questions = questions,
      Submission = await GetOrCreateSubmissionAsync(student, yearGroup, course.RowKey, dueDateText, className, questions)
    };
  }

  private async Task<AssignmentSubmissionEntity> GetOrCreateSubmissionAsync(User student, int yearGroup, string courseId, string dueDateText, string className, IReadOnlyList<AssignmentQuestionEntity> questions)
  {
    var rowKey = BuildSubmissionRowKey(student.Id, yearGroup, courseId);
    var existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(dueDateText, rowKey);
    if (existing.HasValue) return existing.Value;

    var submission = new AssignmentSubmissionEntity
    {
      PartitionKey = dueDateText,
      RowKey = rowKey,
      ClassName = className,
      Progress = BuildInitialProgress(questions),
      Completed = 0,
      LockedUntil = DateTimeOffset.UtcNow
    };

    try
    {
      await _submissionsClient.AddEntityAsync(submission);
      return await ReloadSubmissionAsync(dueDateText, rowKey);
    }
    catch (RequestFailedException ex) when (ex.Status == 409)
    {
      existing = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(dueDateText, rowKey);
      if (existing.HasValue) return existing.Value;
      throw;
    }
  }

  private async Task<AssignmentSubmissionEntity> ReloadSubmissionAsync(string partitionKey, string rowKey)
  {
    var submission = await _submissionsClient.GetEntityIfExistsAsync<AssignmentSubmissionEntity>(partitionKey, rowKey);
    if (submission.HasValue) return submission.Value;
    throw new InvalidOperationException("The submission could not be reloaded.");
  }

  private static string BuildInitialProgress(IReadOnlyList<AssignmentQuestionEntity> questions)
  {
    var questionNumbers = Enumerable.Range(1, questions.Count).ToArray();
    Shuffle(questionNumbers);
    return string.Join(";", questionNumbers.Select(questionNumber => $"{questionNumber},{CreateAnswerOrder()},0,0"));
  }

  private static string BuildProgress(IEnumerable<AssignmentProgressEntry> entries)
  {
    return string.Join(";", entries.Select(entry => $"{entry.QuestionNumber},{entry.AnswerOrder},{entry.Attempts},{(entry.IsCorrect ? 1 : 0)}"));
  }

  private static string NormalizeQuestionText(string question)
  {
    return (question ?? string.Empty).Trim();
  }

  private static void EnsureSubmissionDelaySatisfied(AssignmentSubmissionEntity submission)
  {
    ArgumentNullException.ThrowIfNull(submission);

    if (submission.LockedUntil > DateTimeOffset.UtcNow)
    {
      throw new TooManyRequestsException("Please wait before submitting another answer.");
    }
  }

  private static string CreateAnswerOrder()
  {
    var digits = new[] { '0', '1', '2', '3' };
    Shuffle(digits);
    return new string(digits);
  }

  private static string CreateAnswerOrder(string currentOrder)
  {
    string nextOrder;
    do
    {
      nextOrder = CreateAnswerOrder();
    }
    while (string.Equals(nextOrder, currentOrder, StringComparison.Ordinal));

    return nextOrder;
  }

  private static AssignmentQuestionDto LoadNextQuestion(string progress, List<AssignmentQuestionEntity> questions)
  {
    var entries = ParseProgress(progress, questions.Count);
    var nextEntry = GetCurrentQueueEntry(entries);
    if (nextEntry is null) return null;

    var question = questions[nextEntry.QuestionNumber - 1];

    return new AssignmentQuestionDto
    {
      QuestionNumber = nextEntry.QuestionNumber,
      CourseId = question.CourseId,
      UnitId = question.UnitId ?? string.Empty,
      UnitTitle = question.UnitTitle ?? string.Empty,
      QuestionText = question.Question,
      Answers = BuildAnswers(question, nextEntry.AnswerOrder)
    };
  }

  private static AssignmentProgressEntry GetCurrentQueueEntry(List<AssignmentProgressEntry> entries)
  {
    if (entries.Count == 0) return null;

    var incompleteCount = GetIncompletePrefixCount(entries);
    if (incompleteCount == 0) return null;

    return entries[0];
  }

  private static AssignmentProgressEntry UpdateQueueAfterAnswer(List<AssignmentProgressEntry> entries, bool isCorrect)
  {
    var entry = GetCurrentQueueEntry(entries) ?? throw new InvalidOperationException("This assignment has already been completed.");

    entries.RemoveAt(0);

    var updatedEntry = entry with
    {
      AnswerOrder = isCorrect ? entry.AnswerOrder : CreateAnswerOrder(entry.AnswerOrder),
      Attempts = entry.Attempts + 1,
      IsCorrect = isCorrect
    };

    if (updatedEntry.IsCorrect)
    {
      entries.Add(updatedEntry);
    }
    else
    {
      entries.Insert(GetIncorrectInsertIndex(GetIncompletePrefixCount(entries)), updatedEntry);
    }

    return updatedEntry;
  }

  private static int GetIncompletePrefixCount(List<AssignmentProgressEntry> entries)
  {
    var incompleteCount = 0;
    var seenCorrect = false;

    foreach (var entry in entries)
    {
      if (entry.IsCorrect)
      {
        seenCorrect = true;
        continue;
      }

      if (seenCorrect)
      {
        throw new InvalidOperationException("Assignment progress is invalid.");
      }

      incompleteCount++;
    }

    return incompleteCount;
  }

  private static int GetIncorrectInsertIndex(int remainingIncompleteCount)
  {
    if (remainingIncompleteCount <= 0) return 0;
    if (remainingIncompleteCount == 1) return 1;
    return Random.Shared.Next(2, remainingIncompleteCount + 1);
  }

  private static int GetCorrectAnswerIndex(string answerOrder)
  {
    var index = answerOrder.IndexOf('0', StringComparison.Ordinal);
    if (index < 0) throw new InvalidOperationException("Assignment answer order is invalid.");
    return index;
  }

  private static List<AssignmentProgressEntry> ParseProgress(string progress, int questionCount)
  {
    var entries = string.IsNullOrWhiteSpace(progress)
      ? [] : progress.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseProgressEntry).ToList();

    if (entries.Count != questionCount || entries.Any(entry => entry.QuestionNumber < 1 || entry.QuestionNumber > questionCount) ||
      entries.Select(entry => entry.QuestionNumber).Distinct().Count() != questionCount)
    {
      throw new InvalidOperationException("Assignment progress does not match question count.");
    }

    return entries;
  }

  private static AssignmentProgressEntry ParseProgressEntry(string value)
  {
    var parts = value.Split(',', StringSplitOptions.TrimEntries);
    if (parts.Length != 4
      || !int.TryParse(parts[0], out var questionNumber)
      || parts[1].Length != 4
      || !parts[1].All(ch => ch is >= '0' and <= '3')
      || parts[1].Distinct().Count() != 4
      || !int.TryParse(parts[2], out var attempts)
      || attempts < 0
      || !int.TryParse(parts[3], out var isCorrectValue)
      || isCorrectValue is < 0 or > 1)
    {
      throw new InvalidOperationException("Assignment progress is invalid.");
    }

    return new AssignmentProgressEntry(questionNumber, parts[1], attempts, isCorrectValue == 1);
  }

  private static void Shuffle<T>(T[] values)
  {
    for (var index = values.Length - 1; index > 0; index--)
    {
      var swapIndex = Random.Shared.Next(index + 1);
      (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
    }
  }

  private static string[] BuildAnswers(AssignmentQuestionEntity question, string answerOrder)
  {
    return answerOrder.Select(digit => digit switch
    {
      '0' => question.CorrectAnswer,
      '1' => question.IncorrectAnswer1,
      '2' => question.IncorrectAnswer2,
      '3' => question.IncorrectAnswer3,
      _ => throw new InvalidOperationException("Assignment answer order is invalid.")
    }).ToArray();
  }
}
