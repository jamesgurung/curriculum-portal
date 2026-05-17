using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CurriculumPortal;

public partial class AIService
{
  private readonly OpenAIClient _aiClient;
  private readonly CourseService _courseService;
  private readonly CacheService _cache;
  private readonly string _model;
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

  public AIService(AppOptions options, CourseService courseService, CacheService cache)
  {
    ArgumentNullException.ThrowIfNull(options);
    var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(10) };
    var credential = new ApiKeyCredential(options.OpenAIApiKey);

    if (!string.IsNullOrWhiteSpace(options.MicrosoftFoundryEndpoint))
    {
      clientOptions.Endpoint = new Uri($"{options.MicrosoftFoundryEndpoint.TrimEnd('/')}/openai/v1/");
    }

    _aiClient = new OpenAIClient(credential, clientOptions);
    _courseService = courseService;
    _cache = cache;
    _model = options.OpenAIModel;
  }

  public async Task<Assessment> ImportTextAssessmentAsync(string value, CancellationToken cancellationToken = default)
  {
    var client = _aiClient.GetResponsesClient();

    var systemMessage = """
      You are a meticulous assistant to the user, who is an experienced teacher. They will provide a school assessment in plain text format.
      Your task is to extract the questions in a structured JSON format.
      Please note:
      * An assessment consists of sections. Each section has a one-word title (usually "Recap", "Knowledge", and "Application") and contains questions. The title must only be one word.
      * If options are provided for a question, then it is multiple choice. The `answers` field should be an array of the four options, and the `markScheme` field should be the single letter a, b, c, or d. Set `lines` to null for multiple-choice questions.
      * All other questions are open-ended. For open-ended questions, set `answers` to null, and set the `lines` field to the estimated number of lines for a response (1 for one-word answers, up to 40 for extended writing).
      * The `marks` field should be the number of marks available for the question, as stated in the text. If not stated, estimate the number of marks in line with similar questions.
      * The `markScheme` field should exactly use the mark scheme provided at the end of the text, if available. Otherwise, suggest an appropriate mark scheme. For multiple-choice questions, this must be the letter of the correct answer. For open-ended questions, it is sometimes short and sometimes very long and detailed (in which case, copy the whole mark scheme text in full).
      * The `successCriteria` field should be an array of the success criteria for the question, if provided (otherwise, null).
      * Keep all the same questions provided by the user, but correct any spelling, punctuation, or grammatical errors in British English. Also rephrase questions for clarity if needed.
      * For mathematical expressions, always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.
      * Prefer double quotes ("") instead of single quotes (').
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
      Model = _model
    };

    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<Assessment>(json, JsonOptions) ?? new Assessment();
  }

  public async Task<List<QuestionBankQuestion>> GenerateQuizQuestionsAsync(UnitEntity unit, KeyKnowledge keyKnowledge, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(unit);
    ArgumentNullException.ThrowIfNull(keyKnowledge);
    if (keyKnowledge.DeclarativeKnowledge.Count == 0)
    {
      return [];
    }

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
      - Before returning the final JSON, silently reject and rewrite any question with ambiguous wording, multiple defensible answers, clueing, or answer options that are obviously implausible using common sense. Before finalising each question, apply this test: "Could a student with no lesson knowledge eliminate this option using common sense alone?" If yes, rewrite the option.
    
      # Style
    
      - Keep all questions and answers as succinct as possible. All answer options should be one word or a short phrase.
      - Use Tier 3 vocabulary and student-friendly language that is clear and accessible.
      - Avoid long, complex sentences and prefer plain English instead of technical notation.
      - Avoid the trap of the correct answers being noticably longer than the incorrect answers.
      - During quizzing, the question will be shown for a few seconds before the options appear. Therefore, make sure the question text is answerable in its own right without seeing the options. For example, do not ask "Which of these...".
      - Use British English spelling and terminology.
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
        Model = _model
      };

      options.InputItems.Add(ResponseItem.CreateUserMessageItem(CreateUserMessage(knowledgeItems)));
      return options;
    }

    async Task<List<QuestionBankQuestion>> GenerateQuizQuestionsForItemsAsync(IEnumerable<string> knowledgeItems)
    {
      var options = CreateOptions(knowledgeItems);
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      var questions = JsonSerializer.Deserialize<QuestionBank>(json, JsonOptions)?.Questions ?? [];
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
        Model = _model
      };

      options.InputItems.Add(ResponseItem.CreateUserMessageItem(JsonSerializer.Serialize(new QuestionBank { Questions = questions }, JsonOptions)));
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      return JsonSerializer.Deserialize<QuestionBank>(json, JsonOptions)?.Questions ?? [];
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

    List<string[]> CreateKnowledgeBatches()
    {
      if (keyKnowledge.DeclarativeKnowledge.Count < 40)
      {
        // Counts 31-39 cannot be split into multiple 20-30 item batches.
        return [keyKnowledge.DeclarativeKnowledge.ToArray()];
      }

      var batchCount = (int)Math.Ceiling(keyKnowledge.DeclarativeKnowledge.Count / 30d);
      var batchSize = keyKnowledge.DeclarativeKnowledge.Count / batchCount;
      var largerBatchCount = keyKnowledge.DeclarativeKnowledge.Count % batchCount;
      var batches = new List<string[]>(batchCount);
      var index = 0;

      for (var i = 0; i < batchCount; i++)
      {
        var currentBatchSize = batchSize + (i < largerBatchCount ? 1 : 0);
        batches.Add(keyKnowledge.DeclarativeKnowledge.Skip(index).Take(currentBatchSize).ToArray());
        index += currentBatchSize;
      }

      return batches;
    }

    var batches = CreateKnowledgeBatches().Select((items, index) => (Items: items, Index: index)).ToList();
    var results = new List<QuestionBankQuestion>[batches.Count];
    var completed = 0;

    Console.WriteLine($"Generating quiz questions in {batches.Count} batches");
    await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancellationToken }, async (batch, _) =>
    {
      results[batch.Index] = await GenerateBatchWithRetryAsync(batch.Items);
      Console.WriteLine($"Completed batches: {Interlocked.Increment(ref completed)}/{batches.Count}");
    });

    return results.SelectMany(o => o ?? []).ToList();
  }

  public async Task<int> CreateQuizQuestionsAsync(CancellationToken cancellationToken = default)
  {
    var units = await _courseService.ListUnitsAsync();
    var unitsToProcess = units.Where(o => o.YearGroup <= 9 && o.RevisionQuizStatus < 2 && o.KeyKnowledgeStatus == 2).ToList();
    if (unitsToProcess.Count == 0)
    {
      return 0;
    }
    var processed = 0;

    await Parallel.ForEachAsync(unitsToProcess, new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = cancellationToken }, async (unit, ct) =>
    {
      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey);
      var questions = await GenerateQuizQuestionsAsync(unit, keyKnowledge, ct);
      if (questions.Count == 0)
      {
        return;
      }

      var questionBank = new QuestionBank { Questions = questions };
      await _courseService.UploadBlobAsync(unit.RowKey, questionBank);

      keyKnowledge.RevisionQuiz = questionBank.Questions.Select(o => new KeyKnowledgeRevisionQuestion
      {
        Question = o.Question,
        CorrectAnswer = o.CorrectAnswer,
        IncorrectAnswer = o.IncorrectAnswer1
      }).ToList();
      await _courseService.UploadBlobAsync(unit.RowKey, keyKnowledge);
      _cache.Update(unit.RowKey, JsonSerializer.Serialize(keyKnowledge, JsonDefaults.CamelCase));

      unit.RevisionQuizStatus = 2;
      await _courseService.UpdateUnitAsync(unit);
      var completed = Interlocked.Increment(ref processed);

      Console.WriteLine($"Generated quiz questions for unit {unit.Title} ({completed}/{unitsToProcess.Count})");
    });

    _cache.Invalidate("units");
    return processed;
  }

  public async Task<string> GenerateMarkSchemeAsync(string courseId, AssessmentQuestion question)
  {
    ArgumentNullException.ThrowIfNull(courseId);
    ArgumentNullException.ThrowIfNull(question);
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
      specificInstructions = $"This is a {question.Marks}-mark question, so specify precicely and succinctly what is required for each mark. Where necessary, include indicative content. Keep it very brief and use new lines sparingly.";
      if (isMathematics)
      {
        specificInstructions += "\n* You MUST preface each line of working with 'M1: ' for a method mark, 'A1: ' for an accuracy (answer) mark, or possibly M2, A2, etc. where multiple marks are issued at once (in which case, the next line must show in brackets how the corresponding M1 or A1 would be awarded).";
      }

      reasoningEffort = ResponseReasoningEffortLevel.Low;
    }
    else
    {
      specificInstructions = $"This is an extended writing question worth {question.Marks}, so respond with a comprehensive mark scheme. Make it specific, objective, and ambitious, not generic. Consider splitting the marks into sections, and specify precicely and succinctly what is required for each mark, including examples where appropriate.";
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
      Model = _model
    };

    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<MarkSchemeResponse>(json, JsonOptions)?.MarkScheme ?? string.Empty;
  }

  public async Task<KeyKnowledge> GenerateKeyKnowledgeAsync(string value, CancellationToken cancellationToken = default)
  {
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
    For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.
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
      Model = _model
    };

    options.InputItems.Add(userMessage);
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    return JsonSerializer.Deserialize<KeyKnowledge>(json, JsonOptions) ?? new KeyKnowledge();
  }

  public async Task<List<AssessmentQuestion>> GenerateQuestionsAsync(GenerateQuestionsRequest model, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(model);
    var client = _aiClient.GetResponsesClient();
    var systemMessage = $"""
    You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
    The user will provide a list of key knowledge for a unit. Your task is to write questions to assess this knowledge. Return these in a structured JSON format.

    # Multiple-choice questions    
    You must write {model.MultipleChoiceCount} multiple-choice questions, each with one correct answer and three incorrect answers.
    - The incorrect answers MUST be plausible and not easily dismissable, yet unambiguously wrong.
    - Incorrect answers must be credible alternatives that a student might genuinely confuse with the correct answer. They should be from the same category, use the same grammatical form, and have a similar level of specificity and realism.
    - Do not use absurd, extreme, or giveaway distractors, including simple opposites or negations of the correct answer.
    - Before finalising each question, reject and rewrite any answer option that a student could eliminate without knowing the lesson content.
    - Design the questions to draw out common misconceptions.
    - The difficulty and language should be appropriate for secondary school students.
    - Ensure each question is worded so that it makes sense and is self-contained and answerable in its own right, without relying on the answer options or any other context.
    - Before returning the final JSON, silently reject and rewrite any question with ambiguous wording, multiple defensible answers, clueing, or answer options that are obviously implausible using common sense.
    - All answer options should be one word or a short phrase. Avoid the trap of the correct answers being noticably longer than the incorrect answers.

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
    - For mathematical expressions (but not just numbers), always use LaTeX within backticks `...` for inline or within double dollar signs $$...$$ for display. Do NOT use \(...\) or \[...\] as these are not accepted.
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
      Model = _model
    };

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));
    var response = await client.CreateResponseAsync(options, cancellationToken);

    var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
    var typedQuestions = JsonSerializer.Deserialize<GenerateQuestionsResponse>(json, JsonOptions) ?? new GenerateQuestionsResponse();

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

  public async Task<string> SummariseCourseAsync(CourseEntity course, IReadOnlyList<UnitEntity> units)
  {
    ArgumentNullException.ThrowIfNull(course);
    var sb = new StringBuilder($"# {course.Name}\n\n");
    if (!string.IsNullOrWhiteSpace(course.Intent))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Course intent\n{course.Intent}\n\n");
    }

    if (!string.IsNullOrWhiteSpace(course.Specification))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Specification\n{course.Specification}\n\n");
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

      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey);
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

    var overallEvaluationPrompt = """
      You are an expert curriculum designer. The user will provide information about a course from a curriculum portal.
      Evaluate the overall quality of the curriculum, focusing on the selection and sequencing of knowledge.
      Do not evaluate assessment design, rubrics, lesson plans, or other artefacts in this response.

      Consider whether the curriculum is ambitious, knowledge-rich, broad, balanced, appropriately sequenced, accurate, and well matched to any National Curriculum or exam board requirements stated by the user.

      Return structured JSON only.
      Use every field in the schema. Use empty arrays only when there is no relevant feedback for that field.
      Give separate ratings for sequencing and for coverage and balance.
      Put concise plain-English strings in arrays. Do not include Markdown bullet syntax or headings in field values.
      Use British English spelling and terminology.
      If the user input includes an [Image] placeholder, assume a real image is part of the key knowledge at that point. Do not ask for the image.
      Provide one short summary paragraph, then prioritise the highest-impact feedback in the structured arrays.
      """;

    var keyKnowledgeEvaluationPrompt = """
      You are an experienced secondary school teacher with exceptional pedagogical subject knowledge.
      The user will provide one unit from a curriculum portal.
      Evaluate the quality of the key knowledge statements. Focus on how effectively they summarise the most powerful knowledge students need to know.

      Consider whether the statements are specific, accurate, ambitious, memorable, assessable, well matched to the unit, and written as meaningful knowledge rather than vague signposting.
      If key knowledge is missing, say this clearly and explain the most important consequence.

      Return structured JSON only.
      Use every field in the schema. Use empty arrays only when there is no relevant feedback for that field.
      The overview must be a single short sentence or paragraph, not bullet points.
      Put concise plain-English strings in arrays. Do not include Markdown bullet syntax or headings in field values.
      Use British English spelling and terminology.
      Provide concise, constructive, specific feedback in the structured arrays.
      """;

    var assessmentAlignmentEvaluationPrompt = """
      You are an experienced secondary school teacher with strong assessment expertise.
      The user will provide one unit from a curriculum portal, including key knowledge and assessment information where available.
      Evaluate how closely the assessment assesses students' understanding of the key knowledge.
      Focus only on alignment between the stated key knowledge and the assessment content; do not evaluate general assessment design quality unless it directly affects alignment.

      Consider coverage, omissions, overemphasis, whether questions test the intended knowledge, and whether mark schemes reflect the knowledge students should demonstrate.
      If the assessment or key knowledge is missing, say this clearly and explain what cannot be evaluated.

      Return structured JSON only.
      Use every field in the schema. Use empty arrays only when there is no relevant feedback for that field.
      Put concise plain-English strings in arrays. Do not include Markdown bullet syntax or headings in field values.
      Use British English spelling and terminology.
      If the user input includes an [Image] placeholder, assume a real image is part of the key knowledge or assessment at that point. Do not ask for the image.
      Set notApplicableReason to an empty string unless the assessment or key knowledge is missing and the alignment cannot be evaluated.
      Provide concise, constructive, specific feedback in the structured arrays.
      """;

    var assessmentDesignEvaluationPrompt = """
      You are an experienced secondary school teacher with strong assessment design expertise.
      The user will provide one unit assessment from a curriculum portal.
      Evaluate the quality of the assessment design.
      Focus only on the technical construction of the assessment; do not repeat feedback about whether the assessment covers the stated key knowledge.

      Consider whether questions are clear, unambiguous, appropriately challenging, well sequenced, resistant to guessing, supported by plausible distractors where relevant, and matched to useful mark schemes.
      For multiple-choice questions, look for implausible distractors, clueing, ambiguity, and correct answers that are noticeably longer than incorrect answers.
      If assessment information is missing, say this clearly and explain the most important next step.

      Return structured JSON only.
      Use every field in the schema. Use empty arrays only when there is no relevant feedback for that field.
      The overview must be a single short sentence or paragraph, not bullet points.
      Put concise plain-English strings in arrays. Do not include Markdown bullet syntax or headings in field values.
      Use British English spelling and terminology.
      If the user input includes an [Image] placeholder, assume a real image is part of the assessment at that point. Do not ask for the image.
      Set notApplicableReason to an empty string unless assessment information is missing and the design cannot be evaluated.
      Provide concise, constructive, specific feedback in the structured arrays.
      """;

    var overallSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "sequencingRating": { "type": "string", "enum": ["Excellent", "Broadly Good", "Moderate Issues", "Serious Issues"] },
          "coverageBalanceRating": { "type": "string", "enum": ["Excellent", "Broadly Good", "Moderate Issues", "Serious Issues"] },
          "summary": { "type": "string" },
          "sequencingIssues": { "type": "array", "items": { "type": "string" } },
          "coverageBalanceIssues": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["sequencingRating", "coverageBalanceRating", "summary", "sequencingIssues", "coverageBalanceIssues"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var keyKnowledgeSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "rating": { "type": "string", "enum": ["Excellent", "Broadly Good", "Moderate Issues", "Serious Issues"] },
          "overview": { "type": "string" },
          "specificityAccuracyIssues": { "type": "array", "items": { "type": "string" } },
          "missingOrVagueKnowledge": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["rating", "overview", "specificityAccuracyIssues", "missingOrVagueKnowledge"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var assessmentAlignmentSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "rating": { "type": "string", "enum": ["Excellent", "Broadly Good", "Moderate Issues", "Serious Issues"] },
          "alignedCoverage": { "type": "array", "items": { "type": "string" } },
          "gapsOrMisalignments": { "type": "array", "items": { "type": "string" } },
          "notApplicableReason": { "type": "string" }
        },
        "required": ["rating", "alignedCoverage", "gapsOrMisalignments", "notApplicableReason"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var assessmentDesignSchema = BinaryData.FromBytes("""
      {
        "type": "object",
        "properties": {
          "rating": { "type": "string", "enum": ["Excellent", "Broadly Good", "Moderate Issues", "Serious Issues"] },
          "overview": { "type": "string" },
          "designIssues": { "type": "array", "items": { "type": "string" } },
          "notApplicableReason": { "type": "string" }
        },
        "required": ["rating", "overview", "designIssues", "notApplicableReason"],
        "additionalProperties": false
      }
      """u8.ToArray());

    var total = 1 + (units.Count * 3);
    var completed = 0;
    var contexts = new List<CourseEvaluationUnitContext>();
    foreach (var unit in units)
    {
      var keyKnowledge = await _courseService.GetBlobAsync<KeyKnowledge>(unit.RowKey);
      var assessment = await _courseService.GetBlobAsync<Assessment>(unit.RowKey);
      contexts.Add(new CourseEvaluationUnitContext(unit, BuildUnitEvaluationContext(unit, keyKnowledge, assessment)));
    }

    var courseSummary = await SummariseCourseAsync(course, units);
    var semaphore = new SemaphoreSlim(5);
    var overallTask = RunEvaluationRequestAsync<CourseOverallEvaluationResponse>(
      semaphore,
      overallEvaluationPrompt,
      overallSchema,
      "courseOverallEvaluation",
      courseSummary,
      () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
      cancellationToken);

    var unitTasks = contexts.Select(async context =>
    {
      var keyKnowledgeTask = RunEvaluationRequestAsync<KeyKnowledgeEvaluationResponse>(
        semaphore,
        keyKnowledgeEvaluationPrompt,
        keyKnowledgeSchema,
        "keyKnowledgeEvaluation",
        context.Context,
        () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
        cancellationToken);

      var alignmentTask = RunEvaluationRequestAsync<AssessmentAlignmentEvaluationResponse>(
        semaphore,
        assessmentAlignmentEvaluationPrompt,
        assessmentAlignmentSchema,
        "assessmentAlignmentEvaluation",
        context.Context,
        () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
        cancellationToken);

      var designTask = RunEvaluationRequestAsync<AssessmentDesignEvaluationResponse>(
        semaphore,
        assessmentDesignEvaluationPrompt,
        assessmentDesignSchema,
        "assessmentDesignEvaluation",
        context.Context,
        () => reportProgress?.Invoke(Interlocked.Increment(ref completed), total),
        cancellationToken);

      await Task.WhenAll(keyKnowledgeTask, alignmentTask, designTask);
      return new CourseEvaluationUnitResult(context.Unit.Title, await keyKnowledgeTask, await alignmentTask, await designTask);
    }).ToList();

    await Task.WhenAll(unitTasks.Cast<Task>().Prepend(overallTask));

    return new CourseEvaluationResult
    {
      Overall = await overallTask,
      Units = unitTasks.Select(o => o.Result).ToList()
    };
  }

  private async Task<T> RunEvaluationRequestAsync<T>(SemaphoreSlim semaphore, string instructions, BinaryData schema, string schemaName, string input, Action onComplete,
    CancellationToken cancellationToken) where T : new()
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
#if DEBUG
        ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.None },
        Model = "gpt-5.4-mini"
