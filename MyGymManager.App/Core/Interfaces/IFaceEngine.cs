namespace MyGymManager.Core.Interfaces;

using OpenCvSharp;
using MyGymManager.Core.Models;

/// <summary>
/// Interface lõi AI nhận diện khuôn mặt (ONNX Runtime) tuân thủ Dependency Inversion Principle.
/// Kết nối bộ model InsightFace: SCRFD phát hiện khuôn mặt & ArcFace trích xuất vector đặc trưng.
/// </summary>
public interface IFaceEngine : IDisposable
{
    /// <summary>
    /// Phát hiện toàn bộ khuôn mặt trong một frame ảnh bằng mô hình SCRFD (det_10g.onnx).
    /// </summary>
    /// <param name="frame">Ảnh gốc từ camera stream.</param>
    /// <returns>Danh sách các khuôn mặt tìm thấy kèm 5 landmarks và điểm tin cậy.</returns>
    Task<List<FaceBox>> DetectFacesAsync(Mat frame);

    /// <summary>
    /// Trích xuất vector đặc trưng khuôn mặt chuẩn 512 chiều bằng mô hình ArcFace (w600k_r50.onnx).
    /// </summary>
    /// <param name="faceCrop">Ảnh khuôn mặt đã căn chỉnh chuẩn kích thước 112x112 qua Affine Transform.</param>
    /// <returns>Mảng float[512] đã chuẩn hóa L2 (norm = 1.0) hoặc null nếu không thành công.</returns>
    Task<float[]?> ExtractEmbeddingAsync(Mat faceCrop);
}
