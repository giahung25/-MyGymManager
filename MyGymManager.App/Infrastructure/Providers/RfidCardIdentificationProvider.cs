namespace MyGymManager.Infrastructure.Providers;

using MyGymManager.Core.Interfaces;

/// <summary>
/// Provider xác minh danh tính qua đầu đọc thẻ từ RFID / NFC (Mifare 13.56MHz).
/// Kế thừa IIdentificationProvider, hỗ trợ test giả lập qua SimulateSwipe.
/// </summary>
public class RfidCardIdentificationProvider : IIdentificationProvider
{
    private bool _disposed;

    public string ProviderId { get; }
    public string ProviderName { get; }
    public IdentificationMethod Method => IdentificationMethod.RfidCard;
    public bool IsRunning { get; private set; }

    public event EventHandler<IdentificationResult>? IdentificationOccurred;

    public RfidCardIdentificationProvider(
        string providerId = "rfid_reader_1",
        string providerName = "Đầu đọc thẻ từ RFID / NFC")
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
    /// Giả lập hành vi quẹt thẻ RFID để phục vụ kiểm thử.
    /// </summary>
    /// <param name="identifier">Mã UID thẻ RFID.</param>
    /// <param name="confidence">Độ tin cậy của lượt quét (mặc định 1.0f cho thẻ từ vật lý).</param>
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
            ErrorMessage: isSuccess ? null : "Mã thẻ RFID không hợp lệ hoặc rỗng."
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
