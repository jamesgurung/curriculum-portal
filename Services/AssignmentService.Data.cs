using Azure;
using Azure.Data.Tables;
using System.Text.Json;

namespace CurriculumPortal;

public partial class AssignmentService
{
  private static List<string> NormalizeKeys(IEnumerable<string> keys)
  {
    return keys
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .Select(o => o.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private List<DateOnly> GetVisibleDueDates(DateOnly today)
  {
    var upcomingDueDate = ResolveDueDate(GetNextMonday(today));
    var dueDates = new List<DateOnly> { upcomingDueDate };
    var candidateDueDate = upcomingDueDate.AddDays(-7);

    while (dueDates.Count < 5)
    {
      if (ResolveDueDate(candidateDueDate) == candidateDueDate)
      {
        dueDates.Add(candidateDueDate);
      }

      candidateDueDate = candidateDueDate.AddDays(-7);
    }

    return dueDates.OrderByDescending(o => o).ToList();
  }

  private static DateOnly GetNextMonday(DateOnly date)
  {
    var daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
    if (daysUntilMonday == 0) daysUntilMonday = 7;
    return date.AddDays(daysUntilMonday);
  }

  private async Task<(Dictionary<string, List<AssignmentEntity>> Assignments, Dictionary<string, List<AssignmentSubmissionEntity>> Submissions)> LoadStaffCompletionDataAsync(
    List<string> assignmentKeys,
    IEnumerable<DateOnly> dueDates,
    DateOnly upcomingDueDate,
    int academicYear)
  {
    var assignments = CreateBuckets<AssignmentEntity>(assignmentKeys);
    var submissions = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    var results = await Task.WhenAll(dueDates.Distinct().Select(dueDate => LoadStaffCompletionDateDataAsync(assignmentKeys, dueDate, upcomingDueDate, academicYear)));

    foreach (var result in results.OrderBy(o => o.DueDate))
    {
      AddBuckets(assignments, result.Assignments);
      AddBuckets(submissions, result.Submissions);
    }

    return (assignments, submissions);
  }

  private async Task<(DateOnly DueDate, Dictionary<string, List<AssignmentEntity>> Assignments, Dictionary<string, List<AssignmentSubmissionEntity>> Submissions)> LoadStaffCompletionDateDataAsync(
    List<string> assignmentKeys,
    DateOnly dueDate,
    DateOnly upcomingDueDate,
    int academicYear)
  {
    var cached = dueDate == upcomingDueDate ? null : await TryDownloadAssignmentCacheAsync<AssignmentCompletionCache>(BuildCompletionCacheBlobName(dueDate, academicYear));
    if (cached is not null)
    {
      var assignments = CreateBuckets<AssignmentEntity>(assignmentKeys);
      var submissions = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
      AddCompletionCacheToBuckets(cached, assignmentKeys, assignments, submissions);
      return (dueDate, assignments, submissions);
    }

    var assignmentsTask = LoadAssignmentsByDueDateAsync(assignmentKeys, dueDate);
    var submissionsTask = LoadSubmissionsByDueDateAsync(assignmentKeys, dueDate);
    await Task.WhenAll(assignmentsTask, submissionsTask);
    var dateAssignments = await assignmentsTask;
    var dateSubmissions = await submissionsTask;
    if (dueDate != upcomingDueDate)
    {
      await UploadAssignmentCacheAsync(BuildCompletionCacheBlobName(dueDate, academicYear), BuildCompletionCache(dueDate, dateAssignments, dateSubmissions));
    }

    return (dueDate, dateAssignments, dateSubmissions);
  }

  private async Task<Dictionary<string, List<AssignmentsStaffQuestion>>> LoadStaffQuestionSummariesAsync(
    List<string> partitionKeys,
    DateOnly dueDate,
    DateOnly upcomingDueDate,
    IReadOnlyList<SubjectClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    DateOnly currentDate)
  {
    var academicYear = ClassNameParser.GetAcademicYear(currentDate);
    var cached = dueDate == upcomingDueDate ? null : await TryDownloadAssignmentCacheAsync<AssignmentQuestionsCache>(BuildQuestionsCacheBlobName(dueDate, academicYear));
    if (cached is not null)
    {
      return new Dictionary<string, List<AssignmentsStaffQuestion>>(cached.Contexts, StringComparer.OrdinalIgnoreCase);
    }

    var liveCache = await BuildQuestionsCacheAsync(
      partitionKeys,
      dueDate,
      schoolClasses,
      classRosters,
      assignmentCourses,
      assignmentCourseYearGroups,
      coursesByKeyStageAndSubjectCode,
      partitionData,
      currentDate);

    if (dueDate != upcomingDueDate)
    {
      await UploadAssignmentCacheAsync(BuildQuestionsCacheBlobName(dueDate, academicYear), liveCache);
    }

    return new Dictionary<string, List<AssignmentsStaffQuestion>>(liveCache.Contexts, StringComparer.OrdinalIgnoreCase);
  }

  private async Task<AssignmentQuestionsCache> BuildQuestionsCacheAsync(
    List<string> partitionKeys,
    DateOnly dueDate,
    IReadOnlyList<SubjectClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    DateOnly currentDate)
  {
    var questionsByPartitionTask = LoadQuestionsByDueDateAsync(partitionKeys, dueDate);
    var submissionsByPartitionTask = LoadSubmissionsByDueDatesAsync(partitionKeys, [dueDate], true);
    await Task.WhenAll(questionsByPartitionTask, submissionsByPartitionTask);
    var questionsByPartition = await questionsByPartitionTask;
    var submissionsByPartition = await submissionsByPartitionTask;
    var assignmentsByPartition = CreateBuckets<AssignmentEntity>(partitionKeys);

    foreach (var partitionKey in partitionKeys)
    {
      if (partitionData.TryGetValue(partitionKey, out var data) && data.AssignmentsByDate.TryGetValue(dueDate, out var assignment))
      {
        assignmentsByPartition[partitionKey].Add(assignment);
      }
    }

    var questionData = BuildPartitionData(partitionKeys, assignmentsByPartition, submissionsByPartition, questionsByPartition);
    var contexts = BuildQuestionContexts(schoolClasses, classRosters, assignmentCourses, assignmentCourseYearGroups, coursesByKeyStageAndSubjectCode, partitionData, dueDate, currentDate);
    var progressCache = new Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), List<AssignmentProgressEntry>>();
    var generatedAt = RoundedNow();
    var cache = new AssignmentQuestionsCache
    {
      DueDate = FormatDate(dueDate),
      GeneratedAtUtc = generatedAt,
      ExpiresAtUtc = generatedAt.Add(StaffAssignmentCacheLifetime)
    };

    foreach (var context in contexts)
    {
      cache.Contexts[context.Key] = questionData.TryGetValue(context.AssignmentKey, out var data)
        ? BuildQuestionSummaries(data, context.StudentIds, dueDate, progressCache)
        : [];
    }

    return cache;
  }

  private static List<AssignmentQuestionContext> BuildQuestionContexts(
    IReadOnlyList<SubjectClass> schoolClasses,
    IReadOnlyDictionary<string, List<User>> classRosters,
    IReadOnlyList<CourseEntity> assignmentCourses,
    IReadOnlyDictionary<string, HashSet<int>> assignmentCourseYearGroups,
    IReadOnlyDictionary<string, CourseEntity> coursesByKeyStageAndSubjectCode,
    IReadOnlyDictionary<string, PartitionAssignmentData> partitionData,
    DateOnly dueDate,
    DateOnly currentDate)
  {
    var contexts = new List<AssignmentQuestionContext>();
    var yearGroupOffset = ClassNameParser.GetAcademicYear(dueDate) - ClassNameParser.GetAcademicYear(currentDate);

    foreach (var cls in schoolClasses)
    {
      var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, yearGroupOffset);
      if (assignmentKey is null
        || !partitionData.TryGetValue(assignmentKey, out var data)
        || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      contexts.Add(new AssignmentQuestionContext(BuildClassQuestionCacheKey(cls.Name), assignmentKey, roster.Select(o => o.Id).ToList()));
    }

    foreach (var course in assignmentCourses)
    {
      if (!assignmentCourseYearGroups.TryGetValue(course.RowKey, out var courseYearGroups) || courseYearGroups.Count == 0) continue;

      foreach (var yearGroup in courseYearGroups.Order())
      {
        var partitionKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
        if (!partitionData.TryGetValue(partitionKey, out var data) || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

        var studentIds = schoolClasses
          .Where(o => o.YearGroup + yearGroupOffset == yearGroup && o.SubjectCode.Equals(course.SubjectCode, StringComparison.OrdinalIgnoreCase))
          .SelectMany(cls => classRosters.TryGetValue(cls.Name, out var roster) ? roster : [])
          .Select(o => o.Id)
          .Distinct()
          .ToList();
        contexts.Add(new AssignmentQuestionContext(BuildCourseQuestionCacheKey(partitionKey), partitionKey, studentIds));
      }
    }

    return contexts
      .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
      .Select(o => o.First())
      .ToList();
  }

  private static AssignmentCompletionCache BuildCompletionCache(
    DateOnly dueDate,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissionsByPartition)
  {
    var generatedAt = RoundedNow();
    var cache = new AssignmentCompletionCache
    {
      DueDate = FormatDate(dueDate),
      GeneratedAtUtc = generatedAt,
      ExpiresAtUtc = generatedAt.Add(StaffAssignmentCacheLifetime)
    };

    foreach (var assignment in assignmentsByPartition.Values.SelectMany(o => o).OrderBy(o => o.RowKey, StringComparer.OrdinalIgnoreCase))
    {
      submissionsByPartition.TryGetValue(assignment.RowKey, out var submissions);
      cache.Assignments.Add(new AssignmentCompletionCacheItem
      {
        AssignmentKey = assignment.RowKey,
        Length = assignment.Length,
        Students = (submissions ?? [])
          .OrderBy(o => o.StudentId)
          .Select(o => new AssignmentCompletionStudentCacheItem
          {
            StudentId = o.StudentId,
            Completed = o.Completed
          })
          .ToList()
      });
    }

    return cache;
  }

  private static void AddCompletionCacheToBuckets(
    AssignmentCompletionCache cache,
    List<string> assignmentKeys,
    Dictionary<string, List<AssignmentEntity>> assignments,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissions)
  {
    var allowedKeys = assignmentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var item in cache.Assignments.Where(o => allowedKeys.Contains(o.AssignmentKey)))
    {
      var assignment = new AssignmentEntity
      {
        PartitionKey = cache.DueDate,
        RowKey = item.AssignmentKey,
        Length = item.Length
      };
      assignments[item.AssignmentKey].Add(assignment);

      foreach (var student in item.Students)
      {
        submissions[item.AssignmentKey].Add(new AssignmentSubmissionEntity
        {
          PartitionKey = cache.DueDate,
          RowKey = BuildSubmissionRowKey(student.StudentId, assignment.YearGroup, assignment.CourseId),
          Completed = student.Completed
        });
      }
    }
  }

  private async Task<T> TryDownloadAssignmentCacheAsync<T>(string blobName) where T : AssignmentCacheFile
  {
    try
    {
      var response = await _cacheClient.GetBlobClient(blobName).DownloadContentAsync();
      var cache = JsonSerializer.Deserialize<T>(response.Value.Content.ToString(), JsonDefaults.CamelCase);
      return cache is not null && cache.ExpiresAtUtc > DateTimeOffset.UtcNow ? cache : null;
    }
    catch (RequestFailedException ex) when (ex.Status == 404)
    {
      return null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private async Task UploadAssignmentCacheAsync<T>(string blobName, T cache) where T : AssignmentCacheFile
  {
    var blobClient = _cacheClient.GetBlobClient(blobName);
    var binaryData = new BinaryData(JsonSerializer.Serialize(cache, JsonDefaults.CamelCase));
    await blobClient.UploadAsync(binaryData, overwrite: true);
  }

  private static void AddBuckets<T>(Dictionary<string, List<T>> target, Dictionary<string, List<T>> source)
  {
    foreach (var item in source)
    {
      if (target.TryGetValue(item.Key, out var bucket))
      {
        bucket.AddRange(item.Value);
      }
    }
  }

  private static List<AssignmentsStaffQuestion> GetQuestionSummaries(Dictionary<string, List<AssignmentsStaffQuestion>> summariesByContext, string key)
    => summariesByContext.TryGetValue(key, out var summaries) ? summaries : [];

  private static string BuildCompletionCacheBlobName(DateOnly dueDate, int academicYear) => $"{FormatDate(dueDate)}-{academicYear}-completion.json";

  private static string BuildQuestionsCacheBlobName(DateOnly dueDate, int academicYear) => $"{FormatDate(dueDate)}-{academicYear}-questions.json";

  private static string BuildClassQuestionCacheKey(string className) => $"class:{className}";

  private static string BuildCourseQuestionCacheKey(string assignmentKey) => $"course:{assignmentKey}";

  private static DateTimeOffset RoundedNow()
  {
    var now = DateTimeOffset.UtcNow;
    return now.AddTicks(-now.Ticks % TimeSpan.TicksPerSecond);
  }

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
    => await LoadAssignmentsByDueDatesAsync(assignmentKeys, [dueDate]);

  private async Task<Dictionary<string, List<AssignmentEntity>>> LoadAssignmentsByDueDatesAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates)
  {
    var buckets = CreateBuckets<AssignmentEntity>(assignmentKeys);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _assignmentsClient,
        BuildPartitionKeyFilter(FormatDate(dueDate)),
        ["PartitionKey", "RowKey", "Length"],
        buckets,
        entity => entity.RowKey);
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadStudentSubmissionsAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates, int studentId)
  {
    var buckets = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    var prefix = BuildStudentSubmissionRowKeyPrefix(studentId);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _submissionsClient,
        $"{BuildPartitionKeyFilter(FormatDate(dueDate))} and {BuildRowKeyPrefixFilter(prefix)}",
        ["PartitionKey", "RowKey", "Completed"],
        buckets,
        entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadSubmissionsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
    => await LoadSubmissionsByDueDatesAsync(assignmentKeys, [dueDate]);

  private async Task<Dictionary<string, List<AssignmentSubmissionEntity>>> LoadSubmissionsByDueDatesAsync(List<string> assignmentKeys, IEnumerable<DateOnly> dueDates, bool includeProgress = false)
  {
    var buckets = CreateBuckets<AssignmentSubmissionEntity>(assignmentKeys);
    foreach (var dueDate in dueDates.Distinct().OrderBy(o => o))
    {
      await AddQueryResultsAsync(
        _submissionsClient,
        BuildPartitionKeyFilter(FormatDate(dueDate)),
        includeProgress ? ["PartitionKey", "RowKey", "Completed", "Progress"] : ["PartitionKey", "RowKey", "Completed"],
        buckets,
        entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    }

    return buckets;
  }

  private async Task<Dictionary<string, List<AssignmentQuestionEntity>>> LoadQuestionsByDueDateAsync(List<string> assignmentKeys, DateOnly dueDate)
  {
    var buckets = CreateBuckets<AssignmentQuestionEntity>(assignmentKeys);
    await AddQueryResultsAsync(
      _questionsClient,
      BuildPartitionKeyFilter(FormatDate(dueDate)),
      ["PartitionKey", "RowKey", "Question", "CorrectAnswer", "IncorrectAnswer1", "IncorrectAnswer2", "IncorrectAnswer3", "UnitTitle"],
      buckets,
      entity => BuildAssignmentRowKey(entity.YearGroup, entity.CourseId));
    return buckets;
  }

  private static Dictionary<string, PartitionAssignmentData> BuildPartitionData(
    List<string> partitionKeys,
    Dictionary<string, List<AssignmentEntity>> assignmentsByPartition,
    Dictionary<string, List<AssignmentSubmissionEntity>> submissionsByPartition,
    Dictionary<string, List<AssignmentQuestionEntity>> questionsByPartition = null)
  {
    var results = new Dictionary<string, PartitionAssignmentData>(partitionKeys.Count, StringComparer.OrdinalIgnoreCase);

    foreach (var partitionKey in partitionKeys)
    {
      assignmentsByPartition.TryGetValue(partitionKey, out var assignments);
      submissionsByPartition.TryGetValue(partitionKey, out var submissions);
      List<AssignmentQuestionEntity> questions = null;
      questionsByPartition?.TryGetValue(partitionKey, out questions);
      assignments ??= [];
      submissions ??= [];
      questions ??= [];

      results[partitionKey] = new PartitionAssignmentData
      {
        AssignmentsByDate = assignments
          .OrderBy(o => o.DueDate)
          .ToDictionary(o => o.DueDate, o => o),
        SubmissionsByStudentAndDate = submissions.ToDictionary(o => (o.DueDate, o.StudentId), o => o),
        QuestionsByDate = questions
          .GroupBy(o => o.DueDate)
          .ToDictionary(
            g => g.Key,
            g => g.OrderBy(o => o.QuestionNumber).ToList())
      };
    }

    return results;
  }

  private static async Task AddQueryResultsAsync<T>(TableClient client, string filter, IEnumerable<string> select, Dictionary<string, List<T>> buckets, Func<T, string> getBucketKey)
    where T : class, ITableEntity, new()
  {
    await foreach (var entity in client.QueryAsync<T>(filter: filter, select: select))
    {
      var bucketKey = getBucketKey(entity);
      if (!string.IsNullOrEmpty(bucketKey) && buckets.TryGetValue(bucketKey, out var bucket))
      {
        bucket.Add(entity);
      }
    }
  }

  private static Dictionary<string, List<T>> CreateBuckets<T>(IReadOnlyList<string> partitionKeys)
  {
    return partitionKeys.ToDictionary(o => o, _ => new List<T>(), StringComparer.OrdinalIgnoreCase);
  }

  private static string BuildPartitionKeyFilter(string partitionKey) => $"PartitionKey eq '{EscapeODataValue(partitionKey)}'";

  private static string BuildPartitionKeyLessThanFilter(string partitionKeyExclusiveUpperBound) => $"PartitionKey lt '{EscapeODataValue(partitionKeyExclusiveUpperBound)}'";

  private static string BuildRowKeyPrefixFilter(string prefix) => $"RowKey ge '{EscapeODataValue(prefix)}' and RowKey lt '{EscapeODataValue(prefix + "~")}'";

  private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
