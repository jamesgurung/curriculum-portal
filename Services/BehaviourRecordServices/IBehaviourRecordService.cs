namespace CurriculumPortal;

public interface IBehaviourRecordService
{
  Task<(int Positive, int Negative)> IssueBehavioursAsync(Dictionary<string, List<User>> positiveStudentsByBehaviour, Dictionary<string, List<User>> negativeStudentsByBehaviour);
}
