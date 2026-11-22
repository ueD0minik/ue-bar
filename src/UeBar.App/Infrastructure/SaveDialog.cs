// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using UeBar.Bar;
using UeBar.Core.Logging;
using UeBar.Core.Settings;
using UeBar.Sdk;

namespace UeBar.App.Infrastructure;

/// <summary>
/// The Windows "Save as" dialog for plugins (<see cref="IFileSaveDialog"/>) and the pin menu (P2-5-Fix 1). Free
/// choice of folder and name; starts in the folder picked last time for the same owner, remembered per PC in
/// the local settings (<see cref="SaveFolders"/>). Always on the UI thread.
/// </summary>
internal sealed class SaveDialog(Dispatcher dispatcher, SettingsService settings, IUeLogger log)
{
    public Task<string?> AskAsync(string owner, SaveFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return dispatcher.InvokeAsync(() => Ask(owner, request)).Task;
    }

    public string? Ask(string owner, SaveFileRequest request)
    {
        var folders = settings.GetSection<SaveFolders>(SaveFolders.Section);
        var extension = request.Extension.StartsWith('.') ? request.Extension : "." + request.Extension;
        var type = string.IsNullOrWhiteSpace(request.TypeName) ? extension.TrimStart('.').ToUpperInvariant() : request.TypeName;
        var dialog = new SaveFileDialog
        {
            Title = request.Title ?? "",
            Filter = $"{type} (*{extension})|*{extension}",
            FileName = Path.GetFileName(request.SuggestedName),
            InitialDirectory = folders.StartFolder(owner, request.InitialFolder, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            AddExtension = true,
            DefaultExt = extension,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            log.Info(owner, "Save as: cancelled");
            return null;
        }

        if (folders.Remember(owner, dialog.FileName))
        {
            settings.SetSection(SaveFolders.Section, folders);
        }

        log.Info(owner, "Save as: folder chosen");
        return dialog.FileName;
    }
}
