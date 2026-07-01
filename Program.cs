using System.Globalization;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

return SafeDeleteApp.Run(args);

internal static class SafeDeleteApp
{
    private const int ExitSuccess = 0;
    private const int ExitArgumentError = 2;
    private const int ExitPolicyRejected = 3;
    private const int ExitNotFound = 4;
    private const int ExitDeleteFailed = 5;
    private const int ExitAuditBeforeExecutionFailed = 10;
    private const int ExitAuditAfterExecutionFailed = 11;

    public static int Run(string[] args)
    {
        var operationId = Guid.NewGuid().ToString("N")[..12];
        var rawArgs = args.ToArray();
        var workingDirectory = Directory.GetCurrentDirectory();
        var auditLogger = AuditLogger.Create(workingDirectory);

        var parse = CommandLineOptions.Parse(args);
        if (parse.ShowHelp)
        {
            PrintUsage();
            return ExitSuccess;
        }

        if (parse.Error is not null || parse.Options is null)
        {
            var audit = AuditEvent.Create(operationId, "decision", rawArgs, workingDirectory);
            return CompleteRejected(
                auditLogger,
                audit,
                "argument_error",
                parse.Error ?? "Invalid arguments.",
                ExitArgumentError,
                printUsage: true);
        }

        var options = parse.Options;
        var evaluation = PolicyEvaluator.Evaluate(options.Path, workingDirectory, auditLogger.ProtectedDirectories);
        var decisionAudit = AuditEvent.Create(operationId, "decision", rawArgs, workingDirectory);
        decisionAudit.InputPath = options.Path;
        decisionAudit.NormalizedPath = evaluation.NormalizedPath is not null
            ? ToRelativeDisplay(evaluation.NormalizedPath, workingDirectory)
            : null;
        decisionAudit.DryRun = options.DryRun ? true : null;
        decisionAudit.Yes = options.Yes ? true : null;
        decisionAudit.Reason = options.Reason;
        decisionAudit.TargetExists = evaluation.Exists;
        decisionAudit.TargetKind = evaluation.Inventory?.Kind switch
        {
            TargetKind.File => "file",
            TargetKind.Directory => "dir",
            _ => evaluation.Inventory?.Kind.ToString().ToLowerInvariant()
        };
        decisionAudit.FileCount = evaluation.Inventory?.FileCount;
        decisionAudit.DirectoryCount = evaluation.Inventory?.DirectoryCount;
        decisionAudit.TotalBytes = evaluation.Inventory?.TotalBytes;

        if (evaluation.Status == PolicyStatus.NotFound)
        {
            return CompleteRejected(auditLogger, decisionAudit, "not_found", evaluation.Message, ExitNotFound);
        }

        if (evaluation.Status == PolicyStatus.Rejected)
        {
            return CompleteRejected(auditLogger, decisionAudit, "policy_rejected", evaluation.Message, ExitPolicyRejected);
        }

        var inventory = evaluation.Inventory;
        if (inventory is null)
        {
            return CompleteRejected(
                auditLogger,
                decisionAudit,
                "policy_rejected",
                "Target inventory could not be created.",
                ExitPolicyRejected);
        }

        PrintTargetSummary(inventory, workingDirectory);

        if (options.DryRun)
        {
            decisionAudit.Decision = "allowed";
            decisionAudit.Result = "dry_run";
            decisionAudit.ExitCode = ExitSuccess;

            if (!WriteAudit(auditLogger, decisionAudit, out var auditError))
            {
                Console.Error.WriteLine($"Audit write failed: {auditError}");
                return ExitAuditBeforeExecutionFailed;
            }

            Console.WriteLine("Dry-run OK.");
            return ExitSuccess;
        }

        if (!options.Yes && !ConfirmDeletion())
        {
            var denialReason = Console.IsInputRedirected
                ? "Deletion requires --yes when input is redirected."
                : "User did not confirm deletion.";
            return CompleteRejected(auditLogger, decisionAudit, "confirmation_rejected", denialReason, ExitPolicyRejected);
        }

        decisionAudit.Decision = "allowed";
        decisionAudit.Result = "execute_started";
        decisionAudit.ExitCode = null;

        if (!WriteAudit(auditLogger, decisionAudit, out var preDeleteAuditError))
        {
            Console.Error.WriteLine($"Audit write failed: {preDeleteAuditError}");
            return ExitAuditBeforeExecutionFailed;
        }

        var resultAudit = decisionAudit.CloneForEvent("result");
        var revalidation = PolicyEvaluator.Evaluate(inventory.FullPath, workingDirectory, auditLogger.ProtectedDirectories);
        if (revalidation.Status != PolicyStatus.Allowed || revalidation.Inventory is null)
        {
            var exitCode = revalidation.Status == PolicyStatus.NotFound ? ExitNotFound : ExitPolicyRejected;
            return CompleteRejected(
                auditLogger,
                resultAudit,
                "pre_delete_revalidation_failed",
                $"Delete refused during final validation: {revalidation.Message}",
                exitCode,
                auditFailureExitCode: ExitAuditAfterExecutionFailed);
        }

        if (!inventory.HasSameSnapshotAs(revalidation.Inventory))
        {
            return CompleteRejected(
                auditLogger,
                resultAudit,
                "pre_delete_revalidation_failed",
                "Target changed after approval.",
                ExitPolicyRejected,
                auditFailureExitCode: ExitAuditAfterExecutionFailed);
        }

        try
        {
            DeleteToRecycleBin(inventory);
            resultAudit.Result = "deleted_to_recycle_bin";
            resultAudit.ExitCode = ExitSuccess;

            if (!WriteAudit(auditLogger, resultAudit, out var postDeleteAuditError, bestEffort: true))
            {
                Console.Error.WriteLine($"Audit write failed: {postDeleteAuditError}");
                return ExitAuditAfterExecutionFailed;
            }

            Console.WriteLine($"Deleted: {ToRelativeDisplay(inventory.FullPath, workingDirectory)}");
            return ExitSuccess;
        }
        catch (Exception ex)
        {
            resultAudit.Result = "delete_failed";
            resultAudit.ExceptionType = ex.GetType().FullName;
            resultAudit.ExceptionMessage = ex.Message;
            resultAudit.ExitCode = ExitDeleteFailed;

            if (!WriteAudit(auditLogger, resultAudit, out var deleteFailureAuditError, bestEffort: true))
            {
                Console.Error.WriteLine($"Audit write failed: {deleteFailureAuditError}");
                return ExitAuditAfterExecutionFailed;
            }

            Console.Error.WriteLine($"Delete failed: {ex.Message}");
            return ExitDeleteFailed;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  safe-delete <path> [--dry-run] [--reason <text>] [--yes]");
        Console.WriteLine();
        Console.WriteLine("Moves a file or directory inside the current workspace to the Windows Recycle Bin.");
    }

    private static void PrintTargetSummary(TargetInventory inventory, string workingDirectory)
    {
        var relPath = ToRelativeDisplay(inventory.FullPath, workingDirectory);
        if (inventory.Kind == TargetKind.File)
        {
            Console.WriteLine($"Target: {relPath} (file, {inventory.TotalBytes} B)");
        }
        else
        {
            Console.WriteLine($"Target: {relPath} (dir, {inventory.FileCount} files, {inventory.DirectoryCount} dirs, {inventory.TotalBytes} B)");
        }
    }

    /// <summary>
    /// 将绝对路径转为相对当前工作目录的显示路径。
    /// 失败时回退返回绝对路径。调用方需知悉：CLI 与审计日志的 path 字段在异常情况下会退化为绝对路径。
    /// </summary>
    private static string ToRelativeDisplay(string absolutePath, string cwd)
    {
        try
        {
            var relative = Path.GetRelativePath(cwd, absolutePath);
            // Path.GetRelativePath on Windows 返回反斜杠分隔的路径；
            // 若 relative 与输入相同（如跨盘情况），直接返回 absolutePath（非异常回退）
            return relative;
        }
        catch (ArgumentException) { return absolutePath; }
        catch (PathTooLongException) { return absolutePath; }
        catch (NotSupportedException) { return absolutePath; }
    }

    private static bool ConfirmDeletion()
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.Write("Confirm delete? Type DELETE: ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "DELETE", StringComparison.Ordinal);
    }

    private static void DeleteToRecycleBin(TargetInventory inventory)
    {
        RecycleBin.MoveToRecycleBin(inventory.FullPath);

        if (File.Exists(inventory.FullPath) || Directory.Exists(inventory.FullPath))
        {
            throw new IOException("Shell delete reported success but the target still exists.");
        }
    }

    private static bool WriteAudit(AuditLogger logger, AuditEvent audit, out string error, bool bestEffort = false)
    {
        try
        {
            logger.Write(audit, bestEffort);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int CompleteRejected(
        AuditLogger logger,
        AuditEvent audit,
        string result,
        string denialReason,
        int exitCode,
        bool printUsage = false,
        int auditFailureExitCode = ExitAuditBeforeExecutionFailed)
    {
        audit.Decision = "rejected";
        audit.Result = result;
        audit.DenialReason = denialReason;
        audit.ExitCode = exitCode;

        if (!WriteAudit(logger, audit, out var auditError))
        {
            Console.Error.WriteLine($"Audit write failed: {auditError}");
            return auditFailureExitCode;
        }

        Console.Error.WriteLine(denialReason);
        if (printUsage)
        {
            PrintUsage();
        }

        return exitCode;
    }
}

internal sealed class CommandLineOptions
{
    public required string Path { get; init; }
    public bool DryRun { get; init; }
    public bool Yes { get; init; }
    public string? Reason { get; init; }

    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return ParseResult.Fail("Missing required path.");
        }

        string? path = null;
        string? reason = null;
        var dryRun = false;
        var yes = false;
        var endOfOptions = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!endOfOptions && (arg == "-h" || arg == "--help"))
            {
                return ParseResult.Help();
            }

            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg == "--dry-run")
            {
                dryRun = true;
                continue;
            }

