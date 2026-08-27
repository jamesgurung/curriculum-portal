using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CurriculumPortal;

public static partial class Api
{
  private static void MapSyncPaths(WebApplication app)
  {
    app.MapPut("/api/users", [AllowAnonymous] async (HttpContext context, [FromHeader(Name = "X-Api-Key")] string auth, ConfigService config, AppOptions options) =>
    {
      if (string.IsNullOrEmpty(options.SyncApiKey)) return Results.Conflict("An sync API key is not configured.");
      if (auth != options.SyncApiKey) return Results.Unauthorized();

      var formFiles = context.Request.Form.Files;
      if (formFiles.Count != 2) return Results.BadRequest();
      if (formFiles.Any(o => o.Length == 0)) return Results.BadRequest();
      var teachersFile = formFiles.SingleOrDefault(o => o.Name == "teachers");
      var studentsFile = formFiles.SingleOrDefault(o => o.Name == "students");
      if (teachersFile is null || studentsFile is null) return Results.BadRequest();

      using (var teachersStream = teachersFile.OpenReadStream())
      {
        using var teachersReader = new StreamReader(teachersStream);
        var teachersCsv = await teachersReader.ReadToEndAsync();
        await config.UpdateDataFileAsync("teachers.csv", teachersCsv);
      }

      using (var studentsStream = studentsFile.OpenReadStream())
      {
        using var studentsReader = new StreamReader(studentsStream);
        var studentsCsv = await studentsReader.ReadToEndAsync();
        await config.UpdateDataFileAsync("students.csv", studentsCsv);
      }

      await config.ReloadAsync();
      return Results.NoContent();
    });

    app.MapGet("/api/completion/{dueDate}", [AllowAnonymous] async (DateOnly dueDate, [FromHeader(Name = "X-Api-Key")] string auth, AssignmentService assignmentService, AppOptions options) =>
    {
      if (string.IsNullOrEmpty(options.SyncApiKey)) return Results.Conflict("An sync API key is not configured.");
      if (auth != options.SyncApiKey) return Results.Unauthorized();
      if (dueDate >= DateOnly.FromDateTime(DateTime.UtcNow)) return Results.BadRequest("Due date must be in the past.");

      var students = await assignmentService.GetStudentsWithCompletionAsync(dueDate);
      return Results.Json(new
      {
        dueDate,
        completedQuestions = students.Sum(o => o.CompletedQuestions),
        totalQuestions = students.Sum(o => o.TotalQuestions)
      });
    });
  }
}
