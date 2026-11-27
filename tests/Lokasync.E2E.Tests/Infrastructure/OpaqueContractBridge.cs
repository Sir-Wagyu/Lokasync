using System.Collections;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.E2E.Tests.Infrastructure;

/// <summary>
/// Opaque-box reflection and DI bridge that binds at compile time ONLY to the 7 locked
/// PRD Section 4 contracts while dynamically discovering and invoking concrete services
/// across Lokasync.Core, Sanitizer, Translator, Adapters, and App.
/// </summary>
public sealed class OpaqueContractBridge
{
    private static readonly string[] TargetAssemblyNames =
    [
        "Lokasync.Core",
        "Lokasync.Sanitizer",
        "Lokasync.Translator",
        "Lokasync.Adapters",
        "Lokasync.App"
    ];

    private readonly IReadOnlyDictionary<string, Assembly> _assemblies;
    private readonly IReadOnlyList<Type> _allTypes;

    public OpaqueContractBridge()
    {
        Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);

        Assembly coreAsm = typeof(SubtitleEntry).Assembly;
        loaded[coreAsm.GetName().Name ?? "Lokasync.Core"] = coreAsm;

        foreach (string asmName in TargetAssemblyNames)
        {
            if (loaded.ContainsKey(asmName))
            {
                continue;
            }

            Assembly? asm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase));

            if (asm is null)
            {
                try
                {
                    asm = Assembly.Load(new AssemblyName(asmName));
                }
                catch (Exception)
                {
                    string candidatePath = Path.Combine(AppContext.BaseDirectory, $"{asmName}.dll");
                    if (File.Exists(candidatePath))
                    {
                        asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
                    }
                }
            }

            if (asm is not null)
            {
                loaded[asmName] = asm;
            }
        }

        _assemblies = loaded;
        _allTypes = loaded.Values.SelectMany(GetLoadableTypes).Distinct().ToArray();
    }

    public IReadOnlyCollection<string> LoadedAssemblyNames => _assemblies.Keys.ToArray();

    public ITextSanitizer CreateTextSanitizer()
    {
        Type impl = FindSingleOrBestConcreteType<ITextSanitizer>("Lokasync.Sanitizer");
        return (ITextSanitizer)CreateInstance(impl, new Dictionary<Type, object>());
    }

    public ITranslatorProvider CreateTranslatorProvider(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HttpClient httpClient = new(handler, disposeHandler: false);

        Dictionary<Type, object> context = new()
        {
            [typeof(HttpMessageHandler)] = handler,
            [typeof(HttpClient)] = httpClient
        };

        Type impl = FindSingleOrBestConcreteType<ITranslatorProvider>("Lokasync.Translator");
        ITranslatorProvider provider = (ITranslatorProvider)CreateInstance(impl, context);

        // If constructor didn't accept HttpClient/HttpMessageHandler, check writable HttpClient property.
        bool ctorTookHttp = impl.GetConstructors().Any(c =>
            c.GetParameters().Any(p =>
                typeof(HttpClient).IsAssignableFrom(p.ParameterType) ||
                typeof(HttpMessageHandler).IsAssignableFrom(p.ParameterType)));

        if (!ctorTookHttp)
        {
            PropertyInfo? httpProp = impl
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.CanWrite && typeof(HttpClient).IsAssignableFrom(p.PropertyType));
            if (httpProp is not null)
            {
                httpProp.SetValue(provider, httpClient);
            }
        }

        return provider;
    }

    public IReadOnlyList<IEngineAdapter> CreateAllEngineAdapters()
    {
        Type interfaceType = typeof(IEngineAdapter);
        List<Type> candidates = _allTypes
            .Where(t => !t.IsAbstract && !t.IsInterface && interfaceType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No concrete IEngineAdapter implementations found across loaded assemblies: [{string.Join(", ", _assemblies.Keys)}].");
        }

        Dictionary<Type, object> context = new();
        List<IEngineAdapter> adapters = [];
        foreach (Type candidate in candidates)
        {
            if (TryCreateInstance(candidate, context, new HashSet<Type>(), out object? instance) &&
                instance is IEngineAdapter adapter)
            {
                adapters.Add(adapter);
            }
        }

        if (adapters.Count == 0)
        {
            throw new InvalidOperationException(
                $"Found IEngineAdapter candidates [{string.Join(", ", candidates.Select(c => c.FullName))}], but failed to instantiate any.");
        }

        return adapters;
    }

    public IEngineAdapter GetEngineAdapter(string engineKeyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineKeyword);
        IReadOnlyList<IEngineAdapter> adapters = CreateAllEngineAdapters();

        IEngineAdapter? match = adapters.FirstOrDefault(a =>
            a.EngineName.Contains(engineKeyword, StringComparison.OrdinalIgnoreCase) ||
            a.GetType().Name.Contains(engineKeyword, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"No IEngineAdapter matched keyword '{engineKeyword}'. Available adapters: [{string.Join(", ", adapters.Select(a => $"{a.EngineName} ({a.GetType().Name})"))}].");
    }

    public (IEngineAdapter? Adapter, string? DetectedEngineName) DetectEngine(string gameRootPath) =>
        DetectEngineAsync(gameRootPath, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<(IEngineAdapter? Adapter, string? DetectedEngineName)> DetectEngineAsync(
        string gameRootPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);
        IReadOnlyList<IEngineAdapter> adapters = CreateAllEngineAdapters();
        Dictionary<Type, object> context = new()
        {
            [typeof(IEnumerable<IEngineAdapter>)] = adapters,
            [typeof(IReadOnlyList<IEngineAdapter>)] = adapters,
            [typeof(IReadOnlyCollection<IEngineAdapter>)] = adapters,
            [typeof(IEngineAdapter[])] = adapters.ToArray()
        };

        Type? detectorType = FindTypesInAssembly("Lokasync.Adapters")
            .Concat(FindTypesInAssembly("Lokasync.App"))
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                !typeof(IEngineAdapter).IsAssignableFrom(t) &&
                (t.Name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Detector", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Resolver", StringComparison.OrdinalIgnoreCase) ||
                 HasMethodReturning<IEngineAdapter>(t, typeof(string)) ||
                 HasMethodReturning<IEngineAdapter>(t, typeof(string), typeof(CancellationToken))));

        if (detectorType is not null)
        {
            object detector = CreateInstance(detectorType, context);

            MethodInfo? adapterMethod = FindMethod(
                detectorType,
                returnPredicate: r => typeof(IEngineAdapter).IsAssignableFrom(r),
                paramTypes: [typeof(string)]);
            object?[] adapterArgs = [gameRootPath];

            if (adapterMethod is null)
            {
                adapterMethod = FindMethod(
                    detectorType,
                    returnPredicate: r => typeof(IEngineAdapter).IsAssignableFrom(r),
                    paramTypes: [typeof(string), typeof(CancellationToken)]);
                adapterArgs = [gameRootPath, ct];
            }

            if (adapterMethod is not null)
            {
                object? res = await InvokeAnyAsync(detector, adapterMethod, adapterArgs).ConfigureAwait(false);
                IEngineAdapter? detected = (IEngineAdapter?)res;
                return (detected, detected?.EngineName);
            }

            MethodInfo? nameMethod = FindMethod(
                detectorType,
                returnPredicate: r => r == typeof(string),
                paramTypes: [typeof(string)]);
            object?[] nameArgs = [gameRootPath];

            if (nameMethod is null)
            {
                nameMethod = FindMethod(
                    detectorType,
                    returnPredicate: r => r == typeof(string),
                    paramTypes: [typeof(string), typeof(CancellationToken)]);
                nameArgs = [gameRootPath, ct];
            }

            if (nameMethod is not null)
            {
                object? res = await InvokeAnyAsync(detector, nameMethod, nameArgs).ConfigureAwait(false);
                string? name = (string?)res;
                IEngineAdapter? matched = string.IsNullOrWhiteSpace(name)
                    ? null
                    : adapters.FirstOrDefault(a => string.Equals(a.EngineName, name, StringComparison.OrdinalIgnoreCase));
                return (matched, name);
            }
        }

        throw new InvalidOperationException(
            $"Could not locate an engine fingerprinter/detector service in Lokasync.Adapters or Lokasync.App. Inspected types: [{string.Join(", ", _allTypes.Select(t => t.Name))}].");
    }

    public IBackupService CreateBackupService(
        string dbPathOrConnectionString,
        string backupRootPath,
        SqliteConnection? openConnection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPathOrConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRootPath);

        Dictionary<Type, object> context = BuildDatabaseContext(dbPathOrConnectionString, openConnection);
        AddPathContext(context, dbPathOrConnectionString, backupRootPath, profilesJsonPath: null);

        Type impl = FindSingleOrBestConcreteType<IBackupService>("Lokasync.App", "Lokasync.Core");
        return (IBackupService)CreateInstance(impl, context);
    }

    public async Task<DatabaseSession> OpenDatabaseAsync(
        string dbPathOrConnectionString,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPathOrConnectionString);

        string connectionString = dbPathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? dbPathOrConnectionString
            : new SqliteConnectionStringBuilder { DataSource = dbPathOrConnectionString, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

        string dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;

        List<Type> coreTypes = FindTypesInAssembly("Lokasync.Core")
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .ToList();

        object? factoryInstance = null;
        SqliteConnection? connection = null;
        bool ownsConnection = true;

        Type? factoryType =
            coreTypes.FirstOrDefault(t => HasMethodReturningConnection(t) && t.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase)) ??
            coreTypes.FirstOrDefault(HasMethodReturningConnection) ??
            coreTypes.FirstOrDefault(t =>
                t.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("Database", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("DbContext", StringComparison.OrdinalIgnoreCase));

        if (factoryType is not null)
        {
            Dictionary<Type, object> factoryContext = new();
            AddPathContext(factoryContext, connectionString, backupRootPath: null, profilesJsonPath: null);
            factoryContext[typeof(string)] = dbPath;

            if (TryCreateInstance(factoryType, factoryContext, new HashSet<Type>(), out factoryInstance) &&
                factoryInstance is not null)
            {
                // Initialize schema on factory BEFORE acquiring any connection lease to avoid re-entrant lease deadlocks.
                await TryInvokeSchemaInitAsync(factoryInstance, factoryInstance.GetType(), connection: null, ct).ConfigureAwait(false);

                (SqliteConnection? factoryConn, object? lease) =
                    await TryOpenViaFactoryAsync(factoryInstance, dbPath, connectionString, ct).ConfigureAwait(false);

                if (lease is not null)
                {
                    // Release scoped lease immediately so repository calls never deadlock on factory locks.
                    if (lease is IAsyncDisposable asyncLease)
                    {
                        await asyncLease.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (lease is IDisposable dispLease)
                    {
                        dispLease.Dispose();
                    }

                    if (factoryConn is not null && factoryConn.State == ConnectionState.Open)
                    {
                        connection = factoryConn;
                        ownsConnection = false;
                    }
                }
                else if (factoryConn is not null)
                {
                    connection = factoryConn;
                }
            }
        }

        if (connection is null)
        {
            connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            ownsConnection = true;
        }

        // Ensure schema initialization method is invoked if factory separates Open and InitializeSchema,
        // and also invoke standalone Schema/Init classes idempotently even when factoryInstance != null.
        if (factoryInstance is not null)
        {
            await TryInvokeSchemaInitAsync(factoryInstance, factoryInstance.GetType(), connection, ct).ConfigureAwait(false);
        }

        Dictionary<Type, object> schemaContext = BuildDatabaseContext(connectionString, connection);
        RegisterInstanceAndInterfaces(schemaContext, factoryInstance);

        foreach (Type schemaType in FindTypesInAssembly("Lokasync.Core")
                     .Where(t => !t.IsInterface &&
                                 t != factoryInstance?.GetType() &&
                                 (t.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase) ||
                                  t.Name.Contains("Init", StringComparison.OrdinalIgnoreCase))))
        {
            if (schemaType.IsAbstract && schemaType.IsSealed)
            {
                await TryInvokeSchemaInitAsync(null, schemaType, connection, ct).ConfigureAwait(false);
            }
            else if (!schemaType.IsAbstract &&
                     TryCreateInstance(schemaType, schemaContext, new HashSet<Type>(), out object? initObj) &&
                     initObj is not null)
            {
                await TryInvokeSchemaInitAsync(initObj, schemaType, connection, ct).ConfigureAwait(false);
            }
        }

        Dictionary<Type, object> repoContext = BuildDatabaseContext(connectionString, connection);
        RegisterInstanceAndInterfaces(repoContext, factoryInstance);

        object projectRepo = ResolveRepository("Project", repoContext, coreTypes);
        object subtitleRepo = ResolveRepository("Subtitle", repoContext, coreTypes, typeof(SubtitleEntry));
        object manifestRepo = ResolveRepository("Backup", repoContext, coreTypes);

        return new DatabaseSession(
            this,
            connectionString,
            dbPath,
            connection,
            factoryInstance,
            projectRepo,
            subtitleRepo,
            manifestRepo,
            ownsConnection);
    }

    public string BuildSystemPrompt(
        string gameTitle,
        string genreTone,
        IReadOnlyList<(string Source, string Target, string Note)> glossary)
    {
        ArgumentNullException.ThrowIfNull(gameTitle);
        ArgumentNullException.ThrowIfNull(genreTone);
        ArgumentNullException.ThrowIfNull(glossary);

        List<Type> translatorTypes = FindTypesInAssembly("Lokasync.Translator")
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .ToList();

        Type builderType = translatorTypes.FirstOrDefault(t =>
                t.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No PromptBuilder type found in Lokasync.Translator. Types: [{string.Join(", ", translatorTypes.Select(t => t.Name))}].");

        object builder = CreateInstance(builderType, new Dictionary<Type, object>());

        // Prioritize System/Build/Prompt methods and support 1-, 2-, and 3-parameter signatures.
        MethodInfo[] methods = builderType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        ReturnUnwrappedType(m.ReturnType) == typeof(string))
            .OrderByDescending(m => m.Name.Contains("System", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Name.Contains("Build", StringComparison.OrdinalIgnoreCase) && m.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Name.Contains("Build", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object?[]? args = TryBuildPromptArgs(parameters, gameTitle, genreTone, glossary);
            if (args is null)
            {
                continue;
            }

            object? res = InvokeMethod(method.IsStatic ? null : builder, method, args);
            if (res is Task<string> taskStr)
            {
                return taskStr.GetAwaiter().GetResult();
            }

            if (res is string prompt)
            {
                return prompt;
            }
        }

        throw new InvalidOperationException(
            $"Could not invoke prompt builder method on '{builderType.FullName}'. Methods found: [{string.Join(", ", methods.Select(m => m.ToString()))}].");
    }

    public async Task SaveProfilesAsync(
        string profilesJsonPath,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProfile);
        ArgumentNullException.ThrowIfNull(profiles);

        (object? configService, Type configType) = ResolveProfilesConfigService(profilesJsonPath);
        MethodInfo[] methods = configType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => !m.IsSpecialName &&
                        (m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string) &&
                ((p.Name ?? string.Empty).Contains("path", StringComparison.OrdinalIgnoreCase) ||
                 (p.Name ?? string.Empty).Contains("file", StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (!method.IsStatic && configService is null)
            {
                continue;
            }

            if (TryBuildProfileSaveArgs(method, profilesJsonPath, activeProfile, profiles, ct, out object?[] args))
            {
                await InvokeVoidOrTaskAsync(method.IsStatic ? null : configService, method, args).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Found profile config service '{configType.FullName}' but no compatible Save/Write method matched. Methods: [{string.Join(", ", methods.Select(m => m.ToString()))}].");
    }

    public async Task<(string ActiveProfile, IReadOnlyDictionary<string, RouterProfile> Profiles)> LoadProfilesAsync(
        string profilesJsonPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesJsonPath);
        (object? configService, Type configType) = ResolveProfilesConfigService(profilesJsonPath);

        MethodInfo[] methods = configType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => !m.IsSpecialName &&
                        (m.Name.Contains("Load", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Read", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (!method.IsStatic && configService is null)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            object?[]? args = null;
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                args = [profilesJsonPath];
            }
            else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(CancellationToken))
            {
                args = [profilesJsonPath, ct];
            }
            else if (configService is not null && parameters.Length == 0)
            {
                args = [];
            }
            else if (configService is not null && parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken))
            {
                args = [ct];
            }

            if (args is null)
            {
                continue;
            }

            object? result = await InvokeAnyAsync(method.IsStatic ? null : configService, method, args).ConfigureAwait(false);
            if (result is RouterProfile singleProfile)
            {
                return ("default", new Dictionary<string, RouterProfile> { ["default"] = singleProfile });
            }

            if (result is IReadOnlyDictionary<string, RouterProfile> dict)
            {
                return ("local_router", dict);
            }

            if (result is not null)
            {
                string active = GetStringProperty(result, "ActiveProfile", "ActiveProfileName") ?? "local_router";
                object? profilesProp = GetPropertyValue(result, "Profiles");
                if (profilesProp is IDictionary rawDict)
                {
                    Dictionary<string, RouterProfile> map = new(StringComparer.Ordinal);
                    foreach (DictionaryEntry entry in rawDict)
                    {
                        if (entry.Key is string k && entry.Value is RouterProfile rp)
                        {
                            map[k] = rp;
                        }
                    }

                    return (active, map);
                }
            }
        }

        throw new InvalidOperationException(
            $"Found profile config service '{configType.FullName}' but no compatible Load/Read method matched.");
    }

    public SequentialWorkerHandle CreateSequentialWorker(
        ITranslatorProvider translator,
        ITextSanitizer sanitizer,
        DatabaseSession db,
        Action<string>? onAutoPause = null,
        Action<int, int>? onProgress = null,
        Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(db);

        Dictionary<Type, object> context = BuildDatabaseContext(db.ConnectionString, db.Connection);
        context[typeof(ITranslatorProvider)] = translator;
        context[typeof(ITextSanitizer)] = sanitizer;
        context[db.SubtitleEntryRepo.GetType()] = db.SubtitleEntryRepo;
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

        Type workerType = FindTypesInAssembly("Lokasync.Translator")
            .Concat(FindTypesInAssembly("Lokasync.App"))
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                (t.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Queue", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException(
                "Could not locate sequential queue worker type in Lokasync.Translator or Lokasync.App.");

        object worker = CreateInstance(workerType, context);
        AttachWorkerEvents(worker, workerType, onAutoPause, onProgress, onLog);
        return new SequentialWorkerHandle(worker, workerType);
    }

    public PipelineOrchestratorHandle CreatePipelineOrchestrator(
        DatabaseSession db,
        ITextSanitizer sanitizer,
        ITranslatorProvider translator,
        IBackupService backupService,
        IEnumerable<IEngineAdapter> adapters) =>
        CreatePipelineOrchestrator(db, sanitizer, translator, backupService, adapters, backupRootPath: null);

    public PipelineOrchestratorHandle CreatePipelineOrchestrator(
        DatabaseSession db,
        ITextSanitizer sanitizer,
        ITranslatorProvider translator,
        IBackupService backupService,
        IEnumerable<IEngineAdapter> adapters,
        string? backupRootPath)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(adapters);

        List<IEngineAdapter> adapterList = adapters.ToList();
        Dictionary<Type, object> context = BuildDatabaseContext(db.ConnectionString, db.Connection);
        if (!string.IsNullOrWhiteSpace(backupRootPath))
        {
            AddPathContext(context, db.ConnectionString, backupRootPath, profilesJsonPath: null);
        }

        context[typeof(ITextSanitizer)] = sanitizer;
        context[typeof(ITranslatorProvider)] = translator;
        context[typeof(IBackupService)] = backupService;
        context[typeof(IEnumerable<IEngineAdapter>)] = adapterList;
        context[typeof(IReadOnlyList<IEngineAdapter>)] = adapterList;
        context[typeof(IReadOnlyCollection<IEngineAdapter>)] = adapterList;
        context[typeof(IEngineAdapter[])] = adapterList.ToArray();
        RegisterInstanceAndInterfaces(context, db.ProjectRepo);
        RegisterInstanceAndInterfaces(context, db.SubtitleEntryRepo);
        RegisterInstanceAndInterfaces(context, db.BackupManifestRepo);
        RegisterInstanceAndInterfaces(context, db.ConnectionFactory);

        Type orchestratorType = FindTypesInAssembly("Lokasync.App")
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
                $"Could not locate pipeline orchestrator type in Lokasync.App. Types: [{string.Join(", ", FindTypesInAssembly("Lokasync.App").Select(t => t.Name))}].");

        object orchestrator = CreateInstance(orchestratorType, context);
        return new PipelineOrchestratorHandle(orchestrator, orchestratorType);
    }

    public ViewModelHandle CreateViewModel(string keyword, params object[] dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        Dictionary<Type, object> context = new();
        foreach (object dep in dependencies)
        {
            context[dep.GetType()] = dep;
            foreach (Type iface in dep.GetType().GetInterfaces())
            {
                context[iface] = dep;
            }
        }

        bool allowItemVm = keyword.Contains("Term", StringComparison.OrdinalIgnoreCase) ||
                           keyword.Contains("Item", StringComparison.OrdinalIgnoreCase) ||
                           keyword.Contains("Row", StringComparison.OrdinalIgnoreCase);

        List<Type> vmTypes = FindTypesInAssembly("Lokasync.App")
            .Where(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                !typeof(System.Windows.DependencyObject).IsAssignableFrom(t) &&
                (typeof(INotifyPropertyChanged).IsAssignableFrom(t) ||
                 t.Name.EndsWith("ViewModel", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Type vmType = vmTypes
            .Where(t =>
                t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                (allowItemVm ||
                 (!t.Name.Contains("Term", StringComparison.OrdinalIgnoreCase) &&
                  !t.Name.Contains("Item", StringComparison.OrdinalIgnoreCase) &&
                  !t.Name.Contains("Row", StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(t => string.Equals(t.Name, keyword + "ViewModel", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => string.Equals(t.Name, keyword, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.Name.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Name.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No ViewModel matched keyword '{keyword}'. Available ViewModels: [{string.Join(", ", vmTypes.Select(t => t.Name))}].");

        object instance = CreateInstance(vmType, context);
        return new ViewModelHandle(instance, vmType);
    }

    private Type FindSingleOrBestConcreteType<TInterface>(params string[] preferredAssemblies)
    {
        Type target = typeof(TInterface);
        List<Type> matches = [];

        foreach (string asmName in preferredAssemblies)
        {
            matches.AddRange(FindTypesInAssembly(asmName)
                .Where(t => !t.IsAbstract && !t.IsInterface && target.IsAssignableFrom(t)));
        }

        if (matches.Count == 0)
        {
            matches.AddRange(_allTypes
                .Where(t => !t.IsAbstract && !t.IsInterface && target.IsAssignableFrom(t)));
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No concrete implementation of {target.FullName} found in assemblies [{string.Join(", ", _assemblies.Keys)}].");
        }

        return matches[0];
    }

    private IEnumerable<Type> FindTypesInAssembly(string assemblyName) =>
        _assemblies.TryGetValue(assemblyName, out Assembly? asm)
            ? GetLoadableTypes(asm)
            : [];

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
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

    private Dictionary<Type, object> BuildDatabaseContext(string connectionString, SqliteConnection? openConnection)
    {
        string dbPath = connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? new SqliteConnectionStringBuilder(connectionString).DataSource
            : connectionString;

        Dictionary<Type, object> context = new();

        if (openConnection is not null)
        {
            context[typeof(SqliteConnection)] = openConnection;
        }

        AddPathContext(context, dbPath, backupRootPath: null, profilesJsonPath: null);
        return context;
    }

    private static void AddPathContext(
        Dictionary<Type, object> context,
        string? dbOrConn,
        string? backupRootPath,
        string? profilesJsonPath)
    {
        if (!string.IsNullOrEmpty(dbOrConn))
        {
            context[typeof(PathMarkerDb)] = new PathMarkerDb(dbOrConn);
        }

        if (!string.IsNullOrEmpty(backupRootPath))
        {
            context[typeof(PathMarkerBackup)] = new PathMarkerBackup(backupRootPath);
        }

        if (!string.IsNullOrEmpty(profilesJsonPath))
        {
            context[typeof(PathMarkerProfiles)] = new PathMarkerProfiles(profilesJsonPath);
        }
    }

    private object ResolveRepository(
        string keyword,
        Dictionary<Type, object> context,
        IReadOnlyList<Type> coreTypes,
        Type? requiredTypeRef = null)
    {
        Type? repoType = coreTypes.FirstOrDefault(t =>
            t.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            (t.Name.Contains("Repo", StringComparison.OrdinalIgnoreCase) ||
             t.Name.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
             (requiredTypeRef is not null && HasMethodUsingType(t, requiredTypeRef))));

        if (repoType is null && requiredTypeRef is not null)
        {
            repoType = coreTypes.FirstOrDefault(t => HasMethodUsingType(t, requiredTypeRef));
        }

        if (repoType is null)
        {
            throw new InvalidOperationException(
                $"Could not resolve '{keyword}' repository in Lokasync.Core. Candidate types: [{string.Join(", ", coreTypes.Select(t => t.Name))}].");
        }

        return CreateInstance(repoType, context);
    }

    private (object? Service, Type ServiceType) ResolveProfilesConfigService(string profilesJsonPath)
    {
        Type? configType = FindTypesInAssembly("Lokasync.Translator")
            .Concat(FindTypesInAssembly("Lokasync.App"))
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                (t.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Config", StringComparison.OrdinalIgnoreCase)) &&
                t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Any(m => m.Name.Contains("Load", StringComparison.OrdinalIgnoreCase) ||
                              m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase)));

        if (configType is null)
        {
            throw new InvalidOperationException(
                "Could not locate profiles.json configuration service in Lokasync.Translator or Lokasync.App.");
        }

        Dictionary<Type, object> context = new()
        {
            [typeof(string)] = profilesJsonPath
        };
        AddPathContext(context, dbOrConn: null, backupRootPath: null, profilesJsonPath: profilesJsonPath);
        TryCreateInstance(configType, context, new HashSet<Type>(), out object? instance);
        return (instance, configType);
    }

    private object CreateInstance(Type targetType, Dictionary<Type, object> context)
    {
        if (TryCreateInstance(targetType, context, new HashSet<Type>(), out object? instance) && instance is not null)
        {
            return instance;
        }

        string ctors = string.Join("; ", targetType.GetConstructors().Select(c =>
            $"({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})"));
        throw new InvalidOperationException(
            $"Failed to construct '{targetType.FullName}'. Available constructors: [{ctors}].");
    }

    private bool TryCreateInstance(
        Type targetType,
        Dictionary<Type, object> context,
        HashSet<Type> visiting,
        out object? instance)
    {
        instance = null;
        if (context.TryGetValue(targetType, out object? existing))
        {
            instance = existing;
            return true;
        }

        Type unwrapped = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (unwrapped.IsPrimitive ||
            unwrapped == typeof(string) ||
            unwrapped.IsEnum ||
            unwrapped == typeof(decimal) ||
            typeof(System.Windows.DependencyObject).IsAssignableFrom(unwrapped))
        {
            return false;
        }

        if (targetType.IsInterface || targetType.IsAbstract)
        {
            Type? concrete = _allTypes.FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                !typeof(System.Windows.DependencyObject).IsAssignableFrom(t) &&
                targetType.IsAssignableFrom(t));
            if (concrete is null)
            {
                return false;
            }

            return TryCreateInstance(concrete, context, visiting, out instance);
        }

        if (!visiting.Add(targetType))
        {
            return false;
        }

        try
        {
            ConstructorInfo[] ctors = targetType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Count(p =>
                    p.ParameterType == typeof(HttpClient) ||
                    p.ParameterType == typeof(HttpMessageHandler) ||
                    p.ParameterType == typeof(SqliteConnection)))
                .ThenByDescending(c => c.GetParameters().Length)
                .ToArray();

            foreach (ConstructorInfo ctor in ctors)
            {
                ParameterInfo[] parameters = ctor.GetParameters();
                object?[] args = new object?[parameters.Length];
                bool ok = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (!TryResolveParameter(parameters[i], context, visiting, out args[i]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    instance = ctor.Invoke(args);
                    return true;
                }
            }

            if (TryCreateViaStaticFactory(targetType, context, out instance))
            {
                return true;
            }

            return false;
        }
        finally
        {
            visiting.Remove(targetType);
        }
    }

    private bool TryResolveParameter(
        ParameterInfo parameter,
        Dictionary<Type, object> context,
        HashSet<Type> visiting,
        out object? value)
    {
        Type pType = parameter.ParameterType;
        if (pType != typeof(string) && context.TryGetValue(pType, out value))
        {
            return true;
        }

        if (pType == typeof(string))
        {
            string pName = parameter.Name ?? string.Empty;
            if (pName.Contains("backup", StringComparison.OrdinalIgnoreCase) &&
                context.TryGetValue(typeof(PathMarkerBackup), out object? backupObj))
            {
                value = ((PathMarkerBackup)backupObj).Path;
                return true;
            }

            if (pName.Contains("profile", StringComparison.OrdinalIgnoreCase) &&
                context.TryGetValue(typeof(PathMarkerProfiles), out object? profObj))
            {
                value = ((PathMarkerProfiles)profObj).Path;
                return true;
            }

            if (pName.Contains("conn", StringComparison.OrdinalIgnoreCase) &&
                context.TryGetValue(typeof(PathMarkerDb), out object? connDbObj))
            {
                string raw = ((PathMarkerDb)connDbObj).Path;
                value = raw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                    ? raw
                    : $"Data Source={raw}";
                return true;
            }

            if ((pName.Contains("db", StringComparison.OrdinalIgnoreCase) ||
                 pName.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                 pName.Contains("sqlite", StringComparison.OrdinalIgnoreCase)) &&
                context.TryGetValue(typeof(PathMarkerDb), out object? dbObj))
            {
                string raw = ((PathMarkerDb)dbObj).Path;
                value = raw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                    ? new SqliteConnectionStringBuilder(raw).DataSource
                    : raw;
                return true;
            }

            if (pName.Contains("root", StringComparison.OrdinalIgnoreCase) ||
                pName.Contains("dir", StringComparison.OrdinalIgnoreCase) ||
                pName.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                pName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                pName.Contains("file", StringComparison.OrdinalIgnoreCase))
            {
                bool isBackupDeclaringType = parameter.Member.DeclaringType is Type declType &&
                                             typeof(IBackupService).IsAssignableFrom(declType);
                if (context.TryGetValue(typeof(PathMarkerBackup), out object? genericBackupObj) &&
                    (isBackupDeclaringType || !context.ContainsKey(typeof(PathMarkerDb))))
                {
                    value = ((PathMarkerBackup)genericBackupObj).Path;
                    return true;
                }

                if (context.TryGetValue(typeof(PathMarkerProfiles), out object? genericProfObj) &&
                    !context.ContainsKey(typeof(PathMarkerDb)))
                {
                    value = ((PathMarkerProfiles)genericProfObj).Path;
                    return true;
                }

                if (context.TryGetValue(typeof(PathMarkerDb), out object? genericDbObj))
                {
                    string raw = ((PathMarkerDb)genericDbObj).Path;
                    value = raw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                        ? new SqliteConnectionStringBuilder(raw).DataSource
                        : raw;
                    return true;
                }

                if (genericBackupObj is PathMarkerBackup fallbackBackup)
                {
                    value = fallbackBackup.Path;
                    return true;
                }
            }

            if (parameter.HasDefaultValue)
            {
                value = parameter.DefaultValue;
                return true;
            }

            if (context.TryGetValue(typeof(string), out object? defaultStr))
            {
                value = defaultStr;
                return true;
            }
        }

        if (pType == typeof(Random))
        {
            value = Random.Shared;
            return true;
        }

        if (pType == typeof(TimeProvider))
        {
            value = TimeProvider.System;
            return true;
        }

        if (parameter.HasDefaultValue &&
            (pType.IsPrimitive || pType.IsEnum || pType == typeof(decimal) || pType.IsValueType))
        {
            value = parameter.DefaultValue;
            return true;
        }

        if (pType.IsGenericType)
        {
            Type genDef = pType.GetGenericTypeDefinition();
            if (genDef == typeof(IEnumerable<>) || genDef == typeof(IReadOnlyList<>) || genDef == typeof(IReadOnlyCollection<>))
            {
                Type elemType = pType.GetGenericArguments()[0];
                if (elemType == typeof(IEngineAdapter))
                {
                    value = CreateAllEngineAdapters();
                    return true;
                }
            }
        }

        if (pType == typeof(IEngineAdapter[]))
        {
            value = CreateAllEngineAdapters().ToArray();
            return true;
        }

        if (TryCreateInstance(pType, context, visiting, out value))
        {
            return true;
        }

        if (parameter.HasDefaultValue)
        {
            value = parameter.DefaultValue;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryCreateViaStaticFactory(
        Type targetType,
        Dictionary<Type, object> context,
        out object? instance)
    {
        instance = null;
        Type unwrapped = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (unwrapped.IsPrimitive ||
            unwrapped == typeof(string) ||
            unwrapped.IsEnum ||
            unwrapped == typeof(decimal) ||
            (unwrapped.Namespace?.StartsWith("System", StringComparison.Ordinal) == true))
        {
            return false;
        }
        string? dbRaw = context.TryGetValue(typeof(PathMarkerDb), out object? dbMarker)
            ? ((PathMarkerDb)dbMarker).Path
            : context.TryGetValue(typeof(string), out object? strObj) ? strObj as string : null;

        bool isInMemory = !string.IsNullOrEmpty(dbRaw) &&
                          (string.Equals(dbRaw, ":memory:", StringComparison.OrdinalIgnoreCase) ||
                           dbRaw.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase) ||
                           dbRaw.Contains("Data Source=:memory:", StringComparison.OrdinalIgnoreCase));

        string dbPath = !string.IsNullOrEmpty(dbRaw) && dbRaw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? new SqliteConnectionStringBuilder(dbRaw).DataSource
            : dbRaw ?? string.Empty;

        MethodInfo[] factories = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && targetType.IsAssignableFrom(m.ReturnType))
            .OrderByDescending(m => isInMemory && m.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => !isInMemory && (m.Name.Contains("File", StringComparison.OrdinalIgnoreCase) ||
                                                  m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                                                  m.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase)))
            .ThenBy(m => m.Name.Contains("Project", StringComparison.OrdinalIgnoreCase) ||
                          m.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (MethodInfo method in factories)
        {
            if (!isInMemory && method.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ParameterInfo[] ps = method.GetParameters();
            object?[] args = new object?[ps.Length];
            bool ok = true;

            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (p.ParameterType == typeof(string) && !string.IsNullOrEmpty(dbRaw))
                {
                    bool wantsConn = (p.Name ?? string.Empty).Contains("conn", StringComparison.OrdinalIgnoreCase);
                    args[i] = wantsConn
                        ? (dbRaw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) ? dbRaw : $"Data Source={dbRaw}")
                        : dbPath;
                }
                else if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                }
                else if (p.ParameterType == typeof(bool) && (p.Name ?? string.Empty).Contains("create", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = true;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                instance = InvokeMethod(null, method, args);
                if (instance is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<(SqliteConnection? Connection, object? Lease)> TryOpenViaFactoryAsync(
        object factoryInstance,
        string dbPath,
        string connectionString,
        CancellationToken ct)
    {
        MethodInfo[] methods = factoryInstance.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => IsConnectionOrLeaseType(ReturnUnwrappedType(m.ReturnType)))
            .OrderByDescending(m => ReturnUnwrappedType(m.ReturnType) == typeof(SqliteConnection))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object?[]? args = parameters.Length switch
            {
                0 => [],
                1 when parameters[0].ParameterType == typeof(string) => [parameters[0].Name?.Contains("conn", StringComparison.OrdinalIgnoreCase) == true ? connectionString : dbPath],
                1 when parameters[0].ParameterType == typeof(CancellationToken) => [ct],
                2 when parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(CancellationToken)
                    => [parameters[0].Name?.Contains("conn", StringComparison.OrdinalIgnoreCase) == true ? connectionString : dbPath, ct],
                _ => null
            };

            if (args is null)
            {
                continue;
            }

            object? res = await InvokeAnyAsync(factoryInstance, method, args).ConfigureAwait(false);
            if (res is SqliteConnection conn)
            {
                if (conn.State != ConnectionState.Open)
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                }

                return (conn, null);
            }

            if (res is not null)
            {
                PropertyInfo? connProp = GetSqliteConnectionProperty(res.GetType());
                if (connProp?.GetValue(res) is SqliteConnection leasedConn)
                {
                    if (leasedConn.State != ConnectionState.Open)
                    {
                        await leasedConn.OpenAsync(ct).ConfigureAwait(false);
                    }

                    return (leasedConn, res);
                }
            }
        }

        return (null, null);
    }

    private static void RegisterInstanceAndInterfaces(Dictionary<Type, object> context, object? instance)
    {
        if (instance is null)
        {
            return;
        }

        Type type = instance.GetType();
        context[type] = instance;
        foreach (Type iface in type.GetInterfaces())
        {
            context[iface] = instance;
        }
    }

    private static async Task TryInvokeSchemaInitAsync(
        object? target,
        Type targetType,
        SqliteConnection? connection,
        CancellationToken ct)
    {
        BindingFlags flags = target is null
            ? BindingFlags.Public | BindingFlags.Static
            : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        MethodInfo[] methods = targetType
            .GetMethods(flags)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        !m.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase) &&
                        !IsConnectionOrLeaseType(ReturnUnwrappedType(m.ReturnType)) &&
                        (m.Name.Contains("Init", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Ensure", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Init", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => connection is not null && m.GetParameters().Any(p => p.ParameterType == typeof(SqliteConnection)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (target is null && !method.IsStatic)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            object?[]? args = parameters.Length switch
            {
                0 => [],
                1 when connection is not null && parameters[0].ParameterType == typeof(SqliteConnection) => [connection],
                1 when parameters[0].ParameterType == typeof(CancellationToken) => [ct],
                2 when connection is not null && parameters[0].ParameterType == typeof(SqliteConnection) && parameters[1].ParameterType == typeof(CancellationToken) => [connection, ct],
                2 when connection is not null && parameters[0].ParameterType == typeof(CancellationToken) && parameters[1].ParameterType == typeof(SqliteConnection) => [ct, connection],
                _ => null
            };

            if (args is not null)
            {
                await InvokeVoidOrTaskAsync(method.IsStatic ? null : target, method, args).ConfigureAwait(false);
                return;
            }
        }
    }

    private static object?[]? TryBuildPromptArgs(
        ParameterInfo[] parameters,
        string gameTitle,
        string genreTone,
        IReadOnlyList<(string Source, string Target, string Note)> glossary)
    {
        if (parameters.Length == 1)
        {
            Type p0 = parameters[0].ParameterType;
            if (!p0.IsPrimitive && p0 != typeof(string))
            {
                if (TryBuildNarrativeContextObject(p0, gameTitle, genreTone, glossary, out object? ctxArg))
                {
                    return [ctxArg];
                }

                return null;
            }

            if (p0 == typeof(string))
            {
                string narrativeJson = JsonSerializer.Serialize(new
                {
                    game_title = gameTitle,
                    genre_tone = genreTone,
                    glossary = glossary.Select(g => new { source = g.Source, target = g.Target, note = g.Note }).ToArray()
                });
                return [narrativeJson];
            }

            return null;
        }

        if (parameters.Length == 2)
        {
            Type p0 = parameters[0].ParameterType;
            Type p1 = parameters[1].ParameterType;
            if (p0 == typeof(string) && p1 == typeof(string))
            {
                bool firstIsTone = (parameters[0].Name ?? string.Empty).Contains("tone", StringComparison.OrdinalIgnoreCase) &&
                                   (parameters[1].Name ?? string.Empty).Contains("title", StringComparison.OrdinalIgnoreCase);
                return firstIsTone ? [genreTone, gameTitle] : [gameTitle, genreTone];
            }

            if (p0 == typeof(string) && TryBuildGlossaryCollection(p1, glossary, out object? glossaryArg))
            {
                return [gameTitle, glossaryArg];
            }

            if (!p0.IsPrimitive && p0 != typeof(string) &&
                TryBuildGlossaryCollection(p1, glossary, out object? ctxGlossaryArg) &&
                TryBuildNarrativeContextObject(p0, gameTitle, genreTone, glossary, out object? ctxArg))
            {
                return [ctxArg, ctxGlossaryArg];
            }

            return null;
        }

        if (parameters.Length >= 3 && parameters.Skip(3).All(p => p.HasDefaultValue))
        {
            object?[] args = new object?[parameters.Length];
            for (int j = 3; j < parameters.Length; j++)
            {
                args[j] = parameters[j].DefaultValue;
            }

            // (string gameTitle, string genreTone, IEnumerable<TGlossary> glossary)
            if (parameters[0].ParameterType == typeof(string) &&
                parameters[1].ParameterType == typeof(string) &&
                TryBuildGlossaryCollection(parameters[2].ParameterType, glossary, out object? glossaryArg))
            {
                bool firstIsTone = (parameters[0].Name ?? string.Empty).Contains("tone", StringComparison.OrdinalIgnoreCase) &&
                                   (parameters[1].Name ?? string.Empty).Contains("title", StringComparison.OrdinalIgnoreCase);
                args[0] = firstIsTone ? genreTone : gameTitle;
                args[1] = firstIsTone ? gameTitle : genreTone;
                args[2] = glossaryArg;
                return args;
            }
        }

        return null;
    }

    private static bool TryBuildGlossaryCollection(
        Type targetType,
        IReadOnlyList<(string Source, string Target, string Note)> glossary,
        out object? collection)
    {
        if (targetType == typeof(string))
        {
            collection = JsonSerializer.Serialize(
                glossary.Select(g => new { source = g.Source, target = g.Target, note = g.Note }).ToArray());
            return true;
        }

        if (targetType.IsArray)
        {
            Type? elemType = targetType.GetElementType();
            if (elemType is null || elemType.IsPrimitive || elemType == typeof(string))
            {
                collection = null;
                return false;
            }

            Array arr = Array.CreateInstance(elemType, glossary.Count);
            for (int i = 0; i < glossary.Count; i++)
            {
                (string src, string tgt, string note) = glossary[i];
                if (!TryConstructGlossaryEntry(elemType, src, tgt, note, out object? item) || item is null)
                {
                    collection = null;
                    return false;
                }

                arr.SetValue(item, i);
            }

            collection = arr;
            return true;
        }

        if (targetType.IsGenericType)
        {
            Type genDef = targetType.GetGenericTypeDefinition();
            if (genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IReadOnlyCollection<>) ||
                genDef == typeof(IList<>) ||
                genDef == typeof(ICollection<>) ||
                genDef == typeof(List<>))
            {
                Type elemType = targetType.GetGenericArguments()[0];
                if (elemType.IsPrimitive || elemType == typeof(string))
                {
                    collection = null;
                    return false;
                }

                Type listType = typeof(List<>).MakeGenericType(elemType);
                if (!targetType.IsAssignableFrom(listType))
                {
                    collection = null;
                    return false;
                }

                IList list = (IList)Activator.CreateInstance(listType)!;
                foreach ((string src, string tgt, string note) in glossary)
                {
                    if (!TryConstructGlossaryEntry(elemType, src, tgt, note, out object? item) || item is null)
                    {
                        collection = null;
                        return false;
                    }

                    list.Add(item);
                }

                collection = list;
                return true;
            }
        }

        collection = null;
        return false;
    }

    private static bool TryBuildNarrativeContextObject(
        Type contextType,
        string gameTitle,
        string genreTone,
        IReadOnlyList<(string Source, string Target, string Note)> glossary,
        out object? contextObj)
    {
        contextObj = null;
        Type? glossaryElemType = null;
        PropertyInfo? glossaryProp = contextType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name.Contains("Glossary", StringComparison.OrdinalIgnoreCase));

        if (glossaryProp is not null && glossaryProp.PropertyType.IsGenericType)
        {
            glossaryElemType = glossaryProp.PropertyType.GetGenericArguments()[0];
        }
        else
        {
            foreach (ConstructorInfo ctor in contextType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                ParameterInfo? gParam = ctor.GetParameters().FirstOrDefault(p =>
                    p.ParameterType.IsGenericType &&
                    (p.Name ?? string.Empty).Contains("glossary", StringComparison.OrdinalIgnoreCase));
                if (gParam is not null)
                {
                    glossaryElemType = gParam.ParameterType.GetGenericArguments()[0];
                    break;
                }
            }
        }

        IList? glossaryList = null;
        if (glossaryElemType is not null)
        {
            Type listType = typeof(List<>).MakeGenericType(glossaryElemType);
            glossaryList = (IList)Activator.CreateInstance(listType)!;
            foreach ((string src, string tgt, string note) in glossary)
            {
                if (!TryConstructGlossaryEntry(glossaryElemType, src, tgt, note, out object? item) || item is null)
                {
                    return false;
                }

                glossaryList.Add(item);
            }
        }

        ConstructorInfo[] ctors = contextType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (ConstructorInfo ctor in ctors.OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length == 0)
            {
                object obj = ctor.Invoke([]);
                SetIfWritable(obj, "GameTitle", gameTitle);
                SetIfWritable(obj, "GenreTone", genreTone);
                if (glossaryProp is not null && glossaryProp.CanWrite && glossaryList is not null)
                {
                    glossaryProp.SetValue(obj, glossaryList);
                }

                contextObj = obj;
                return true;
            }

            object?[] args = new object?[parameters.Length];
            bool ok = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                string pName = p.Name ?? string.Empty;
                if (p.ParameterType == typeof(string) && pName.Contains("title", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = gameTitle;
                }
                else if (p.ParameterType == typeof(string))
                {
                    args[i] = genreTone;
                }
                else if (glossaryList is not null && p.ParameterType.IsAssignableFrom(glossaryList.GetType()))
                {
                    args[i] = glossaryList;
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
                contextObj = ctor.Invoke(args);
                return true;
            }
        }

        return false;
    }

    private static bool TryConstructGlossaryEntry(Type itemType, string source, string target, string note, out object? entry)
    {
        if (itemType == typeof(ValueTuple<string, string, string>))
        {
            entry = (source, target, note);
            return true;
        }

        if (itemType == typeof(ValueTuple<string, string>))
        {
            entry = (source, target);
            return true;
        }

        foreach (ConstructorInfo ctor in itemType.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            if (ps.Length == 0)
            {
                object item = ctor.Invoke([]);
                SetIfWritable(item, "Source", source);
                SetIfWritable(item, "Target", target);
                SetIfWritable(item, "Note", note);
                entry = item;
                return true;
            }

            if (ps.Length is 2 or 3 && ps.All(p => p.ParameterType == typeof(string)))
            {
                object?[] args = ps.Length == 2 ? [source, target] : [source, target, note];
                entry = ctor.Invoke(args);
                return true;
            }
        }

        entry = null;
        return false;
    }

    private static bool TryBuildProfileSaveArgs(
        MethodInfo method,
        string profilesJsonPath,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct,
        out object?[] args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo p = parameters[i];
            if (p.ParameterType == typeof(string))
            {
                string pName = p.Name ?? string.Empty;
                args[i] = (pName.Contains("active", StringComparison.OrdinalIgnoreCase) ||
                           pName.Contains("profileName", StringComparison.OrdinalIgnoreCase) ||
                           pName.Contains("key", StringComparison.OrdinalIgnoreCase))
                    ? activeProfile
                    : profilesJsonPath;
            }
            else if (p.ParameterType == typeof(RouterProfile))
            {
                args[i] = profiles.TryGetValue(activeProfile, out RouterProfile? rp)
                    ? rp
                    : profiles.Values.First();
            }
            else if (p.ParameterType.IsAssignableFrom(typeof(Dictionary<string, RouterProfile>)))
            {
                args[i] = new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal);
            }
            else if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else if (TryBuildProfilesWrapper(p.ParameterType, activeProfile, profiles, out object? wrapper))
            {
                args[i] = wrapper;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildProfilesWrapper(
        Type wrapperType,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        out object? wrapper)
    {
        wrapper = null;
        if (wrapperType.IsPrimitive ||
            wrapperType.IsEnum ||
            wrapperType.IsAbstract ||
            wrapperType.IsInterface ||
            wrapperType == typeof(string) ||
            wrapperType == typeof(CancellationToken) ||
            wrapperType == typeof(RouterProfile) ||
            typeof(IDictionary).IsAssignableFrom(wrapperType))
        {
            return false;
        }

        bool looksLikeProfileWrapper =
            wrapperType.GetProperty("Profiles", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null ||
            wrapperType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Any(c =>
                c.GetParameters().Any(cp => cp.ParameterType.IsAssignableFrom(typeof(Dictionary<string, RouterProfile>))));

        if (!looksLikeProfileWrapper)
        {
            return false;
        }

        ConstructorInfo[] ctors = wrapperType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToArray();

        foreach (ConstructorInfo ctor in ctors)
        {
            ParameterInfo[] ps = ctor.GetParameters();
            if (ps.Length == 0)
            {
                object obj = ctor.Invoke([]);
                SetIfWritable(obj, "ActiveProfile", activeProfile);
                SetIfWritable(obj, "ActiveProfileName", activeProfile);

                PropertyInfo? profilesProp = wrapperType.GetProperty("Profiles", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (profilesProp is not null)
                {
                    if (profilesProp.CanWrite)
                    {
                        profilesProp.SetValue(obj, new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal));
                    }
                    else if (profilesProp.GetValue(obj) is IDictionary existing)
                    {
                        existing.Clear();
                        foreach (KeyValuePair<string, RouterProfile> kv in profiles)
                        {
                            existing[kv.Key] = kv.Value;
                        }
                    }
                }

                wrapper = obj;
                return true;
            }

            object?[] ctorArgs = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo cp = ps[i];
                if (cp.ParameterType == typeof(string))
                {
                    ctorArgs[i] = activeProfile;
                }
                else if (cp.ParameterType.IsAssignableFrom(typeof(Dictionary<string, RouterProfile>)))
                {
                    ctorArgs[i] = new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal);
                }
                else if (cp.HasDefaultValue)
                {
                    ctorArgs[i] = cp.DefaultValue;
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

    private static void AttachWorkerEvents(
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
                TrySubscribeSimpleEvent(worker, evt, msg => onAutoPause(msg));
            }
            else if (onLog is not null && evt.Name.Contains("Log", StringComparison.OrdinalIgnoreCase))
            {
                TrySubscribeSimpleEvent(worker, evt, msg => onLog(msg));
            }
            else if (onProgress is not null && evt.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase))
            {
                TrySubscribeProgressEvent(worker, evt, onProgress);
            }
        }
    }

    private static void TrySubscribeSimpleEvent(object target, EventInfo evt, Action<string> callback)
    {
        Type? handlerType = evt.EventHandlerType;
        if (handlerType is null)
        {
            return;
        }

        MethodInfo? invoke = handlerType.GetMethod("Invoke");
        if (invoke is null)
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
            EventHandler<EventArgs> adapter = (_, e) => callback(e.ToString() ?? evt.Name);
            if (handlerType == typeof(EventHandler))
            {
                evt.AddEventHandler(target, new EventHandler((_, e) => callback(e.ToString() ?? evt.Name)));
            }
            else
            {
                MethodInfo genericFactory = typeof(OpaqueContractBridge)
                    .GetMethod(nameof(CreateEventArgsBridge), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(ps[1].ParameterType);
                Delegate del = (Delegate)genericFactory.Invoke(null, [handlerType, callback])!;
                evt.AddEventHandler(target, del);
            }

            _ = adapter;
        }
    }

    private static Delegate CreateEventArgsBridge<TEventArgs>(Type handlerType, Action<string> callback)
    {
        Action<object?, TEventArgs> wrapper = (_, args) => callback(args?.ToString() ?? typeof(TEventArgs).Name);
        return Delegate.CreateDelegate(handlerType, wrapper.Target, wrapper.Method);
    }

    private static void TrySubscribeProgressEvent(object target, EventInfo evt, Action<int, int> onProgress)
    {
        Type? handlerType = evt.EventHandlerType;
        MethodInfo? invoke = handlerType?.GetMethod("Invoke");
        if (handlerType is null || invoke is null)
        {
            return;
        }

        ParameterInfo[] ps = invoke.GetParameters();
        if (ps.Length == 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int))
        {
            evt.AddEventHandler(target, Delegate.CreateDelegate(handlerType, onProgress.Target, onProgress.Method));
        }
    }

    internal static async Task<object?> InvokeAnyAsync(object? target, MethodInfo method, params object?[] args)
    {
        object? rawResult;
        try
        {
            rawResult = method.Invoke(target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        if (rawResult is null)
        {
            return null;
        }

        if (rawResult is Task task)
        {
            await task.ConfigureAwait(false);
            if (method.ReturnType == typeof(Task) || task.GetType() == typeof(Task))
            {
                return null;
            }

            PropertyInfo? resultProp = task.GetType().GetProperty("Result");
            if (resultProp is null || resultProp.PropertyType.Name == "VoidTaskResult")
            {
                return null;
            }

            return resultProp.GetValue(task);
        }

        Type rType = rawResult.GetType();
        if (method.ReturnType == typeof(ValueTask) || rType == typeof(ValueTask))
        {
            await ((ValueTask)rawResult).ConfigureAwait(false);
            return null;
        }

        if (rType.IsGenericType && rType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            MethodInfo asTask = rType.GetMethod("AsTask")!;
            Task vtTask = (Task)asTask.Invoke(rawResult, null)!;
            await vtTask.ConfigureAwait(false);
            return vtTask.GetType().GetProperty("Result")!.GetValue(vtTask);
        }

        return rawResult;
    }

    internal static async Task InvokeVoidOrTaskAsync(object? target, MethodInfo method, params object?[] args)
    {
        await InvokeAnyAsync(target, method, args).ConfigureAwait(false);
    }

    internal static object? InvokeMethod(object? target, MethodInfo method, params object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static bool HasMethodReturning<T>(Type type, params Type[] paramTypes) =>
        FindMethod(type, r => typeof(T).IsAssignableFrom(r), paramTypes) is not null;

    private static bool HasMethodReturningConnection(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => IsConnectionOrLeaseType(ReturnUnwrappedType(m.ReturnType)));

    private static bool IsConnectionOrLeaseType(Type type) =>
        type == typeof(SqliteConnection) || GetSqliteConnectionProperty(type) is not null;

    private static PropertyInfo? GetSqliteConnectionProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.PropertyType == typeof(SqliteConnection));

    private static bool HasMethodUsingType(Type type, Type targetType) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m =>
                ReturnUnwrappedType(m.ReturnType) == targetType ||
                ReturnUnwrappedType(m.ReturnType).GetGenericArguments().Contains(targetType) ||
                m.GetParameters().Any(p =>
                    p.ParameterType == targetType ||
                    (p.ParameterType.IsGenericType && p.ParameterType.GetGenericArguments().Contains(targetType))));

    private static MethodInfo? FindMethod(Type type, Func<Type, bool> returnPredicate, params Type[] paramTypes)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.IsSpecialName || method.DeclaringType == typeof(object))
            {
                continue;
            }

            if (!returnPredicate(ReturnUnwrappedType(method.ReturnType)))
            {
                continue;
            }

            ParameterInfo[] ps = method.GetParameters();
            if (ps.Length != paramTypes.Length)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < ps.Length; i++)
            {
                if (!ps[i].ParameterType.IsAssignableFrom(paramTypes[i]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return method;
            }
        }

        return null;
    }

    internal static Type ReturnUnwrappedType(Type returnType)
    {
        if (returnType.IsGenericType)
        {
            Type def = returnType.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }

    private static void SetIfWritable(object target, string propertyName, object? value)
    {
        PropertyInfo? prop = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }

    private static string? GetStringProperty(object target, params string[] names)
    {
        foreach (string name in names)
        {
            if (GetPropertyValue(target, name) is string s)
            {
                return s;
            }
        }

        return null;
    }

    private static object? GetPropertyValue(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(target);

    private sealed record PathMarkerDb(string Path);
    private sealed record PathMarkerBackup(string Path);
    private sealed record PathMarkerProfiles(string Path);
}

public sealed class DatabaseSession : IAsyncDisposable, IDisposable
{
    private readonly OpaqueContractBridge _bridge;
    private readonly bool _ownsConnection;

    internal DatabaseSession(
        OpaqueContractBridge bridge,
        string connectionString,
        string databasePath,
        SqliteConnection connection,
        object? connectionFactory,
        object projectRepo,
        object subtitleEntryRepo,
        object backupManifestRepo,
        bool ownsConnection = true)
    {
        _bridge = bridge;
        _ownsConnection = ownsConnection;
        ConnectionString = connectionString;
        DatabasePath = databasePath;
        Connection = connection;
        ConnectionFactory = connectionFactory;
        ProjectRepo = projectRepo;
        SubtitleEntryRepo = subtitleEntryRepo;
        BackupManifestRepo = backupManifestRepo;
    }

    public string ConnectionString { get; }
    public string DatabasePath { get; }
    public SqliteConnection Connection { get; }
    public object? ConnectionFactory { get; }
    public object ProjectRepo { get; }
    public object SubtitleEntryRepo { get; }
    public object BackupManifestRepo { get; }

    public Task<object?> InvokeRepoAsync(
        object repo,
        Func<MethodInfo, bool> methodSelector,
        params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(methodSelector);

        MethodInfo? method = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => !m.IsSpecialName && methodSelector(m));

        if (method is null)
        {
            string available = string.Join(", ", repo.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).Select(m => m.ToString()));
            throw new InvalidOperationException(
                $"No matching method found on repository '{repo.GetType().FullName}'. Available methods: [{available}].");
        }

        return OpaqueContractBridge.InvokeAnyAsync(repo, method, args);
    }

    public async Task<IReadOnlyList<SubtitleEntry>> GetPendingEntriesAsync(CancellationToken ct = default)
    {
        MethodInfo? method = SubtitleEntryRepo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (m.IsSpecialName ||
                    !m.Name.Contains("Pending", StringComparison.OrdinalIgnoreCase) ||
                    !typeof(IEnumerable<SubtitleEntry>).IsAssignableFrom(OpaqueContractBridge.ReturnUnwrappedType(m.ReturnType)))
                {
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                return ps.Length == 0 || (ps.Length == 1 && ps[0].ParameterType == typeof(CancellationToken));
            });

        if (method is not null)
        {
            object?[] args = method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(CancellationToken)
                ? [ct]
                : [];
            object? res = await OpaqueContractBridge.InvokeAnyAsync(SubtitleEntryRepo, method, args).ConfigureAwait(false);
            if (res is IEnumerable<SubtitleEntry> pendingList)
            {
                return pendingList.ToList();
            }
        }

        IReadOnlyList<SubtitleEntry> all = await GetAllEntriesAsync(ct).ConfigureAwait(false);
        return all.Where(e => e.Status == EntryStatus.Pending).ToList();
    }

    public async Task<IReadOnlyList<SubtitleEntry>> GetAllEntriesAsync(CancellationToken ct = default)
    {
        MethodInfo? method = SubtitleEntryRepo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (m.IsSpecialName ||
                    m.Name.Contains("Pending", StringComparison.OrdinalIgnoreCase) ||
                    !typeof(IEnumerable<SubtitleEntry>).IsAssignableFrom(OpaqueContractBridge.ReturnUnwrappedType(m.ReturnType)))
                {
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                return ps.Length == 0 || (ps.Length == 1 && ps[0].ParameterType == typeof(CancellationToken));
            });

        if (method is null)
        {
            throw new InvalidOperationException(
                $"Could not find GetAll subtitle entries method on '{SubtitleEntryRepo.GetType().FullName}'.");
        }

        object?[] args = method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(CancellationToken)
            ? [ct]
            : [];
        object? res = await OpaqueContractBridge.InvokeAnyAsync(SubtitleEntryRepo, method, args).ConfigureAwait(false);
        return res is IEnumerable<SubtitleEntry> list ? list.ToList() : [];
    }

    public async Task InsertSubtitleEntriesAsync(IReadOnlyList<SubtitleEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        MethodInfo? method = SubtitleEntryRepo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                (m.Name.Contains("Insert", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase)) &&
                m.GetParameters().Any(p => p.ParameterType.IsAssignableFrom(typeof(List<SubtitleEntry>)) || p.ParameterType.IsAssignableFrom(typeof(IReadOnlyList<SubtitleEntry>))));

        if (method is null)
        {
            throw new InvalidOperationException(
                $"Could not find bulk insert method on '{SubtitleEntryRepo.GetType().FullName}'.");
        }

        ParameterInfo[] ps = method.GetParameters();
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

        await OpaqueContractBridge.InvokeVoidOrTaskAsync(SubtitleEntryRepo, method, args).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }

        if (ConnectionFactory is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (ConnectionFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        if (_ownsConnection)
        {
            Connection.Dispose();
        }

        if (ConnectionFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed class SequentialWorkerHandle
{
    public SequentialWorkerHandle(object instance, Type workerType)
    {
        Instance = instance;
        WorkerType = workerType;
    }

    public object Instance { get; }
    public Type WorkerType { get; }

    public Task<object?> InvokeAsync(string methodKeyword, params object?[] args)
    {
        MethodInfo? method = WorkerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.Name.Contains(methodKeyword, StringComparison.OrdinalIgnoreCase) &&
                m.GetParameters().Length == args.Length);

        if (method is null)
        {
            throw new InvalidOperationException(
                $"No method matching '{methodKeyword}' with {args.Length} argument(s) on worker '{WorkerType.FullName}'.");
        }

        return OpaqueContractBridge.InvokeAnyAsync(Instance, method, args);
    }
}

public sealed class PipelineOrchestratorHandle
{
    public PipelineOrchestratorHandle(object instance, Type orchestratorType)
    {
        Instance = instance;
        OrchestratorType = orchestratorType;
    }

    public object Instance { get; }
    public Type OrchestratorType { get; }

    public Task<object?> InvokeAsync(string methodKeyword, params object?[] args)
    {
        MethodInfo? method = OrchestratorType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.Name.Contains(methodKeyword, StringComparison.OrdinalIgnoreCase) &&
                m.GetParameters().Length == args.Length);

        if (method is null)
        {
            throw new InvalidOperationException(
                $"No method matching '{methodKeyword}' with {args.Length} argument(s) on orchestrator '{OrchestratorType.FullName}'.");
        }

        return OpaqueContractBridge.InvokeAnyAsync(Instance, method, args);
    }
}

public sealed class ViewModelHandle
{
    public ViewModelHandle(object instance, Type viewModelType)
    {
        Instance = instance;
        ViewModelType = viewModelType;
    }

    public object Instance { get; }
    public Type ViewModelType { get; }

    public T? GetProperty<T>(string propertyName)
    {
        PropertyInfo prop = GetPropertyInfo(propertyName);
        return (T?)prop.GetValue(Instance);
    }

    public void SetProperty(string propertyName, object? value)
    {
        PropertyInfo prop = GetPropertyInfo(propertyName);
        prop.SetValue(Instance, value);
    }

    public ICommand GetCommand(string commandNameKeyword)
    {
        PropertyInfo? prop = ViewModelType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p =>
                typeof(ICommand).IsAssignableFrom(p.PropertyType) &&
                p.Name.Contains(commandNameKeyword, StringComparison.OrdinalIgnoreCase));

        if (prop?.GetValue(Instance) is ICommand cmd)
        {
            return cmd;
        }

        throw new InvalidOperationException(
            $"No ICommand matching '{commandNameKeyword}' found on ViewModel '{ViewModelType.FullName}'.");
    }

    public static Task RunOnStaAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private PropertyInfo GetPropertyInfo(string propertyName) =>
        ViewModelType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
        ?? throw new InvalidOperationException(
            $"Property '{propertyName}' not found on ViewModel '{ViewModelType.FullName}'.");
}
