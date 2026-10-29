namespace MyGymManager.Services;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MyGymManager.Core.Entities;
using MyGymManager.Core.Events;
using MyGymManager.Core.Interfaces;
using MyGymManager.Data;

/// <summary>
/// Bộ điều phối Điểm danh đa phương thức độc lập (Central Attendance & Access Coordinator).
/// Quản lý xác thực hội viên qua: Thẻ từ RFID/NFC, Mã QR, Số điện thoại hoặc Thiết bị Access Control ngoài.
/// Cung cấp bộ nhớ đệm RAM tra cứu tức thời (< 0.001ms), chống ghi đúp (Debounce Cooldown) và ghi nhận CSDL SQLite.
/// </summary>
public class AttendanceCoordinator : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AudioNotificationService? _audioService;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly List<IIdentificationProvider> _registeredProviders = new();

    // Cache hội viên đang hoạt động đa chỉ mục (Tra cứu O(1) theo Guid, SĐT, Thẻ RFID, Mã QR)
    private readonly ConcurrentDictionary<Guid, Member> _membersById = new();
    private readonly ConcurrentDictionary<string, Member> _membersByPhone = new();
    private readonly ConcurrentDictionary<string, Member> _membersByCardUid = new();
    private readonly ConcurrentDictionary<string, Member> _membersByQrKey = new();

    // Debounce Cooldown: Lưu thời điểm check-in gần nhất của từng hội viên
    private readonly ConcurrentDictionary<Guid, DateTime> _lastCheckInTimes = new();

    private volatile bool _isPaused = false;
    private bool _disposed = false;

    public bool IsPaused => _isPaused;
    public TimeSpan CheckInCooldown { get; set; } = TimeSpan.FromSeconds(15);
    public int CachedMembersCount => _membersById.Count;

    public event EventHandler<MemberIdentifiedEventArgs>? MemberIdentified;
    public event EventHandler<string>? UnidentifiedDetected;

    public AttendanceCoordinator(AppDbContext dbContext, AudioNotificationService? audioService = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _audioService = audioService;
    }

    public void Pause() => _isPaused = true;
    public void Resume() => _isPaused = false;

    /// <summary>
    /// Đăng ký lắng nghe sự kiện từ một Provider xác minh danh tính bất kỳ (RFID, QR, Vân tay, Thiết bị ngoài).
    /// </summary>
    public void RegisterProvider(IIdentificationProvider provider)
    {
        if (provider == null) return;
        lock (_registeredProviders)
        {
            if (!_registeredProviders.Contains(provider))
            {
                provider.IdentificationOccurred += OnProviderIdentificationOccurred;
                _registeredProviders.Add(provider);
            }
        }
    }

    /// <summary>
    /// Hủy đăng ký một Provider.
    /// </summary>
    public void UnregisterProvider(IIdentificationProvider provider)
    {
        if (provider == null) return;
        lock (_registeredProviders)
        {
            if (_registeredProviders.Remove(provider))
            {
                provider.IdentificationOccurred -= OnProviderIdentificationOccurred;
            }
        }
    }

    private async void OnProviderIdentificationOccurred(object? sender, IdentificationResult result)
    {
        if (_isPaused || !result.IsSuccess || string.IsNullOrWhiteSpace(result.Identifier))
        {
            if (!result.IsSuccess)
            {
                UnidentifiedDetected?.Invoke(this, result.ErrorMessage ?? "Xác thực không thành công.");
            }
            return;
        }

        await ProcessCheckInAsync(result.Identifier, result.Method, result.ConfidenceScore);
    }

    /// <summary>
    /// Nạp toàn bộ hội viên đang hoạt động vào bộ nhớ RAM đa chỉ mục.
    /// </summary>
    public async Task LoadActiveMembersCacheAsync()
    {
        await _dbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var activeMembers = await _dbContext.Members
                .AsNoTracking()
                .Include(m => m.Package)
                .Where(m => m.IsActive)
                .ToListAsync()
                .ConfigureAwait(false);

            _membersById.Clear();
            _membersByPhone.Clear();
            _membersByCardUid.Clear();
            _membersByQrKey.Clear();

            foreach (var m in activeMembers)
            {
                IndexMember(m);
            }

            Debug.WriteLine($"[AttendanceCoordinator] Đã nạp {_membersById.Count} hội viên vào RAM Cache đa chỉ mục.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private void IndexMember(Member m)
    {
        _membersById[m.Id] = m;
        if (!string.IsNullOrWhiteSpace(m.PhoneNumber))
            _membersByPhone[m.PhoneNumber.Trim()] = m;
        if (!string.IsNullOrWhiteSpace(m.CardUid))
            _membersByCardUid[m.CardUid.Trim()] = m;
        if (!string.IsNullOrWhiteSpace(m.QrKey))
            _membersByQrKey[m.QrKey.Trim()] = m;
    }

    public void UpdateMemberInCache(Member member)
    {
        if (member == null) return;

        if (member.IsActive)
        {
            IndexMember(member);
        }
        else
        {
            RemoveMemberFromCache(member.Id);
        }
    }

    public void RemoveMemberFromCache(Guid memberId)
    {
        if (_membersById.TryRemove(memberId, out var m))
        {
            if (!string.IsNullOrWhiteSpace(m.PhoneNumber))
                _membersByPhone.TryRemove(m.PhoneNumber.Trim(), out _);
            if (!string.IsNullOrWhiteSpace(m.CardUid))
                _membersByCardUid.TryRemove(m.CardUid.Trim(), out _);
            if (!string.IsNullOrWhiteSpace(m.QrKey))
                _membersByQrKey.TryRemove(m.QrKey.Trim(), out _);
        }
        _lastCheckInTimes.TryRemove(memberId, out _);
    }

    /// <summary>
    /// Xử lý điểm danh hội viên bằng mã định danh bất kỳ (Id, SĐT, Mã Thẻ từ, Mã QR).
    /// </summary>
    public async Task<bool> ProcessCheckInAsync(string identifier, IdentificationMethod method = IdentificationMethod.Manual, float confidence = 1.0f)
    {
        if (_isPaused || string.IsNullOrWhiteSpace(identifier))
            return false;

        var key = identifier.Trim();
        Member? member = null;

        // 1. Thử tra cứu theo Guid Id
        if (Guid.TryParse(key, out var id) && _membersById.TryGetValue(id, out var foundById))
        {
            member = foundById;
        }
        // 2. Thử tra cứu theo Số điện thoại
        else if (_membersByPhone.TryGetValue(key, out var foundByPhone))
        {
            member = foundByPhone;
        }
        // 3. Thử tra cứu theo Mã thẻ từ RFID UID
        else if (_membersByCardUid.TryGetValue(key, out var foundByCard))
        {
            member = foundByCard;
        }
        // 4. Thử tra cứu theo Mã QR
        else if (_membersByQrKey.TryGetValue(key, out var foundByQr))
        {
            member = foundByQr;
        }

        if (member == null)
        {
            UnidentifiedDetected?.Invoke(this, $"Không tìm thấy hội viên với mã định danh '{identifier}'.");
            _audioService?.PlayUnidentifiedFace();
            return false;
        }

        bool isExpired = member.IsExpired();
        bool isNewCheckIn = false;
        var now = DateTime.UtcNow;

        // Debounce Cooldown: Không ghi log dồn dập
        if (!_lastCheckInTimes.TryGetValue(member.Id, out var lastTime) || (now - lastTime) >= CheckInCooldown)
        {
            _lastCheckInTimes[member.Id] = now;
            isNewCheckIn = true;
            await RecordCheckInLogAsync(member.Id, confidence, method).ConfigureAwait(false);
        }

        // Phát âm thanh phản hồi
        if (isNewCheckIn && _audioService != null)
        {
            if (isExpired)
                _audioService.PlayMembershipExpired(member.FullName);
            else
                _audioService.PlayCheckInSuccess(member.FullName);
        }

        // Bắn sự kiện lên UI
        MemberIdentified?.Invoke(this, new MemberIdentifiedEventArgs(member, confidence, isNewCheckIn, method));
        return true;
    }

    private async Task RecordCheckInLogAsync(Guid memberId, float confidence, IdentificationMethod method)
    {
        await _dbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var log = CheckInLog.Create(memberId, confidence, DateTime.UtcNow, method);
            await _dbContext.CheckInLogs.AddAsync(log).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            Debug.WriteLine($"[AttendanceCoordinator] Đã ghi nhận CheckInLog: {memberId} qua {method}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttendanceCoordinator] Lỗi ghi SQLite: {ex.Message}");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Start() => Resume();
    public void Stop() => Pause();

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        lock (_registeredProviders)
        {
            foreach (var p in _registeredProviders)
            {
                p.IdentificationOccurred -= OnProviderIdentificationOccurred;
            }
            _registeredProviders.Clear();
        }
        _dbLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
