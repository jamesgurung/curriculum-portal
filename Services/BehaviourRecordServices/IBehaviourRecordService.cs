namespace CurriculumPortal;

public interface IBehaviourRecordService
{
  Task<(int Positive, int Negative)> IssueBehaviours(Dictionary<string, List<User>> positiveStudentsByBehaviour, Dictionary<string, List<User>> negativeStudentsByBehaviour);
}
