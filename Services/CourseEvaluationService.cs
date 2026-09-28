namespace CurriculumPortal;

public sealed class CourseEvaluationService(
  CourseService courseService,
  AIService ai,
  AITokenBudgetService aiTokenBudget,
  AppOptions options,
  ILogger<CourseEvaluationService> logger) : IDisposable
{
  private const string LegacyModelName = "gpt-5.6-sol";
  private readonly SemaphoreSlim refreshGate = new(1, 1);

  internal static string GetModelName(string model) => string.IsNullOrWhiteSpace(model) ? LegacyModelName : model;

  internal static CourseEvaluationUnitResult ResolveUnitEvaluation(IReadOnlyList<UnitEntity> units, CourseEvaluation evaluation, string unitId) =>
    ResolveUnitEvaluations(units, evaluation).GetValueOrDefault(unitId)?.Evaluation;

  internal CourseEvaluationStatus GetStatus(CourseEntity course, IReadOnlyList<UnitEntity> units, CourseEvaluation evaluation)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(evaluation);

    var resolvedUnitEvaluations = ResolveUnitEvaluations(units, evaluation);

    var currentUnitIds = units.Select(o => o.RowKey).ToList();
    var overviewSourceUnitIds = evaluation.Overall.SourceUnitIds
      ?? evaluation.Units.Select(o => o.UnitId).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
    var overviewRosterChanged = !overviewSourceUnitIds.SequenceEqual(currentUnitIds, StringComparer.Ordinal);
    var currentSourceUpdatedAt = units.Select(o => o.EvaluationUpdatedAt ?? DateTimeOffset.MinValue)
      .Append(course.EvaluationUpdatedAt ?? DateTimeOffset.MinValue)
      .Max();
    var isOverviewOutdated = !string.Equals(GetModelName(evaluation.Overall.Model), options.OpenAIModel, StringComparison.Ordinal)
      || !evaluation.Overall.EvaluationSourceUpdatedAt.HasValue
      || currentSourceUpdatedAt > evaluation.Overall.EvaluationSourceUpdatedAt.Value
      || overviewRosterChanged;

    var outdatedUnitIds = currentUnitIds.Where(o => !resolvedUnitEvaluations.ContainsKey(o)).ToHashSet(StringComparer.Ordinal);
    foreach (var item in resolvedUnitEvaluations.Values)
    {
      var isOutdated = !string.Equals(GetModelName(item.Evaluation.Model), options.OpenAIModel, StringComparison.Ordinal)
        || !item.Evaluation.EvaluationSourceUpdatedAt.HasValue
        || item.Unit.EvaluationUpdatedAt.GetValueOrDefault() > item.Evaluation.EvaluationSourceUpdatedAt.Value;
      if (isOutdated)
        outdatedUnitIds.Add(item.Unit.RowKey);
    }

    return new CourseEvaluationStatus(
      isOverviewOutdated,
      outdatedUnitIds,
      resolvedUnitEvaluations.ToDictionary(o => o.Key, o => o.Value.Evaluation, StringComparer.Ordinal));
  }

  internal async Task<DateTimeOffset> RegenerateSectionsAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, CourseEvaluation evaluation,
    IReadOnlyList<UnitEntity> unitsToEvaluate, bool includeOverall, Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(evaluation);
    ArgumentNullException.ThrowIfNull(unitsToEvaluate);

    var unitEvaluations = ResolveUnitEvaluations(units, evaluation)
      .ToDictionary(o => o.Key, o => o.Value.Evaluation, StringComparer.Ordinal);
    var result = await ai.EvaluateCourseSectionsAsync(course, units, unitsToEvaluate, includeOverall, reportProgress, cancellationToken);
    var generatedAt = DateTimeOffset.UtcNow;
    evaluation.Overall ??= new();
    evaluation.Units ??= [];

    if (includeOverall)
    {
      result.Overall.GeneratedAt = generatedAt;
      result.Overall.Model = ai.ModelName;
      result.Overall.EvaluationSourceUpdatedAt = units.Select(o => o.EvaluationUpdatedAt ?? DateTimeOffset.MinValue)
        .Append(course.EvaluationUpdatedAt ?? DateTimeOffset.MinValue)
        .Max();
      result.Overall.SourceUnitIds = units.Select(o => o.RowKey).ToList();
      evaluation.Overall = result.Overall;
    }

    var sourceUpdatedAtByUnitId = units.ToDictionary(o => o.RowKey, o => o.EvaluationUpdatedAt ?? DateTimeOffset.MinValue, StringComparer.Ordinal);
    foreach (var unitResult in result.Units)
    {
      if (!sourceUpdatedAtByUnitId.TryGetValue(unitResult.UnitId, out var sourceUpdatedAt)) continue;

      unitResult.GeneratedAt = generatedAt;
      unitResult.Model = ai.ModelName;
      unitResult.EvaluationSourceUpdatedAt = sourceUpdatedAt;
      unitEvaluations[unitResult.UnitId] = unitResult;
    }

    evaluation.Units = units
      .Where(o => unitEvaluations.ContainsKey(o.RowKey))
      .Select(o => string.IsNullOrWhiteSpace(unitEvaluations[o.RowKey].UnitId)
        ? unitEvaluations[o.RowKey] with { UnitId = o.RowKey }
        : unitEvaluations[o.RowKey])
      .ToList();
    await courseService.UploadCourseEvaluationAsync(course.RowKey, evaluation, CancellationToken.None);
    return generatedAt;
  }

  internal async Task<CourseEvaluationRefreshResult> RefreshOutdatedEvaluationsAsync(CancellationToken cancellationToken = default)
  {
    await refreshGate.WaitAsync(cancellationToken);
    try
    {
      return await RefreshOutdatedEvaluationsCoreAsync(cancellationToken);
    }
    finally
    {
      refreshGate.Release();
    }
  }

  private async Task<CourseEvaluationRefreshResult> RefreshOutdatedEvaluationsCoreAsync(CancellationToken cancellationToken)
  {
    logger.LogInformation("Starting evaluation refresh.");
    var courses = await courseService.ListCoursesAsync(cancellationToken);
    var units = await courseService.ListUnitsAsync(cancellationToken: cancellationToken);
    var unitsByCourseId = units.ToLookup(o => o.PartitionKey, StringComparer.Ordinal);
    var candidates = new List<CourseEvaluationRefreshWorkItem>();
    var evaluatedCourseCount = 0;
    var failedCourseIds = new HashSet<string>(StringComparer.Ordinal);

    for (var courseIndex = 0; courseIndex < courses.Count; courseIndex++)
    {
      var course = courses[courseIndex];
      if (course.KeyStage != 3) continue;

      try
      {
        var evaluation = await courseService.TryGetCourseEvaluationAsync(course.RowKey, cancellationToken);
        if (evaluation is null) continue;

        evaluatedCourseCount++;
        var courseUnits = unitsByCourseId[course.RowKey].ToList();
        var status = GetStatus(course, courseUnits, evaluation);
        var state = new CourseEvaluationRefreshCourse(course, courseUnits, evaluation, courseIndex);
        if (status.IsOverviewOutdated)
        {
          candidates.Add(new CourseEvaluationRefreshWorkItem(
            state,
            null,
            evaluation.Overall.GeneratedAt,
            AIService.CourseOverviewEvaluationReservedTokens,
            -1));
        }

        for (var unitIndex = 0; unitIndex < courseUnits.Count; unitIndex++)
        {
          var unit = courseUnits[unitIndex];
          if (!status.OutdatedUnitIds.Contains(unit.RowKey)) continue;

          var unitEvaluation = status.UnitEvaluations.GetValueOrDefault(unit.RowKey);
          candidates.Add(new CourseEvaluationRefreshWorkItem(
            state,
            unit.RowKey,
            unitEvaluation?.GeneratedAt ?? default,
            AIService.CourseUnitEvaluationReservedTokens,
            unitIndex));
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        failedCourseIds.Add(course.RowKey);
        logger.LogError(ex, "Failed to inspect outdated evaluation feedback for course {CourseId}.", course.RowKey);
      }
    }

    var tokenBudget = int.MaxValue;
    if (options.DailyTokenLimit > 0 && candidates.Count > 0)
    {
      var availableTokens = await aiTokenBudget.GetAvailableTokensAsync(cancellationToken);
      tokenBudget = availableTokens;
    }

    var selected = new List<CourseEvaluationRefreshWorkItem>();
    var overviewCandidatesByCourse = candidates.Where(o => o.UnitId is null).ToDictionary(o => o.Course);
    var reservedTokens = 0;
    foreach (var candidate in candidates
      .OrderBy(o => o.GeneratedAt)
      .ThenBy(o => o.Course.CourseOrder)
      .ThenBy(o => o.UnitId is null ? 0 : 1)
      .ThenBy(o => o.UnitOrder))
    {
      if (selected.Contains(candidate)) continue;
      if (candidate.UnitId is not null
        && overviewCandidatesByCourse.TryGetValue(candidate.Course, out var overviewCandidate)
        && !selected.Contains(overviewCandidate))
      {
        if (overviewCandidate.ReservedTokens > tokenBudget - reservedTokens) continue;

        selected.Add(overviewCandidate);
        reservedTokens += overviewCandidate.ReservedTokens;
      }
      if (candidate.ReservedTokens > tokenBudget - reservedTokens) continue;

      selected.Add(candidate);
      reservedTokens += candidate.ReservedTokens;
    }

    var courseGroups = selected.GroupBy(o => o.Course).OrderBy(o => o.Key.CourseOrder).ToList();
    logger.LogInformation(
      "Evaluation refresh found {DiscoveredSectionCount} outdated sections, selected {SelectedSectionCount} across {SelectedCourseCount} courses, deferred {DeferredSectionCount}, and reserved {ReservedTokens} tokens.",
      candidates.Count,
      selected.Count,
      courseGroups.Count,
      candidates.Count - selected.Count,
      reservedTokens);

    var regeneratedSectionCount = 0;
    for (var courseGroupIndex = 0; courseGroupIndex < courseGroups.Count; courseGroupIndex++)
    {
      var courseGroup = courseGroups[courseGroupIndex];
      var state = courseGroup.Key;
      try
      {
        logger.LogInformation(
          "Refreshing {SectionCount} evaluation sections for {CourseName} ({CourseId}), course {CourseNumber} of {CourseCount}.",
          courseGroup.Count(),
          state.Course.Name,
          state.Course.RowKey,
          courseGroupIndex + 1,
          courseGroups.Count);
        var selectedUnitIds = courseGroup.Where(o => o.UnitId is not null).Select(o => o.UnitId).ToHashSet(StringComparer.Ordinal);
        var selectedUnits = state.Units.Where(o => selectedUnitIds.Contains(o.RowKey)).ToList();
        var includeOverall = courseGroup.Any(o => o.UnitId is null);
        await RegenerateSectionsAsync(state.Course, state.Units, state.Evaluation, selectedUnits, includeOverall, null, cancellationToken);
        regeneratedSectionCount += courseGroup.Count();
        logger.LogInformation(
          "Completed evaluation refresh for {CourseName} ({CourseId}), course {CourseNumber} of {CourseCount}.",
          state.Course.Name,
          state.Course.RowKey,
          courseGroupIndex + 1,
          courseGroups.Count);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        failedCourseIds.Add(state.Course.RowKey);
        logger.LogError(ex, "Failed to regenerate outdated evaluation feedback for course {CourseId}.", state.Course.RowKey);
      }
    }

    var result = new CourseEvaluationRefreshResult(
      courses.Count,
      evaluatedCourseCount,
      candidates.Count,
      selected.Count,
      candidates.Count - selected.Count,
      regeneratedSectionCount,
      failedCourseIds.Count,
      reservedTokens);
    logger.LogInformation(
      "Evaluation refresh inspected {CourseCount} courses with {EvaluatedCourseCount} existing evaluations, discovered {DiscoveredSectionCount} outdated sections, selected {SelectedSectionCount}, deferred {DeferredSectionCount}, regenerated {RegeneratedSectionCount}, failed {FailedCourseCount} courses, and reserved {ReservedTokens} tokens.",
      result.CourseCount,
      result.EvaluatedCourseCount,
      result.DiscoveredSectionCount,
      result.SelectedSectionCount,
      result.DeferredSectionCount,
      result.RegeneratedSectionCount,
      result.FailedCourseCount,
      result.ReservedTokens);
    return result;
  }

  private static Dictionary<string, ResolvedCourseEvaluationUnit> ResolveUnitEvaluations(IReadOnlyList<UnitEntity> units, CourseEvaluation evaluation)
  {
    evaluation.Overall ??= new();
    evaluation.Units ??= [];
    var currentUnitsById = units.ToDictionary(o => o.RowKey, StringComparer.Ordinal);
    var resolved = new Dictionary<string, ResolvedCourseEvaluationUnit>(StringComparer.Ordinal);
    for (var index = 0; index < evaluation.Units.Count; index++)
    {
      var unitEvaluation = evaluation.Units[index];
      var isLegacy = string.IsNullOrWhiteSpace(unitEvaluation.UnitId);
      var sourceUnitId = evaluation.Overall.SourceUnitIds?.ElementAtOrDefault(index);
      var unitId = isLegacy
        ? !string.IsNullOrWhiteSpace(sourceUnitId) ? sourceUnitId : units.ElementAtOrDefault(index)?.RowKey
        : unitEvaluation.UnitId;
      if (string.IsNullOrWhiteSpace(unitId) || !currentUnitsById.TryGetValue(unitId, out var unit)) continue;

      var item = new ResolvedCourseEvaluationUnit(unitEvaluation, unit, isLegacy);
      if (!resolved.TryGetValue(unitId, out var existing) || (existing.IsLegacy && !isLegacy))
        resolved[unitId] = item;
    }

    return resolved;
  }

  public void Dispose() => refreshGate.Dispose();

  private sealed record CourseEvaluationRefreshCourse(CourseEntity Course, List<UnitEntity> Units, CourseEvaluation Evaluation, int CourseOrder);

  private sealed record ResolvedCourseEvaluationUnit(CourseEvaluationUnitResult Evaluation, UnitEntity Unit, bool IsLegacy);

  private sealed record CourseEvaluationRefreshWorkItem(
    CourseEvaluationRefreshCourse Course,
    string UnitId,
    DateTimeOffset GeneratedAt,
    int ReservedTokens,
    int UnitOrder);
}

internal sealed record CourseEvaluationStatus(
  bool IsOverviewOutdated,
  HashSet<string> OutdatedUnitIds,
  Dictionary<string, CourseEvaluationUnitResult> UnitEvaluations);

internal sealed record CourseEvaluationRefreshResult(
  int CourseCount,
  int EvaluatedCourseCount,
  int DiscoveredSectionCount,
  int SelectedSectionCount,
  int DeferredSectionCount,
  int RegeneratedSectionCount,
  int FailedCourseCount,
  int ReservedTokens);
