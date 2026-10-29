namespace MyGymManager.Infrastructure.Providers;

using MyGymManager.Core.Interfaces;

/// <summary>
/// Provider xác minh danh tính qua máy quét mã QR Code thành viên.
/// Kế thừa IIdentificationProvider, hỗ trợ test giả lập qua SimulateSwipe.
/// </summary>
public class QrCodeIdentificationProvider : IIdentificationProvider
{
    private bool _disposed;

    public string ProviderId { get; }
    public string ProviderName { get; }
    public IdentificationMethod Method => IdentificationMethod.QrCode;
    public bool IsRunning { get; private set; }

    public event EventHandler<IdentificationResult>? IdentificationOccurred;

    public QrCodeIdentificationProvider(
        string providerId = "qr_scanner_1",
        string providerName = "Máy quét mã QR Code")
    {
        ProviderId = providerId;
        ProviderName = providerName;
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Giả lập hành vi quét mã QR Code thành viên để phục vụ kiểm thử.
    /// </summary>
    /// <param name="identifier">Nội dung mã QR (QrKey / Member Code).</param>
    /// <param name="confidence">Độ tin cậy giải mã QR (0.0 -> 1.0, mặc định 1.0f).</param>
    public void SimulateSwipe(string identifier, float confidence = 1.0f)
    {
        bool isSuccess = !string.IsNullOrWhiteSpace(identifier);
        var result = new IdentificationResult(
            IsSuccess: isSuccess,
            Identifier: identifier,
            Method: Method,
            ConfidenceScore: confidence,
            Timestamp: DateTime.UtcNow,
            DisplayName: ProviderName,
            ErrorMessage: isSuccess ? null : "Mã QR không hợp lệ hoặc rỗng."
        );

        IdentificationOccurred?.Invoke(this, result);
    }

    public void SimulateSwipe(string identifier, double confidence)
    {
        SimulateSwipe(identifier, (float)confidence);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            IsRunning = false;
        }
        _disposed = true;
    }
}
