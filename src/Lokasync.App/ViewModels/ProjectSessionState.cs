using CommunityToolkit.Mvvm.ComponentModel;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;

namespace Lokasync.App.ViewModels;

public sealed partial class ProjectSessionState : ObservableObject
{
    [ObservableProperty]
    private string? _projectId;

    [ObservableProperty]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private string _gameTitle = string.Empty;

    [ObservableProperty]
    private string _detectedEngine = string.Empty;

    [ObservableProperty]
    private bool _hasSupportedEngine;

    [ObservableProperty]
    private RouterProfile _activeRouterProfile = RouterProfileDefaults.CreateDefaultProfile();
}
