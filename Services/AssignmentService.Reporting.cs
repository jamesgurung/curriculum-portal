using System.Globalization;

namespace CurriculumPortal;

public partial class AssignmentService
{
  public async Task<AssignmentsStaffData> GetStaffAssignmentsAsync(User teacher)
  {
    ArgumentNullException.ThrowIfNull(teacher);

    var coursesTask = _courseService.ListCoursesAsync();
    var unitsTask = _courseService.ListUnitsAsync();
    await Task.WhenAll(coursesTask, unitsTask);
    var courses = await coursesTask;
    var assignmentCourses = courses
      .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
    if (assignmentCourses.Count == 0) return new AssignmentsStaffData();
    var assignmentCourseIds = assignmentCourses
      .Select(o => o.RowKey)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var assignmentCourseYearGroups = (await unitsTask)
      .Where(o => assignmentCourseIds.Contains(o.PartitionKey))
      .GroupBy(o => o.PartitionKey, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(
        g => g.Key,
        g => g.Select(o => o.YearGroup).ToHashSet(),
        StringComparer.OrdinalIgnoreCase);
    var assignmentSubjectCodes = assignmentCourses
      .Select(o => o.SubjectCode)
      .Where(o => !string.IsNullOrWhiteSpace(o))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var coursesByKeyStageAndSubjectCode = BuildCoursesByKeyStageAndSubjectCode(assignmentCourses);

    var teacherClasses = ParseClasses(teacher.Classes)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var studentClassesById = _config.Students.ToDictionary(student => student.Id, student => ParseClasses(student.Classes));
    var classRosters = BuildClassRosters(studentClassesById);
    var tutorGroupRosters = BuildTutorGroupRosters();
    var schoolClasses = studentClassesById.Values
      .SelectMany(o => o)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .DistinctBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .OrderBy(o => o.YearGroup)
      .ThenBy(o => o.SubjectCode, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var visibleDueDates = GetVisibleDueDates(today);
    var upcomingDueDate = visibleDueDates.First();
    var questionDueDate = visibleDueDates.Where(o => o <= today).DefaultIfEmpty(visibleDueDates.Last()).Max();
    var questionsTitle = $"Questions ({FormatShortDate(questionDueDate)})";
    var staffDateColumns = visibleDueDates
      .Take(5)
      .Select(date =>
      {
        var value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new StaffDateColumn(date, value, new AssignmentsDateColumn
        {
          Value = value,
          Label = FormatShortDate(date)
        }, ClassNameParser.GetAcademicYear(date) - ClassNameParser.GetAcademicYear(today));
      }).ToList();
    var relevantPartitions = teacherClasses
      .Concat(schoolClasses)
      .SelectMany(cls => staffDateColumns.Select(date => GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, date.YearGroupOffset)))
      .Where(o => o is not null)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (relevantPartitions.Count == 0) return new AssignmentsStaffData();

    var partitionKeys = NormalizeKeys(relevantPartitions);
    var dateColumns = staffDateColumns.Select(o => o.Column).ToList();
    var academicYear = ClassNameParser.GetAcademicYear(today);
    var completionData = await LoadStaffCompletionDataAsync(partitionKeys, visibleDueDates, upcomingDueDate, academicYear);
    var partitionData = BuildPartitionData(partitionKeys, completionData.Assignments, completionData.Submissions);
    var questionSummariesByContext = await LoadStaffQuestionSummariesAsync(
      partitionKeys,
      questionDueDate,
      upcomingDueDate,
      schoolClasses,
      classRosters,
      assignmentCourses,
      assignmentCourseYearGroups,
      coursesByKeyStageAndSubjectCode,
      partitionData,
      today);
    var progressCache = new Dictionary<(string AssignmentKey, DateOnly DueDate, int StudentId), AssignmentProgressTotals>();

    var details = new List<AssignmentsStaffDetail>();
    var classRowsByName = new Dictionary<string, AssignmentsStaffRow>(StringComparer.OrdinalIgnoreCase);
    var classDetailIndex = 0;
    foreach (var cls in schoolClasses)
    {
      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      var studentIds = roster.Select(o => o.Id).ToList();
      var pupilPremiumStudentIds = roster.Where(o => o.PupilPremium).Select(o => o.Id).ToList();
      var cells = staffDateColumns.Select(date => BuildAggregateCell(
        partitionData,
        GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, date.YearGroupOffset),
        studentIds,
        date,
        progressCache,
        pupilPremiumStudentIds)).ToList();
      if (!cells.Any(o => o.HasAssignment)) continue;

      var detailId = $"class-{++classDetailIndex}";

      classRowsByName[cls.Name] = new AssignmentsStaffRow
      {
        Title = cls.Name,
        DetailId = detailId,
        Cells = cells
      };

      details.Add(new AssignmentsStaffDetail
      {
        Id = detailId,
        Title = cls.Name,
        FirstColumnTitle = "Student",
        Rows = BuildClassStudentRows(roster, cls, partitionData, staffDateColumns, coursesByKeyStageAndSubjectCode, progressCache),
        QuestionsTitle = questionsTitle,
        Questions = GetQuestionSummaries(questionSummariesByContext, BuildClassQuestionCacheKey(cls.Name))
      });
    }

    var classRows = teacherClasses
      .Where(o => classRowsByName.ContainsKey(o.Name))
      .Select(o => classRowsByName[o.Name])
      .ToList();

    var yearGroupRows = new List<AssignmentsStaffRow>();
    var yearGroupDetailIndex = 0;
    var tutorGroupDetailIndex = 0;
    foreach (var yearGroup in tutorGroupRosters
      .Where(o => ClassNameParser.GetLeadingNumber(o.Key) > 0)
      .GroupBy(o => ClassNameParser.GetLeadingNumber(o.Key))
      .OrderBy(o => o.Key))
    {
      var yearStudents = yearGroup
        .SelectMany(o => o.Value)
        .DistinctBy(o => o.Id)
        .ToList();
      var yearCells = staffDateColumns.Select(date =>
      {
        var studentIdsByPartition = BuildStudentIdsByPartition(yearStudents, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key, date.YearGroupOffset);
        var pupilPremiumStudentIdsByPartition = BuildStudentIdsByPartition(yearStudents.Where(o => o.PupilPremium), coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key, date.YearGroupOffset);
        return BuildAggregateCell(partitionData, studentIdsByPartition, date, progressCache, pupilPremiumStudentIdsByPartition);
      }).ToList();
      if (!yearCells.Any(o => o.HasAssignment)) continue;

      var tutorGroupRows = new List<AssignmentsStaffRow>();
      foreach (var tutorGroup in yearGroup.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase))
      {
        var tutorStudents = tutorGroup.Value;
        var tutorCells = staffDateColumns.Select(date =>
        {
          var studentIdsByPartition = BuildStudentIdsByPartition(tutorStudents, coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key, date.YearGroupOffset);
          var pupilPremiumStudentIdsByPartition = BuildStudentIdsByPartition(tutorStudents.Where(o => o.PupilPremium), coursesByKeyStageAndSubjectCode, partitionData, studentClassesById, yearGroup.Key, date.YearGroupOffset);
          return BuildAggregateCell(partitionData, studentIdsByPartition, date, progressCache, pupilPremiumStudentIdsByPartition);
        }).ToList();
        if (!tutorCells.Any(o => o.HasAssignment)) continue;

        var tutorDetailId = $"tutor-{++tutorGroupDetailIndex}";
        tutorGroupRows.Add(new AssignmentsStaffRow
        {
          Title = tutorGroup.Key,
          DetailId = tutorDetailId,
          Cells = tutorCells
        });

        details.Add(new AssignmentsStaffDetail
        {
          Id = tutorDetailId,
          Title = tutorGroup.Key,
          FirstColumnTitle = "Student",
          Rows = BuildAggregateStudentRows(tutorStudents, partitionData, staffDateColumns, coursesByKeyStageAndSubjectCode, studentClassesById, progressCache, yearGroup.Key)
        });
      }

      if (tutorGroupRows.Count == 0) continue;

      var yearDetailId = $"year-{++yearGroupDetailIndex}";
      yearGroupRows.Add(new AssignmentsStaffRow
      {
        Title = $"Year {yearGroup.Key}",
        DetailId = yearDetailId,
        Cells = yearCells
      });

      details.Add(new AssignmentsStaffDetail
      {
        Id = yearDetailId,
        Title = $"Year {yearGroup.Key}",
        FirstColumnTitle = "Tutor Group",
        ClickableRows = true,
        Rows = tutorGroupRows
      });
    }

