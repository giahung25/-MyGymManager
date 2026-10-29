namespace MyGymManager.Infrastructure.Providers;

using MyGymManager.Core.Interfaces;

/// <summary>
/// Provider xác minh danh tính qua máy quét vân tay sinh trắc học.
/// Kế thừa IIdentificationProvider, hỗ trợ test giả lập qua SimulateSwipe.
/// </summary>
public class FingerprintIdentificationProvider : IIdentificationProvider
{
    private bool _disposed;

    public string ProviderId { get; }
    public string ProviderName { get; }
    public IdentificationMethod Method => IdentificationMethod.Fingerprint;
    public bool IsRunning { get; private set; }

    public event EventHandler<IdentificationResult>? IdentificationOccurred;

    public FingerprintIdentificationProvider(
        string providerId = "fingerprint_scanner_1",
        string providerName = "Máy quét vân tay sinh trắc học")
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
    /// Giả lập hành vi đặt ngón tay / quét vân tay để phục vụ kiểm thử.
    /// </summary>
    /// <param name="identifier">Mã định danh dữ liệu vân tay (Fingerprint ID / Template Key).</param>
    /// <param name="confidence">Độ tin cậy khớp mẫu vân tay (0.0 -> 1.0, mặc định 1.0f).</param>
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
            ErrorMessage: isSuccess ? null : "Dữ liệu vân tay không hợp lệ hoặc rỗng."
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
