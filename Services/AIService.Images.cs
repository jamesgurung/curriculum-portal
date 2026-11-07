using OpenAI.Responses;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CurriculumPortal;

public sealed partial class AIService
{
  private readonly SemaphoreSlim _imageSemaphore = new(8, 8);
  private readonly SemaphoreSlim _imageSceneSemaphore = new(8, 8);
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _imageSubjectSemaphores = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, byte> _pendingImageUnits = new();

  private const string ImageSceneInstructions = """
    Using the unit title, rationale and key knowledge, describe a natural, representative view for an editorial-print curriculum header. Choose the most informative framing: a wide view when the unit's central idea depends on context or relationships across a setting, or a close-up when it depends on a subject's form, structure or detail. State the framing explicitly and use coherent perspective and natural proportions. Show only the surrounding context needed to make the subject clear. Keep the most informative features at a similar vertical level so a shallow crop retains them. For abstract units such as mathematics, first look for a clear, credible real-world application in which the concept is visibly at work. Prefer that scene when the connection is direct and recognisable, rather than merely a decorative resemblance. Use a restrained abstract composition only when no convincing real-world scene represents the concept without forcing it. In that case, show one concept or relationship with the minimum relevant forms or symbols; a single motif or short expression can be enough, without an invented setting or extra representations. Any essential notation should be one short, well-formed expression or fragment, without worked examples, derivations or answers. Avoid school settings and scenes of people studying. Avoid visual metaphors, enlarged symbolic objects, infographic layouts and posed line-ups. Keep the setting, period and underlying facts faithful to the unit. Describe only what is visible, without naming an artistic medium, specifying colours or including instructions or cautions. Include writing only when essential to the subject, such as algebraic symbols, keeping it minimal, relevant and mathematically or factually correct.
    The existingSceneSummaries list describes scenes already chosen for units in courses with the same subject code, including images still being generated. Choose a distinctly different representative view: vary the setting, main visible subject, activity or viewpoint rather than merely rewording an existing scene. Remain faithful to the unit's content.
    Return a JSON object with description and summary. The description must be one paragraph of 40–65 words, or shorter for a simple abstract composition. The summary must be 6–12 words identifying the distinctive subject, framing and any relevant setting or activity so later scenes can avoid repeating it. Include no heading or explanation.
    """;

  private const string CoverImageInstructions = """
    Create a representational editorial-print illustration for a curriculum content-page header. The image is quiet supporting artwork.
    Render the supplied scene at the specified subject scale and framing, with coherent perspective, natural proportions, spatial depth and natural light. Let the subject and any relevant surroundings or activity carry the meaning. Make the composition feel observed and unstaged. Abstract-unit exception: only when the supplied scene explicitly calls for an abstract composition, it replaces the physical-environment requirements above. Render only its essential forms or symbols against a quiet paper-like or softly inked ground, without an invented landscape, room or other setting. Use the same editorial-print palette, contours and grain. Preserve the specified shapes, proportions and relationships exactly, using simple geometry and ample quiet space. Do not add explanatory marks, extra notation or a worksheet layout. Avoid school settings and scenes of people studying. Preserve recognisable real forms while simplifying detail for a small header. Avoid infographic arrangements, isolated icon collections, oversized symbolic objects and people posed as a line-up.
    Where people appear, allow natural variation in ethnicity and gender, broadly reflecting life in England where appropriate to the setting and period.
    Style: a representational editorial screenprint with a modest degree of artistic simplification. Group light and shadow into broad, softly bounded ink-colour areas. Simplify incidental surface texture and facial detail while retaining natural proportions, believable materials, spatial depth and the details that define the subject. Use lightly hand-drawn contours, subtle overlapping colour separations and a very light, even ink grain. Use muted teal, slate blue, sage, warm cream and small terracotta/peach accents, allowing the depicted materials and light to guide their use. Keep contrast calm but legible. The scene should remain natural and recognisable, with only a gentle print-like stylisation; avoid photographic microtexture, heavy outlines, flat clip-art forms and distressed effects.
    Canvas: a full-bleed 3:1 landscape master. Desktop uses a centred 4:1 crop, removing the top 12.5% and bottom 12.5%; mobile shows the full master. Let the composition use the full width naturally. Keep the features essential to recognising the subject between 15% and 85% of canvas height; other parts of the subject may extend beyond the frame. The top and bottom may contain expendable background or continuation of the subject. Do not squeeze the whole scene into a central cluster or draw crop guides, borders or inset panels.
    Include writing only when essential to represent the subject itself; use only the minimal writing specified in the scene, with correct symbols and relationships. No decorative or incidental writing, labels, logos, watermarks or UI.
    """;

