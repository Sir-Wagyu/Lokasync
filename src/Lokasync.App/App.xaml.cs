using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.App.ViewModels;
using Lokasync.Sanitizer;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;

namespace Lokasync.App;

public partial class App : Application
{
    private HttpClient? _httpClient;
    private DesktopProjectContext? _projectContext;
    private LocalizationPipelineService? _pipelineService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ProjectSessionState session = new();
        _projectContext = new DesktopProjectContext();
        _projectContext.BindGameRootResolver(() => session.GameRootPath);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        TextSanitizer sanitizer = new();
        OpenAiTranslatorProvider translatorProvider = new(_httpClient);
        OpenAiNarrativeContextGenerator narrativeContextGenerator = new(_httpClient);
        EngineFingerprinter fingerprinter = new();
        RouterProfileStore profileStore = new();

        _pipelineService = new LocalizationPipelineService(
            fingerprinter: fingerprinter,
            sanitizer: sanitizer,
            translatorProvider: translatorProvider,
            profileStore: profileStore,
            sessionFactory: _projectContext);

        WpfFolderPickerService folderPicker = new();
        WpfUserNotificationService notifications = new();
        WpfDispatcherService dispatcher = new();

        PipelineControlViewModel pipelineVm = new(
            pipelineService: _pipelineService,
            projectRepository: _projectContext,
            subtitleEntryRepository: _projectContext,
            fingerprinter: fingerprinter,
            session: session,
            dispatcher: dispatcher,
            notifications: notifications);

        ProjectDashboardViewModel dashboardVm = new(
            projectRepository: _projectContext,
            fingerprinter: fingerprinter,
            session: session,
            folderPicker: folderPicker,
            notifications: notifications,
            pipeline: pipelineVm);

        RouterProfileViewModel routerProfileVm = new(
            profileStore: profileStore,
            session: session,
            notifications: notifications);

        GlossaryViewModel glossaryVm = new(
            projectRepository: _projectContext,
            session: session,
            entryRepository: _projectContext,
            profileStore: profileStore,
            contextGenerator: narrativeContextGenerator,
            extractEntriesFallback: ct => _pipelineService.ExtractAndPreparePendingAsync(ct));

        TranslationGridViewModel translationGridVm = new(
            entryRepository: _projectContext,
            sanitizer: sanitizer,
            pipelineService: _pipelineService,
            session: session,
            dispatcher: dispatcher);

        MainViewModel mainVm = new(
            dashboard: dashboardVm,
            routerProfile: routerProfileVm,
            glossary: glossaryVm,
            pipeline: pipelineVm,
            translationGrid: translationGridVm,
            session: session);

        MainWindow window = new(mainVm);
        MainWindow = window;
        window.Show();

        try
        {
            await mainVm.InitializeAsync();
        }
        catch (Exception ex)
        {
            notifications.ShowError("Initialization Warning", ex.Message);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;

        if (_pipelineService is not null)
        {
            await _pipelineService.DisposeAsync();
        }

        if (_projectContext is not null)
        {
            await _projectContext.DisposeAsync();
        }

        _httpClient?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Unexpected application error:\n{e.Exception.Message}",
            "Lokasync Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