            if (!endOfOptions && arg == "--yes")
            {
                yes = true;
                continue;
            }

            if (!endOfOptions && arg == "--reason")
            {
                if (i + 1 >= args.Length)
                {
                    return ParseResult.Fail("--reason requires a value.");
                }

                reason = args[++i];
                continue;
            }

            if (!endOfOptions && arg.StartsWith("--reason=", StringComparison.Ordinal))
            {
                reason = arg["--reason=".Length..];
                continue;
            }

            if (!endOfOptions && arg.StartsWith("-", StringComparison.Ordinal))
            {
                return ParseResult.Fail($"Unknown option: {arg}");
            }

            if (path is not null)
            {
                return ParseResult.Fail("Exactly one path is supported.");
            }

            path = arg;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return ParseResult.Fail("Missing required path.");
        }

        return ParseResult.Ok(new CommandLineOptions
        {
            Path = path,
            DryRun = dryRun,
            Yes = yes,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason
        });
    }
}

internal sealed class ParseResult
{
    private ParseResult(CommandLineOptions? options, string? error, bool showHelp)
    {
        Options = options;
        Error = error;
        ShowHelp = showHelp;
    }

    public CommandLineOptions? Options { get; }
    public string? Error { get; }
    public bool ShowHelp { get; }

    public static ParseResult Ok(CommandLineOptions options) => new(options, null, false);