  public async Task<object> GenerateCourseImagesAsync(CourseEntity course, IReadOnlyList<UnitEntity> units,
    Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);
    var courses = (await _courseService.ListCoursesAsync(cancellationToken)).ToDictionary(other => other.RowKey, StringComparer.Ordinal);
    courses[course.RowKey] = course;
    return await GenerateImagesAsync(courses, units, reportProgress, cancellationToken);
  }

  public async Task<object> GenerateMissingImagesAsync(Action<int, int> reportProgress, CancellationToken cancellationToken = default)
  {
    var courses = (await _courseService.ListCoursesAsync(cancellationToken)).ToDictionary(course => course.RowKey, StringComparer.Ordinal);
    var units = await _courseService.ListUnitsAsync(cancellationToken: cancellationToken);
    return await GenerateImagesAsync(courses, units, reportProgress, cancellationToken);
  }

  private async Task<object> GenerateImagesAsync(IReadOnlyDictionary<string, CourseEntity> courses, IReadOnlyList<UnitEntity> units,
    Action<int, int> reportProgress, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(reportProgress);
    units = units.Where(unit => !unit.HasImage).ToList();
    reportProgress(0, units.Count);
    if (units.Count > 0)
      await _courseService.PrepareImageContainerAsync(cancellationToken);

    var completed = 0;
    var outcomes = await Task.WhenAll(units.Select(async unit =>
    {
      var claimed = _pendingImageUnits.TryAdd(unit.RowKey, 0);
      try
      {
        if (!claimed)
          return (Unit: unit, Generated: false, Error: "This unit's image is already being generated. Please wait for that run to finish.");
        if (!courses.TryGetValue(unit.PartitionKey, out var course))
          throw new InvalidOperationException("The course no longer exists.");

        UnitEntity current;
        UnitImageScene scene;
        var subjectKey = string.IsNullOrWhiteSpace(course.SubjectCode) ? $"course:{course.RowKey}" : $"subject:{course.SubjectCode}";
        var subjectSemaphore = _imageSubjectSemaphores.GetOrAdd(subjectKey, _ => new SemaphoreSlim(1, 1));
        await subjectSemaphore.WaitAsync(cancellationToken);
        try
        {
          await _imageSceneSemaphore.WaitAsync(cancellationToken);
          try
          {
            current = await _courseService.TryGetUnitAsync(course.RowKey, unit.RowKey, cancellationToken)
              ?? throw new InvalidOperationException("The unit no longer exists.");
            if (current.HasImage || await _courseService.RestoreUnitImageAsync(current, cancellationToken))
              return (Unit: current, Generated: false, Error: (string)null);

            var subjectCourseIds = courses.Values
              .Where(other => other.RowKey == course.RowKey || !string.IsNullOrWhiteSpace(course.SubjectCode)
                && string.Equals(other.SubjectCode, course.SubjectCode, StringComparison.OrdinalIgnoreCase))
              .Select(other => other.RowKey);
            var subjectUnits = await Task.WhenAll(subjectCourseIds.Select(id => _courseService.ListUnitsAsync(id, cancellationToken)));
            var summaries = subjectUnits.SelectMany(other => other)
              .Where(other => !string.IsNullOrWhiteSpace(other.ImageSceneSummary))
              .Select(other => other.ImageSceneSummary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var knowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, cancellationToken);
            scene = await GenerateUnitImageSceneAsync(course, current, knowledge, summaries, cancellationToken);
            await _courseService.UpdateUnitImageSceneSummaryAsync(current, scene.Summary, cancellationToken);
          }
          finally
          {
            _imageSceneSemaphore.Release();
          }
        }
        finally
        {
          subjectSemaphore.Release();
        }

        await _imageSemaphore.WaitAsync(cancellationToken);
        try
        {
          var image = await GenerateUnitImageAsync(scene.Description, cancellationToken);
          var generated = await _courseService.UploadUnitImageAsync(current, image, cancellationToken);
          return (Unit: current, Generated: generated, Error: (string)null);
        }
        finally
        {
          _imageSemaphore.Release();
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Cover image generation failed for unit {UnitId}", unit.RowKey);
        return (Unit: unit, Generated: false, Error: ex is InsufficientTokensException ? ex.Message : "The cover could not be generated. Please retry.");
      }
      finally
      {
        _cache.Invalidate("units");
        if (claimed)
          _pendingImageUnits.TryRemove(unit.RowKey, out _);
        reportProgress(Interlocked.Increment(ref completed), units.Count);
      }
    }));

    return new
    {
      generated = outcomes.Count(outcome => outcome.Generated),
      skipped = outcomes.Count(outcome => !outcome.Generated && outcome.Error is null),
      images = outcomes.Where(outcome => outcome.Error is null).Select(outcome => new { unitId = outcome.Unit.RowKey, imageUrl = outcome.Unit.ImageUrl }),
      failures = outcomes.Where(outcome => outcome.Error is not null).Select(outcome => new { unitId = outcome.Unit.RowKey, title = outcome.Unit.Title, message = outcome.Error })
    };
  }

  private async Task<UnitImageScene> GenerateUnitImageSceneAsync(CourseEntity course, UnitEntity unit, KeyKnowledge knowledge,
    IReadOnlyList<string> summaries, CancellationToken cancellationToken)
  {
    using var tokenReservation = await _tokenBudget.ReserveAsync(16000, cancellationToken);
    var schema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "description": { "type": "string" },
          "summary": { "type": "string" }
        },
        "required": ["description", "summary"],
        "additionalProperties": false
      }
      """u8.ToArray());
    var options = new CreateResponseOptions
    {
      Instructions = ImageSceneInstructions,
      Model = ModelName,
      StoredOutputEnabled = false,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium },
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("unit_image_scene", schema, jsonSchemaIsStrict: true) }
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(JsonSerializer.Serialize(new
    {
      course = course.Name,
      title = unit.Title,
      yearGroup = unit.YearGroup,
      whyThis = unit.WhyThis,
      whyNow = unit.WhyNow,
      declarativeKnowledge = knowledge.DeclarativeKnowledge,
      proceduralKnowledge = knowledge.ProceduralKnowledge,
      existingSceneSummaries = summaries
    }, JsonDefaults.CamelCase)));
    var response = await _aiClient.GetResponsesClient().CreateResponseAsync(options, cancellationToken);
    var json = string.Join(" ", response.Value.OutputItems.OfType<MessageResponseItem>().SelectMany(item => item.Content).Select(content => content.Text));
    var scene = JsonSerializer.Deserialize<UnitImageScene>(json, JsonDefaults.CamelCase);
    if (string.IsNullOrWhiteSpace(scene.Description) || string.IsNullOrWhiteSpace(scene.Summary) || scene.Summary.Length > 500)
      throw new InvalidOperationException("No valid scene description and summary were generated.");
    return scene;
  }

  private async Task<BinaryData> GenerateUnitImageAsync(string scene, CancellationToken cancellationToken)
  {
    using var http = _httpClientFactory.CreateClient();
    http.Timeout = TimeSpan.FromMinutes(10);
    var apiKey = string.IsNullOrWhiteSpace(_options.OpenAIImageApiKey) ? _options.OpenAIApiKey : _options.OpenAIImageApiKey;
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    var endpoint = string.IsNullOrWhiteSpace(_options.MicrosoftFoundryEndpoint)
      ? "https://api.openai.com/v1/images/generations"
      : $"{_options.MicrosoftFoundryEndpoint.TrimEnd('/')}/openai/v1/images/generations";
    for (var attempt = 0; ; attempt++)
    {
      using var imageResponse = await http.PostAsJsonAsync(endpoint, new
      {
        model = _options.OpenAIImageModel,
        prompt = $"{CoverImageInstructions}\n\nScene: {scene}",
        n = 1,
        size = "1536x512",
        complexity = "high",
        quality = "high",
        background = "opaque",
        output_format = "webp",
        output_compression = 80
      }, cancellationToken);
      if (imageResponse.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
      {
        var delay = imageResponse.Headers.RetryAfter?.Delta
          ?? imageResponse.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow
          ?? TimeSpan.FromSeconds(5 * Math.Pow(2, attempt));
        await Task.Delay((delay > TimeSpan.Zero ? delay : TimeSpan.Zero) + TimeSpan.FromMilliseconds(Random.Shared.Next(1000)), cancellationToken);
        continue;
      }

      imageResponse.EnsureSuccessStatusCode();
      using var imageJson = await JsonDocument.ParseAsync(await imageResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
      var bytes = Convert.FromBase64String(imageJson.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString());
      if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        throw new InvalidOperationException("The image service did not return a WebP image.");
      return new BinaryData(bytes);
    }
  }

  private readonly record struct UnitImageScene(string Description, string Summary);
}