    var courseRows = new List<AssignmentsStaffRow>();
    var courseDetailIndex = 0;
    foreach (var course in assignmentCourses)
    {
      if (!assignmentCourseYearGroups.TryGetValue(course.RowKey, out var courseYearGroups) || courseYearGroups.Count == 0) continue;

      foreach (var yearGroup in courseYearGroups.Order())
      {
        var partitionKey = BuildAssignmentRowKey(yearGroup, course.RowKey);
        if (!partitionData.TryGetValue(partitionKey, out var data)) continue;

        var courseClasses = schoolClasses
          .Where(o => o.SubjectCode.Equals(course.SubjectCode, StringComparison.OrdinalIgnoreCase))
          .Where(o => staffDateColumns.Any(date => o.YearGroup + date.YearGroupOffset == yearGroup))
          .Where(o => classRowsByName.ContainsKey(o.Name))
          .ToList();
        if (courseClasses.Count == 0) continue;

        var cells = staffDateColumns.Select(date =>
        {
          var students = courseClasses
            .Where(cls => cls.YearGroup + date.YearGroupOffset == yearGroup)
            .SelectMany(cls => classRosters.TryGetValue(cls.Name, out var roster) ? roster : [])
            .DistinctBy(o => o.Id)
            .ToList();
          return BuildAggregateCell(
            data,
            students.Select(o => o.Id).ToList(),
            date,
            progressCache,
            students.Where(o => o.PupilPremium).Select(o => o.Id).ToList());
        }).ToList();
        if (!cells.Any(o => o.HasAssignment)) continue;

        var title = $"{course.Name} – Year {yearGroup}";
        var detailId = $"course-{++courseDetailIndex}";
        courseRows.Add(new AssignmentsStaffRow
        {
          Title = title,
          DetailId = detailId,
          Cells = cells
        });

        details.Add(new AssignmentsStaffDetail
        {
          Id = detailId,
          Title = title,
          FirstColumnTitle = "Class",
          ClickableRows = true,
          Rows = courseClasses.Select(cls =>
          {
            classRosters.TryGetValue(cls.Name, out var roster);
            roster ??= [];
            var classRow = classRowsByName[cls.Name];
            return new AssignmentsStaffRow
            {
              Title = classRow.Title,
              DetailId = classRow.DetailId,
              Cells = staffDateColumns.Select(date => cls.YearGroup + date.YearGroupOffset == yearGroup
                ? BuildAggregateCell(data, roster.Select(o => o.Id).ToList(), date, progressCache, roster.Where(o => o.PupilPremium).Select(o => o.Id).ToList())
                : new AssignmentsProgressCell { DueDate = date.Value }).ToList()
            };
          }).ToList(),
          QuestionsTitle = questionsTitle,
          Questions = GetQuestionSummaries(questionSummariesByContext, BuildCourseQuestionCacheKey(partitionKey))
        });
      }
    }

