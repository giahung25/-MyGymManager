namespace MyGymManager.Core.Events;

using MyGymManager.Core.Entities;

/// <summary>
/// Dữ liệu sự kiện khi hệ thống nhận diện thành công một hội viên (Observer Pattern).
/// Bắn tín hiệu về UI Thread (WPF) để hiển thị thông báo và phát âm thanh chào mừng.
/// </summary>
public class MemberIdentifiedEventArgs : EventArgs
{
    public Member Member { get; init; } = null!;
    public float Confidence { get; init; }
    public DateTime Timestamp { get; init; }
    public bool IsNewCheckIn { get; init; }

    public MemberIdentifiedEventArgs() { }

    public MemberIdentifiedEventArgs(Member member, float confidence, bool isNewCheckIn = true)
    {
        Member = member;
        Confidence = confidence;
        Timestamp = DateTime.UtcNow;
        IsNewCheckIn = isNewCheckIn;
    }
}
