using System.Collections;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;

namespace Lokasync.E2E.Tests.Tiers3_4;

internal sealed record AutoPauseNotification(string RawMessage, int? StatusCode, string? Reason);

internal static class Tiers3_4TestHelper
{
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

    public static List<SubtitleEntry> MaskEntries(ITextSanitizer sanitizer, IEnumerable<SubtitleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(entries);

        return entries.Select(e =>
        {
            (string masked, Dictionary<string, string> placeholders) = sanitizer.Mask(e.RawText);
            return e with
            {
                MaskedText = masked,
                Placeholders = placeholders
            };
        }).ToList();
    }

    public static List<SubtitleEntry> UnmaskEntries(
        ITextSanitizer sanitizer,
        IEnumerable<SubtitleEntry> entries,
        IReadOnlyDictionary<string, string> translatedByKey)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(translatedByKey);

        List<SubtitleEntry> results = [];
        foreach (SubtitleEntry entry in entries)
        {
            if (!translatedByKey.TryGetValue(entry.EntryKey, out string? translatedMasked))
            {
                results.Add(entry);
                continue;
            }

            if (!sanitizer.TryUnmask(translatedMasked, entry.Placeholders, out string unmasked))
            {
                throw new InvalidOperationException(
                    $"Expected valid unmask for entry '{entry.EntryKey}', but TryUnmask returned false for '{translatedMasked}'.");
            }

            results.Add(entry with
            {
                TranslatedText = translatedMasked,
                FinalPatchedText = unmasked,
                Status = EntryStatus.Translated
            });
        }

