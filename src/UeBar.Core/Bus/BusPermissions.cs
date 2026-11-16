// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Core.Abstractions;

namespace UeBar.Core.Bus;

/// <summary>Permission: <see cref="Consumer"/> may use <see cref="Access"/> of <see cref="Provider"/>.</summary>
public sealed class BusGrant
{
    public string Consumer { get; set; } = "";

    public string Provider { get; set; } = "";

    public BusAccess Access { get; set; }
}

public sealed record BusPermissionRequest(string ConsumerModuleId, string ProviderModuleId, BusAccess Access, string? Reason);

/// <summary>Checks permissions between modules. Default: denied.</summary>
public interface IBusPermissionPolicy
{
    bool IsAllowed(string consumerModuleId, string providerModuleId, BusAccess access);

    void RecordRequest(BusPermissionRequest request);
}

/// <summary>Permissions from a list (e.g. from settings.json). Everything not listed is denied.</summary>
public sealed class GrantListPermissionPolicy : IBusPermissionPolicy
{
    private readonly Func<IReadOnlyList<BusGrant>> _grants;
    private readonly List<BusPermissionRequest> _requests = [];
    private readonly Lock _lock = new();

    public GrantListPermissionPolicy(Func<IReadOnlyList<BusGrant>> grants)
    {
        _grants = grants;
    }

    public GrantListPermissionPolicy(params BusGrant[] grants)
        : this(() => grants)
    {
    }

    /// <summary>Open requests (no UI yet).</summary>
    public IReadOnlyList<BusPermissionRequest> PendingRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public bool IsAllowed(string consumerModuleId, string providerModuleId, BusAccess access)
    {
        if (access == BusAccess.None)
        {
            return false;
        }

        foreach (var grant in _grants())
        {
            if (string.Equals(grant.Consumer, consumerModuleId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(grant.Provider, providerModuleId, StringComparison.OrdinalIgnoreCase)
                && (grant.Access & access) == access)
            {
                return true;
            }
        }

        return false;
    }

    public void RecordRequest(BusPermissionRequest request)
    {
        lock (_lock)
        {
            if (!_requests.Contains(request))
            {
                _requests.Add(request);
            }
        }
    }
}
