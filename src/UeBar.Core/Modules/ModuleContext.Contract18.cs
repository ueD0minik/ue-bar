// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using UeBar.Sdk;

namespace UeBar.Core.Modules;

/// <summary>
/// Contract 1.8 (P3-1) on the module's context: content actions of all modules – list them, start one, or let the user
/// choose. The provider is called by the answer window through the guard; nothing runs once this module has stopped.
/// </summary>
internal sealed partial class ModuleContext : IContentActions
{
    public IReadOnlyList<ContentActionEntry> GetContentActions(ContentKinds kinds) =>
        _host.ContentActions is { } actions && !Stopping.IsCancellationRequested ? actions.GetActions(kinds) : [];

    public Task<bool> RunContentActionAsync(string moduleId, string actionId, ContentItem content, ContentActionStart? start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_host.ContentActions is not { } actions || Stopping.IsCancellationRequested || string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(actionId))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(actions.Run(ModuleId, moduleId, actionId, content, start));
    }

    public async Task<bool> ChooseContentActionAsync(ContentItem content, ContentActionStart? start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_host.ContentActions is not { } actions || Stopping.IsCancellationRequested || content.IsEmpty)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
        return await actions.ChooseAsync(ModuleId, content, start, linked.Token).ConfigureAwait(false);
    }
}
