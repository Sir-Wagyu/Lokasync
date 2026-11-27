using System.Collections;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

internal sealed record ProjectSnapshot(
    string Id,
    string GameTitle,
    string GameRootPath,
    string DetectedEngine,
    string? NarrativeContextJson,
    string? CreatedAt);

internal sealed record BackupManifestSnapshot(
    long Id,
    string ProjectId,
    string OriginalRelativePath,
    string BackupFilePath,
    string Sha256Hash,
    string? BackedUpAt);

internal sealed record AutoPauseNotification(
    string RawMessage,
    int? StatusCode,
    string? Reason);

internal static class CoreTranslatorTestHelpers
{
    public static Assembly CoreAssembly => typeof(SubtitleEntry).Assembly;

    public static Assembly TranslatorAssembly => typeof(OpaqueContractBridge).Assembly
        .GetReferencedAssemblies()
        .Select(Assembly.Load)
        .FirstOrDefault(a => string.Equals(a.GetName().Name, "Lokasync.Translator", StringComparison.OrdinalIgnoreCase))
        ?? AppDomain.CurrentDomain.GetAssemblies()
            .First(a => string.Equals(a.GetName().Name, "Lokasync.Translator", StringComparison.OrdinalIgnoreCase));

    // =========================================================================
    // F1: SQLite Connection Factory & Default %LocalAppData% Path Helpers
    // =========================================================================

