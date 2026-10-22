namespace MyGymManager.Core.Models;

using OpenCvSharp;

/// <summary>
/// Đại diện cho kết quả phát hiện khuôn mặt từ mô hình SCRFD (det_10g.onnx).
/// Bao gồm tọa độ Bounding Box, điểm tin cậy và 5 điểm mốc đặc trưng (Landmarks).
/// </summary>
public class FaceBox
{
    public float X1 { get; init; }
    public float Y1 { get; init; }
    public float X2 { get; init; }
    public float Y2 { get; init; }
    public float Score { get; init; }

    /// <summary>
    /// 5 điểm mốc khuôn mặt chuẩn InsightFace:
    /// [0]: Mắt trái, [1]: Mắt phải, [2]: Đầu mũi, [3]: Khóe miệng trái, [4]: Khóe miệng phải.
    /// Dùng cho thuật toán Affine Transformation căn chỉnh khuôn mặt chuẩn 112x112 trước khi nạp vào ArcFace.
    /// </summary>
    public Point2f[]? Landmarks { get; init; }

    public float Width => Math.Max(0, X2 - X1);
    public float Height => Math.Max(0, Y2 - Y1);

    /// <summary>
    /// Chuyển đổi sang hình chữ nhật OpenCvSharp.Rect để vẽ khung hoặc cắt ảnh (Crop).
    /// </summary>
    public Rect ToRect() => new Rect((int)X1, (int)Y1, (int)Width, (int)Height);
}
