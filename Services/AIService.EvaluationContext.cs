using OpenAI.Responses;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public partial class AIService
{
  private async Task AssertTokensRemainingAsync(int reservedTokens, CancellationToken cancellationToken = default)
  {
    if (_dailyTokenLimit == default) return;

    var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
    var end = start.AddDays(1);
    var url = new Uri($"https://api.openai.com/v1/organization/usage/completions?start_time={start.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d");

    using var http = _httpClientFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAIAdminApiKey);

    var json = await http.GetStringAsync(url, cancellationToken);
    using var doc = JsonDocument.Parse(json);

    var inputTokens = 0;
    var outputTokens = 0;

    foreach (var bucket in doc.RootElement.GetProperty("data").EnumerateArray())
    {
      foreach (var result in bucket.GetProperty("results").EnumerateArray())
      {
        inputTokens += result.GetProperty("input_tokens").GetInt32();
        outputTokens += result.GetProperty("output_tokens").GetInt32();
      }
    }

    if (inputTokens + outputTokens + reservedTokens >= _dailyTokenLimit) throw new InsufficientTokensException();
  }

  private async Task<T> RunEvaluationRequestAsync<T>(SemaphoreSlim semaphore, string instructions, BinaryData schema, string schemaName, string input, Action onComplete,
    CancellationToken cancellationToken, IReadOnlyList<string> images = null) where T : new()
  {
    await semaphore.WaitAsync(cancellationToken);
    try
    {
      var client = _aiClient.GetResponsesClient();
      var options = new CreateResponseOptions
      {
        Instructions = instructions,
        StoredOutputEnabled = false,
        TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(schemaName, schema, jsonSchemaIsStrict: true) },
        ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
        Model = _model
      };

      options.InputItems.Add(images?.Count > 0
        ? ResponseItem.CreateUserMessageItem([ResponseContentPart.CreateInputTextPart(input), .. images.Select(o => ResponseContentPart.CreateInputImagePart(new Uri(o)))])
        : ResponseItem.CreateUserMessageItem(input));
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      return JsonSerializer.Deserialize<T>(json, JsonDefaults.CamelCase) ?? new T();
    }
    finally
    {
      semaphore.Release();
      onComplete?.Invoke();
    }
  }

  private static string BuildKeyKnowledgeEvaluationContext(UnitEntity unit, KeyKnowledge keyKnowledge, IReadOnlyList<UnitEntity> units)
  {
    var sb = new StringBuilder();
    AppendUnitEvaluationHeader(sb, unit);

    AppendKeyKnowledgeEvaluationSection(sb, keyKnowledge);
    sb.Append("## Course units\n\n");
    foreach (var courseUnit in units)
    {
      var current = courseUnit.RowKey == unit.RowKey ? " (current unit)" : string.Empty;
      var term = string.IsNullOrWhiteSpace(courseUnit.Term) ? "No term" : $"{courseUnit.Term} Term";
      sb.Append(CultureInfo.InvariantCulture, $"- Year {courseUnit.YearGroup}, {term}: {courseUnit.Title}{current}\n");
    }

    return sb.ToString().Trim();
  }

  private static EvaluationInput BuildAssessmentEvaluationContext(UnitEntity unit, KeyKnowledge keyKnowledge, Assessment assessment)
  {
    var sb = new StringBuilder();
    var images = new List<string>();
    AppendUnitEvaluationHeader(sb, unit);
    AppendKeyKnowledgeEvaluationSection(sb, keyKnowledge);
    AppendAssessmentEvaluationSection(sb, assessment, images);
    return new EvaluationInput(sb.ToString().Trim(), images);
  }

  private async Task<string> BuildAssessmentRecapEvaluationContextAsync(IReadOnlyList<UnitEntity> units, CancellationToken cancellationToken)
  {
    var sb = new StringBuilder();
    for (var i = 0; i < units.Count; i++)
    {
      var unit = units[i];
      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, cancellationToken);

      if (i > 0)
      {
        sb.Append(CultureInfo.InvariantCulture, $"# Recap questions in assessment for {unit.Title}\n\n");
        if (unit.AssessmentStatus < 2)
        {
          sb.Append("There is no completed assessment for this unit\n\n");
        }
        else
        {
          var assessment = await _courseService.GetBlobAsync<Assessment>(unit.RowKey, cancellationToken);
          var recapSection = assessment.Sections.FirstOrDefault(o => o.Title?.StartsWith("Recap", StringComparison.OrdinalIgnoreCase) ?? false);
          if (recapSection is null || recapSection.Questions.Count == 0)
          {
            sb.Append("There is an assessment without a recap section\n\n");
          }
          else
          {
            foreach (var question in recapSection.Questions)
            {
              AppendAssessmentQuestionEvaluationMarkdown(sb, question, false);
            }

            sb.Append('\n');
          }
        }
      }

      sb.Append(CultureInfo.InvariantCulture, $"# Declarative knowledge for {unit.Title}\n\n");
      if (keyKnowledge.DeclarativeKnowledge.Count == 0)
      {
        sb.Append("(No declarative knowledge provided.)\n\n");
      }
      else
      {
        sb.Append(string.Join("\n", keyKnowledge.DeclarativeKnowledge.Select(o => $"- {o}")) + "\n\n");
      }
    }

    return sb.ToString().Trim();
  }

  private static void AppendUnitEvaluationHeader(StringBuilder sb, UnitEntity unit)
  {
    var term = string.IsNullOrWhiteSpace(unit.Term) ? string.Empty : $" {unit.Term} Term";
    sb.Append(CultureInfo.InvariantCulture, $"# {unit.Title} (Year {unit.YearGroup}{term})\n\n");

    if (!string.IsNullOrWhiteSpace(unit.WhyThis))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Why this?\n\n{unit.WhyThis}\n\n");
    }

    if (!string.IsNullOrWhiteSpace(unit.WhyNow))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Why now?\n\n{unit.WhyNow}\n\n");
    }
  }

  private static void AppendKeyKnowledgeEvaluationSection(StringBuilder sb, KeyKnowledge keyKnowledge)
  {
    sb.Append("## Key knowledge\n\n");
    if (keyKnowledge.DeclarativeKnowledge.Count == 0 && keyKnowledge.ProceduralKnowledge.Count == 0)
    {
      sb.Append("(No key knowledge provided.)\n\n");
    }
    else
    {
      if (keyKnowledge.DeclarativeKnowledge.Count > 0)
      {
        sb.Append("### Students must know that:\n\n" + string.Join("\n", keyKnowledge.DeclarativeKnowledge.Select(o => $"- {o}")) + "\n\n");
      }

      if (keyKnowledge.ProceduralKnowledge.Count > 0)
      {
        sb.Append("### Students must be able to:\n\n" + string.Join("\n", keyKnowledge.ProceduralKnowledge.Select(o => $"- {o}")) + "\n\n");
      }
    }
  }

  private static void AppendAssessmentEvaluationSection(StringBuilder sb, Assessment assessment, List<string> images)
  {
    sb.Append("## Assessment\n\n");
    if (!assessment.Sections.SelectMany(o => o.Questions).Any())
    {
      sb.Append("(No assessment provided.)");
    }
    else
    {
      var questionNumber = 1;
      foreach (var section in assessment.Sections.Where(o => o.Questions.Count > 0))
      {
        sb.Append(CultureInfo.InvariantCulture, $"### {section.Title}\n\n");
        foreach (var question in section.Questions)
        {
          AppendAssessmentQuestionEvaluationMarkdown(sb, question, true, questionNumber++, images);
        }

        sb.Append('\n');
      }
    }
  }

  private static void AppendAssessmentQuestionEvaluationMarkdown(StringBuilder sb, AssessmentQuestion question, bool includeMarkScheme, int? questionNumber = null, List<string> images = null)
  {
    if (question.Marks == 0) return;
    if (!string.IsNullOrWhiteSpace(question.Image) && images is not null) images.Add(question.Image);
    var questionPrefix = string.IsNullOrWhiteSpace(question.Image) ? string.Empty : images is null ? "[Image] " : $"[Image {images.Count}] ";
    var questionNumberPrefix = questionNumber.HasValue ? $"Q{questionNumber.Value}. " : string.Empty;
    var questionText = string.Join(" ", (question.Question ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0));
    var marksSuffix = question.Marks == 1 ? "mark" : "marks";
    sb.Append(CultureInfo.InvariantCulture, $"#### {questionNumberPrefix}{questionPrefix}{questionText} ({question.Marks} {marksSuffix})\n\n");

    if (question.Answers?.Count > 0)
    {
      sb.Append("Options:\n\n");
      foreach (var answer in question.Answers)
      {
        sb.Append(CultureInfo.InvariantCulture, $"- {(answer ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Replace("\n", "\n  ", StringComparison.Ordinal)}\n");
      }

      sb.Append('\n');
    }

    if (question.SuccessCriteria?.Count > 0)
    {
      sb.Append("Success criteria:\n\n");
      foreach (var criterion in question.SuccessCriteria)
      {
        sb.Append(CultureInfo.InvariantCulture, $"- {(criterion ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Replace("\n", "\n  ", StringComparison.Ordinal)}\n");
      }

      sb.Append('\n');
    }

    if (includeMarkScheme && !string.IsNullOrWhiteSpace(question.MarkScheme))
    {
      var markScheme = question.MarkScheme.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Trim();
      if (markScheme.Contains('\n', StringComparison.Ordinal))
        sb.Append(CultureInfo.InvariantCulture, $"Mark scheme:\n\n\"\"\"\n{markScheme}\n\"\"\"\n\n");
      else
        sb.Append(CultureInfo.InvariantCulture, $"Mark scheme: {markScheme}\n\n");
    }
  }

  private sealed record CourseEvaluationUnitContext(UnitEntity Unit, string KeyKnowledgeContext, EvaluationInput AssessmentInput);
  private sealed record EvaluationInput(string Text, IReadOnlyList<string> Images);

  private sealed class AssessmentRecapEvaluationResponse
  {
    public string Overview { get; set; } = string.Empty;
    public List<CourseEvaluationRecommendedAction> RecommendedActions { get; set; } = [];
  }
}