    public static string GetExpectedLocalAppDataDbPath(string projectHash)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(localAppData, "Lokasync", "Projects", $"{projectHash}.db");
    }

    public static async Task<(string ResolvedDbPath, SqliteConnection Connection, object? Factory, IDisposable? Lease)>
        OpenDefaultProjectDatabaseAsync(string projectHash, CancellationToken ct = default)
    {
        Type factoryType = FindConnectionFactoryType();

        // 1. Check static factory methods that accept a projectHash/projectId string (e.g., ForProject / CreateForProject)
        MethodInfo[] staticFactories = factoryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && factoryType.IsAssignableFrom(m.ReturnType))
            .OrderByDescending(m => m.Name.Contains("Project", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string) &&
                ((p.Name ?? "").Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                 (p.Name ?? "").Contains("project", StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        object? factory = null;
        foreach (MethodInfo sf in staticFactories)
        {
            if (sf.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ParameterInfo[] ps = sf.GetParameters();
            if (ps.Length >= 1 && ps[0].ParameterType == typeof(string) && ps.Skip(1).All(p => p.HasDefaultValue))
            {
                string paramName = ps[0].Name ?? string.Empty;
                if (!sf.Name.Contains("Project", StringComparison.OrdinalIgnoreCase) &&
                    !paramName.Contains("hash", StringComparison.OrdinalIgnoreCase) &&
                    !paramName.Contains("project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                object?[] args = new object?[ps.Length];
                args[0] = projectHash;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].DefaultValue;
                }

                factory = InvokeSync(null, sf, args);
                break;
            }
        }

        // 2. Fallback: construct factory with projectHash or parameterless, or compute path via static path helper
        string? resolvedPath = TryGetDefaultPathFromFactoryType(factoryType, projectHash);
        if (factory is null)
        {
            ConstructorInfo? ctor = factoryType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] ps = c.GetParameters();
                    return ps.Length == 0 ||
                           (ps.Length >= 1 && ps[0].ParameterType == typeof(string) && ps.Skip(1).All(p => p.HasDefaultValue));
                });

            if (ctor is not null)
            {
                ParameterInfo[] ps = ctor.GetParameters();
                if (ps.Length == 0)
                {
                    factory = ctor.Invoke([]);
                }
                else
                {
                    object?[] args = new object?[ps.Length];
                    string firstName = ps[0].Name ?? string.Empty;
                    args[0] = (firstName.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                               firstName.Contains("project", StringComparison.OrdinalIgnoreCase))
                        ? projectHash
                        : resolvedPath ?? GetExpectedLocalAppDataDbPath(projectHash);
                    for (int i = 1; i < ps.Length; i++)
                    {
                        args[i] = ps[i].DefaultValue;
                    }

                    factory = ctor.Invoke(args);
                }
            }
        }

        if (factory is null)
        {
            throw new InvalidOperationException($"Could not instantiate connection factory '{factoryType.FullName}' for project hash.");
        }

        await InvokeSchemaInitIfSeparatedAsync(factory, factoryType, null, ct).ConfigureAwait(false);

        // Try opening via instance method accepting projectHash or no args
        (SqliteConnection? conn, IDisposable? lease) = await OpenConnectionFromFactoryAsync(factory, projectHash, ct).ConfigureAwait(false);
        if (conn is null)
        {
            throw new InvalidOperationException($"Factory '{factoryType.FullName}' did not return an open SqliteConnection.");
        }

        await InvokeSchemaInitIfSeparatedAsync(factory, factoryType, conn, ct).ConfigureAwait(false);
        string finalPath = !string.IsNullOrWhiteSpace(conn.DataSource)
            ? conn.DataSource
            : resolvedPath ?? GetExpectedLocalAppDataDbPath(projectHash);

        return (finalPath, conn, factory, lease);
    }

    public static async Task<(SqliteConnection Connection, IDisposable? Lease)> OpenViaFactoryPathAsync(
        string dbPathOrConnectionString,
        CancellationToken ct = default)
    {
        Type factoryType = FindConnectionFactoryType();
        object? factory = CreateFactoryForPath(factoryType, dbPathOrConnectionString);
        if (factory is null)
        {
            throw new InvalidOperationException($"Could not construct connection factory '{factoryType.FullName}' for '{dbPathOrConnectionString}'.");
        }

        await InvokeSchemaInitIfSeparatedAsync(factory, factoryType, null, ct).ConfigureAwait(false);
        (SqliteConnection? conn, IDisposable? lease) = await OpenConnectionFromFactoryAsync(factory, dbPathOrConnectionString, ct).ConfigureAwait(false);
        if (conn is null)
        {
            throw new InvalidOperationException($"Connection factory '{factoryType.FullName}' returned null connection.");
        }

        await InvokeSchemaInitIfSeparatedAsync(factory, factoryType, conn, ct).ConfigureAwait(false);
        return (conn, lease);
    }

    public static async Task InvokeFactorySchemaInitAsync(DatabaseSession db, CancellationToken ct = default)
    {
        if (db.ConnectionFactory is not null)
        {
            await InvokeSchemaInitIfSeparatedAsync(db.ConnectionFactory, db.ConnectionFactory.GetType(), db.Connection, ct).ConfigureAwait(false);
            return;
        }

        foreach (Type t in GetCoreLoadableTypes().Where(t =>
                     !t.IsInterface &&
                     (t.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase) ||
                      t.Name.Contains("Init", StringComparison.OrdinalIgnoreCase))))
        {
            await InvokeSchemaInitIfSeparatedAsync(null, t, db.Connection, ct).ConfigureAwait(false);
        }
    }

    private static Type FindConnectionFactoryType()
    {
        List<Type> coreTypes = GetCoreLoadableTypes().Where(t => !t.IsAbstract && !t.IsInterface).ToList();
        return coreTypes.FirstOrDefault(t =>
                   t.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase) &&
                   (t.Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Db", StringComparison.OrdinalIgnoreCase)))
               ?? coreTypes.FirstOrDefault(t => t.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("No SQLite connection factory type found in Lokasync.Core.");
    }

    private static string? TryGetDefaultPathFromFactoryType(Type factoryType, string projectHash)
    {
        MethodInfo? pathMethod = factoryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.ReturnType == typeof(string) &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(string));

        return pathMethod is not null ? InvokeSync(null, pathMethod, [projectHash]) as string : null;
    }

    private static object? CreateFactoryForPath(Type factoryType, string dbPathOrConnectionString)
    {
        bool isMemory = dbPathOrConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase) ||
                        dbPathOrConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

        string dataSource = dbPathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? new SqliteConnectionStringBuilder(dbPathOrConnectionString).DataSource
            : dbPathOrConnectionString;

        MethodInfo[] staticFactories = factoryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && factoryType.IsAssignableFrom(m.ReturnType))
            .OrderByDescending(m => isInMemoryMatch(m, isMemory))
            .ThenByDescending(m => !isMemory && (m.Name.Contains("File", StringComparison.OrdinalIgnoreCase) ||
                                                 m.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        static bool isInMemoryMatch(MethodInfo m, bool mem) =>
            mem && m.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase);

        foreach (MethodInfo sf in staticFactories)
        {
            if (!isMemory && sf.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sf.Name.Contains("Project", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ParameterInfo[] ps = sf.GetParameters();
            object?[] args = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(string))
                {
                    bool wantsConn = (ps[i].Name ?? "").Contains("conn", StringComparison.OrdinalIgnoreCase);
                    args[i] = wantsConn && !dbPathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                        ? $"Data Source={dbPathOrConnectionString}"
                        : (wantsConn ? dbPathOrConnectionString : dataSource);
                }
                else if (ps[i].HasDefaultValue)
                {
                    args[i] = ps[i].DefaultValue;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return InvokeSync(null, sf, args);
            }
        }

        foreach (ConstructorInfo ctor in factoryType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            object?[] args = new object?[ps.Length];
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(string))
                {
                    bool wantsConn = (ps[i].Name ?? "").Contains("conn", StringComparison.OrdinalIgnoreCase);
                    args[i] = wantsConn && !dbPathOrConnectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
                        ? $"Data Source={dbPathOrConnectionString}"
                        : (wantsConn ? dbPathOrConnectionString : dataSource);
                }
                else if (ps[i].HasDefaultValue)
                {
                    args[i] = ps[i].DefaultValue;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return ctor.Invoke(args);
            }
        }

        return null;
    }

    private static async Task<(SqliteConnection? Connection, IDisposable? Lease)> OpenConnectionFromFactoryAsync(
        object factory,
        string pathOrHash,
        CancellationToken ct)
    {
        MethodInfo[] methods = factory.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && IsConnectionOrLeaseType(UnwrapReturnType(m.ReturnType)))
            .OrderByDescending(m => UnwrapReturnType(m.ReturnType) == typeof(SqliteConnection))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] ps = method.GetParameters();
            object?[]? args = ps.Length switch
            {
                0 => [],
                1 when ps[0].ParameterType == typeof(CancellationToken) => [ct],
                1 when ps[0].ParameterType == typeof(string) => [pathOrHash],
                2 when ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(CancellationToken) => [pathOrHash, ct],
                _ => null
            };

            if (args is null)
            {
                continue;
            }

            object? res = await InvokeAnyAsync(factory, method, args).ConfigureAwait(false);
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
                PropertyInfo? connProp = res.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(p => p.PropertyType == typeof(SqliteConnection));
                if (connProp?.GetValue(res) is SqliteConnection leasedConn)
                {
                    if (leasedConn.State != ConnectionState.Open)
                    {
                        await leasedConn.OpenAsync(ct).ConfigureAwait(false);
                    }

                    return (leasedConn, res as IDisposable);
                }
            }
        }

        return (null, null);
    }

    private static async Task InvokeSchemaInitIfSeparatedAsync(
        object? target,
        Type targetType,
        SqliteConnection? conn,
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
                        UnwrapReturnType(m.ReturnType) != typeof(SqliteConnection) &&
                        (m.Name.Contains("Init", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Schema", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Ensure", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (target is null && !method.IsStatic)
            {
                continue;
            }

            ParameterInfo[] ps = method.GetParameters();
            object?[]? args = ps.Length switch
            {
                0 => [],
                1 when conn is not null && ps[0].ParameterType == typeof(SqliteConnection) => [conn],
                1 when ps[0].ParameterType == typeof(CancellationToken) => [ct],
                2 when conn is not null && ps[0].ParameterType == typeof(SqliteConnection) && ps[1].ParameterType == typeof(CancellationToken) => [conn, ct],
                2 when conn is not null && ps[0].ParameterType == typeof(CancellationToken) && ps[1].ParameterType == typeof(SqliteConnection) => [ct, conn],
                _ => null
            };

            if (args is not null)
            {
                await InvokeAnyAsync(method.IsStatic ? null : target, method, args).ConfigureAwait(false);
                return;
            }
        }
    }

    // =========================================================================
    // F2: projects Repository CRUD Helpers
    // =========================================================================

    public static async Task CreateProjectAsync(
        DatabaseSession db,
        string id,
        string gameTitle,
        string gameRootPath,
        string detectedEngine,
        string? narrativeContextJson,
        CancellationToken ct = default)
    {
        object repo = db.ProjectRepo;
        MethodInfo[] methods = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        (m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Insert", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (TryBuildProjectMethodArgs(method, id, gameTitle, gameRootPath, detectedEngine, narrativeContextJson, ct, out object?[] args))
            {
                await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidOperationException($"No compatible Create/Insert/Add method found on '{repo.GetType().FullName}'.");
    }

    public static async Task<ProjectSnapshot?> GetProjectByIdAsync(
        DatabaseSession db,
        string id,
        CancellationToken ct = default)
    {
        object repo = db.ProjectRepo;
        MethodInfo? method = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (m.IsSpecialName || m.DeclaringType == typeof(object))
                {
                    return false;
                }

                Type unwrapped = UnwrapReturnType(m.ReturnType);
                if (typeof(IEnumerable).IsAssignableFrom(unwrapped) && unwrapped != typeof(string))
                {
                    return false;
                }

                if (!(m.Name.Contains("Get", StringComparison.OrdinalIgnoreCase) ||
                      m.Name.Contains("Find", StringComparison.OrdinalIgnoreCase) ||
                      m.Name.Contains("Fetch", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                return (ps.Length == 1 && ps[0].ParameterType == typeof(string)) ||
                       (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(CancellationToken));
            });

        if (method is null)
        {
            throw new InvalidOperationException($"No compatible GetById method found on '{repo.GetType().FullName}'.");
        }

        object?[] args = method.GetParameters().Length == 2 ? [id, ct] : [id];
        object? result = await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);
        return result is null ? null : MapProjectSnapshot(result);
    }

    public static async Task UpdateProjectNarrativeContextAsync(
        DatabaseSession db,
        string id,
        string? narrativeContextJson,
        CancellationToken ct = default)
    {
        object repo = db.ProjectRepo;
        MethodInfo? specific = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                m.Name.Contains("Narrative", StringComparison.OrdinalIgnoreCase));

        if (specific is not null)
        {
            ParameterInfo[] ps = specific.GetParameters();
            object?[] args = ps.Length == 3 && ps[2].ParameterType == typeof(CancellationToken)
                ? [id, narrativeContextJson, ct]
                : [id, narrativeContextJson];
            await InvokeAnyAsync(repo, specific, args).ConfigureAwait(false);
            return;
        }

        ProjectSnapshot existing = await GetProjectByIdAsync(db, id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{id}' not found before update.");

        await UpdateFullProjectAsync(db, existing with { NarrativeContextJson = narrativeContextJson }, ct).ConfigureAwait(false);
    }

    public static async Task UpdateProjectDetectedEngineAsync(
        DatabaseSession db,
        string id,
        string detectedEngine,
        CancellationToken ct = default)
    {
        object repo = db.ProjectRepo;
        MethodInfo? specific = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                m.Name.Contains("Engine", StringComparison.OrdinalIgnoreCase));

        if (specific is not null)
        {
            ParameterInfo[] ps = specific.GetParameters();
            object?[] args = ps.Length == 3 && ps[2].ParameterType == typeof(CancellationToken)
                ? [id, detectedEngine, ct]
                : [id, detectedEngine];
            await InvokeAnyAsync(repo, specific, args).ConfigureAwait(false);
            return;
        }

        ProjectSnapshot existing = await GetProjectByIdAsync(db, id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{id}' not found before update.");

        await UpdateFullProjectAsync(db, existing with { DetectedEngine = detectedEngine }, ct).ConfigureAwait(false);
    }

    private static async Task UpdateFullProjectAsync(
        DatabaseSession db,
        ProjectSnapshot updated,
        CancellationToken ct)
    {
        object repo = db.ProjectRepo;
        MethodInfo[] methods = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (TryBuildProjectMethodArgs(
                    method,
                    updated.Id,
                    updated.GameTitle,
                    updated.GameRootPath,
                    updated.DetectedEngine,
                    updated.NarrativeContextJson,
                    ct,
                    out object?[] args))
            {
                await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidOperationException($"No compatible Update method found on '{repo.GetType().FullName}'.");
    }

    public static async Task<IReadOnlyList<ProjectSnapshot>> ListProjectsAsync(
        DatabaseSession db,
        CancellationToken ct = default)
    {
        object repo = db.ProjectRepo;
        MethodInfo? method = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (m.IsSpecialName || m.DeclaringType == typeof(object))
                {
                    return false;
                }

                Type unwrapped = UnwrapReturnType(m.ReturnType);
                if (!typeof(IEnumerable).IsAssignableFrom(unwrapped) || unwrapped == typeof(string))
                {
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                return ps.Length == 0 || (ps.Length == 1 && ps[0].ParameterType == typeof(CancellationToken));
            });

        if (method is null)
        {
            throw new InvalidOperationException($"No compatible List/GetAll method found on '{repo.GetType().FullName}'.");
        }

        object?[] args = method.GetParameters().Length == 1 ? [ct] : [];
        object? result = await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);

        List<ProjectSnapshot> list = [];
        if (result is IEnumerable items)
        {
            foreach (object? item in items)
            {
                if (item is not null)
                {
                    list.Add(MapProjectSnapshot(item));
                }
            }
        }

        return list;
    }

    private static bool TryBuildProjectMethodArgs(
        MethodInfo method,
        string id,
        string gameTitle,
        string gameRootPath,
        string detectedEngine,
        string? narrativeContextJson,
        CancellationToken ct,
        out object?[] args)
    {
        ParameterInfo[] ps = method.GetParameters();
        args = new object?[ps.Length];

        for (int i = 0; i < ps.Length; i++)
        {
            ParameterInfo p = ps[i];
            if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
                continue;
            }

            if (p.ParameterType == typeof(string))
            {
                string name = p.Name ?? string.Empty;
                if (name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.Contains("projectId", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = id;
                }
                else if (name.Contains("title", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = gameTitle;
                }
                else if (name.Contains("root", StringComparison.OrdinalIgnoreCase) || name.Contains("path", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = gameRootPath;
                }
                else if (name.Contains("engine", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = detectedEngine;
                }
                else if (name.Contains("narrative", StringComparison.OrdinalIgnoreCase) || name.Contains("context", StringComparison.OrdinalIgnoreCase) || name.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = narrativeContextJson;
                }
                else
                {
                    return false;
                }

                continue;
            }

            if (!p.ParameterType.IsPrimitive && p.ParameterType != typeof(DateTime) && p.ParameterType != typeof(DateTimeOffset))
            {
                if (TryBuildProjectDto(p.ParameterType, id, gameTitle, gameRootPath, detectedEngine, narrativeContextJson, out object? dto))
                {
                    args[i] = dto;
                    continue;
                }
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

    private static bool TryBuildProjectDto(
        Type dtoType,
        string id,
        string gameTitle,
        string gameRootPath,
        string detectedEngine,
        string? narrativeContextJson,
        out object? dto)
    {
        dto = null;
        foreach (ConstructorInfo ctor in dtoType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            object?[] ctorArgs = new object?[ps.Length];
            bool ok = true;

            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                string name = p.Name ?? string.Empty;
                if (p.ParameterType == typeof(string))
                {
                    if (name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.Contains("projectId", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = id;
                    }
                    else if (name.Contains("title", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = gameTitle;
                    }
                    else if (name.Contains("root", StringComparison.OrdinalIgnoreCase) || name.Contains("path", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = gameRootPath;
                    }
                    else if (name.Contains("engine", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = detectedEngine;
                    }
                    else if (name.Contains("narrative", StringComparison.OrdinalIgnoreCase) || name.Contains("context", StringComparison.OrdinalIgnoreCase) || name.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = narrativeContextJson;
                    }
                    else if (name.Contains("created", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = DateTime.UtcNow.ToString("O");
                    }
                    else if (p.HasDefaultValue)
                    {
                        ctorArgs[i] = p.DefaultValue;
                    }
                    else
                    {
                        ctorArgs[i] = null;
                    }
                }
                else if (p.HasDefaultValue)
                {
                    ctorArgs[i] = p.DefaultValue;
                }
                else if (p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?))
                {
                    ctorArgs[i] = DateTime.UtcNow;
                }
                else if (p.ParameterType == typeof(DateTimeOffset) || p.ParameterType == typeof(DateTimeOffset?))
                {
                    ctorArgs[i] = DateTimeOffset.UtcNow;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                object instance = ctor.Invoke(ctorArgs);
                if (ps.Length == 0)
                {
                    SetPropIfExists(instance, "Id", id);
                    SetPropIfExists(instance, "GameTitle", gameTitle);
                    SetPropIfExists(instance, "GameRootPath", gameRootPath);
                    SetPropIfExists(instance, "DetectedEngine", detectedEngine);
                    SetPropIfExists(instance, "NarrativeContextJson", narrativeContextJson);
                }

                dto = instance;
                return true;
            }
        }

        return false;
    }

    private static ProjectSnapshot MapProjectSnapshot(object obj)
    {
        string id = GetPropString(obj, "Id", "ProjectId") ?? string.Empty;
        string title = GetPropString(obj, "GameTitle", "Title") ?? string.Empty;
        string root = GetPropString(obj, "GameRootPath", "RootPath") ?? string.Empty;
        string engine = GetPropString(obj, "DetectedEngine", "Engine") ?? string.Empty;
        string? narrative = GetPropString(obj, "NarrativeContextJson", "NarrativeJson");
        object? createdRaw = GetPropObj(obj, "CreatedAt", "CreatedUtc");
        string? created = createdRaw switch
        {
            string s => s,
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => createdRaw?.ToString()
        };

        return new ProjectSnapshot(id, title, root, engine, narrative, created);
    }

    // =========================================================================
    // F3: subtitle_entries Repository Helpers
    // =========================================================================

    public static async Task UpdateSubtitleEntriesBatchAsync(
        DatabaseSession db,
        IReadOnlyList<SubtitleEntry> entries,
        CancellationToken ct = default)
    {
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

            await InvokeAnyAsync(repo, batchMethod, args).ConfigureAwait(false);
            return;
        }

        MethodInfo? singleMethod = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                !m.IsSpecialName &&
                m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                m.GetParameters().Any(p => p.ParameterType == typeof(SubtitleEntry)));

        if (singleMethod is not null)
        {
            foreach (SubtitleEntry entry in entries)
            {
                ParameterInfo[] ps = singleMethod.GetParameters();
                object?[] args = ps.Length == 2 && ps[1].ParameterType == typeof(CancellationToken)
                    ? [entry, ct]
                    : [entry];
                await InvokeAnyAsync(repo, singleMethod, args).ConfigureAwait(false);
            }

            return;
        }

        throw new InvalidOperationException($"No Update method found on '{repo.GetType().FullName}'.");
    }

    public static async Task<IReadOnlyList<SubtitleEntry>> QuerySubtitleEntriesAsync(
        DatabaseSession db,
        EntryStatus? statusFilter,
        string? searchText,
        CancellationToken ct = default)
    {
        object repo = db.SubtitleEntryRepo;
        MethodInfo[] methods = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        typeof(IEnumerable<SubtitleEntry>).IsAssignableFrom(UnwrapReturnType(m.ReturnType)))
            .ToArray();

        // 1. Try combined (EntryStatus?, string?, CancellationToken?) method
        foreach (MethodInfo m in methods)
        {
            ParameterInfo[] ps = m.GetParameters();
            bool hasStatus = ps.Any(p => p.ParameterType == typeof(EntryStatus?) || p.ParameterType == typeof(EntryStatus));
            bool hasSearch = ps.Any(p => p.ParameterType == typeof(string));
            if (hasStatus && hasSearch && ps.All(p =>
                    p.ParameterType == typeof(EntryStatus?) ||
                    p.ParameterType == typeof(EntryStatus) ||
                    p.ParameterType == typeof(string) ||
                    p.ParameterType == typeof(CancellationToken) ||
                    p.HasDefaultValue))
            {
                if (!statusFilter.HasValue && ps.Any(p => p.ParameterType == typeof(EntryStatus)))
                {
                    continue;
                }

                object?[] args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(EntryStatus?))
                    {
                        args[i] = statusFilter;
                    }
                    else if (ps[i].ParameterType == typeof(EntryStatus))
                    {
                        args[i] = statusFilter!.Value;
                    }
                    else if (ps[i].ParameterType == typeof(string))
                    {
                        args[i] = searchText;
                    }
                    else if (ps[i].ParameterType == typeof(CancellationToken))
                    {
                        args[i] = ct;
                    }
                    else
                    {
                        args[i] = ps[i].DefaultValue;
                    }
                }

                object? res = await InvokeAnyAsync(repo, m, args).ConfigureAwait(false);
                if (res is IEnumerable<SubtitleEntry> list)
                {
                    return list.ToList();
                }
            }
        }

        // 2. Try single-filter methods (by status OR by search text)
        if (statusFilter.HasValue && string.IsNullOrEmpty(searchText))
        {
            foreach (MethodInfo m in methods)
            {
                ParameterInfo[] ps = m.GetParameters();
                if ((ps.Length == 1 && (ps[0].ParameterType == typeof(EntryStatus) || ps[0].ParameterType == typeof(EntryStatus?))) ||
                    (ps.Length == 2 && (ps[0].ParameterType == typeof(EntryStatus) || ps[0].ParameterType == typeof(EntryStatus?)) && ps[1].ParameterType == typeof(CancellationToken)))
                {
                    object?[] args = ps.Length == 2 ? [statusFilter.Value, ct] : [statusFilter.Value];
                    object? res = await InvokeAnyAsync(repo, m, args).ConfigureAwait(false);
                    if (res is IEnumerable<SubtitleEntry> list)
                    {
                        return list.ToList();
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(searchText) && !statusFilter.HasValue)
        {
            foreach (MethodInfo m in methods)
            {
                ParameterInfo[] ps = m.GetParameters();
                if ((ps.Length == 1 && ps[0].ParameterType == typeof(string)) ||
                    (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(CancellationToken)))
                {
                    object?[] args = ps.Length == 2 ? [searchText, ct] : [searchText];
                    object? res = await InvokeAnyAsync(repo, m, args).ConfigureAwait(false);
                    if (res is IEnumerable<SubtitleEntry> list)
                    {
                        return list.ToList();
                    }
                }
            }
        }

        // 3. Fallback to GetAllEntriesAsync + in-memory filter
        IReadOnlyList<SubtitleEntry> all = await db.GetAllEntriesAsync(ct).ConfigureAwait(false);
        return all.Where(e =>
                (!statusFilter.HasValue || e.Status == statusFilter.Value) &&
                (string.IsNullOrEmpty(searchText) ||
                 e.RawText.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                 (e.TranslatedText?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 e.EntryKey.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // =========================================================================
    // F4: backup_manifests Repository CRUD Helpers
    // =========================================================================

    public static async Task AddBackupManifestAsync(
        DatabaseSession db,
        string projectId,
        string originalRelativePath,
        string backupFilePath,
        string sha256Hash,
        CancellationToken ct = default)
    {
        object repo = db.BackupManifestRepo;
        MethodInfo[] methods = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.DeclaringType != typeof(object) &&
                        (m.Name.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Insert", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Record", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("Register", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (MethodInfo method in methods)
        {
            if (TryBuildManifestArgs(method, projectId, originalRelativePath, backupFilePath, sha256Hash, ct, out object?[] args))
            {
                await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);
                return;
            }
        }

        string available = string.Join("; ", repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => m.ToString()));
        throw new InvalidOperationException($"No compatible Add/Insert method found on '{repo.GetType().FullName}'. Available: [{available}].");
    }

    public static async Task<IReadOnlyList<BackupManifestSnapshot>> GetBackupManifestsByProjectIdAsync(
        DatabaseSession db,
        string projectId,
        CancellationToken ct = default)
    {
        object repo = db.BackupManifestRepo;
        MethodInfo? method = repo.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                if (m.IsSpecialName || m.DeclaringType == typeof(object))
                {
                    return false;
                }

                Type unwrapped = UnwrapReturnType(m.ReturnType);
                if (!typeof(IEnumerable).IsAssignableFrom(unwrapped) || unwrapped == typeof(string))
                {
                    return false;
                }

                ParameterInfo[] ps = m.GetParameters();
                return (ps.Length == 1 && ps[0].ParameterType == typeof(string)) ||
                       (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(CancellationToken));
            });

        if (method is null)
        {
            throw new InvalidOperationException($"No compatible GetByProjectId method found on '{repo.GetType().FullName}'.");
        }

        object?[] args = method.GetParameters().Length == 2 ? [projectId, ct] : [projectId];
        object? result = await InvokeAnyAsync(repo, method, args).ConfigureAwait(false);

        List<BackupManifestSnapshot> list = [];
        if (result is IEnumerable items)
        {
            foreach (object? item in items)
            {
                if (item is not null)
                {
                    list.Add(MapBackupManifestSnapshot(item));
                }
            }
        }

        return list;
    }

    private static bool TryBuildManifestArgs(
        MethodInfo method,
        string projectId,
        string originalRelativePath,
        string backupFilePath,
        string sha256Hash,
        CancellationToken ct,
        out object?[] args)
    {
        ParameterInfo[] ps = method.GetParameters();
        args = new object?[ps.Length];

        // Support 4-string positional methods (projectId, originalRelativePath, backupFilePath, sha256Hash) + optional CancellationToken
        List<int> stringIndices = [];
        for (int i = 0; i < ps.Length; i++)
        {
            if (ps[i].ParameterType == typeof(string))
            {
                stringIndices.Add(i);
            }
        }

        for (int i = 0; i < ps.Length; i++)
        {
            ParameterInfo p = ps[i];
            if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
                continue;
            }

            if (p.ParameterType == typeof(string))
            {
                string name = p.Name ?? string.Empty;
                if (name.Contains("project", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = projectId;
                }
                else if (name.Contains("orig", StringComparison.OrdinalIgnoreCase) || name.Contains("rel", StringComparison.OrdinalIgnoreCase) || name.Contains("source", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = originalRelativePath;
                }
                else if (name.Contains("backup", StringComparison.OrdinalIgnoreCase) || name.Contains("dest", StringComparison.OrdinalIgnoreCase) || name.Contains("target", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = backupFilePath;
                }
                else if (name.Contains("sha", StringComparison.OrdinalIgnoreCase) || name.Contains("hash", StringComparison.OrdinalIgnoreCase) || name.Contains("digest", StringComparison.OrdinalIgnoreCase))
                {
                    args[i] = sha256Hash;
                }
                else if (stringIndices.Count == 4)
                {
                    int pos = stringIndices.IndexOf(i);
                    args[i] = pos switch
                    {
                        0 => projectId,
                        1 => originalRelativePath,
                        2 => backupFilePath,
                        3 => sha256Hash,
                        _ => null
                    };
                }
                else
                {
                    return false;
                }

                continue;
            }

            if (!p.ParameterType.IsPrimitive && p.ParameterType != typeof(DateTime) && p.ParameterType != typeof(DateTimeOffset))
            {
                if (TryBuildManifestDto(p.ParameterType, projectId, originalRelativePath, backupFilePath, sha256Hash, out object? dto))
                {
                    args[i] = dto;
                    continue;
                }
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

    private static bool TryBuildManifestDto(
        Type dtoType,
        string projectId,
        string originalRelativePath,
        string backupFilePath,
        string sha256Hash,
        out object? dto)
    {
        dto = null;
        foreach (ConstructorInfo ctor in dtoType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            ParameterInfo[] ps = ctor.GetParameters();
            object?[] ctorArgs = new object?[ps.Length];
            bool ok = true;

            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                string name = p.Name ?? string.Empty;
                if (p.ParameterType == typeof(long) || p.ParameterType == typeof(int))
                {
                    ctorArgs[i] = p.ParameterType == typeof(long) ? 0L : 0;
                }
                else if (p.ParameterType == typeof(string))
                {
                    if (name.Contains("project", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = projectId;
                    }
                    else if (name.Contains("orig", StringComparison.OrdinalIgnoreCase) || name.Contains("rel", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = originalRelativePath;
                    }
                    else if (name.Contains("backup", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = backupFilePath;
                    }
                    else if (name.Contains("sha", StringComparison.OrdinalIgnoreCase) || name.Contains("hash", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = sha256Hash;
                    }
                    else if (name.Contains("backed", StringComparison.OrdinalIgnoreCase) || name.Contains("at", StringComparison.OrdinalIgnoreCase))
                    {
                        ctorArgs[i] = DateTime.UtcNow.ToString("O");
                    }
                    else
                    {
                        ctorArgs[i] = p.HasDefaultValue ? p.DefaultValue : null;
                    }
                }
                else if (p.ParameterType == typeof(DateTime) || p.ParameterType == typeof(DateTime?))
                {
                    ctorArgs[i] = DateTime.UtcNow;
                }
                else if (p.ParameterType == typeof(DateTimeOffset) || p.ParameterType == typeof(DateTimeOffset?))
                {
                    ctorArgs[i] = DateTimeOffset.UtcNow;
                }
                else if (p.HasDefaultValue)
                {
                    ctorArgs[i] = p.DefaultValue;
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                object instance = ctor.Invoke(ctorArgs);
                if (ps.Length == 0)
                {
                    SetPropIfExists(instance, "ProjectId", projectId);
                    SetPropIfExists(instance, "OriginalRelativePath", originalRelativePath);
                    SetPropIfExists(instance, "BackupFilePath", backupFilePath);
                    SetPropIfExists(instance, "Sha256Hash", sha256Hash);
                }

                dto = instance;
                return true;
            }
        }

        return false;
    }

    private static BackupManifestSnapshot MapBackupManifestSnapshot(object obj)
    {
        long id = GetPropObj(obj, "Id") is IConvertible conv ? Convert.ToInt64(conv) : 0L;
        string projectId = GetPropString(obj, "ProjectId") ?? string.Empty;
        string orig = GetPropString(obj, "OriginalRelativePath", "RelativePath") ?? string.Empty;
        string backup = GetPropString(obj, "BackupFilePath", "BackupPath") ?? string.Empty;
        string sha = GetPropString(obj, "Sha256Hash", "Hash") ?? string.Empty;
        object? backedUpRaw = GetPropObj(obj, "BackedUpAt", "CreatedAt");
        string? backedUp = backedUpRaw switch
        {
            string s => s,
            DateTime dt => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            _ => backedUpRaw?.ToString()
        };

        return new BackupManifestSnapshot(id, projectId, orig, backup, sha, backedUp);
    }

    // =========================================================================
    // F7: Prompt Builder Reflection Helpers
    // =========================================================================

    public static string BuildSystemPromptFromNarrativeJson(OpaqueContractBridge bridge, string? narrativeContextJson)
    {
        Type? builderType = TranslatorAssembly.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && t.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase));

        if (builderType is not null)
        {
            object builder = Activator.CreateInstance(builderType)!;
            MethodInfo? jsonMethod = builderType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m =>
                    !m.IsSpecialName &&
                    UnwrapReturnType(m.ReturnType) == typeof(string) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string));

            if (jsonMethod is not null)
            {
                object? res = InvokeSync(jsonMethod.IsStatic ? null : builder, jsonMethod, [narrativeContextJson]);
                if (res is string s)
                {
                    return s;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(narrativeContextJson))
        {
            return bridge.BuildSystemPrompt(string.Empty, string.Empty, []);
        }

        using JsonDocument doc = JsonDocument.Parse(narrativeContextJson);
        JsonElement root = doc.RootElement;
        string title = root.TryGetProperty("game_title", out JsonElement tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
        string tone = root.TryGetProperty("genre_tone", out JsonElement gProp) ? gProp.GetString() ?? string.Empty : string.Empty;
        List<(string, string, string)> glossary = [];
        if (root.TryGetProperty("glossary", out JsonElement glProp) && glProp.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in glProp.EnumerateArray())
            {
                string src = item.TryGetProperty("source", out JsonElement sp) ? sp.GetString() ?? string.Empty : string.Empty;
                string tgt = item.TryGetProperty("target", out JsonElement tp) ? tp.GetString() ?? string.Empty : string.Empty;
                string note = item.TryGetProperty("note", out JsonElement np) ? np.GetString() ?? string.Empty : string.Empty;
                glossary.Add((src, tgt, note));
            }
        }

        return bridge.BuildSystemPrompt(title, tone, glossary);
    }

    public static bool TryBuildUserPromptDirect(Dictionary<string, string> maskedBatch, out string? userPrompt)
    {
        userPrompt = null;
        Type? builderType = TranslatorAssembly.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && t.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase));
        if (builderType is null)
        {
            return false;
        }

        object builder = Activator.CreateInstance(builderType)!;
        MethodInfo? userMethod = builderType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && UnwrapReturnType(m.ReturnType) == typeof(string))
            .FirstOrDefault(m =>
            {
                ParameterInfo[] ps = m.GetParameters();
                return ps.Length == 1 &&
                       (ps[0].ParameterType.IsAssignableFrom(typeof(Dictionary<string, string>)) ||
                        ps[0].ParameterType.IsAssignableFrom(typeof(IReadOnlyDictionary<string, string>)));
            });

        if (userMethod is null)
        {
            return false;
        }

        userPrompt = InvokeSync(userMethod.IsStatic ? null : builder, userMethod, [maskedBatch]) as string;
        return userPrompt is not null;
    }

    // =========================================================================
    // F7/F8: profiles.json Safe Save Helper
    // =========================================================================

    public static async Task SaveProfilesSafeAsync(
        OpaqueContractBridge bridge,
        string profilesJsonPath,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default)
    {
        Type? configType = TranslatorAssembly.GetTypes()
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
                    await InvokeAnyAsync(method.IsStatic ? null : instance, method, args).ConfigureAwait(false);
                    if (File.Exists(profilesJsonPath))
                    {
                        return;
                    }
                }
            }
        }

        await bridge.SaveProfilesAsync(profilesJsonPath, activeProfile, profiles, ct).ConfigureAwait(false);
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

    // =========================================================================
    // F10 / F11 / F12: Signature-Aware Sequential Worker Helpers
    // =========================================================================

    public static SequentialWorkerHandle CreateSequentialWorkerSafe(
        OpaqueContractBridge bridge,
        ITranslatorProvider translator,
        ITextSanitizer sanitizer,
        DatabaseSession db,
        Action<string>? onAutoPause = null,
        Action<int, int>? onProgress = null,
        Action<string>? onLog = null)
    {
        try
        {
            return bridge.CreateSequentialWorker(translator, sanitizer, db, onAutoPause, onProgress, onLog);
        }
        catch (Exception)
        {
            // Fallback around OpaqueContractBridge.TryCreateViaStaticFactory invoking Int32.Parse(dbPath)
            // when constructor parameters have primitive types with default values or optional helpers.
        }

        Type workerType = TranslatorAssembly.GetTypes()
            .FirstOrDefault(t =>
                !t.IsAbstract &&
                !t.IsInterface &&
                (t.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Contains("Queue", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("Could not locate sequential queue worker type in Lokasync.Translator.");

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

        object worker = CreateWorkerInstanceSafe(workerType, context, new HashSet<Type>())
            ?? throw new InvalidOperationException(
                $"Failed to construct sequential worker '{workerType.FullName}'. Constructors: [{string.Join("; ", workerType.GetConstructors().Select(c => c.ToString()))}].");

        AttachWorkerEventsSafe(worker, workerType, onAutoPause, onProgress, onLog);
        return new SequentialWorkerHandle(worker, workerType);
    }

    private static object? CreateWorkerInstanceSafe(
        Type targetType,
        Dictionary<Type, object> context,
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
            Type? impl = TranslatorAssembly.GetTypes().Concat(GetCoreLoadableTypes())
                .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && targetType.IsAssignableFrom(t));
            return impl is not null ? CreateWorkerInstanceSafe(impl, context, visiting) : null;
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
                    else if (pType.IsGenericType && pType.GetGenericTypeDefinition() == typeof(Func<>))
                    {
                        Type ret = pType.GetGenericArguments()[0];
                        if (ret == typeof(int))
                        {
                            // e.g. jitter/random provider Func<int> or Func<int, int, int>
                            args[i] = null;
                            ok = false;
                            break;
                        }
                    }
                    else
                    {
                        object? nested = CreateWorkerInstanceSafe(pType, context, visiting);
                        if (nested is not null)
                        {
                            args[i] = nested;
                        }
                        else if (Nullable.GetUnderlyingType(pType) is not null || !pType.IsValueType)
                        {
                            // Allow optional reference/nullable dependency only if no non-null alternative
                            args[i] = null;
                            ok = false;
                            break;
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
                SubscribeStringOrEventArgs(worker, evt, onAutoPause);
            }
            else if (onLog is not null && evt.Name.Contains("Log", StringComparison.OrdinalIgnoreCase))
            {
                SubscribeStringOrEventArgs(worker, evt, onLog);
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

    private static void SubscribeStringOrEventArgs(object target, EventInfo evt, Action<string> callback)
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
                MethodInfo generic = typeof(CoreTranslatorTestHelpers)
                    .GetMethod(nameof(CreateSimpleEventArgsBridge), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(ps[1].ParameterType);
                Delegate del = (Delegate)generic.Invoke(null, [handlerType, callback])!;
                evt.AddEventHandler(target, del);
            }
        }
    }

    private static Delegate CreateSimpleEventArgsBridge<TEventArgs>(Type handlerType, Action<string> callback)
    {
        Action<object?, TEventArgs> wrapper = (_, args) => callback(args?.ToString() ?? typeof(TEventArgs).Name);
        return Delegate.CreateDelegate(handlerType, wrapper.Target, wrapper.Method);
    }

    public static List<AutoPauseNotification> AttachDetailedAutoPauseListener(SequentialWorkerHandle worker)
    {
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
                    MethodInfo generic = typeof(CoreTranslatorTestHelpers)
                        .GetMethod(nameof(CreateDetailedEventBridge), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(ps[1].ParameterType);
                    Delegate del = (Delegate)generic.Invoke(null, [handlerType, notifications])!;
                    evt.AddEventHandler(worker.Instance, del);
                }
            }
        }

        return notifications;
    }

    private static Delegate CreateDetailedEventBridge<TEventArgs>(
        Type handlerType,
        List<AutoPauseNotification> notifications)
    {
        Action<object?, TEventArgs> wrapper = (_, args) =>
        {
            notifications.Add(ExtractAutoPauseNotification(args));
        };
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
        object? statusObj = GetPropObj(args, "StatusCode", "HttpStatusCode", "Code");
        if (statusObj is HttpStatusCode httpCode)
        {
            statusCode = (int)httpCode;
        }
        else if (statusObj is int intCode)
        {
            statusCode = intCode;
        }

        string? reason = GetPropString(args, "Reason", "Message", "ErrorMessage", "Details") ?? raw;
        return new AutoPauseNotification(raw, statusCode, reason);
    }

    public static async Task<object?> RunWorkerAsync(
        SequentialWorkerHandle worker,
        DatabaseSession db,
        RouterProfile profile,
        string systemPrompt,
        CancellationToken ct = default)
    {
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
                return await InvokeAnyAsync(worker.Instance, method, args).ConfigureAwait(false);
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
                return await InvokeAnyAsync(worker.Instance, method, args).ConfigureAwait(false);
            }
        }

        // If Resume takes 0 args (signals pause gate), also check if a property sets profile first
        SetPropIfExists(worker.Instance, "Profile", updatedProfile);
        SetPropIfExists(worker.Instance, "RouterProfile", updatedProfile);

        MethodInfo? signalResume = resumeMethods.FirstOrDefault(m => m.GetParameters().Length == 0);
        if (signalResume is not null)
        {
            await InvokeAnyAsync(worker.Instance, signalResume, []).ConfigureAwait(false);
            return null;
        }

        return await RunWorkerAsync(worker, db, updatedProfile, systemPrompt, ct).ConfigureAwait(false);
    }

    public static bool TryRequestWorkerPause(SequentialWorkerHandle worker)
    {
        MethodInfo? pauseMethod = worker.WorkerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => !m.IsSpecialName &&
                                 (m.Name.Equals("Pause", StringComparison.OrdinalIgnoreCase) ||
                                  m.Name.Equals("RequestPause", StringComparison.OrdinalIgnoreCase) ||
                                  m.Name.Contains("Pause", StringComparison.OrdinalIgnoreCase)) &&
                                 m.GetParameters().Length == 0);

        if (pauseMethod is null)
        {
            return false;
        }

        InvokeSync(worker.Instance, pauseMethod, []);
        return true;
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

    // =========================================================================
    // Low-Level Reflection Utilities
    // =========================================================================

    private static IEnumerable<Type> GetCoreLoadableTypes()
    {
        try
        {
            return CoreAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static bool IsConnectionOrLeaseType(Type type) =>
        type == typeof(SqliteConnection) ||
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.PropertyType == typeof(SqliteConnection));

    private static Type UnwrapReturnType(Type returnType)
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

    private static object? InvokeSync(object? target, MethodInfo method, params object?[] args)
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

    private static async Task<object?> InvokeAnyAsync(object? target, MethodInfo method, params object?[] args)
    {
        object? rawResult = InvokeSync(target, method, args);
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
            Task vtTask = (Task)rType.GetMethod("AsTask")!.Invoke(rawResult, null)!;
            await vtTask.ConfigureAwait(false);
            return vtTask.GetType().GetProperty("Result")!.GetValue(vtTask);
        }

        return rawResult;
    }

    private static void SetPropIfExists(object target, string propName, object? value)
    {
        PropertyInfo? prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }

    private static object? GetPropObj(object target, params string[] names)
    {
        foreach (string name in names)
        {
            PropertyInfo? prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not null)
            {
                return prop.GetValue(target);
            }
        }

        return null;
    }

    private static string? GetPropString(object target, params string[] names) =>
        GetPropObj(target, names) as string;
}
