using System.Globalization;
using System.Text;

namespace CurriculumPortal;

public partial class AIService
{
  public async Task<string> SummariseCourseAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    var sb = new StringBuilder($"# {course.Name}\n\n");
    if (!string.IsNullOrWhiteSpace(course.Intent))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Course intent\n\n{course.Intent}\n\n");
    }

    if (!string.IsNullOrWhiteSpace(course.Specification))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Specification\n\n{course.Specification}\n\n");
    }

    sb.Append("## Units\n\n");

    foreach (var unit in units.Where(o => o.KeyKnowledgeStatus == 2))
    {
      var term = string.IsNullOrWhiteSpace(unit.Term) ? string.Empty : $" {unit.Term} Term";
      sb.Append(CultureInfo.InvariantCulture, $"### {unit.Title} (Year {unit.YearGroup}{term})\n\n");

      if (!string.IsNullOrWhiteSpace(unit.WhyThis))
      {
        sb.Append(CultureInfo.InvariantCulture, $"#### Why this?\n\n{unit.WhyThis}\n\n");
      }

      if (!string.IsNullOrWhiteSpace(unit.WhyNow))
      {
        sb.Append(CultureInfo.InvariantCulture, $"#### Why now?\n\n{unit.WhyNow}\n\n");
      }

      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, cancellationToken);
      if (keyKnowledge.DeclarativeKnowledge.Count > 0)
      {
        sb.Append("#### Students must know that:\n\n" + string.Join("\n", keyKnowledge.DeclarativeKnowledge.Select(o => $"- {o}")) + "\n\n");
      }

      if (keyKnowledge.ProceduralKnowledge.Count > 0)
      {
        sb.Append("#### Students must be able to:\n\n" + string.Join("\n", keyKnowledge.ProceduralKnowledge.Select(o => $"- {o}")) + "\n\n");
      }
    }

    return sb.ToString().Trim();
  }

  public async Task<CourseEvaluationResult> EvaluateCourseAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    await AssertTokensRemainingAsync((6000 * units.Count * 2) + 20000, cancellationToken);
    return await EvaluateCourseSectionsAsync(course, units, units, true, reportProgress, cancellationToken);
  }

  public async Task<CourseOverallEvaluationResponse> EvaluateCourseOverviewAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    await AssertTokensRemainingAsync(20000, cancellationToken);
    var result = await EvaluateCourseSectionsAsync(course, units, [], true, reportProgress, cancellationToken);
    return result.Overall;
  }

  public async Task<CourseEvaluationUnitResult> EvaluateCourseUnitAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, UnitEntity unit, Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(unit);
    await AssertTokensRemainingAsync(6000 * 2, cancellationToken);
    var result = await EvaluateCourseSectionsAsync(course, units, [unit], false, reportProgress, cancellationToken);
    return result.Units.First();
  }

  private async Task<CourseEvaluationResult> EvaluateCourseSectionsAsync(CourseEntity course, IReadOnlyList<UnitEntity> units, IReadOnlyList<UnitEntity> unitsToEvaluate, bool includeOverall,
    Action<int, int> reportProgress, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(unitsToEvaluate);

    var overviewPrompt = """
      You are an experienced secondary school teacher and expert curriculum designer.
      The user will provide information about a course being taught to secondary school students in the UK.
      Evaluate the overall quality of the curriculum, focusing on the selection and sequencing of knowledge.
      You do not need to answer every question below, but consider them when formulating your overall evaluation.

      # Coverage and Balance

      - Is the scheme ambitious and knowledge-rich?
      - Does it meet the requirements of the National Curriculum, where applicable? (A clear gap would be a Priority 1 issue.)
      - Is it the right level of challenge for Key Stage 3?
      - Is it broad and balanced?
      - Has the most powerful knowledge been included, being mindful of constraints on curriculum time? Is anything substantial missing?
      - Is there any unnecessary or low-value content that could be removed to make space for more important knowledge or threshold concepts?

      # Sequencing

      - Does the order in which units are taught make sense?
      - Is there a logical progression of knowledge and increasing level of challenge over time?
      - Are there any significant sequencing issues, such as important knowledge being taught too late?
      - In the case of hierarchical knowledge, are foundational concepts taught before more complex ones that depend on them?
      - Are there any units that seem out of place or disconnected from the overall curriculum?
      - In cases where sequencing is mostly arbitrary, for example between disconnected topics with a similar level of challenge, do not feel the need to express an opinion.

      # Guidance

      - Provide separate short overview paragraphs for coverage/balance and sequencing. These should evaluate the quality of the curriculum in these areas.
      - Provide recommended actions in priority order, with the most important first, in structured arrays of concise, plain-English strings (up to 6 actions in each array but typically fewer).
      - Write each recommended action in the imperative mood.
      - Actions must be very specific, avoiding generic guidance like "Audit the sequence" or "Include more powerful knowledge".
      - Associate each action with a priority. Use Priority 1 sparingly, only for essential, critical, non-negotiable actions that must be addressed with urgency. Use Priority 2 only for important actions that fix substantial, indisputable problems which must be addressed promptly because they have significant impact. Use Priority 3 for strongly recommended actions that require attention. Use Priority 4 for suggested enhancements, refinements, or ideas, or opinionated changes. Use a best fit approach.
      - You do not necessarily need to include actions for every priority level. A well-designed curriculum might have only Priority 4 actions (or none at all), while one with significant issues might have mostly Priorities 1 and 2.
      - Preserve Priorities 1 and 2 for fixing serious problems, if any. Do not assign a high priority to actions that are more subjective or open to debate.
      - Do not include Markdown bullet syntax, headings, or formatting.
      - The provided curriculum information is not intended to include assessments, rubrics, knowledge organisers, lesson plans, or other artefacts, so do not comment on these.
      - Keep a high-level view and do not address minor unit-level or statement-level details as these will be covered in the individual unit evaluations.
      - Use British English spelling and terminology.
      - Keep your feedback as concise and information-dense as possible. Be judicious about what to include, focusing on the most impactful points.
      """;

    var assessmentRecapPrompt = """
      You are an experienced secondary school teacher with strong assessment expertise.
      The user will provide course information in Markdown. For each unit, they will provide the questions from the Recap section of the assessment, followed by the declarative knowledge statements taught in that unit.
      Evaluate the effectiveness of the Recap sections as retrieval practice of previous units across the course.
      You do not need to answer every question below, but consider them when formulating your overall evaluation.

      # Assessment recap questions

      - Do the Recap questions align with knowledge that has already been taught by that point in the course?
      - Do they prioritise the most powerful knowledge and threshold concepts taught to date?
      - Do they retrieve knowledge cumulatively over time, rather than repeatedly focusing only on the immediately preceding unit?
      - Across successive assessments, is there a reasonable range of topics revisited?

      # Guidance

      - Recap sections are intended to be very short, so only a small sample of prior knowledge can be revisited each time. Do not penalise the curriculum simply because every prior topic is not included in every recap.
      - Reward thoughtful sampling: the most powerful knowledge should recur when useful, but the recap pattern should also help students retrieve a range of important topics over time.
      - Provide one short overview paragraph which evaluates the overall quality of retrieval practice.
      - Provide recommended actions in priority order, with the most important first, in a structured array of concise, plain-English strings (up to 6 actions but typically fewer).
      - Write each recommended action in the imperative mood.
      - Actions must be very specific, avoiding generic guidance such as "Audit the knowledge selected for recap" or "Sample from a wider range of topics".
      - Associate each action with a priority. Use Priority 1 sparingly, only for essential, critical, non-negotiable actions that must be addressed with urgency. Use Priority 2 only for important actions that fix substantial, indisputable problems which must be addressed promptly because they have significant impact. Use Priority 3 for strongly recommended actions that require attention. Use Priority 4 for suggested enhancements, refinements, or ideas, or opinionated changes. Use a best fit approach.
      - You do not necessarily need to include actions for every priority level. A well-designed selection of recap questions might have only Priority 4 actions (or none at all), while one with significant issues might have mostly Priorities 1 and 2.
      - If the user input includes an [Image] placeholder, assume a real image is part of the recap question at that point. Do not ask for the image.
      - Do not feed back on individual question wording. Focus on the overall quality of retrieval practice provided by the recap sections, and highlight specific assessments that are notably strong or require improvement.
      - For each unit's Recap section, judge alignment only against knowledge from earlier units already listed above. Do not treat the current unit's knowledge as prior knowledge for that recap.
      - Do not include Markdown bullet syntax, headings, or formatting.
      - Use British English spelling and terminology.
      - Keep your feedback as concise and information-dense as possible. Be judicious about what to include, focusing on the most impactful points.
      """;

    var keyKnowledgePrompt = """
      You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
      The user will provide one unit of a course and show how the unit fits into our broader curriculum.
      Evaluate the quality of the key knowledge statements. Focus on how effectively they capture the most powerful knowledge students need to know.
      You do not need to answer every question below, but consider them when formulating your overall evaluation.

      # Declarative knowledge statements

      - Is the coverage comprehensive and ambitious, prioritising the most important knowledge that students need to know and remember?
      - Does the list comprise powerful knowledge and threshold concepts that underpin deep understanding, rather than trivial or low-value facts?
      - Are all the statements factually accurate? (It is a Priority 1 issue if there is an absolute, incontrovertible factual inaccuracy. Condone simplifications appropriate to Key Stage 3, such as omitted qualifications or exceptions.)
      - Are they specific enough to be assessed in a knowledge quiz?
      - Are they stated as facts, rather than signposts? For example, instead of "Know the houses of Hogwarts.", a well-written statement would say "The houses of Hogwarts are Gryffindor, Hufflepuff, Ravenclaw, and Slytherin."
      - Is any important knowledge missing?

      # Procedural knowledge statements

      - Does the list comprise specific, knowledge-rich skills and techniques that students need to develop?
      - Are the highest-priority skills included, within the scope of the unit?
      - Are the skills precise enough to be assessed through performance, demonstration, or worked responses? Note that detailed success criteria are intentionally omitted.
      - Does the list correctly avoid generic study skills and vague verbs like "know" and "understand"?

      # Guidance

      - Be mindful that the scheme fits within a wider curriculum. Focus on this specific unit and its level of challenge. If key knowledge is absent but might reasonably be included in an earlier or later unit, do not penalise the curriculum for this. However, if substantial knowledge is missing and it does not seem reasonable for it to be covered elsewhere in the curriculum, then this should be highlighted.
      - Avoid significantly expanding the scope of the knowledge content to completely new areas; balance ambition with consideration of constraints on curriculum time.
      - Key knowledge statements are not intended to be overly technical and it is usually acceptable for them to be simplified for clarity and accessibility.
      - Provide one short overview paragraph which evaluates the overall quality of the key knowledge.
      - Provide recommended actions in priority order, with the most important first, in a structured array of concise, plain-English strings (up to 10 actions but typically fewer).
      - Write each recommended action in the imperative mood.
      - Actions must be very specific, avoiding generic guidance.
      - Associate each action with a priority. Use Priority 1 sparingly, only for essential, critical, non-negotiable actions that must be addressed with urgency. Use Priority 2 only for important actions that fix substantial, indisputable problems which must be addressed promptly because they have significant impact. Use Priority 3 for strongly recommended actions that require attention. Use Priority 4 for suggested enhancements, refinements, or ideas, or opinionated changes. Use a best fit approach.
      - You do not necessarily need to include actions for every priority level. A well-designed selection of key knowledge might have only Priority 4 actions (or none at all), while one with significant issues might have mostly Priorities 1 and 2.
      - Do not include Markdown bullet syntax, headings, or formatting.
      - Use British English spelling and terminology.
      - Keep your feedback as concise and information-dense as possible. Be judicious about what to include, focusing on the most impactful points.
      """;

    var assessmentPrompt = """
      You are an experienced secondary school teacher with strong assessment expertise.
      The user will provide one unit from a curriculum portal, including its key knowledge statements and end-of-unit assessment.
      Evaluate how closely the assessment tests students' understanding of the key knowledge, and evaluate the quality of question design.

      # Alignment

      - Is there close alignment between the key knowledge and assessment?
      - Does the assessment broadly cover the key knowledge statements? It's fine for the assessment to sample from the key knowledge, as long as it covers a selection of the most important knowledge.
      - Is all the most substantial knowledge required for the assessment included among the key knowledge statements?
      - Are both declarative and procedural knowledge statements assessed?
      - Is the assessment faithful to the spirit of the unit, as defined in the unit information and key knowledge?
      - Is there a balance of emphasis in the assessment that reflects the relative importance of different knowledge statements?
      - Does the rigour of the mark scheme reflect the expectations of the key knowledge statements?

      # Design

      - Are the questions clearly and unambiguously worded?
      - Is there an appropriate level of difficulty, including some questions that are more accessible and others that provide stretch and challenge?
      - Are the questions resistant to guessing? For multiple choice questions, are the incorrect options plausible and not easily dismissible, yet unambiguously wrong?
      - Is the mark scheme accurate and specific, and for multiple-mark questions, does it specify what is required for each mark?

      # Guidance

      - Assessments typically take the format of Recap (retrieval practice from previous units), Knowledge, and Application. When considering alignment, focus on the Knowledge and Application sections only.
      - Provide one short overview paragraph which evaluates the overall quality of the assessment.
      - Provide recommended actions in priority order, with the most important first, in structured arrays of concise, plain-English strings (up to 10 actions but typically fewer).
      - Write each recommended action in the imperative mood.
      - Actions must be very specific, avoiding generic guidance.
      - Associate each action with a priority. Use Priority 1 sparingly, only for essential, critical, non-negotiable actions that must be addressed with urgency. Use Priority 2 only for important actions that fix substantial, indisputable problems which must be addressed promptly because they have significant impact. Use Priority 3 for strongly recommended actions that require attention. Use Priority 4 for suggested enhancements, refinements, or ideas, or opinionated changes. Use a best fit approach.
      - You do not necessarily need to include actions for every priority level. A well-designed assessment might have only Priority 4 actions (or none at all), while one with significant issues might have mostly Priorities 1 and 2.
      - When referring to a specific question, state the question number.
      - Condone underspecified instructions for practical, physical, or performance-based assessment elements, as they are placeholders for fuller teacher guidance. An appropriate mark scheme should still be provided (mark bands or threshold descriptors are acceptable for this type of task).
      - If the text refers to an additional resource, such as an audio recording, assume that the resource is available to students and do not penalise the assessment for this.
      - Be cautious about advising the addition of too many new questions, as this may not be feasible given constraints on assessment length. One or two new questions could be added but otherwise consider replacing or refining existing questions.
      - If the assessment includes images, each [Image n] reference corresponds to the nth image included with the user input. Review these images as part of the assessment.
      - Do not include Markdown bullet syntax, headings, or formatting.
      - Use British English spelling and terminology.
      - Keep your feedback as concise and information-dense as possible. Be judicious about what to include, focusing on the most impactful points.
      """;

    var overviewSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "coverageBalanceOverview": { "type": "string" },
          "coverageBalanceRecommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          },
          "sequencingOverview": { "type": "string" },
          "sequencingRecommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          }
        },
        "required": ["coverageBalanceOverview", "coverageBalanceRecommendedActions", "sequencingOverview", "sequencingRecommendedActions"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var keyKnowledgeSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "overview": { "type": "string" },
          "recommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          }
        },
        "required": ["overview", "recommendedActions"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var assessmentRecapSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "overview": { "type": "string" },
          "recommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          }
        },
        "required": ["overview", "recommendedActions"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var assessmentSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "overview": { "type": "string" },
          "alignmentRecommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          },
          "designRecommendedActions": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "action": { "type": "string" },
                "priority": { "type": "integer", "minimum": 1, "maximum": 4 }
              },
              "required": ["action", "priority"],
              "additionalProperties": false
            }
          }
        },
        "required": ["overview", "alignmentRecommendedActions", "designRecommendedActions"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var total = (includeOverall ? 2 : 0) + (unitsToEvaluate.Count * 2);
    var completed = 0;
    var contexts = new List<CourseEvaluationUnitContext>();
    foreach (var unit in unitsToEvaluate)
    {
      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, cancellationToken);
      var assessment = await _courseService.GetBlobAsync<Assessment>(unit.RowKey, cancellationToken);
      contexts.Add(new CourseEvaluationUnitContext(unit, BuildKeyKnowledgeEvaluationContext(unit, keyKnowledge, units), BuildAssessmentEvaluationContext(unit, keyKnowledge, assessment)));
    }

    var semaphore = new SemaphoreSlim(5);
    Task<CourseOverallEvaluationResponse> overallTask = null;
    Task<AssessmentRecapEvaluationResponse> assessmentRecapTask = null;
    if (includeOverall)
    {
      var courseSummary = await SummariseCourseAsync(course, units, cancellationToken);
      overallTask = RunEvaluationRequestAsync<CourseOverallEvaluationResponse>(
        semaphore,
        overviewPrompt,
        overviewSchema,
        "courseOverallEvaluation",
        courseSummary,
        () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
        cancellationToken);

      var assessmentRecapContext = await BuildAssessmentRecapEvaluationContextAsync(units, cancellationToken);
      assessmentRecapTask = RunEvaluationRequestAsync<AssessmentRecapEvaluationResponse>(
        semaphore,
        assessmentRecapPrompt,
        assessmentRecapSchema,
        "assessmentRecapEvaluation",
        assessmentRecapContext,
        () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
        cancellationToken);
    }

    var unitTasks = contexts.Select(async context =>
    {
      var keyKnowledgeTask = context.Unit.KeyKnowledgeStatus switch
      {
        0 => Task.FromResult(new KeyKnowledgeEvaluationResponse
        {
          Overview = "There is no key knowledge for this unit.",
          RecommendedActions = [new CourseEvaluationRecommendedAction { Action = "Add key knowledge for this unit.", Priority = 1 }]
        }),
        1 => Task.FromResult(new KeyKnowledgeEvaluationResponse
        {
          Overview = "There key knowledge for this unit is incomplete.",
          RecommendedActions = [new CourseEvaluationRecommendedAction { Action = "Complete the key knowledge for this unit.", Priority = 1 }]
        }),
        _ => RunEvaluationRequestAsync<KeyKnowledgeEvaluationResponse>(
          semaphore,
          keyKnowledgePrompt,
          keyKnowledgeSchema,
          "keyKnowledgeEvaluation",
          context.KeyKnowledgeContext,
          () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
          cancellationToken)
      };

      if (context.Unit.KeyKnowledgeStatus < 2)
      {
        reportProgress?.Invoke(Interlocked.Increment(ref completed), total);
      }

      var assessmentTask = context.Unit.AssessmentStatus switch
      {
        0 => Task.FromResult(new AssessmentEvaluationResponse
        {
          Overview = "There is no assessment for this unit.",
          DesignRecommendedActions = [new CourseEvaluationRecommendedAction { Action = "Add an assessment for this unit.", Priority = 1 }]
        }),
        1 => Task.FromResult(new AssessmentEvaluationResponse
        {
          Overview = "The assessment for this unit is incomplete.",
          DesignRecommendedActions = [new CourseEvaluationRecommendedAction { Action = "Complete the assessment for this unit.", Priority = 1 }]
        }),
        _ => RunEvaluationRequestAsync<AssessmentEvaluationResponse>(
          semaphore,
          assessmentPrompt,
          assessmentSchema,
          "assessmentEvaluation",
          context.AssessmentInput.Text,
          () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
          cancellationToken,
          context.AssessmentInput.Images)
      };

      if (context.Unit.AssessmentStatus < 2)
      {
        reportProgress?.Invoke(Interlocked.Increment(ref completed), total);
      }

      await Task.WhenAll(keyKnowledgeTask, assessmentTask);
      var keyKnowledge = await keyKnowledgeTask;
      keyKnowledge.RecommendedActions = SortEvaluationActions(keyKnowledge.RecommendedActions);

      var assessment = await assessmentTask;
      assessment.AlignmentRecommendedActions = SortEvaluationActions(assessment.AlignmentRecommendedActions);
      assessment.DesignRecommendedActions = SortEvaluationActions(assessment.DesignRecommendedActions);

      return new CourseEvaluationUnitResult(context.Unit.RowKey, context.Unit.Title, keyKnowledge, assessment);
    }).ToList();

    var tasks = unitTasks.Cast<Task>().ToList();
    if (overallTask is not null)
    {
      tasks.Add(overallTask);
    }

    if (assessmentRecapTask is not null)
    {
      tasks.Add(assessmentRecapTask);
    }

    await Task.WhenAll(tasks);
    var overall = overallTask is null ? new CourseOverallEvaluationResponse() : await overallTask;
    overall.CoverageBalanceRecommendedActions = SortEvaluationActions(overall.CoverageBalanceRecommendedActions);
    overall.SequencingRecommendedActions = SortEvaluationActions(overall.SequencingRecommendedActions);
    if (assessmentRecapTask is not null)
    {
      var assessmentRecap = await assessmentRecapTask;
      overall.AssessmentRecapOverview = assessmentRecap.Overview;
      overall.AssessmentRecapRecommendedActions = SortEvaluationActions(assessmentRecap.RecommendedActions);
    }
    else
    {
      overall.AssessmentRecapRecommendedActions = SortEvaluationActions(overall.AssessmentRecapRecommendedActions);
    }

    var unitResults = (await Task.WhenAll(unitTasks)).ToList();

    return new CourseEvaluationResult
    {
      Overall = overall,
      Units = unitResults
    };
  }

  private static List<CourseEvaluationRecommendedAction> SortEvaluationActions(IEnumerable<CourseEvaluationRecommendedAction> actions) =>
    actions?.OrderBy(o => o.Priority).ToList() ?? [];
}