    return new AssignmentsStaffData
    {
      Dates = dateColumns,
      Classes = classRows,
      YearGroups = yearGroupRows,
      Courses = courseRows,
      Details = details
    };
  }

  public async Task<WeeklyCompletionReports> GetWeeklyCompletionReportsAsync(DateOnly dueDate)
  {
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var yearGroupOffset = ClassNameParser.GetAcademicYear(dueDate) - ClassNameParser.GetAcademicYear(today);
    var reports = new WeeklyCompletionReports
    {
      DueDate = dueDate,
      DueDateLabel = FormatLongDate(dueDate)
    };

    var assignmentCourses = (await _courseService.ListCoursesAsync())
      .Where(o => !string.IsNullOrWhiteSpace(o.SubjectCode))
      .ToList();
    var assignmentSubjectCodes = assignmentCourses
      .Select(o => o.SubjectCode)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (assignmentSubjectCodes.Count == 0) return reports;

    var coursesByKeyStageAndSubjectCode = BuildCoursesByKeyStageAndSubjectCode(assignmentCourses);
    var classRosters = BuildClassRosters();
    var tutorGroupRosters = BuildTutorGroupRosters();
    var schoolClasses = ParseClasses(classRosters.Keys)
      .Where(o => assignmentSubjectCodes.Contains(o.SubjectCode))
      .ToList();
    var partitionKeys = NormalizeKeys(schoolClasses.Select(o => GetAssignmentKey(o, coursesByKeyStageAndSubjectCode, yearGroupOffset)).Where(o => o is not null));
    if (partitionKeys.Count == 0) return reports;

    var assignmentsByPartition = await LoadAssignmentsByDueDateAsync(partitionKeys, dueDate);
    if (assignmentsByPartition.Values.All(o => o.Count == 0)) return reports;

    var submissionsByPartition = await LoadSubmissionsByDueDateAsync(partitionKeys, dueDate);

    var partitionData = BuildPartitionData(partitionKeys, assignmentsByPartition, submissionsByPartition);
    var dueDateText = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var classReportsByName = new Dictionary<string, ClassCompletionReport>(StringComparer.OrdinalIgnoreCase);

    foreach (var cls in schoolClasses)
    {
      var assignmentKey = GetAssignmentKey(cls, coursesByKeyStageAndSubjectCode, yearGroupOffset);
      if (assignmentKey is null || !partitionData.TryGetValue(assignmentKey, out var data) || !data.AssignmentsByDate.ContainsKey(dueDate)) continue;

      classRosters.TryGetValue(cls.Name, out var roster);
      roster ??= [];
      var students = BuildCompletionStudentRows(roster, student => BuildStudentCell(data, student.Id, dueDateText));
      var totalQuestions = students.Sum(o => o.TotalQuestions);
      if (totalQuestions <= 0) continue;

      classReportsByName[cls.Name] = new ClassCompletionReport
      {
        ClassName = cls.Name,
        CourseName = coursesByKeyStageAndSubjectCode.TryGetValue(BuildCourseLookupKey(GetKeyStage(cls.YearGroup + yearGroupOffset), cls.SubjectCode), out var course)
          ? course.Name
          : cls.SubjectCode,
        CompletedQuestions = students.Sum(o => o.CompletedQuestions),
        TotalQuestions = totalQuestions,
        CompletionPercentage = GetCompletionPercentage(students.Sum(o => o.CompletedQuestions), totalQuestions),
        Students = students
      };
    }

    reports.Teachers = _config.Teachers
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase)
      .Select(teacher => new TeacherCompletionReport
      {
        Teacher = teacher,
        DueDateLabel = reports.DueDateLabel,
        Classes = ParseClasses(teacher.Classes)
          .Select(cls => classReportsByName.GetValueOrDefault(cls.Name))
          .Where(o => o is not null)
          .DistinctBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
          .OrderBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase)
          .ToList()
      })
      .Where(o => o.Classes.Count > 0)
      .ToList();

    var tutorGroupRowsByYear = tutorGroupRosters
      .Select(tutorGroup =>
      {
        var studentIdsByPartition = BuildStudentIdsByPartition(tutorGroup.Value, coursesByKeyStageAndSubjectCode, partitionData, yearGroupOffset: yearGroupOffset);
        var cell = BuildAggregateCell(partitionData, studentIdsByPartition, dueDateText);
        return new TutorGroupCompletionRow
        {
          TutorGroup = tutorGroup.Key,
          CompletedQuestions = cell.Completed,
          TotalQuestions = cell.Total,
          CompletionPercentage = GetCompletionPercentage(cell.Completed, cell.Total)
        };
      })
      .Where(o => o.TotalQuestions > 0 && ClassNameParser.GetLeadingNumber(o.TutorGroup) > 0)
      .GroupBy(o => ClassNameParser.GetLeadingNumber(o.TutorGroup))
      .ToDictionary(
        g => g.Key,
        g => g
          .OrderByDescending(o => o.CompletionPercentage)
          .ThenBy(o => o.TutorGroup, StringComparer.OrdinalIgnoreCase)
          .Select((row, index) =>
          {
            row.Rank = index + 1;
            return row;
          })
          .ToList());

    foreach (var tutor in _config.Teachers
      .Where(o => !string.IsNullOrWhiteSpace(o.TutorGroup))
      .OrderBy(o => o.LastName, StringComparer.OrdinalIgnoreCase)
      .ThenBy(o => o.FirstName, StringComparer.OrdinalIgnoreCase))
    {
      var tutorGroup = tutor.TutorGroup.Trim();
      if (!tutorGroupRosters.TryGetValue(tutorGroup, out var roster)) continue;

      var students = BuildCompletionStudentRows(roster, student =>
      {
        var partitionKeysForStudent = GetStudentPartitionKeys(student, coursesByKeyStageAndSubjectCode, partitionData, yearGroupOffset: yearGroupOffset);
        return BuildAggregateStudentCell(partitionData, partitionKeysForStudent, student.Id, dueDateText);
      });
      var totalQuestions = students.Sum(o => o.TotalQuestions);
      if (totalQuestions <= 0) continue;

      var yearGroup = ClassNameParser.GetLeadingNumber(tutorGroup);
      var tutorGroupLeaderboard = tutorGroupRowsByYear.GetValueOrDefault(yearGroup)?
        .Select(o => new TutorGroupCompletionRow
        {
          Rank = o.Rank,
          TutorGroup = o.TutorGroup,
          CompletedQuestions = o.CompletedQuestions,
          TotalQuestions = o.TotalQuestions,
          CompletionPercentage = o.CompletionPercentage,
          IsCurrentTutorGroup = o.TutorGroup.Equals(tutorGroup, StringComparison.OrdinalIgnoreCase)
        })
        .ToList() ?? [];

      reports.Tutors.Add(new TutorCompletionReport
      {
        Tutor = tutor,
        DueDateLabel = reports.DueDateLabel,
        TutorGroup = tutorGroup,
        CompletedQuestions = students.Sum(o => o.CompletedQuestions),
        TotalQuestions = totalQuestions,
        CompletionPercentage = GetCompletionPercentage(students.Sum(o => o.CompletedQuestions), totalQuestions),
        Students = students,
        TutorGroups = tutorGroupLeaderboard
      });
    }

    return reports;
  }

}
