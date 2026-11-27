using System.Collections;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

internal static class AdaptersBackupUiTestHelper
{
    public static Task RunOnStaAsync(Action action) => ViewModelHandle.RunOnStaAsync(action);

    public static Task RunOnStaWithDispatcherAsync(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            DispatcherFrame frame = new();

            async void RunAsync()
            {
                try
                {
                    await asyncAction();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    frame.Continue = false;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }

            dispatcher.BeginInvoke(new Action(RunAsync));
            Dispatcher.PushFrame(frame);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static SubtitleEntry CreateEntry(
        long id,
        string entryKey,
        string sourceFile,
        string rawText,
        string? assetPath = null,
        string? maskedText = null,
        Dictionary<string, string>? placeholders = null,
        string? translatedText = null,
        string? finalPatchedText = null,
        EntryStatus status = EntryStatus.Pending,
        bool hasOverflowWarning = false) =>
        new(
            Id: id,
            EntryKey: entryKey,
            SourceFile: sourceFile,
            AssetPath: assetPath,
            RawText: rawText,
            MaskedText: maskedText ?? rawText,
            Placeholders: placeholders ?? new Dictionary<string, string>(StringComparer.Ordinal),
            TranslatedText: translatedText,
            FinalPatchedText: finalPatchedText,
            Status: status,
            HasOverflowWarning: hasOverflowWarning);

    public static async Task SeedProjectAsync(
        DatabaseSession db,
        string projectId,
        string gameTitle,
        string gameRootPath,
        string detectedEngine,
        string? narrativeContextJson = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (db.Connection.State != ConnectionState.Open)
        {
            await db.Connection.OpenAsync(ct);
        }

        await using SqliteCommand cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (id, game_title, game_root_path, detected_engine, narrative_context_json, created_at)
            VALUES (@id, @gameTitle, @gameRootPath, @detectedEngine, @narrativeContextJson, @createdAt)
            ON CONFLICT(id) DO UPDATE SET
                game_title = excluded.game_title,
                game_root_path = excluded.game_root_path,
                detected_engine = excluded.detected_engine,
                narrative_context_json = COALESCE(excluded.narrative_context_json, projects.narrative_context_json);
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@gameTitle", gameTitle);
        cmd.Parameters.AddWithValue("@gameRootPath", gameRootPath);
        cmd.Parameters.AddWithValue("@detectedEngine", detectedEngine);
        cmd.Parameters.AddWithValue("@narrativeContextJson", (object?)narrativeContextJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<string?> QueryProjectNarrativeContextJsonAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT narrative_context_json FROM projects WHERE id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", projectId);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is DBNull or null ? null : Convert.ToString(result);
    }

    public static async Task<int> QueryBackupManifestCountAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM backup_manifests WHERE project_id = @projectId;";
        cmd.Parameters.AddWithValue("@projectId", projectId);
        object? scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar);
    }

    public static async Task UpdateSubtitleEntriesAsync(
        DatabaseSession db,
        IReadOnlyList<SubtitleEntry> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entries);

        object repo = db.SubtitleEntryRepo;
        MethodInfo? batchMethod = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m =>
                !m.IsSpecialName &&
                (m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase)) &&
                m.GetParameters().Any(p =>
                    p.ParameterType.IsAssignableFrom(typeof(List<SubtitleEntry>)) ||
                    p.ParameterType.IsAssignableFrom(typeof(IReadOnlyList<SubtitleEntry>))))
            .OrderByDescending(m => m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                                    !m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (batchMethod is not null)
        {
            ParameterInfo[] ps = batchMethod.GetParameters();
            object?[] args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(CancellationToken))
                {
                    args[i] = ct;
                }
                else if (ps[i].ParameterType.IsAssignableFrom(entries.GetType()))
                {
                    args[i] = entries;
                }
                else
                {
                    args[i] = entries.ToList();
                }
            }

            await InvokeAndUnwrapAsync(repo, batchMethod, args);
            return;
        }

        MethodInfo? singleMethod = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                (m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase)) &&
                m.GetParameters().Any(p => p.ParameterType == typeof(SubtitleEntry)));

        if (singleMethod is not null)
        {
            foreach (SubtitleEntry entry in entries)
            {
                ParameterInfo[] ps = singleMethod.GetParameters();
                object?[] args = ps.Length == 2 && ps[1].ParameterType == typeof(CancellationToken)
                    ? [entry, ct]
                    : [entry];
                await InvokeAndUnwrapAsync(repo, singleMethod, args);
            }

            return;
        }

        throw new InvalidOperationException($"No Update/Upsert method found on '{repo.GetType().FullName}'.");
    }

    public static SequentialWorkerHandle CreateSequentialWorkerSafe(
        OpaqueContractBridge bridge,
        ITranslatorProvider translator,
        ITextSanitizer sanitizer,
        DatabaseSession db,
        Action<string>? onAutoPause = null,
        Action<int, int>? onProgress = null,
        Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            return bridge.CreateSequentialWorker(translator, sanitizer, db, onAutoPause, onProgress, onLog);
        }
        catch (FormatException)
        {
            // OpaqueContractBridge.TryCreateViaStaticFactory invokes Int32.Parse(dbPath) on primitive parameters (e.g., Random(int)).
        }
        catch (InvalidOperationException)
        {
            // Fallback to safe worker resolution when OpaqueContractBridge constructor wiring cannot bind optional/primitive parameters.
        }

        Assembly translatorAsm = FindLoadedAssembly("Lokasync.Translator");
        Assembly? appAsm = TryFindLoadedAssembly("Lokasync.App");
        IEnumerable<Type> candidateAssemblies = appAsm is null
            ? GetAssemblyLoadableTypes(translatorAsm)
            : GetAssemblyLoadableTypes(translatorAsm).Concat(GetAssemblyLoadableTypes(appAsm));

        Type workerType = candidateAssemblies
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                (t.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Queue", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("Could not locate sequential queue worker type in Lokasync.Translator or Lokasync.App.");

        Dictionary<Type, object> context = new()
        {
            [typeof(ITranslatorProvider)] = translator,
            [typeof(ITextSanitizer)] = sanitizer,
            [typeof(SqliteConnection)] = db.Connection,
            [typeof(string)] = db.ConnectionString,
            [db.SubtitleEntryRepo.GetType()] = db.SubtitleEntryRepo,
            [db.ProjectRepo.GetType()] = db.ProjectRepo,
            [db.BackupManifestRepo.GetType()] = db.BackupManifestRepo
        };

        foreach (Type iface in db.SubtitleEntryRepo.GetType().GetInterfaces())
        {
            context[iface] = db.SubtitleEntryRepo;
        }

        if (db.ConnectionFactory is not null)
        {
            context[db.ConnectionFactory.GetType()] = db.ConnectionFactory;
            foreach (Type iface in db.ConnectionFactory.GetType().GetInterfaces())
            {
                context[iface] = db.ConnectionFactory;
            }
        }

        List<Type> searchTypes = GetAssemblyLoadableTypes(translatorAsm)
            .Concat(GetAssemblyLoadableTypes(typeof(SubtitleEntry).Assembly))
            .ToList();
        if (appAsm is not null)
        {
            searchTypes.AddRange(GetAssemblyLoadableTypes(appAsm));
        }

        object worker = CreateWorkerInstanceSafe(workerType, context, searchTypes, new HashSet<Type>())
            ?? throw new InvalidOperationException(
                $"Failed to construct sequential worker '{workerType.FullName}'. Constructors: [{string.Join("; ", workerType.GetConstructors().Select(c => c.ToString()))}].");

        AttachWorkerEventsSafe(worker, workerType, onAutoPause, onProgress, onLog);
        return new SequentialWorkerHandle(worker, workerType);
    }

    public static IBackupService CreateBackupServiceSafe(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        string backupRootPath)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRootPath);

        Assembly? appAsm = TryFindLoadedAssembly("Lokasync.App");
        IEnumerable<Type> candidateTypes = appAsm is not null
            ? GetAssemblyLoadableTypes(appAsm).Concat(GetAssemblyLoadableTypes(typeof(IBackupService).Assembly))
            : GetAssemblyLoadableTypes(typeof(IBackupService).Assembly);

        Type implType = candidateTypes.FirstOrDefault(t =>
            !t.IsAbstract && !t.IsInterface && typeof(IBackupService).IsAssignableFrom(t))
            ?? throw new InvalidOperationException("No concrete implementation of IBackupService found.");

        Dictionary<Type, object> context = BuildServiceContext(db, backupRootPath);
        List<Type> searchTypes = GetAllServiceSearchTypes();

        object instance = CreateServiceInstanceSafe(implType, context, searchTypes, backupRootPath, db.ConnectionString, new HashSet<Type>())
            ?? throw new InvalidOperationException(
                $"Failed to construct IBackupService '{implType.FullName}'. Constructors: [{string.Join("; ", implType.GetConstructors().Select(c => c.ToString()))}].");

        return (IBackupService)instance;
    }

    public static PipelineOrchestratorHandle CreatePipelineOrchestratorSafe(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        ITextSanitizer sanitizer,
        ITranslatorProvider translator,
        IBackupService backupService,
        IEnumerable<IEngineAdapter> adapters,
        string? backupRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(adapters);

        Assembly appAsm = FindLoadedAssembly("Lokasync.App");
        List<Type> appTypes = GetAssemblyLoadableTypes(appAsm).ToList();

        Type orchestratorType = appTypes
            .Where(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                !typeof(System.Windows.DependencyObject).IsAssignableFrom(t) &&
                !t.Name.EndsWith("View", StringComparison.OrdinalIgnoreCase) &&
                !t.Name.EndsWith("ViewModel", StringComparison.OrdinalIgnoreCase) &&
                !t.Name.EndsWith("Window", StringComparison.OrdinalIgnoreCase) &&
                (t.Name.Contains("Orchestrator", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Pipeline", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.Name.Contains("Orchestrator", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.Name.Contains("Service", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Could not locate pipeline orchestrator service in Lokasync.App. Types: [{string.Join(", ", appTypes.Select(t => t.Name))}].");

        List<IEngineAdapter> adapterList = adapters.ToList();
        Dictionary<Type, object> context = BuildServiceContext(db, backupRootPath);
        context[typeof(ITextSanitizer)] = sanitizer;
        context[typeof(ITranslatorProvider)] = translator;
        context[typeof(IBackupService)] = backupService;
        context[typeof(IEnumerable<IEngineAdapter>)] = adapterList;
        context[typeof(IReadOnlyList<IEngineAdapter>)] = adapterList;
        context[typeof(IReadOnlyCollection<IEngineAdapter>)] = adapterList;
        context[typeof(IEngineAdapter[])] = adapterList.ToArray();

        SequentialWorkerHandle worker = CreateSequentialWorkerSafe(bridge, translator, sanitizer, db);
        context[worker.WorkerType] = worker.Instance;
        foreach (Type iface in worker.WorkerType.GetInterfaces())
        {
            context[iface] = worker.Instance;
        }

        List<Type> searchTypes = GetAllServiceSearchTypes();
        object orchestrator = CreateServiceInstanceSafe(orchestratorType, context, searchTypes, backupRootPath, db.ConnectionString, new HashSet<Type>())
            ?? throw new InvalidOperationException(
                $"Failed to construct pipeline orchestrator '{orchestratorType.FullName}'. Constructors: [{string.Join("; ", orchestratorType.GetConstructors().Select(c => c.ToString()))}].");

        return new PipelineOrchestratorHandle(orchestrator, orchestratorType);
    }

    public static async Task SaveProfilesSafeAsync(
        OpaqueContractBridge bridge,
        string profilesJsonPath,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProfile);
        ArgumentNullException.ThrowIfNull(profiles);

        Assembly translatorAsm = FindLoadedAssembly("Lokasync.Translator");
        Type? configType = GetAssemblyLoadableTypes(translatorAsm)
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                (t.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Config", StringComparison.OrdinalIgnoreCase)) &&
                t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                              m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)));

        if (configType is not null)
        {
            object? instance = null;
            ConstructorInfo? pathCtor = configType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(string));
            if (pathCtor is not null)
            {
                instance = pathCtor.Invoke([profilesJsonPath]);
            }
            else
            {
                ConstructorInfo? emptyCtor = configType.GetConstructor(Type.EmptyTypes);
                if (emptyCtor is not null)
                {
                    instance = emptyCtor.Invoke([]);
                }
            }

            MethodInfo[] saveMethods = configType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => !m.IsSpecialName &&
                            (m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                             m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(m => m.GetParameters().Any(p =>
                    p.ParameterType == typeof(string) &&
                    ((p.Name ?? "").Contains("path", StringComparison.OrdinalIgnoreCase) ||
                     (p.Name ?? "").Contains("file", StringComparison.OrdinalIgnoreCase))))
                .ToArray();

            foreach (MethodInfo method in saveMethods)
            {
                if (!method.IsStatic && instance is null)
                {
                    continue;
                }

                ParameterInfo[] ps = method.GetParameters();
                object?[] args = new object?[ps.Length];
                bool ok = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    ParameterInfo p = ps[i];
                    if (p.ParameterType == typeof(string))
                    {
                        string name = p.Name ?? string.Empty;
                        args[i] = (name.Contains("active", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("profileName", StringComparison.OrdinalIgnoreCase) ||
                                   name.Contains("key", StringComparison.OrdinalIgnoreCase))
                            ? activeProfile
                            : profilesJsonPath;
                    }
                    else if (p.ParameterType == typeof(CancellationToken))
                    {
                        args[i] = ct;
                    }
                    else if (p.ParameterType.IsAssignableFrom(typeof(Dictionary<string, RouterProfile>)))
                    {
                        args[i] = new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal);
                    }
                    else if (p.ParameterType == typeof(RouterProfile))
                    {
                        args[i] = profiles.TryGetValue(activeProfile, out RouterProfile? rp) ? rp : profiles.Values.First();
                    }
                    else if (TryBuildProfilesWrapperSafe(p.ParameterType, activeProfile, profiles, out object? wrapper))
                    {
                        args[i] = wrapper;
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    await InvokeAndUnwrapNullableTargetAsync(method.IsStatic ? null : instance, method, args);
                    if (File.Exists(profilesJsonPath))
                    {
                        return;
                    }
                }
            }
        }

        await bridge.SaveProfilesAsync(profilesJsonPath, activeProfile, profiles, ct);
    }

    public static async Task<object?> InvokeWorkerAsync(
        SequentialWorkerHandle handle,
        IReadOnlyList<string> methodKeywords,
        string? systemPrompt = null,
        RouterProfile? profile = null,
        IReadOnlyList<SubtitleEntry>? entries = null,
        string? projectId = null,
        DatabaseSession? db = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(methodKeywords);

        if (entries is null && db is not null)
        {
            entries = await db.GetAllEntriesAsync(ct);
        }

        List<MethodInfo> candidates = handle.WorkerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToList();

        foreach (string keyword in methodKeywords)
        {
            List<MethodInfo> matching = candidates
                .Where(m => m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => string.Equals(m.Name, keyword, StringComparison.OrdinalIgnoreCase))
                .ThenBy(m => m.GetParameters().Length)
                .ToList();

            foreach (MethodInfo method in matching)
            {
                if (TryBindParameters(
                    method.GetParameters(),
                    projectId,
                    gameRootPath: null,
                    adapter: null,
                    profile,
                    entries,
                    systemPrompt,
                    narrativeContextJson: null,
                    ct,
                    out object?[] args))
                {
                    return await InvokeAndUnwrapAsync(handle.Instance, method, args);
                }
            }
        }

        string available = string.Join(", ", candidates.Select(m => m.ToString()));
        throw new InvalidOperationException(
            $"Could not bind any method matching [{string.Join(", ", methodKeywords)}] on worker '{handle.WorkerType.FullName}'. Available: [{available}].");
    }

    public static async Task<(bool Found, object? Result)> TryInvokePipelineAsync(
        PipelineOrchestratorHandle handle,
        IReadOnlyList<string> methodKeywords,
        string[]? excludedMethodSubstrings = null,
        string? projectId = null,
        string? gameRootPath = null,
        IEngineAdapter? adapter = null,
        RouterProfile? profile = null,
        IReadOnlyList<SubtitleEntry>? entries = null,
        string? systemPrompt = null,
        string? narrativeContextJson = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(methodKeywords);

        List<MethodInfo> candidates = handle.OrchestratorType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToList();

        foreach (string keyword in methodKeywords)
        {
            List<MethodInfo> matching = candidates
                .Where(m =>
                    m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    (excludedMethodSubstrings is null ||
                     !excludedMethodSubstrings.Any(ex => m.Name.Contains(ex, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(m => string.Equals(m.Name, keyword, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenBy(m => m.GetParameters().Length)
                .ToList();

            foreach (MethodInfo method in matching)
            {
                if (TryBindParameters(
                    method.GetParameters(),
                    projectId,
                    gameRootPath,
                    adapter,
                    profile,
                    entries,
                    systemPrompt,
                    narrativeContextJson,
                    ct,
                    out object?[] args))
                {
                    object? res = await InvokeAndUnwrapAsync(handle.Instance, method, args);
                    return (true, res);
                }
            }
        }

        return (false, null);
    }

    public static async Task<object?> InvokePipelineAsync(
        PipelineOrchestratorHandle handle,
        IReadOnlyList<string> methodKeywords,
        string[]? excludedMethodSubstrings = null,
        string? projectId = null,
        string? gameRootPath = null,
        IEngineAdapter? adapter = null,
        RouterProfile? profile = null,
        IReadOnlyList<SubtitleEntry>? entries = null,
        string? systemPrompt = null,
        string? narrativeContextJson = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(methodKeywords);

        List<MethodInfo> candidates = handle.OrchestratorType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToList();

        foreach (string keyword in methodKeywords)
        {
            List<MethodInfo> matching = candidates
                .Where(m =>
                    m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    (excludedMethodSubstrings is null ||
                     !excludedMethodSubstrings.Any(ex => m.Name.Contains(ex, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(m => string.Equals(m.Name, keyword, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                .ThenBy(m => m.GetParameters().Length)
                .ToList();

            foreach (MethodInfo method in matching)
            {
                if (TryBindParameters(
                    method.GetParameters(),
                    projectId,
                    gameRootPath,
                    adapter,
                    profile,
                    entries,
                    systemPrompt,
                    narrativeContextJson,
                    ct,
                    out object?[] args))
                {
                    return await InvokeAndUnwrapAsync(handle.Instance, method, args);
                }
            }
        }

        string available = string.Join(", ", candidates.Select(m => m.ToString()));
        throw new InvalidOperationException(
            $"Could not bind any method matching [{string.Join(", ", methodKeywords)}] on '{handle.OrchestratorType.FullName}'. Available: [{available}].");
    }

    public static object[] BuildFullDependencyGraph(
        DatabaseSession db,
        ITextSanitizer sanitizer,
        ITranslatorProvider translator,
        IBackupService backupService,
        IEnumerable<IEngineAdapter> adapters,
        PipelineOrchestratorHandle? orchestrator = null,
        SequentialWorkerHandle? worker = null)
    {
        List<object> deps =
        [
            db.ProjectRepo,
            db.SubtitleEntryRepo,
            db.BackupManifestRepo,
            db.Connection,
            sanitizer,
            translator,
            backupService,
            adapters.ToArray()
        ];

        if (db.ConnectionFactory is not null)
        {
            deps.Add(db.ConnectionFactory);
        }

        if (orchestrator is not null)
        {
            deps.Add(orchestrator.Instance);
        }

        if (worker is not null)
        {
            deps.Add(worker.Instance);
        }

        return deps.ToArray();
    }

    public static ViewModelHandle ResolveViewModel(
        OpaqueContractBridge bridge,
        IReadOnlyList<string> preferredKeywords,
        params object[] dependencies)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(preferredKeywords);

        Assembly? appAsm = TryFindLoadedAssembly("Lokasync.App");
        if (appAsm is not null)
        {
            List<Type> vmTypes = GetAssemblyLoadableTypes(appAsm)
                .Where(t =>
                    !t.IsAbstract &&
                    !t.IsInterface &&
                    !typeof(System.Windows.DependencyObject).IsAssignableFrom(t) &&
                    (typeof(INotifyPropertyChanged).IsAssignableFrom(t) ||
                     t.Name.EndsWith("ViewModel", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Dictionary<Type, object> context = [];
            foreach (object dep in dependencies)
            {
                context[dep.GetType()] = dep;
                RegisterInterfaces(context, dep);
            }

            List<Type> searchTypes = GetAllServiceSearchTypes();

            foreach (string keyword in preferredKeywords)
            {
                bool allowItemVm = keyword.Contains("Term", StringComparison.OrdinalIgnoreCase) ||
                                   keyword.Contains("Item", StringComparison.OrdinalIgnoreCase) ||
                                   keyword.Contains("Row", StringComparison.OrdinalIgnoreCase);

                List<Type> matching = vmTypes
                    .Where(t =>
                        t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        (allowItemVm ||
                         (!t.Name.Contains("Term", StringComparison.OrdinalIgnoreCase) &&
                          !t.Name.Contains("Item", StringComparison.OrdinalIgnoreCase) &&
                          !t.Name.Contains("Row", StringComparison.OrdinalIgnoreCase))))
                    .OrderByDescending(t => string.Equals(t.Name, keyword + "ViewModel", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(t => t.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (Type vmType in matching)
                {
                    object? instance = CreateServiceInstanceSafe(vmType, context, searchTypes, backupRootPath: null, connectionString: string.Empty, new HashSet<Type>());
                    if (instance is not null)
                    {
                        return new ViewModelHandle(instance, vmType);
                    }
                }
            }
        }

        List<Exception> errors = [];
        foreach (string keyword in preferredKeywords)
        {
            try
            {
                return bridge.CreateViewModel(keyword, dependencies);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        try
        {
            ViewModelHandle mainVm = bridge.CreateViewModel("Main", dependencies);
            foreach (string keyword in preferredKeywords)
            {
                PropertyInfo? subProp = mainVm.ViewModelType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p =>
                        p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        (typeof(INotifyPropertyChanged).IsAssignableFrom(p.PropertyType) ||
                         p.PropertyType.Name.EndsWith("ViewModel", StringComparison.OrdinalIgnoreCase)));

                if (subProp?.GetValue(mainVm.Instance) is object subInstance)
                {
                    return new ViewModelHandle(subInstance, subInstance.GetType());
                }
            }

            return mainVm;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            throw new InvalidOperationException(
                $"Failed to resolve ViewModel for keywords [{string.Join(", ", preferredKeywords)}]: {string.Join(" | ", errors.Select(e => e.Message))}");
        }
    }

    public static bool TryGetProperty<T>(ViewModelHandle vm, out T? value, params string[] candidateNames)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return TryGetProperty(vm.Instance, out value, candidateNames);
    }

    public static bool TryGetProperty<T>(object target, out T? value, params string[] candidateNames)
    {
        ArgumentNullException.ThrowIfNull(target);
        object actualTarget = target is ViewModelHandle vm ? vm.Instance : target;
        Type targetType = actualTarget.GetType();

        foreach (string name in candidateNames)
        {
            PropertyInfo? prop = targetType.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not null)
            {
                object? raw = prop.GetValue(actualTarget);
                if (raw is T typed)
                {
                    value = typed;
                    return true;
                }

                if (typeof(T) == typeof(double) && raw is int or long or float or decimal)
                {
                    value = (T)(object)Convert.ToDouble(raw);
                    return true;
                }

                if (raw is null && default(T) is null)
                {
                    value = default;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static T? GetRequiredProperty<T>(object target, params string[] candidateNames)
    {
        ArgumentNullException.ThrowIfNull(target);
        object actualTarget = target is ViewModelHandle vm ? vm.Instance : target;

        if (TryGetProperty(actualTarget, out T? value, candidateNames))
        {
            return value;
        }

        string available = string.Join(", ", actualTarget.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{p.PropertyType.Name} {p.Name}"));
        throw new InvalidOperationException(
            $"None of properties [{string.Join(", ", candidateNames)}] found on '{actualTarget.GetType().FullName}'. Available: [{available}].");
    }

    public static T? GetProperty<T>(ViewModelHandle vm, params string[] candidateNames)
        => GetRequiredProperty<T>(vm, candidateNames);

    public static bool TrySetProperty(ViewModelHandle vm, object? value, params string[] candidateNames)
    {
        ArgumentNullException.ThrowIfNull(vm);
        foreach (string name in candidateNames)
        {
            PropertyInfo? prop = vm.ViewModelType.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not null && prop.CanWrite)
            {
                object? converted = ConvertValue(value, prop.PropertyType);
                prop.SetValue(vm.Instance, converted);
                return true;
            }
        }

        return false;
    }

    public static void SetProperty(ViewModelHandle vm, object? value, params string[] candidateNames)
    {
        if (!TrySetProperty(vm, value, candidateNames))
        {
            string available = string.Join(", ", vm.ViewModelType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => $"{p.PropertyType.Name} {p.Name}"));
            throw new InvalidOperationException(
                $"None of writable properties [{string.Join(", ", candidateNames)}] found on '{vm.ViewModelType.FullName}'. Available writable: [{available}].");
        }
    }

    public static bool TryGetCommand(
        ViewModelHandle vm,
        IReadOnlyList<string> preferredNames,
        out ICommand? command,
        string[]? excludedSubstrings = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(preferredNames);

        List<PropertyInfo> commandProps = vm.ViewModelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .ToList();

        foreach (string candidate in preferredNames)
        {
            PropertyInfo? exact = commandProps.FirstOrDefault(p =>
                string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, candidate + "Command", StringComparison.OrdinalIgnoreCase));
            if (exact?.GetValue(vm.Instance) is ICommand exactCmd)
            {
                command = exactCmd;
                return true;
            }
        }

        foreach (string candidate in preferredNames)
        {
            PropertyInfo? partial = commandProps.FirstOrDefault(p =>
                p.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase) &&
                (excludedSubstrings is null ||
                 !excludedSubstrings.Any(ex => p.Name.Contains(ex, StringComparison.OrdinalIgnoreCase))));
            if (partial?.GetValue(vm.Instance) is ICommand partialCmd)
            {
                command = partialCmd;
                return true;
            }
        }

        command = null;
        return false;
    }

    public static ICommand GetCommandSafe(
        ViewModelHandle vm,
        IReadOnlyList<string> preferredNames,
        string[]? excludedSubstrings = null)
    {
        if (TryGetCommand(vm, preferredNames, out ICommand? command, excludedSubstrings) && command is not null)
        {
            return command;
        }

        string available = string.Join(", ", vm.ViewModelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name));
        throw new InvalidOperationException(
            $"No ICommand matching [{string.Join(", ", preferredNames)}] found on '{vm.ViewModelType.FullName}'. Available commands: [{available}].");
    }

    public static async Task ExecuteCommandAsync(ICommand command, object? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        Type cmdType = command.GetType();

        MethodInfo? executeAsyncWithParam = cmdType.GetMethod(
            "ExecuteAsync",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(object)],
            modifiers: null);
        if (executeAsyncWithParam is not null)
        {
            object? res = InvokeMethodSync(command, executeAsyncWithParam, [parameter]);
            if (res is Task t)
            {
                await t;
                return;
            }
        }

        MethodInfo? executeAsyncNoParam = cmdType.GetMethod(
            "ExecuteAsync",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (executeAsyncNoParam is not null && parameter is null)
        {
            object? res = InvokeMethodSync(command, executeAsyncNoParam, []);
            if (res is Task t)
            {
                await t;
                return;
            }
        }

        command.Execute(parameter);

        PropertyInfo? execTaskProp = cmdType.GetProperty("ExecutionTask", BindingFlags.Public | BindingFlags.Instance);
        if (execTaskProp?.GetValue(command) is Task execTask)
        {
            await execTask;
        }
    }

    public static async Task<(bool Found, object? Result)> TryInvokeViewModelMethodAsync(
        ViewModelHandle vm,
        IReadOnlyList<string> methodKeywords,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(methodKeywords);

        List<MethodInfo> methods = vm.ViewModelType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .ToList();

        foreach (string keyword in methodKeywords)
        {
            foreach (MethodInfo method in methods
                         .Where(m => m.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(m => string.Equals(m.Name, keyword, StringComparison.OrdinalIgnoreCase)))
            {
                ParameterInfo[] ps = method.GetParameters();
                if (TryMatchPositionalOrDefaultArgs(ps, args, out object?[] boundArgs))
                {
                    object? result = await InvokeAndUnwrapAsync(vm.Instance, method, boundArgs);
                    return (true, result);
                }
            }
        }

        return (false, null);
    }

    public static async Task<object?> InvokeViewModelMethodAsync(
        ViewModelHandle vm,
        IReadOnlyList<string> methodKeywords,
        params object?[] args)
    {
        (bool found, object? result) = await TryInvokeViewModelMethodAsync(vm, methodKeywords, args);
        if (found)
        {
            return result;
        }

        string available = string.Join(", ", vm.ViewModelType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => m.ToString()));
        throw new InvalidOperationException(
            $"No method matching [{string.Join(", ", methodKeywords)}] compatible with {args.Length} arg(s) found on '{vm.ViewModelType.FullName}'. Available: [{available}].");
    }

    public static IReadOnlyList<object> ReadCollectionItems(object? collectionObj)
    {
        if (collectionObj is null)
        {
            return [];
        }

        if (collectionObj is ICollectionView cv)
        {
            List<object> items = [];
            foreach (object? item in cv)
            {
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        if (collectionObj is IEnumerable enumerable and not string)
        {
            List<object> items = [];
            foreach (object? item in enumerable)
            {
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        return [];
    }

    public static bool ComputeExpectedOverflowWarning(string rawText, string finalPatchedText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return false;
        }

        return (long)finalPatchedText.Length * 4L > (long)rawText.Length * 5L;
    }

    private static bool TryBindParameters(
        ParameterInfo[] parameters,
        string? projectId,
        string? gameRootPath,
        IEngineAdapter? adapter,
        RouterProfile? profile,
        IReadOnlyList<SubtitleEntry>? entries,
        string? systemPrompt,
        string? narrativeContextJson,
        CancellationToken ct,
        out object?[] args)
    {
        args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo p = parameters[i];
            Type pType = p.ParameterType;
            string pName = p.Name ?? string.Empty;

            if (pType == typeof(CancellationToken))
            {
                args[i] = ct;
                continue;
            }

            if (pType.IsAssignableFrom(typeof(IEngineAdapter)) || typeof(IEngineAdapter).IsAssignableFrom(pType))
            {
                if (adapter is not null && pType.IsInstanceOfType(adapter))
                {
                    args[i] = adapter;
                    continue;
                }

                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    continue;
                }

                return false;
            }

            if (pType == typeof(RouterProfile))
            {
                if (profile is not null)
                {
                    args[i] = profile;
                    continue;
                }

                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    continue;
                }

                return false;
            }

            if (pType.IsAssignableFrom(typeof(List<SubtitleEntry>)) ||
                pType.IsAssignableFrom(typeof(IReadOnlyList<SubtitleEntry>)) ||
                pType == typeof(SubtitleEntry[]))
            {
                if (entries is not null)
                {
                    args[i] = pType == typeof(SubtitleEntry[])
                        ? entries.ToArray()
                        : pType.IsAssignableFrom(entries.GetType())
                            ? entries
                            : entries.ToList();
                    continue;
                }

                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    continue;
                }

                return false;
            }

            if (pType == typeof(SubtitleEntry))
            {
                if (entries is { Count: > 0 })
                {
                    args[i] = entries[0];
                    continue;
                }

                return false;
            }

            if (pType == typeof(string))
            {
                if (pName.Contains("project", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "id", StringComparison.OrdinalIgnoreCase))
                {
                    if (projectId is not null)
                    {
                        args[i] = projectId;
                        continue;
                    }
                }
                else if (pName.Contains("game", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("root", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("dir", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("path", StringComparison.OrdinalIgnoreCase))
                {
                    if (gameRootPath is not null)
                    {
                        args[i] = gameRootPath;
                        continue;
                    }
                }
                else if (pName.Contains("prompt", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = systemPrompt ?? "Translate dialog to Indonesian preserving placeholders.";
                    continue;
                }
                else if (pName.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("narrative", StringComparison.OrdinalIgnoreCase) ||
                         pName.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    if (narrativeContextJson is not null || p.HasDefaultValue)
                    {
                        args[i] = narrativeContextJson ?? p.DefaultValue;
                        continue;
                    }
                }
                else if (pName.Contains("engine", StringComparison.OrdinalIgnoreCase) && adapter is not null)
                {
                    args[i] = adapter.EngineName;
                    continue;
                }

                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    continue;
                }

                return false;
            }

            if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryMatchPositionalOrDefaultArgs(
        ParameterInfo[] parameters,
        object?[] providedArgs,
        out object?[] boundArgs)
    {
        boundArgs = new object?[parameters.Length];
        if (providedArgs.Length > parameters.Length)
        {
            return false;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            if (i < providedArgs.Length)
            {
                object? val = providedArgs[i];
                if (val is null && parameters[i].ParameterType.IsValueType &&
                    Nullable.GetUnderlyingType(parameters[i].ParameterType) is null)
                {
                    return false;
                }

                if (val is not null && !parameters[i].ParameterType.IsInstanceOfType(val))
                {
                    return false;
                }

                boundArgs[i] = val;
            }
            else if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                boundArgs[i] = CancellationToken.None;
            }
            else if (parameters[i].HasDefaultValue)
            {
                boundArgs[i] = parameters[i].DefaultValue;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null || targetType.IsInstanceOfType(value))
        {
            return value;
        }

        Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum && value is string s)
        {
            return Enum.Parse(underlying, s, ignoreCase: true);
        }

        if (underlying == typeof(string))
        {
            return value.ToString();
        }

        return Convert.ChangeType(value, underlying);
    }

    private static object? InvokeMethodSync(object? instance, MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static Task<object?> InvokeAndUnwrapAsync(object instance, MethodInfo method, object?[] args) =>
        InvokeAndUnwrapNullableTargetAsync(instance, method, args);

    private static async Task<object?> InvokeAndUnwrapNullableTargetAsync(object? instance, MethodInfo method, object?[] args)
    {
        object? result = InvokeMethodSync(instance, method, args);
        if (result is null)
        {
            return null;
        }

        if (result is Task task)
        {
            await task;
            PropertyInfo? resultProp = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProp is not null && resultProp.PropertyType.Name != "VoidTaskResult"
                ? resultProp.GetValue(task)
                : null;
        }

        Type resType = result.GetType();
        if (resType == typeof(ValueTask))
        {
            await (ValueTask)result;
            return null;
        }

        if (resType.IsGenericType && resType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            MethodInfo asTask = resType.GetMethod("AsTask")!;
            Task vtTask = (Task)asTask.Invoke(result, null)!;
            await vtTask;
            return vtTask.GetType().GetProperty("Result")?.GetValue(vtTask);
        }

        return result;
    }

    private static Assembly FindLoadedAssembly(string assemblyName) =>
        TryFindLoadedAssembly(assemblyName)
        ?? throw new InvalidOperationException($"Could not locate assembly '{assemblyName}'.");

    private static Assembly? TryFindLoadedAssembly(string assemblyName)
    {
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            return loaded;
        }

        AssemblyName? referenced = typeof(OpaqueContractBridge).Assembly
            .GetReferencedAssemblies()
            .FirstOrDefault(an => string.Equals(an.Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (referenced is not null)
        {
            return Assembly.Load(referenced);
        }

        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<Type, object> BuildServiceContext(DatabaseSession db, string? backupRootPath)
    {
        Dictionary<Type, object> context = new()
        {
            [typeof(SqliteConnection)] = db.Connection,
            [db.ProjectRepo.GetType()] = db.ProjectRepo,
            [db.SubtitleEntryRepo.GetType()] = db.SubtitleEntryRepo,
            [db.BackupManifestRepo.GetType()] = db.BackupManifestRepo
        };

        RegisterInterfaces(context, db.ProjectRepo);
        RegisterInterfaces(context, db.SubtitleEntryRepo);
        RegisterInterfaces(context, db.BackupManifestRepo);

        if (db.ConnectionFactory is not null)
        {
            context[db.ConnectionFactory.GetType()] = db.ConnectionFactory;
            RegisterInterfaces(context, db.ConnectionFactory);
        }

        if (!string.IsNullOrWhiteSpace(backupRootPath))
        {
            // Keep string out of context[typeof(string)] so named parameter resolution handles backupRootPath vs dbPath accurately.
        }

        return context;
    }

    private static void RegisterInterfaces(Dictionary<Type, object> context, object instance)
    {
        foreach (Type iface in instance.GetType().GetInterfaces())
        {
            context[iface] = instance;
        }
    }

    private static List<Type> GetAllServiceSearchTypes()
    {
        List<Type> types = [];
        foreach (string asmName in new[] { "Lokasync.Core", "Lokasync.Sanitizer", "Lokasync.Translator", "Lokasync.Adapters", "Lokasync.App" })
        {
            Assembly? asm = TryFindLoadedAssembly(asmName);
            if (asm is not null)
            {
                types.AddRange(GetAssemblyLoadableTypes(asm));
            }
        }

        return types;
    }

    private static object? CreateServiceInstanceSafe(
        Type targetType,
        Dictionary<Type, object> context,
        IReadOnlyList<Type> searchTypes,
        string? backupRootPath,
        string connectionString,
        HashSet<Type> visiting)
    {
        if (context.TryGetValue(targetType, out object? existing))
        {
            return existing;
        }

        if (targetType.IsPrimitive || targetType == typeof(string) || targetType.IsEnum || targetType == typeof(decimal) ||
            typeof(System.Windows.DependencyObject).IsAssignableFrom(targetType))
        {
            return null;
        }

        if (targetType.IsInterface || targetType.IsAbstract)
        {
            Type? impl = searchTypes.FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                !typeof(System.Windows.DependencyObject).IsAssignableFrom(t) &&
                targetType.IsAssignableFrom(t));
            return impl is not null
                ? CreateServiceInstanceSafe(impl, context, searchTypes, backupRootPath, connectionString, visiting)
                : null;
        }

        if (!visiting.Add(targetType))
        {
            return null;
        }

        try
        {
            string dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;

            foreach (ConstructorInfo ctor in targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                         .OrderByDescending(c => c.GetParameters().Length))
            {
                ParameterInfo[] ps = ctor.GetParameters();
                object?[] args = new object?[ps.Length];
                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    ParameterInfo p = ps[i];
                    Type pType = p.ParameterType;
                    string pName = p.Name ?? string.Empty;

                    if (context.TryGetValue(pType, out object? val))
                    {
                        args[i] = val;
                    }
                    else if (pType == typeof(string))
                    {
                        if (pName.Contains("backup", StringComparison.OrdinalIgnoreCase) && backupRootPath is not null)
                        {
                            args[i] = backupRootPath;
                        }
                        else if (pName.Contains("conn", StringComparison.OrdinalIgnoreCase))
                        {
                            args[i] = connectionString;
                        }
                        else if (pName.Contains("db", StringComparison.OrdinalIgnoreCase) || pName.Contains("database", StringComparison.OrdinalIgnoreCase))
                        {
                            args[i] = dbPath;
                        }
                        else if ((pName.Contains("root", StringComparison.OrdinalIgnoreCase) ||
                                  pName.Contains("dir", StringComparison.OrdinalIgnoreCase) ||
                                  pName.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                                  pName.Contains("path", StringComparison.OrdinalIgnoreCase)) &&
                                 backupRootPath is not null)
                        {
                            args[i] = backupRootPath;
                        }
                        else if (p.HasDefaultValue)
                        {
                            args[i] = p.DefaultValue;
                        }
                        else
                        {
                            ok = false;
                            break;
                        }
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (pType == typeof(Random))
                    {
                        args[i] = Random.Shared;
                    }
                    else if (pType == typeof(TimeProvider))
                    {
                        args[i] = TimeProvider.System;
                    }
                    else
                    {
                        object? nested = CreateServiceInstanceSafe(pType, context, searchTypes, backupRootPath, connectionString, visiting);
                        if (nested is not null)
                        {
                            args[i] = nested;
                        }
                        else
                        {
                            ok = false;
                            break;
                        }
                    }
                }

                if (ok)
                {
                    return ctor.Invoke(args);
                }
            }

            return null;
        }
        finally
        {
            visiting.Remove(targetType);
        }
    }

    private static IEnumerable<Type> GetAssemblyLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static object? CreateWorkerInstanceSafe(
        Type targetType,
        Dictionary<Type, object> context,
        IReadOnlyList<Type> searchTypes,
        HashSet<Type> visiting)
    {
        if (context.TryGetValue(targetType, out object? existing))
        {
            return existing;
        }

        if (targetType.IsPrimitive || targetType == typeof(string) || targetType.IsEnum || targetType == typeof(decimal))
        {
            return null;
        }

        if (targetType.IsInterface || targetType.IsAbstract)
        {
            Type? impl = searchTypes.FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && targetType.IsAssignableFrom(t));
            return impl is not null ? CreateWorkerInstanceSafe(impl, context, searchTypes, visiting) : null;
        }

        if (!visiting.Add(targetType))
        {
            return null;
        }

        try
        {
            foreach (ConstructorInfo ctor in targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                         .OrderByDescending(c => c.GetParameters().Length))
            {
                ParameterInfo[] ps = ctor.GetParameters();
                object?[] args = new object?[ps.Length];
                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    ParameterInfo p = ps[i];
                    Type pType = p.ParameterType;
                    if (context.TryGetValue(pType, out object? val))
                    {
                        args[i] = val;
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (pType == typeof(Random))
                    {
                        args[i] = Random.Shared;
                    }
                    else if (pType == typeof(TimeProvider))
                    {
                        args[i] = TimeProvider.System;
                    }
                    else
                    {
                        object? nested = CreateWorkerInstanceSafe(pType, context, searchTypes, visiting);
                        if (nested is not null)
                        {
                            args[i] = nested;
                        }
                        else
                        {
                            ok = false;
                            break;
                        }
                    }
                }

                if (ok)
                {
                    return ctor.Invoke(args);
                }
            }

            return null;
        }
        finally
        {
            visiting.Remove(targetType);
        }
    }

    private static void AttachWorkerEventsSafe(
        object worker,
        Type workerType,
        Action<string>? onAutoPause,
        Action<int, int>? onProgress,
        Action<string>? onLog)
    {
        foreach (EventInfo evt in workerType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
        {
            if (onAutoPause is not null && evt.Name.Contains("Pause", StringComparison.OrdinalIgnoreCase))
            {
                SubscribeStringOrEventArgsSafe(worker, evt, onAutoPause);
            }
            else if (onLog is not null && evt.Name.Contains("Log", StringComparison.OrdinalIgnoreCase))
            {
                SubscribeStringOrEventArgsSafe(worker, evt, onLog);
            }
            else if (onProgress is not null && evt.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase))
            {
                Type? handlerType = evt.EventHandlerType;
                MethodInfo? invoke = handlerType?.GetMethod("Invoke");
                ParameterInfo[]? ps = invoke?.GetParameters();
                if (handlerType is not null && ps is { Length: 2 } &&
                    ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int))
                {
                    evt.AddEventHandler(worker, Delegate.CreateDelegate(handlerType, onProgress.Target, onProgress.Method));
                }
            }
        }
    }

    private static void SubscribeStringOrEventArgsSafe(object target, EventInfo evt, Action<string> callback)
    {
        Type? handlerType = evt.EventHandlerType;
        MethodInfo? invoke = handlerType?.GetMethod("Invoke");
        if (handlerType is null || invoke is null)
        {
            return;
        }

        ParameterInfo[] ps = invoke.GetParameters();
        if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
        {
            evt.AddEventHandler(target, Delegate.CreateDelegate(handlerType, callback.Target, callback.Method));
        }
        else if (ps.Length == 2 && typeof(EventArgs).IsAssignableFrom(ps[1].ParameterType))
        {
            if (handlerType == typeof(EventHandler))
            {
                evt.AddEventHandler(target, new EventHandler((_, e) => callback(e.ToString() ?? evt.Name)));
            }
            else
            {
                MethodInfo generic = typeof(AdaptersBackupUiTestHelper)
                    .GetMethod(nameof(CreateSimpleEventArgsBridgeSafe), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(ps[1].ParameterType);
                Delegate del = (Delegate)generic.Invoke(null, [handlerType, callback])!;
                evt.AddEventHandler(target, del);
            }
        }
    }

    private static Delegate CreateSimpleEventArgsBridgeSafe<TEventArgs>(Type handlerType, Action<string> callback)
    {
        Action<object?, TEventArgs> wrapper = (_, args) => callback(args?.ToString() ?? typeof(TEventArgs).Name);
        return Delegate.CreateDelegate(handlerType, wrapper.Target, wrapper.Method);
    }

    private static bool TryBuildProfilesWrapperSafe(
        Type wrapperType,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        out object? wrapper)
    {
        wrapper = null;
        if (wrapperType.IsPrimitive || wrapperType.IsEnum || wrapperType.IsAbstract || wrapperType.IsInterface ||
            wrapperType == typeof(string) || wrapperType == typeof(CancellationToken) ||
            wrapperType == typeof(RouterProfile) || typeof(IDictionary).IsAssignableFrom(wrapperType))
        {
            return false;
        }

        if (wrapperType.GetProperty("Profiles", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null)
        {
            return false;
        }

        foreach (ConstructorInfo ctor in wrapperType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            if (ps.Length == 0)
            {
                object obj = ctor.Invoke([]);
                SetPropIfExists(obj, "ActiveProfile", activeProfile);
                SetPropIfExists(obj, "ActiveProfileName", activeProfile);
                PropertyInfo? profilesProp = wrapperType.GetProperty("Profiles", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (profilesProp is not null && profilesProp.CanWrite)
                {
                    profilesProp.SetValue(obj, new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal));
                }

                wrapper = obj;
                return true;
            }

            object?[] ctorArgs = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(string))
                {
                    ctorArgs[i] = activeProfile;
                }
                else if (ps[i].ParameterType.IsAssignableFrom(typeof(Dictionary<string, RouterProfile>)))
                {
                    ctorArgs[i] = new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal);
                }
                else if (ps[i].HasDefaultValue)
                {
                    ctorArgs[i] = ps[i].DefaultValue;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                wrapper = ctor.Invoke(ctorArgs);
                return true;
            }
        }

        return false;
    }

    private static void SetPropIfExists(object target, string propName, object? value)
    {
        PropertyInfo? prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }
}
