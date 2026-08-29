using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public sealed partial class AIService : IDisposable
{
  private readonly OpenAIClient _aiClient;
  private readonly CourseService _courseService;
  private readonly CacheService _cache;
  private readonly AITokenBudgetService _tokenBudget;
  private readonly ILogger<AIService> _logger;
  private readonly SemaphoreSlim _evaluationSemaphore = new(5, 5);

  public string ModelName { get; }

  public AIService(AppOptions options, CourseService courseService, CacheService cache, AITokenBudgetService tokenBudget, ILogger<AIService> logger)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(tokenBudget);
    ArgumentNullException.ThrowIfNull(logger);
    var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(10) };
    var credential = new ApiKeyCredential(options.OpenAIApiKey);

    if (!string.IsNullOrWhiteSpace(options.MicrosoftFoundryEndpoint))
    {
      clientOptions.Endpoint = new Uri($"{options.MicrosoftFoundryEndpoint.TrimEnd('/')}/openai/v1/");
    }

    _aiClient = new OpenAIClient(credential, clientOptions);
    _courseService = courseService;
    _cache = cache;
    _tokenBudget = tokenBudget;
    _logger = logger;
    ModelName = options.OpenAIModel;
  }

  public void Dispose() => _evaluationSemaphore.Dispose();

  public async Task<Assessment> ImportTextAssessmentAsync(string value, CancellationToken cancellationToken = default)
  {
    using var tokenReservation = await _tokenBudget.ReserveAsync(16000, cancellationToken);
    var client = _aiClient.GetResponsesClient();

    var systemMessage = """
      You are a meticulous assistant to the user, who is an experienced teacher. They will provide a school assessment in plain text format.
      Your task is to extract the questions in a structured JSON format.
      Please note:
      - An assessment consists of sections. Each section has a one-word title (usually "Recap", "Knowledge", and "Application") and contains questions. The title must only be one word.
      - If options are provided for a question, then it is multiple choice. The `answers` field should be an array of the four options, and the `markScheme` field should be the single letter a, b, c, or d. Set `lines` to null for multiple-choice questions.
      - All other questions are open-ended. For open-ended questions, set `answers` to null, and set the `lines` field to the estimated number of lines for a response (1 for one-word answers, up to 40 for extended writing).
      - The `marks` field should be the number of marks available for the question, as stated in the text. If not stated, estimate the number of marks in line with similar questions.
      - The `markScheme` field should exactly use the mark scheme provided at the end of the text, if available. Otherwise, suggest an appropriate mark scheme. For multiple-choice questions, this must be the letter of the correct answer. For open-ended questions, it is sometimes short and sometimes very long and detailed (in which case, copy the whole mark scheme text in full).
      - The `successCriteria` field should be an array of the success criteria for the question, if provided (otherwise, null).
      - Keep all the same questions provided by the user, but correct any spelling, punctuation, or grammatical errors in British English. Also rephrase questions for clarity if needed.
      - For mathematical expressions, always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.
      - Prefer double quotes ("") instead of single quotes (').
      """.Trim();

    var userMessage = ResponseItem.CreateUserMessageItem(value);

    var schema = BinaryData.FromBytes("""
    {
      "type": "object",
      "properties": {
        "sections": { "type": "array", "items": {
            "type": "object", "properties": {
              "title": { "type": "string" },
              "questions": {
                "type": "array",
                "items": { "type": "object", "properties": {
                    "question": { "type": "string" },
                    "marks": { "type": "integer" },
                    "markScheme": { "type": "string" },
                    "answers": { "type": ["array", "null"], "minItems": 4, "maxItems": 4, "items": { "type": "string" } },
                    "lines": { "type": ["integer", "null"] },
                    "successCriteria": { "type": ["array", "null"], "items": { "type": "string" } }
                  },
                  "required": ["question", "marks", "markScheme", "answers", "lines", "successCriteria"], "additionalProperties": false
                }
              }
            },
            "required": ["title", "questions"], "additionalProperties": false
          }
        }
      },
      "required": ["sections"], "additionalProperties": false
    }
    """u8.ToArray());

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Low },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("assessment", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<Assessment>(json, JsonDefaults.CamelCase) ?? new Assessment();
  }

  public async Task<List<QuestionBankQuestion>> GenerateQuizQuestionsAsync(UnitEntity unit, KeyKnowledge keyKnowledge, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(keyKnowledge);
    if (keyKnowledge.DeclarativeKnowledge.Count == 0)
    {
      return [];
    }

    using var tokenReservation = await _tokenBudget.ReserveAsync(12000, cancellationToken);
    var client = _aiClient.GetResponsesClient();

    var criteria = """
      # Criteria for question design
    
      - Each question must be multiple-choice with one correct answer and three incorrect answers.
      - Questions must provide high-quality retrieval practice of learnable, memorable facts.
      - The incorrect answers must be unambiguously wrong. There must be no debate or argument about which answer is correct.
      - The incorrect answers must be plausible, reasonable-sounding answers to the question. They must be credible alternatives that a student might genuinely confuse with the correct answer. They should be from the same conceptual category, use the same grammatical form, and have a similar level of specificity and realism. They could be near misses, misconceptions, confused terms, reversed cause/effect relationships, or answers that apply to a related concept but not this one.
      - Do not use absurd, extreme, or giveaway incorrect options, including opposites or negations of the correct answer. Avoid clue words such as "ignoring", "without", "always", "never", or "only" unless they are present across all options.
      - Assume that students have excellent common sense. They should not be able to guess the answer without strong subject-specific knowledge.
      - Before finalising each question, reject and rewrite any answer options that a student could eliminate without knowing the lesson content. If it proves difficult to find suitable options that meet these criteria, consider changing the question.
      - Design the questions to draw out common misconceptions where appropriate.
      - Ensure each question is worded so that it makes sense and is self-contained and answerable in its own right, without relying on previous questions or any other context. The questions will be presented to students out of sequence and alongside questions from other units.
      - Avoid asking multiple questions in one, for example by combining several related concepts into a single question. Similarly, avoid questions where the answer options require lists of three or more items each, as this can be confusing and make the question harder to answer.
      - Before returning the final JSON, silently reject and rewrite any question with ambiguous wording, multiple defensible answers, clueing, or answer options that are obviously implausible using common sense. Before finalising each question, apply this test: "Could a student with no lesson knowledge eliminate this option using common sense alone?" If yes, rewrite the option.
    
      # Style
    
      - Keep all questions and answers as succinct as possible. All answer options should be one word or a short phrase.
      - Use Tier 3 vocabulary and student-friendly language that is clear and accessible.
      - Avoid long, complex sentences and prefer plain English instead of technical notation.
      - Avoid the trap of the correct answers being noticeably longer than the incorrect answers.
      - During quizzing, the question will be shown for a few seconds before the options appear. Therefore, make sure the question text is answerable in its own right without seeing the options. For example, do not ask "Which of these...".
      - Use British English spelling and terminology.
      - Typically, start each answer option with a capital letter and do not use a full stop at the end.
      - For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted. Do not use backticks for code blocks or any other reason.
      """;

    var generateMessage = $"""
      You are an experienced secondary school teacher with strong pedagogical subject knowledge.
      The user will provide a list of facts that students need to learn and remember.
      Your task is to carefully design 15-30 questions that assess knowledge of these facts. Return these in a structured JSON format.
      Think carefully and reason about all your proposed questions and answers before generating a response.
      The difficulty and language should be appropriate for the age of the students.
      Most or all knowledge items should be covered by at least one question. If there are more than 30 knowledge items, prioritise the most important facts that students need to know and remember.
      If a knowledge item does not lend itself to retrieval practice, for example because it is a common-sense or obvious statement or too vague, do not write a question about it.
      If a stated fact is particularly knowledge-dense, you could ask multiple questions about it, as long as they are disjoint in what they assess.

      {criteria}
      """;

    var improveMessage = $"""
      You are an experienced secondary school teacher with strong pedagogical subject knowledge.
      The user will provide a list of multiple-choice questions.
      Your task is to develop the questions so that they meet all the requirements below. Return the improved questions in a structured JSON format.
      Above all:
      
      - fix any questions whose correct answer can be guessed through common sense; and
      - fix any questions where the indicated correct answer is inaccurate or any of the incorrect answers could be argued to be correct.
      
      Think carefully and reason about all questions and answers before generating a response.
      If a question is already high-quality, return it as-is. Do not make unnecessary changes.
      If a question or its answer options do not fully meet the criteria, make changes to improve it.
      Typically, you should only make minor changes (for example, modifying one or more answer options), but if needed you can rewrite the whole question.
      A new or amended question must assess the same core knowledge as the original.
      Condone questions that assess simplified knowledge or lack technical precision and specificity, as this may be intentional for students' current level.
      If a question is not salvageable, remove it.

      {criteria}
      """;

    var schema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "questions": {
            "type": "array",
            "maxItems": 30,
            "items": {
              "type": "object",
              "properties": {
                "question": { "type": "string" },
                "correctAnswer": { "type": "string" },
                "incorrectAnswer1": { "type": "string" },
                "incorrectAnswer2": { "type": "string" },
                "incorrectAnswer3": { "type": "string" }
              },
              "required": ["question", "correctAnswer", "incorrectAnswer1", "incorrectAnswer2", "incorrectAnswer3"],
              "additionalProperties": false
            }
          }
        },
        "required": ["questions"],
        "additionalProperties": false
      }
      """u8.ToArray());

    string CreateUserMessage(IEnumerable<string> knowledgeItems)
    {
      return $"# Year {unit.YearGroup} (age {unit.YearGroup + 4}) - {unit.Title}\n\n" + string.Join("\n", knowledgeItems.Select(o => $"- {o}"));
    }

    CreateResponseOptions CreateOptions(IEnumerable<string> knowledgeItems)
    {
      var options = new CreateResponseOptions
      {
        Instructions = generateMessage,
        ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium },
        StoredOutputEnabled = false,
        TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("questions", schema, jsonSchemaIsStrict: true) },
        Model = ModelName
      };
      options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
      options.InputItems.Add(ResponseItem.CreateUserMessageItem(CreateUserMessage(knowledgeItems)));
      return options;
    }

    async Task<List<QuestionBankQuestion>> GenerateQuizQuestionsForItemsAsync(IEnumerable<string> knowledgeItems)
    {
      var options = CreateOptions(knowledgeItems);
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      var questions = JsonSerializer.Deserialize<QuestionBank>(json, JsonDefaults.CamelCase)?.Questions ?? [];
      return await ImproveQuizQuestionsAsync(questions);
    }

    async Task<List<QuestionBankQuestion>> ImproveQuizQuestionsAsync(List<QuestionBankQuestion> questions)
    {
      var options = new CreateResponseOptions
      {
        Instructions = improveMessage,
        ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium },
        StoredOutputEnabled = false,
        TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("questions", schema, jsonSchemaIsStrict: true) },
        Model = ModelName
      };
      options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
      options.InputItems.Add(ResponseItem.CreateUserMessageItem(JsonSerializer.Serialize(new QuestionBank { Questions = questions }, JsonDefaults.CamelCase)));
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      return JsonSerializer.Deserialize<QuestionBank>(json, JsonDefaults.CamelCase)?.Questions ?? [];
    }

    if (unit.YearGroup < 10)
    {
      return await GenerateQuizQuestionsForItemsAsync(keyKnowledge.DeclarativeKnowledge);
    }

    async Task<List<QuestionBankQuestion>> GenerateBatchWithRetryAsync(string[] knowledgeItems)
    {
      const int maxAttempts = 3;
      for (var attempt = 1; ; attempt++)
      {
        try
        {
          return await GenerateQuizQuestionsForItemsAsync(knowledgeItems);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && attempt < maxAttempts)
        {
          await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
        }
      }
    }

    var batches = CreateKnowledgeBatches(keyKnowledge.DeclarativeKnowledge).Select((items, index) => (Items: items, Index: index)).ToList();
    var results = new List<QuestionBankQuestion>[batches.Count];
    var completed = 0;

    _logger.LogInformation("Generating quiz questions in {BatchCount} batches.", batches.Count);
    await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancellationToken }, async (batch, _) =>
    {
      results[batch.Index] = await GenerateBatchWithRetryAsync(batch.Items);
      _logger.LogInformation("Completed quiz question batches: {CompletedBatchCount}/{BatchCount}.", Interlocked.Increment(ref completed), batches.Count);
    });

    return results.SelectMany(o => o ?? []).ToList();
  }

  private static List<string[]> CreateKnowledgeBatches(List<string> knowledgeItems)
  {
    if (knowledgeItems.Count < 40)
    {
      // Counts 31-39 cannot be split into multiple 20-30 item batches.
      return [knowledgeItems.ToArray()];
    }

    var batchCount = (int)Math.Ceiling(knowledgeItems.Count / 30d);
    var batchSize = knowledgeItems.Count / batchCount;
    var largerBatchCount = knowledgeItems.Count % batchCount;
    var batches = new List<string[]>(batchCount);
    var index = 0;

    for (var i = 0; i < batchCount; i++)
    {
      var currentBatchSize = batchSize + (i < largerBatchCount ? 1 : 0);
      batches.Add(knowledgeItems.Skip(index).Take(currentBatchSize).ToArray());
      index += currentBatchSize;
    }

    return batches;
  }

  public async Task<int> CreateQuizQuestionsAsync(CancellationToken cancellationToken = default)
  {
    var units = await _courseService.ListUnitsAsync(cancellationToken: cancellationToken);
    var unitsToProcess = units.Where(o => o.YearGroup <= 9 && o.RevisionQuizStatus < 2 && o.KeyKnowledgeStatus == 2).ToList();
    if (unitsToProcess.Count == 0) return 0;
    var processed = 0;

    await Parallel.ForEachAsync(unitsToProcess, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancellationToken }, async (unit, ct) =>
    {
      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, ct);
      var questions = await GenerateQuizQuestionsAsync(unit, keyKnowledge, ct);
      if (questions.Count == 0)
      {
        return;
      }

      var questionBank = new QuestionBank { Questions = questions };
      await _courseService.UploadBlobAsync(unit.RowKey, questionBank, CancellationToken.None);

      keyKnowledge.RevisionQuiz = questionBank.Questions.Select(o => new KeyKnowledgeRevisionQuestion
      {
        Question = o.Question,
        CorrectAnswer = o.CorrectAnswer,
        IncorrectAnswer = o.IncorrectAnswer1
      }).ToList();
      await _courseService.UploadBlobAsync(unit.RowKey, keyKnowledge, CancellationToken.None);
      _cache.Update(unit.RowKey, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));

      unit.RevisionQuizStatus = 2;
      await _courseService.UpdateUnitAsync(unit, CancellationToken.None);
      var completed = Interlocked.Increment(ref processed);

      _logger.LogInformation("Generated quiz questions for unit {UnitTitle} ({CompletedUnitCount}/{UnitCount}).", unit.Title, completed, unitsToProcess.Count);
    });

    _cache.Invalidate("units");
    return processed;
  }

  public async Task<string> GenerateMarkSchemeAsync(string courseId, AssessmentQuestion question, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(courseId);
    ArgumentNullException.ThrowIfNull(question);
    using var tokenReservation = await _tokenBudget.ReserveAsync(20000, cancellationToken);
    var isMathematics = courseId.Contains("mathematics", StringComparison.OrdinalIgnoreCase);
    var client = _aiClient.GetResponsesClient();

    var questionText = $"{question.Question} ({question.Marks} mark{(question.Marks == 1 ? "" : "s")})";
    string specificInstructions;
    var reasoningEffort = ResponseReasoningEffortLevel.None;

    if (question.Answers is not null && question.Answers.Count == 4)
    {
      questionText += "\n\n" + string.Join("\n", question.Answers.Select((o, i) => $"{(char)('a' + i)}. {o}"));
      specificInstructions = "The question is multiple-choice. Please provide the letter of the correct answer (a, b, c, or d) in the markScheme field. Only respond with one letter, nothing else.";
    }
    else if (question.Marks == 1)
    {
      specificInstructions = "This is a one-mark answer, so respond only with a word or short phrase. Only if there are multiple possible completely different answers, say 'Accept:' and a comma-separated list. Do not do this for different variations of the same answer - just say the answer once.";
      if (isMathematics)
      {
        specificInstructions += "\n* You MUST preface the answer with 'A1: ' to indicate that this is an accuracy mark.";
      }
    }
    else if (question.Marks < 6)
    {
      specificInstructions = $"This is a {question.Marks}-mark question, so specify precisely and succinctly what is required for each mark. Where necessary, include indicative content. Keep it very brief and use new lines sparingly.";
      if (isMathematics)
      {
        specificInstructions += "\n* You MUST preface each line of working with 'M1: ' for a method mark, 'A1: ' for an accuracy (answer) mark, or possibly M2, A2, etc. where multiple marks are issued at once (in which case, the next line must show in brackets how the corresponding M1 or A1 would be awarded).";
      }

      reasoningEffort = ResponseReasoningEffortLevel.Low;
    }
    else
    {
      specificInstructions = $"This is an extended writing question worth {question.Marks}, so respond with a comprehensive mark scheme. Make it specific, objective, and ambitious, not generic. Consider splitting the marks into sections, and specify precisely and succinctly what is required for each mark, including examples where appropriate.";
      if (isMathematics)
      {
        specificInstructions += "\n* You MUST preface each line of working with 'M1: ' for a method mark, 'A1: ' for an accuracy (answer) mark, or possibly M2, A2, etc. where multiple marks are issued at once (in which case, the next line must show in brackets how the corresponding M1 or A1 would be awarded).";
      }

      reasoningEffort = ResponseReasoningEffortLevel.Medium;
    }

    if (question.SuccessCriteria is not null && question.SuccessCriteria.Count > 0)
    {
      questionText += "\n\nSuccess criteria:\n" + string.Join("\n", question.SuccessCriteria.Select(o => $"- {o}"));
      specificInstructions += "\n* If appropriate, base the mark scheme on the success criteria.";
    }

    var systemMessage = $"""
    You are an experienced teacher. The user will provide a question from a school assessment.
    Your task is to generate a mark scheme.
    Instructions:
    * {specificInstructions}
    * Use British English spelling and terminology.
    * For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.{(question.Marks == 1 ? " Do NOT use \\displaystyle." : string.Empty)}
    * Prefer double quotes ("") instead of single quotes (').
    * Respond with the final mark scheme in the markScheme field, and nothing else.
    """.Trim();

    var userMessage = ResponseItem.CreateUserMessageItem(questionText);
    var schema = BinaryData.FromBytes("""{"type": "object", "properties": { "markScheme": { "type": "string" } }, "required": ["markScheme"], "additionalProperties": false}"""u8.ToArray());

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = reasoningEffort },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("markScheme", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<MarkSchemeResponse>(json, JsonDefaults.CamelCase)?.MarkScheme ?? string.Empty;
  }

  public async Task<KeyKnowledge> GenerateKeyKnowledgeAsync(string value, CancellationToken cancellationToken = default)
  {
    using var tokenReservation = await _tokenBudget.ReserveAsync(20000, cancellationToken);
    var client = _aiClient.GetResponsesClient();

    var systemMessage = """
    You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
    The user will provide a list of learning outcomes or lesson plans that students will study within a scheme of work. Your task is to identify all the key knowledge from this whole scheme.
    Be mindful that the scheme fits within a wider curriculum, and prior and subsequent knowledge should not be listed. Focus on this specific scheme and its level of challenge.
    Do not include any knowledge that is factually incorrect.
    
    # Declarative knowledge
    Identify the declarative knowledge items. These are the core facts that are essential to a rigorous understanding of the subject matter.
    Write items that are specific enough to be assessed in a knowledge quiz.
    Be sure to actually state the facts, not just signpost them. For example, instead of "Know the houses of Hogwarts.", you would write "The houses of Hogwarts are Gryffindor, Hufflepuff, Ravenclaw, and Slytherin."
    Be comprehensive and ambitious in your coverage. Each item can be information-dense as long as it remains clear and accessible and is written as a single sentence.
    List 10-20 items. If there are more than 20 items, prioritise the most important ones (the 'powerful knowledge' that underpins deep understanding).
    Think carefully about the level of detail and rigour that is appropriate for secondary school students, based on the learning outcomes provided.

    # Procedural knowledge
    Identify the specific, knowledge-rich skills and techniques that students need to develop.
    Write them as clear, observable actions, each starting with a verb (e.g. "Evaluate...", "Solder...").
    Ensure the skills are precise enough to be assessed through performance, demonstration, or worked responses.
    Exclude generic study skills and vague verbs like "know" or "understand".
    There are typically fewer procedural knowledge items than declarative knowledge, so be selective and focused.
    List 5-10 items. If there are more than 10 items, prioritise the most important skills.
    Where appropriate, include brief, succinct success criteria within the sentence. Use the scheme provided by the user for guidance, but for brevity only mention the most essential success criteria. For example, instead of "Bowl a cricket ball", you might write "Bowl a cricket ball with a smooth run-up, releasing it overarm so it bounces on the pitch and aims accurately at the stumps."

    # Style
    Respond with a JSON object containing two arrays: declarativeKnowledge and proceduralKnowledge.
    Use Tier 3 vocabulary and student-friendly language that is clear and accessible. Avoid long, complex sentences.
    Prefer plain English instead of technical notation.
    Use British English spelling and terminology.
    For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted. Do not use backticks for code blocks or any other reason.
    """.Trim();

    var userMessage = ResponseItem.CreateUserMessageItem(value);

    var schema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "declarativeKnowledge": {
            "type": "array",
            "minItems": 10,
            "maxItems": 20,
            "items": { "type": "string" }
          },
          "proceduralKnowledge": {
            "type": "array",
            "minItems": 5,
            "maxItems": 10,
            "items": { "type": "string" }
          }
        },
        "required": ["declarativeKnowledge", "proceduralKnowledge"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("keyKnowledge", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<KeyKnowledge>(json, JsonDefaults.CamelCase) ?? new KeyKnowledge();
  }

  public async Task<KeyKnowledge> EnhanceKeyKnowledgeAsync(CourseEntity course, UnitEntity unit, IReadOnlyList<UnitEntity> units, EnhanceKeyKnowledgeRequest model,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(units);
    ArgumentNullException.ThrowIfNull(model);

    using var tokenReservation = await _tokenBudget.ReserveAsync(20000, cancellationToken);
    var client = _aiClient.GetResponsesClient();
    var systemMessage = """
      You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
      The user will provide the existing key knowledge statements for a unit of work. Your task is to enhance these statements if required to meet the stated expectations. When specific feedback is provided, also incorporate it by adding, removing, or adapting statements as required. Do not make any other changes.

      # Scope
      All finalised statements must meet the requirements in these instructions.
      If necessary, statements may be reworded, restructured, combined, or split into multiple statements.
      Items listed in the wrong category (declarative or procedural) must be moved.
      Preserve existing statements verbatim when they are already suitable.
      Beyond changes required by the feedback provided, do not make any other substantive modifications, such as adding new knowledge items, removing items, adding non-essential detail, or significantly changing the scope of an existing item.
      The only exception is if a statement is factually incorrect, in which case it should be corrected. However, do not modify any statements that may have been intentionally simplified for students' current level, such as "An event with probability 0 will never happen.".
      Be mindful that the unit fits within a wider curriculum, so the omission of prior and subsequent knowledge may be intentional.
      If any statements are borderline acceptable, have a bias towards preserving the current wording or making only minor changes.

      # Declarative knowledge
      Declarative knowledge items are the core facts that are essential to a rigorous understanding of the subject matter.
      Facts must be stated in full sentences, not just signposted. For example, instead of "Know the houses of Hogwarts.", the correct statement would be "The houses of Hogwarts are Gryffindor, Hufflepuff, Ravenclaw, and Slytherin."
      Do not accept vague or generic statements or abbreviated sentence forms.
      Items must be specific enough to be assessed in a knowledge quiz. However, avoid adding detail not present in the original statements unless it is required to act on the provided feedback or meet other requirements.
      Each item may be information-dense as long as it remains clear and accessible and is written as a single sentence.

      # Procedural knowledge
      Procedural knowledge items are specific, knowledge-rich skills and techniques that students need to develop.
      They must be written as clear, observable actions, each starting with a verb (e.g. "Evaluate...", "Solder...").
      Skills must be precise enough to be assessed through performance, demonstration, or worked responses.
      Do not accept generic study skills and vague verbs like "know" or "understand".
      Where appropriate, the most essential brief, succinct success criteria can be included within the sentence. For example, instead of "Bowl a cricket ball", a statement might say "Bowl a cricket ball with a smooth run-up, releasing it overarm so it bounces on the pitch and aims accurately at the stumps."

      # Style
      Use Tier 3 vocabulary and straightforward, student-friendly language that is clear and accessible. Avoid long, complex sentences.
      Prefer plain English instead of technical notation.
      Use British English spelling and terminology.
      Use the Oxford comma in lists.
      All statements must begin with a capital letter and end with a full stop.
      All statements must be individually self-contained, without relying on the context of previous items. To avoid repetition, the context of the subject name and unit title may be assumed.
      For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted. Fix any malformed LaTeX expressions. Do not use backticks for code blocks or any other reason.
      If there are any existing [img:n] image references, preserve them. Do not add any new image references.

      # Response
      Respond with a JSON object containing two arrays: declarativeKnowledge and proceduralKnowledge. Both final lists must be returned in full.
      If there are no changes, echo back the original statements.
      """.Trim();

    var keyKnowledge = new KeyKnowledge
    {
      DeclarativeKnowledge = (model.DeclarativeKnowledge ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).ToList(),
      ProceduralKnowledge = (model.ProceduralKnowledge ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).ToList()
    };
    var input = new StringBuilder();
    input.Append(CultureInfo.InvariantCulture, $"# Year {unit.YearGroup} {course.Name}: {unit.Title}\n\n");
    AppendKeyKnowledgeEvaluationSection(input, keyKnowledge);
    input.Append("\n\n## Feedback to incorporate\n\n");
    var feedback = (model.Feedback ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
    input.Append(feedback.Count == 0 ? "(None - do not make substantive changes.)" : string.Join("\n", feedback.Select(o => $"- {o}")));

    var schema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "declarativeKnowledge": {
            "type": "array",
            "items": { "type": "string" }
          },
          "proceduralKnowledge": {
            "type": "array",
            "items": { "type": "string" }
          }
        },
        "required": ["declarativeKnowledge", "proceduralKnowledge"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("enhancedKeyKnowledge", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(input.ToString()));
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<KeyKnowledge>(json, JsonDefaults.CamelCase) ?? new KeyKnowledge();
  }

  public async Task<List<UnitRationaleResponse>> EnhanceCourseRationalesAsync(CourseEntity course, IReadOnlyList<UnitEntity> units,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(course);
    ArgumentNullException.ThrowIfNull(units);

    using var tokenReservation = await _tokenBudget.ReserveAsync(Math.Max(32000, units.Count * 4000), cancellationToken);
    var client = _aiClient.GetResponsesClient();
    var systemMessage = """
      You are an experienced secondary school teacher and expert curriculum designer. The user will provide a course and all its units. Your task is to enhance the "Why this?" and "Why now?" statements for every unit.

      # Requirements
      - "Why this?" must explain the curricular purpose and benefits of learning the content in its own right. It must not justify inclusion by referring to an examination or specification.
      - "Why now?" must explain why the unit is taught at this point, which might include meaningful prerequisites, progression, or connections with earlier and later units where the supplied curriculum supports them.
      - Use the course name, curriculum intent, unit titles, existing rationales, and key knowledge to understand the curriculum as a whole.
      - Preserve accurate meaning and intentional curriculum choices. Improve clarity, specificity, coherence, and sequencing explanations without inventing unsupported facts or relationships.
      - If an existing statement is already strong, preserve its meaning and make only necessary improvements. If it is missing, write an appropriate statement from the supplied context.
      - If an existing statement is inaccurate or misleading, correct it. If it is accurate but weak, improve and expand upon it.
      - Keep each statement concise and information-dense, normally 1-3 sentences.
      - Write complete sentences with correct grammar, punctuation, and spelling. Avoid sentence fragments.
      - Prefer the Oxford comma in lists.
      - Prefer "we"/"us" instead of "you" or "students".
      - Vary the structures of the statements to avoid repetition. For example, do not start them all in the same way or use the same sentence patterns.
      - Use British English spelling and terminology.

      # Response format
      - Return every supplied unit exactly once, in the same order, using its unit ID exactly as provided.
      - Return the units in the rationales array.
      - Do not include any new lines or paragraphs, Markdown formatting, code fences, or LaTeX.
      - If there are any issues in the unit titles or knowledge statements, do not comment on them. Write the Why this? and Why now? statements as best you can from the supplied context.
      """.Trim();

    var knowledgeTasks = units.Select(unit => _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey, cancellationToken));
    var keyKnowledge = await Task.WhenAll(knowledgeTasks);
    var input = new StringBuilder();
    input.Append(CultureInfo.InvariantCulture, $"# Course\n\n## Course name\n\n{course.Name}\n\n## Curriculum intent\n\n{(string.IsNullOrWhiteSpace(course.Intent) ? "(Not provided.)" : course.Intent)}\n\n# Units\n\n");

    for (var i = 0; i < units.Count; i++)
    {
      var unit = units[i];
      input.Append(CultureInfo.InvariantCulture, $"## Unit {i + 1}\n\n### Unit ID\n\n{unit.RowKey}\n\n### Unit title\n\n{unit.Title}\n\n### Schedule\n\nYear {unit.YearGroup}{(string.IsNullOrWhiteSpace(unit.Term) ? string.Empty : $" {unit.Term} Term")}\n\n### Existing Why this?\n\n{(string.IsNullOrWhiteSpace(unit.WhyThis) ? "(Not provided.)" : unit.WhyThis)}\n\n### Existing Why now?\n\n{(string.IsNullOrWhiteSpace(unit.WhyNow) ? "(Not provided.)" : unit.WhyNow)}\n\n");
      AppendRationaleKnowledge(input, "Declarative knowledge", "declarative", keyKnowledge[i].DeclarativeKnowledge ?? []);
      AppendRationaleKnowledge(input, "Procedural knowledge", "procedural", keyKnowledge[i].ProceduralKnowledge ?? []);
      input.Append("---\n\n");
    }

    var schema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "rationales": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "unitId": { "type": "string" },
                "whyThis": { "type": "string" },
                "whyNow": { "type": "string" }
              },
              "required": ["unitId", "whyThis", "whyNow"],
              "additionalProperties": false
            }
          }
        },
        "required": ["rationales"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("courseRationales", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(input.ToString()));
    var response = await client.CreateResponseAsync(options, cancellationToken);
    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;

    try
    {
      using var document = JsonDocument.Parse(json);
      if (document.RootElement.ValueKind != JsonValueKind.Object
        || !document.RootElement.TryGetProperty("rationales", out var rationalesElement)
        || rationalesElement.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.");

      var rationales = new List<UnitRationaleResponse>();
      foreach (var item in rationalesElement.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.Object)
          throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.");

        var properties = item.EnumerateObject().ToList();
        if (properties.Count != 3
          || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3
          || properties.Any(property => property.Name is not "unitId" and not "whyThis" and not "whyNow"))
          throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.");

        var unitId = item.GetProperty("unitId");
        var whyThis = item.GetProperty("whyThis");
        var whyNow = item.GetProperty("whyNow");
        if (unitId.ValueKind != JsonValueKind.String || whyThis.ValueKind != JsonValueKind.String || whyNow.ValueKind != JsonValueKind.String)
          throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.");

        var rationale = new UnitRationaleResponse
        {
          UnitId = unitId.GetString(),
          WhyThis = whyThis.GetString()?.Trim(),
          WhyNow = whyNow.GetString()?.Trim()
        };
        if (string.IsNullOrWhiteSpace(rationale.UnitId) || string.IsNullOrWhiteSpace(rationale.WhyThis) || string.IsNullOrWhiteSpace(rationale.WhyNow))
          throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.");

        rationales.Add(rationale);
      }

      return rationales;
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException("The AI returned an invalid unit rationale response. No changes were made.", ex);
    }
  }

  private static void AppendRationaleKnowledge(StringBuilder input, string heading, string label, List<string> items)
  {
    input.Append(CultureInfo.InvariantCulture, $"### {heading}\n\n");
    if (items.Count == 0)
    {
      input.Append(CultureInfo.InvariantCulture, $"(No {label} knowledge provided.)\n\n");
      return;
    }

    input.Append(string.Join("\n", items.Take(30).Select(item => $"- {item}")) + "\n");
    if (items.Count > 30)
    {
      var omitted = items.Count - 30;
      input.Append(CultureInfo.InvariantCulture, $"\n_{omitted} additional {label} knowledge {(omitted == 1 ? "item" : "items")} omitted._\n");
    }

    input.Append('\n');
  }

  public async Task<List<AssessmentQuestion>> GenerateQuestionsAsync(GenerateQuestionsRequest model, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(model);
    using var tokenReservation = await _tokenBudget.ReserveAsync(16000, cancellationToken);
    var client = _aiClient.GetResponsesClient();
    var systemMessage = $"""
    You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
    The user will provide a list of key knowledge for a unit. Your task is to write questions to assess this knowledge. Return these in a structured JSON format.

    # Multiple-choice questions    
    You must write {model.MultipleChoiceCount} multiple-choice questions, each with one correct answer and three incorrect answers.
    - The incorrect answers MUST be plausible and not easily dismissible, yet unambiguously wrong.
    - Incorrect answers must be credible alternatives that a student might genuinely confuse with the correct answer. They should be from the same category, use the same grammatical form, and have a similar level of specificity and realism.
    - Do not use absurd, extreme, or giveaway distractors, including simple opposites or negations of the correct answer.
    - Before finalising each question, reject and rewrite any answer option that a student could eliminate without knowing the lesson content.
    - Design the questions to draw out common misconceptions.
    - The difficulty and language should be appropriate for secondary school students.
    - Ensure each question is worded so that it makes sense and is self-contained and answerable in its own right, without relying on the answer options or any other context.
    - Before returning the final JSON, silently reject and rewrite any question with ambiguous wording, multiple defensible answers, clueing, or answer options that are obviously implausible using common sense.
    - All answer options should be one word or a short phrase. Avoid the trap of the correct answers being noticeably longer than the incorrect answers.

    # Short-answer questions
    You must write {model.ShortAnswerCount} short-answer questions.
    - Each question must have a clear and unambiguous short answer.
    - The answer MUST be a single word or short phrase (up to 3 words). Do not include questions with longer answers.
    - Assess students' recall and understanding of the declarative knowledge provided by the user.
    
    # Instructions
    Think carefully and review all your proposed questions and answers before generating a response. Criteria include:
    - Achieve the best possible coverage of all the knowledge provided by the user, prioritising the most important facts that students need to know and remember.
    - The questions must be clear and unambiguous, with only a single correct answer.
    - The questions MUST NOT overlap or assess the same knowledge as each other. They must also avoid knowledge already assessed in any existing questions provided by the user.
    - The difficulty and language should be appropriate for secondary school students.

    # Style
    - Keep all questions and answers as succinct as possible.
    - For mathematical expressions, always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.
    - Respond with a JSON object containing two arrays: multipleChoiceQuestions and shortAnswerQuestions.
    - Use Tier 3 vocabulary and student-friendly language that is clear and accessible. Avoid long, complex sentences and prefer plain English instead of technical notation.
    - Use British English spelling and terminology.
    - For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted. Do not use backticks for code blocks or any other reason.
    """.Trim();

    var userMessage = $"""
    # Key Knowledge
    {string.Join("\n", model.DeclarativeKnowledge.Select(o => $"* {o}"))}

    # Existing Questions
    {(model.ExistingQuestions.Count == 0 ? "(None)" : string.Join("\n", model.ExistingQuestions.Select(o => $"- {o}")))}

    # Task
    Carefully design {model.MultipleChoiceCount} multiple-choice questions and {model.ShortAnswerCount} short-answer questions to assess the key knowledge provided.
    """;

    var schema = BinaryData.FromBytes(Encoding.UTF8.GetBytes($$"""
      {
        "type": "object",
        "properties": {
          "multipleChoiceQuestions": {
            "type": "array",
            "minItems": {{model.MultipleChoiceCount}},
            "maxItems": {{model.MultipleChoiceCount}},
            "items": {
              "type": "object",
              "properties": {
                "question": { "type": "string" },
                "correctAnswer": { "type": "string" },
                "wrongAnswers": {
                  "type": "array",
                  "minItems": 3,
                  "maxItems": 3,
                  "items": { "type": "string" }
                }
              },
              "required": ["question", "correctAnswer", "wrongAnswers"],
              "additionalProperties": false
            }
          },
          "shortAnswerQuestions": {
            "type": "array",
            "minItems": {{model.ShortAnswerCount}},
            "maxItems": {{model.ShortAnswerCount}},
            "items": {
              "type": "object",
              "properties": {
                "question": { "type": "string" },
                "answer": { "type": "string" }
              },
              "required": ["question", "answer"],
              "additionalProperties": false
            }
          }
        },
        "required": ["multipleChoiceQuestions", "shortAnswerQuestions"],
        "additionalProperties": false
      }
      """));

    var options = new CreateResponseOptions
    {
      Instructions = systemMessage,
      ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Medium },
      StoredOutputEnabled = false,
      TextOptions = new ResponseTextOptions { TextFormat = ResponseTextFormat.CreateJsonSchemaFormat("questions", schema, jsonSchemaIsStrict: true) },
      Model = ModelName
    };
    options.Patch.Set("$.prompt_cache_options.mode"u8, "explicit");
    options.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    var typedQuestions = JsonSerializer.Deserialize<GenerateQuestionsResponse>(json, JsonDefaults.CamelCase) ?? new GenerateQuestionsResponse();

    return typedQuestions.MultipleChoiceQuestions.Select(o =>
      {
        var answers = new[] { o.CorrectAnswer, o.WrongAnswers[0], o.WrongAnswers[1], o.WrongAnswers[2] }.OrderBy(_ => Guid.NewGuid()).ToList();
        var markScheme = "abcd"[answers.IndexOf(o.CorrectAnswer)].ToString();
        return new AssessmentQuestion
        {
          Question = o.Question,
          Answers = answers,
          MarkScheme = markScheme,
          Marks = 1
        };
      })
      .Concat(typedQuestions.ShortAnswerQuestions.Select(o => new AssessmentQuestion
      {
        Question = o.Question,
        MarkScheme = o.Answer,
        Marks = 1,
        Lines = 1
      }))
      .ToList();
  }

}

public class InsufficientTokensException : Exception
{
  public InsufficientTokensException() : base("Daily OpenAI token limit has been reached.") { }
  public InsufficientTokensException(string message) : base(message) { }
  public InsufficientTokensException(string message, Exception innerException) : base(message, innerException) { }
}