        return results;
    }

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

    public static async Task<object?> RunWorkerAsync(
        SequentialWorkerHandle worker,
        DatabaseSession db,
        RouterProfile profile,
        string systemPrompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);

        MethodInfo[] methods = worker.WorkerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        !m.Name.Contains("Pause", StringComparison.OrdinalIgnoreCase) &&
                        !m.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase) &&
                        !m.Name.Contains("Stop", StringComparison.OrdinalIgnoreCase) &&
                        !m.Name.Contains("Cancel", StringComparison.OrdinalIgnoreCase) &&
                        (m.Name.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Translate", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Start", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.GetParameters().Any(p => typeof(IEnumerable<SubtitleEntry>).IsAssignableFrom(p.ParameterType)))
            .ToArray();

        IReadOnlyList<SubtitleEntry>? cachedEntries = null;
        foreach (MethodInfo method in methods)
        {
            if (TryBuildWorkerArgs(method, db, profile, systemPrompt, ct, ref cachedEntries, out object?[] args))
            {
                return await InvokeAndUnwrapAsync(worker.Instance, method, args);
            }
        }

        string sigs = string.Join("; ", worker.WorkerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => m.ToString()));
        throw new InvalidOperationException(
            $"No compatible Run/Execute/Process method found on '{worker.WorkerType.FullName}'. Available: [{sigs}].");
    }

    public static async Task<object?> ResumeWorkerAsync(
        SequentialWorkerHandle worker,
        DatabaseSession db,
        RouterProfile updatedProfile,
        string systemPrompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(updatedProfile);

        MethodInfo[] resumeMethods = worker.WorkerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        m.Name.Contains("Resume", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        IReadOnlyList<SubtitleEntry>? cachedEntries = null;
        foreach (MethodInfo method in resumeMethods)
        {
            if (TryBuildWorkerArgs(method, db, updatedProfile, systemPrompt, ct, ref cachedEntries, out object?[] args))
            {
                return await InvokeAndUnwrapAsync(worker.Instance, method, args);
            }
        }

        SetPropIfExists(worker.Instance, "Profile", updatedProfile);
        SetPropIfExists(worker.Instance, "RouterProfile", updatedProfile);

        MethodInfo? signalResume = resumeMethods.FirstOrDefault(m => m.GetParameters().Length == 0);
        if (signalResume is not null)
        {
            await InvokeAndUnwrapAsync(worker.Instance, signalResume, []);
            return null;
        }

        return await RunWorkerAsync(worker, db, updatedProfile, systemPrompt, ct);
    }

    public static List<AutoPauseNotification> AttachDetailedAutoPauseListener(SequentialWorkerHandle worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        List<AutoPauseNotification> notifications = [];

        foreach (EventInfo evt in worker.WorkerType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!evt.Name.Contains("Pause", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Type? handlerType = evt.EventHandlerType;
            MethodInfo? invoke = handlerType?.GetMethod("Invoke");
            if (handlerType is null || invoke is null)
            {
                continue;
            }

            ParameterInfo[] ps = invoke.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
            {
                Action<string> cb = msg => notifications.Add(new AutoPauseNotification(msg, null, msg));
                evt.AddEventHandler(worker.Instance, Delegate.CreateDelegate(handlerType, cb.Target, cb.Method));
            }
            else if (ps.Length == 2 && typeof(EventArgs).IsAssignableFrom(ps[1].ParameterType))
            {
                if (handlerType == typeof(EventHandler))
                {
                    evt.AddEventHandler(worker.Instance, new EventHandler((_, e) =>
                        notifications.Add(ExtractAutoPauseNotification(e))));
                }
                else
                {
                    MethodInfo generic = typeof(Tiers3_4TestHelper)
                        .GetMethod(nameof(CreateDetailedEventBridge), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(ps[1].ParameterType);
                    Delegate del = (Delegate)generic.Invoke(null, [handlerType, notifications])!;
                    evt.AddEventHandler(worker.Instance, del);
                }
            }
        }

        return notifications;
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
        (bool found, object? result) = await TryInvokePipelineAsync(
            handle,
            methodKeywords,
            excludedMethodSubstrings,
            projectId,
            gameRootPath,
            adapter,
            profile,
            entries,
            systemPrompt,
            narrativeContextJson,
            ct);

        if (found)
        {
            return result;
        }

        string available = string.Join(", ", handle.OrchestratorType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => m.ToString()));
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

    public static object[] CreateUiDependencies(
        OpaqueContractBridge bridge,
        DatabaseSession db,
        TempWorkspace ws,
        MockLlmRouterHandler mockRouter,
        IBackupService? customBackup = null,
        ITranslatorProvider? customTranslator = null)
    {
        ITextSanitizer sanitizer = bridge.CreateTextSanitizer();
        ITranslatorProvider translator = customTranslator ?? bridge.CreateTranslatorProvider(mockRouter);
        IBackupService backupService = customBackup ?? bridge.CreateBackupService(db.ConnectionString, ws.BackupRootPath, db.Connection);
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();
        PipelineOrchestratorHandle orchestrator = bridge.CreatePipelineOrchestrator(
            db, sanitizer, translator, backupService, adapters, ws.BackupRootPath);
        SequentialWorkerHandle worker = bridge.CreateSequentialWorker(translator, sanitizer, db);
        return BuildFullDependencyGraph(db, sanitizer, translator, backupService, adapters, orchestrator, worker);
    }

    public static ViewModelHandle ResolveViewModel(
        OpaqueContractBridge bridge,
        IReadOnlyList<string> preferredKeywords,
        params object[] dependencies)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(preferredKeywords);

        List<Exception> errors = [];
        foreach (string keyword in preferredKeywords)
        {
            try
            {
                return bridge.CreateViewModel(keyword, dependencies);
            }
            catch (InvalidOperationException ex)
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

    public static T? GetProperty<T>(ViewModelHandle vm, params string[] candidateNames)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (TryGetProperty(vm.Instance, out T? value, candidateNames))
        {
            return value;
        }

        string available = string.Join(", ", vm.ViewModelType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{p.PropertyType.Name} {p.Name}"));
        throw new InvalidOperationException(
            $"None of properties [{string.Join(", ", candidateNames)}] found on '{vm.ViewModelType.FullName}'. Available: [{available}].");
    }

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

    private static bool TryBuildWorkerArgs(
        MethodInfo method,
        DatabaseSession db,
        RouterProfile profile,
        string systemPrompt,
        CancellationToken ct,
        ref IReadOnlyList<SubtitleEntry>? cachedEntries,
        out object?[] args)
    {
        ParameterInfo[] ps = method.GetParameters();
        args = new object?[ps.Length];

        for (int i = 0; i < ps.Length; i++)
        {
            ParameterInfo p = ps[i];
            if (p.ParameterType == typeof(RouterProfile))
            {
                args[i] = profile;
            }
            else if (p.ParameterType == typeof(string))
            {
                string name = p.Name ?? string.Empty;
                args[i] = name.Contains("project", StringComparison.OrdinalIgnoreCase) ? "test-project" : systemPrompt;
            }
            else if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
            }
            else if (p.ParameterType.IsAssignableFrom(typeof(List<SubtitleEntry>)) ||
                     p.ParameterType.IsAssignableFrom(typeof(IReadOnlyList<SubtitleEntry>)))
            {
                cachedEntries ??= db.GetAllEntriesAsync(ct).GetAwaiter().GetResult();
                args[i] = cachedEntries.ToList();
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else
            {
                return false;
            }
        }

        return true;
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

    private static Delegate CreateDetailedEventBridge<TEventArgs>(
        Type handlerType,
        List<AutoPauseNotification> notifications)
    {
        Action<object?, TEventArgs> wrapper = (_, args) => notifications.Add(ExtractAutoPauseNotification(args));
        return Delegate.CreateDelegate(handlerType, wrapper.Target, wrapper.Method);
    }

    private static AutoPauseNotification ExtractAutoPauseNotification(object? args)
    {
        if (args is null)
        {
            return new AutoPauseNotification(string.Empty, null, null);
        }

        string raw = args.ToString() ?? args.GetType().Name;
        int? statusCode = null;
        object? statusObj = args.GetType().GetProperty("StatusCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(args)
            ?? args.GetType().GetProperty("HttpStatusCode", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(args)
            ?? args.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(args);

        if (statusObj is HttpStatusCode httpCode)
        {
            statusCode = (int)httpCode;
        }
        else if (statusObj is int intCode)
        {
            statusCode = intCode;
        }

        string? reason = args.GetType().GetProperty("Reason", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(args)?.ToString()
            ?? args.GetType().GetProperty("Message", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(args)?.ToString()
            ?? raw;

        return new AutoPauseNotification(raw, statusCode, reason);
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

    private static async Task<object?> InvokeAndUnwrapAsync(object instance, MethodInfo method, object?[] args)
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

    private static void SetPropIfExists(object target, string propName, object? value)
    {
        PropertyInfo? prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }
}
