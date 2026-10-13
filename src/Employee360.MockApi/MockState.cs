namespace Employee360.MockApi;

/// <summary>
/// The mock's own request shape. Deliberately NOT shared with the integration project:
/// Employee 360 is a separate system and the two only agree through the HTTP contract.
/// </summary>
public sealed record EmployeeUpdateRequest(
    string EmployeeId,
    string FirstName,
    string LastName,
    string DisplayName,
    string Department,
    string Location,
    DateTimeOffset EffectiveFromUtc,
    string SourceEventId,
    string SourceSystem);

public sealed record FaultRequest(string Mode, int Times = 1);

/// <summary>In-memory state for the fake Employee 360, plus switchable fault injection.</summary>
public sealed class MockState
{
    public static readonly HashSet<string> FaultModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "error500", "unavailable503", "badrequest400", "timeout"
    };

    /// <summary>
    /// Employees this "master data" refuses permanently (422), regardless of how often we retry.
    /// Used to demonstrate the poison-message / cannot-safely-process scenario.
    /// </summary>
    public static readonly HashSet<string> PoisonEmployeeIds = new(StringComparer.OrdinalIgnoreCase) { "EMP66666" };

    private readonly object _gate = new();
    private readonly Queue<string> _faults = new();
    private readonly Dictionary<string, EmployeeUpdateRequest> _employees = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appliedKeys = new(StringComparer.Ordinal);

    private int _totalRequests;
    private int _appliedUpdates;
    private int _idempotentReplays;

    public void CountRequest() { lock (_gate) _totalRequests++; }

    public void QueueFaults(string mode, int times)
    {
        lock (_gate)
            for (var i = 0; i < Math.Max(1, times); i++) _faults.Enqueue(mode.ToLowerInvariant());
    }

    /// <summary>Returns the next injected fault for this request, or null for normal behaviour.</summary>
    public string? TakeFault()
    {
        lock (_gate) return _faults.Count > 0 ? _faults.Dequeue() : null;
    }

    /// <summary>
    /// Applies the update unless this Idempotency-Key has been seen. Returns true if newly applied,
    /// false if it was a replay. This is the behaviour the integration relies on when a retry
    /// follows a response that was lost on the way back.
    /// </summary>
    public bool Apply(string idempotencyKey, EmployeeUpdateRequest update)
    {
        lock (_gate)
        {
            if (!_appliedKeys.Add(idempotencyKey))
            {
                _idempotentReplays++;
                return false;
            }

            _employees[update.EmployeeId] = update;
            _appliedUpdates++;
            return true;
        }
    }

    public EmployeeUpdateRequest? Find(string employeeId)
    {
        lock (_gate) return _employees.GetValueOrDefault(employeeId);
    }

    public object Stats()
    {
        lock (_gate)
            return new
            {
                totalRequests = _totalRequests,
                appliedUpdates = _appliedUpdates,
                idempotentReplays = _idempotentReplays,
                employeesStored = _employees.Count,
                pendingFaults = _faults.Count
            };
    }

    public void Reset()
    {
        lock (_gate)
        {
            _faults.Clear();
            _employees.Clear();
            _appliedKeys.Clear();
            _totalRequests = _appliedUpdates = _idempotentReplays = 0;
        }
    }
}