#else
        ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.High },
        Model = _model
#endif
      };

      options.InputItems.Add(ResponseItem.CreateUserMessageItem(input));
      var response = await client.CreateResponseAsync(options, cancellationToken);
      var json = response.Value.OutputItems.OfType<MessageResponseItem>().First().Content.First().Text;
      return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
    }
    finally
    {
      semaphore.Release();
      onComplete?.Invoke();
    }
  }

  private static string BuildUnitEvaluationContext(UnitEntity unit, KeyKnowledge keyKnowledge, Assessment assessment)
  {
    var term = string.IsNullOrWhiteSpace(unit.Term) ? string.Empty : $" {unit.Term} Term";
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"# {unit.Title} (Year {unit.YearGroup}{term})\n\n");

    if (!string.IsNullOrWhiteSpace(unit.WhyThis))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Why this?\n{unit.WhyThis}\n\n");
    }

    if (!string.IsNullOrWhiteSpace(unit.WhyNow))
    {
      sb.Append(CultureInfo.InvariantCulture, $"## Why now?\n{unit.WhyNow}\n\n");
    }

    sb.Append("## Key knowledge\n\n");
    if (keyKnowledge.DeclarativeKnowledge.Count == 0 && keyKnowledge.ProceduralKnowledge.Count == 0)
    {
      sb.Append("(No key knowledge provided.)\n\n");
    }
    else
    {
      if (keyKnowledge.DeclarativeKnowledge.Count > 0)
      {
        sb.Append("### Students must know that:\n" + string.Join("\n", keyKnowledge.DeclarativeKnowledge.Select(o => $"- {o}")) + "\n\n");
      }

      if (keyKnowledge.ProceduralKnowledge.Count > 0)
      {
        sb.Append("### Students must be able to:\n" + string.Join("\n", keyKnowledge.ProceduralKnowledge.Select(o => $"- {o}")) + "\n\n");
      }
    }

    sb.Append("## Assessment\n\n");
    var questions = assessment.Sections.SelectMany(section => section.Questions.Select(question => new { section.Title, Question = question })).ToList();
    if (questions.Count == 0)
    {
      sb.Append("(No assessment provided.)");
    }
    else
    {
      foreach (var item in questions)
      {
        var questionPrefix = string.IsNullOrWhiteSpace(item.Question.Image) ? string.Empty : "[Image] ";
        sb.Append(CultureInfo.InvariantCulture, $"* {item.Title}: {questionPrefix}{item.Question.Question}");
        if (item.Question.Answers?.Count > 0)
        {
          sb.Append(CultureInfo.InvariantCulture, $" Options: {string.Join("; ", item.Question.Answers)}.");
        }

        if (!string.IsNullOrWhiteSpace(item.Question.MarkScheme))
        {
          sb.Append(CultureInfo.InvariantCulture, $" Mark scheme: {item.Question.MarkScheme}");
        }

        sb.Append('\n');
      }
    }

    return sb.ToString().Trim();
  }

  private sealed record CourseEvaluationUnitContext(UnitEntity Unit, string Context);
}
