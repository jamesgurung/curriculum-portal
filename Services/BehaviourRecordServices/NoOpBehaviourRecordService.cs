namespace CurriculumPortal;

public sealed class NoOpBehaviourRecordService : IBehaviourRecordService
{
  public Task<(int Positive, int Negative)> IssueBehaviours(Dictionary<string, List<User>> positiveStudentsByBehaviour, Dictionary<string, List<User>> negativeStudentsByBehaviour)
  {
    return Task.FromResult((0, 0));
  }
}