    public static ParseResult Fail(string error) => new(null, error, false);

    public static ParseResult Help() => new(null, null, true);
}

internal static class PolicyEvaluator
{
    public static PolicyEvaluation Evaluate(
        string inputPath,
        string workingDirectory,
        IReadOnlyCollection<string> protectedDirectories)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return PolicyEvaluation.Rejected("Path must not be empty.");
        }

        if (inputPath.IndexOfAny(['*', '?']) >= 0)
        {
            return PolicyEvaluation.Rejected("Wildcards are not allowed.");
        }

        if (ContainsParentSegment(inputPath))
        {
            return PolicyEvaluation.Rejected("Parent directory segments are not allowed.");
        }

        string fullPath;
        string fullWorkingDirectory;
        try
        {
            fullWorkingDirectory = Path.GetFullPath(workingDirectory);
            fullPath = Path.GetFullPath(inputPath, fullWorkingDirectory);
        }
        catch (Exception ex)
        {
            return PolicyEvaluation.Rejected($"Path could not be resolved: {ex.Message}");
        }

        if (TryFindReparsePointInDirectoryChain(fullWorkingDirectory, out var workspaceReparsePoint))
        {
            return PolicyEvaluation.Rejected(
                $"Current working directory is under a reparse point: {workspaceReparsePoint}",
                fullPath,
                exists: File.Exists(fullPath) || Directory.Exists(fullPath));
        }

        var targetExists = File.Exists(fullPath) || Directory.Exists(fullPath);

        if (IsRootPath(fullPath))
        {
            return PolicyEvaluation.Rejected("Root directories are not allowed.", fullPath, targetExists);
        }

        if (PathEquals(fullPath, fullWorkingDirectory))
        {
            return PolicyEvaluation.Rejected("Deleting the current working directory is not allowed.", fullPath, targetExists);
        }

        if (!IsUnderDirectory(fullPath, fullWorkingDirectory))
        {
            return PolicyEvaluation.Rejected("Target must be inside the current working directory.", fullPath, targetExists);
        }

        var protectedDirectory = protectedDirectories.FirstOrDefault(directory =>
            PathEquals(fullPath, directory) || IsUnderDirectory(fullPath, directory));
        if (protectedDirectory is not null)
        {
            return PolicyEvaluation.Rejected($"Deleting an audit directory is not allowed: {protectedDirectory}", fullPath, targetExists);
        }

        if (IsSensitiveExactPath(fullPath) || IsSensitiveSystemPath(fullPath))
        {
            return PolicyEvaluation.Rejected("Sensitive user or system directories are not allowed.", fullPath, targetExists);
        }

        if (!targetExists)
        {
            return PolicyEvaluation.NotFound("Target does not exist.", fullPath);
        }

        var targetAncestor = Directory.Exists(fullPath)
            ? Directory.GetParent(fullPath)?.FullName
            : Path.GetDirectoryName(fullPath);
        if (targetAncestor is not null
            && IsUnderDirectory(targetAncestor, fullWorkingDirectory)
            && TryFindReparsePointBetween(targetAncestor, fullWorkingDirectory, out var targetReparsePoint))
        {
            return PolicyEvaluation.Rejected(
                $"Target is under a reparse point: {targetReparsePoint}",
                fullPath,
                exists: true);
        }

        try
        {
            var inventory = TargetInventory.Create(fullPath);
            return PolicyEvaluation.Allowed(fullPath, inventory);
        }
        catch (PolicyRejectedException ex)
        {
            return PolicyEvaluation.Rejected(ex.Message, fullPath, exists: true);
        }
        catch (Exception ex)
        {
            return PolicyEvaluation.Rejected($"Target could not be inspected: {ex.Message}", fullPath, exists: true);
        }
    }

    private static bool ContainsParentSegment(string path)
    {
        var segments = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment == "..");
    }

    private static bool IsRootPath(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null && PathEquals(path, root);
    }

    private static bool IsSensitiveExactPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(userProfile) && PathEquals(path, userProfile);
    }

    private static bool IsSensitiveSystemPath(string path)
    {
        var sensitivePaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        return sensitivePaths
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Any(item => PathEquals(path, item) || IsUnderDirectory(path, item));
    }

    private static bool TryFindReparsePointInDirectoryChain(string directory, out string reparsePoint)
    {
        var current = Path.GetFullPath(directory);
        while (!IsRootPath(current))
        {
            if (Directory.Exists(current) && HasReparsePointAttribute(current))
            {
                reparsePoint = current;
                return true;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        reparsePoint = string.Empty;
        return false;
    }

    private static bool TryFindReparsePointBetween(string directory, string boundaryDirectory, out string reparsePoint)
    {
        var current = Path.GetFullPath(directory);
        var boundary = Path.GetFullPath(boundaryDirectory);

        while (!PathEquals(current, boundary) && IsUnderDirectory(current, boundary))
        {
            if (Directory.Exists(current) && HasReparsePointAttribute(current))
            {
                reparsePoint = current;
                return true;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        reparsePoint = string.Empty;
        return false;
    }

    private static bool HasReparsePointAttribute(string path)
    {
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsUnderDirectory(string childPath, string parentDirectory)
    {
        var child = NormalizeForComparison(childPath);
        var parent = EnsureTrailingSeparator(NormalizeForComparison(parentDirectory));
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

internal sealed class PolicyEvaluation
{
    private PolicyEvaluation(PolicyStatus status, string message, string? normalizedPath, bool? exists, TargetInventory? inventory)
    {
        Status = status;
        Message = message;
        NormalizedPath = normalizedPath;
        Exists = exists;
        Inventory = inventory;
    }

    public PolicyStatus Status { get; }
    public string Message { get; }
    public string? NormalizedPath { get; }
    public bool? Exists { get; }
    public TargetInventory? Inventory { get; }

    public static PolicyEvaluation Allowed(string normalizedPath, TargetInventory inventory)
        => new(PolicyStatus.Allowed, "Allowed.", normalizedPath, true, inventory);

    public static PolicyEvaluation Rejected(string message, string? normalizedPath = null, bool? exists = null)
        => new(PolicyStatus.Rejected, message, normalizedPath, exists, null);

    public static PolicyEvaluation NotFound(string message, string? normalizedPath = null)
        => new(PolicyStatus.NotFound, message, normalizedPath, false, null);
}

internal enum PolicyStatus
{
    Allowed,
    Rejected,
    NotFound
}

internal sealed class TargetInventory
{
    private TargetInventory(string fullPath, TargetKind kind, long fileCount, long directoryCount, long totalBytes)
    {
        FullPath = fullPath;
        Kind = kind;
        FileCount = fileCount;
        DirectoryCount = directoryCount;
        TotalBytes = totalBytes;
    }

    public string FullPath { get; }
    public TargetKind Kind { get; }
    public long FileCount { get; }
    public long DirectoryCount { get; }
    public long TotalBytes { get; }

    public bool HasSameSnapshotAs(TargetInventory other)
    {
        return string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase)
            && Kind == other.Kind
            && FileCount == other.FileCount
            && DirectoryCount == other.DirectoryCount
            && TotalBytes == other.TotalBytes;
    }

    public static TargetInventory Create(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        RejectReparsePoint(fullPath, attributes);

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var info = new FileInfo(fullPath);
            return new TargetInventory(fullPath, TargetKind.File, 1, 0, info.Length);
        }

        long fileCount = 0;
        long directoryCount = 1;
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(fullPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var entryAttributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, entryAttributes);

                if (entryAttributes.HasFlag(FileAttributes.Directory))
                {
                    directoryCount++;
                    pending.Push(entry);
                    continue;
                }

                checked
                {
                    fileCount++;
                    totalBytes += new FileInfo(entry).Length;
                }
            }
        }

        return new TargetInventory(fullPath, TargetKind.Directory, fileCount, directoryCount, totalBytes);
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PolicyRejectedException($"Reparse points are not allowed: {path}");
        }
    }
}

internal enum TargetKind
{
    File,
    Directory
}

internal static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public static void MoveToRecycleBin(string fullPath)
    {
        var operation = new ShFileOpStruct
        {
            WFunc = FoDelete,
            PFrom = fullPath + '\0' + '\0',
            FFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
        {
            throw new IOException($"Windows shell delete failed with code {result}.");
        }

        if (operation.FAnyOperationsAborted)
        {
            throw new OperationCanceledException("Windows shell delete was aborted.");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOperation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint WFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string PFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? PTo;
        public ushort FFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool FAnyOperationsAborted;
        public IntPtr HNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LpszProgressTitle;
    }
}

internal sealed class PolicyRejectedException : Exception
{
    public PolicyRejectedException(string message) : base(message)
    {
    }
}

internal sealed class AuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private AuditLogger(IReadOnlyList<string> logPaths)
    {
        LogPaths = logPaths;
        ProtectedDirectories = logPaths
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> LogPaths { get; }
    public IReadOnlyList<string> ProtectedDirectories { get; }

    public static AuditLogger Create(string workingDirectory)
    {
        var projectLog = Path.GetFullPath(Path.Combine(workingDirectory, ".safe-delete", "audit.jsonl"));
        var userAuditDirectoryOverride = Environment.GetEnvironmentVariable("SAFE_DELETE_USER_LOG_DIR");
        var userAuditDirectory = string.IsNullOrWhiteSpace(userAuditDirectoryOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeDelete")
            : userAuditDirectoryOverride;
        var userLog = Path.GetFullPath(Path.Combine(userAuditDirectory, "audit.jsonl"));
        var logPaths = new[] { projectLog, userLog }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new AuditLogger(logPaths);
    }

    public void Write(AuditEvent auditEvent, bool bestEffort = false)
    {
        var line = JsonSerializer.Serialize(auditEvent, JsonOptions) + Environment.NewLine;
        var lineBytes = Encoding.UTF8.GetBytes(line);
        var failures = new List<string>();

        foreach (var logPath in LogPaths)
        {
            try
            {
                WriteLineAtomically(logPath, lineBytes);
            }
            catch (Exception ex)
            {
                failures.Add($"{logPath}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        if (bestEffort && failures.Count < LogPaths.Count)
        {
            // BestEffort 模式：至少一个日志写入成功即视为闭环；
            // 有部分失败时通过 stderr 提示但不阻断（删除已发生，成功日志里已有证据）
            Console.Error.WriteLine($"Warning: audit partial write failure: {string.Join("; ", failures)}");
            return;
        }

        throw new IOException("One or more audit logs could not be written. " + string.Join(" | ", failures));
    }

    private static void WriteLineAtomically(string logPath, byte[] lineBytes)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            RejectReparsePointInDirectoryChain(directory);
        }

        using var mutex = new Mutex(false, GetMutexName(logPath));
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Timed out waiting for audit log lock.");
        }

        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(lineBytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string GetMutexName(string logPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(logPath).ToUpperInvariant()));
        return "Local\\SafeDeleteAudit_" + Convert.ToHexString(hash);
    }

    private static void RejectReparsePointInDirectoryChain(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (!PathEqualsRoot(current))
        {
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Audit directory is under a reparse point: {current}");
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static bool PathEqualsRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AuditEvent
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("ts")]
    public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    [JsonPropertyName("op")]
    public required string OperationId { get; init; }

    [JsonPropertyName("event")]
    public required string EventType { get; set; }

    [JsonPropertyName("user")]
    public string User { get; init; } = GetCurrentUserIdentity();

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; } = Environment.ProcessId;

    [JsonPropertyName("cwd")]
    public required string WorkingDirectory { get; init; }

    [JsonPropertyName("args")]
    public required string[] RawArguments { get; init; }

    [JsonPropertyName("path")]
    public string? NormalizedPath { get; set; }

    [JsonPropertyName("input")]
    public string? InputPath { get; set; }

    [JsonPropertyName("exists")]
    public bool? TargetExists { get; set; }

    [JsonPropertyName("kind")]
    public string? TargetKind { get; set; }

    [JsonPropertyName("files")]
    public long? FileCount { get; set; }

    [JsonPropertyName("dirs")]
    public long? DirectoryCount { get; set; }

    [JsonPropertyName("bytes")]
    public long? TotalBytes { get; set; }

    [JsonPropertyName("dry_run")]
    public bool? DryRun { get; set; }

    [JsonPropertyName("yes")]
    public bool? Yes { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("decision")]
    public string? Decision { get; set; }

    [JsonPropertyName("deny")]
    public string? DenialReason { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("ex_type")]
    public string? ExceptionType { get; set; }

    [JsonPropertyName("ex_msg")]
    public string? ExceptionMessage { get; set; }

    [JsonPropertyName("exit")]
    public int? ExitCode { get; set; }

    public static AuditEvent Create(string operationId, string eventType, string[] rawArgs, string workingDirectory)
    {
        return new AuditEvent
        {
            OperationId = operationId,
            EventType = eventType,
            WorkingDirectory = workingDirectory,
            RawArguments = rawArgs
        };
    }

    public AuditEvent CloneForEvent(string eventType)
    {
        return new AuditEvent
        {
            OperationId = OperationId,
            EventType = eventType,
            WorkingDirectory = WorkingDirectory,
            RawArguments = RawArguments,
            InputPath = InputPath,
            NormalizedPath = NormalizedPath,
            TargetExists = TargetExists,
            TargetKind = TargetKind,
            FileCount = FileCount,
            DirectoryCount = DirectoryCount,
            TotalBytes = TotalBytes,
            DryRun = DryRun,
            Yes = Yes,
            Reason = Reason,
            Decision = Decision,
            DenialReason = DenialReason
        };
    }

    private static string GetCurrentUserIdentity()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return $"{Environment.UserName}@{Environment.MachineName}";
        }
    }
}
