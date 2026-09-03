namespace FhirAugury.Processing.Common.Authoring;

public static class AuthoringStatusValues
{
    public static class ProcessorModes
    {
        public const string Legacy = "legacy";
        public const string CuttingOver = "cutting-over";
        public const string RunBacked = "run-backed";
    }

    public static class Runs
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Finalizing = "finalizing";
        public const string Completed = "completed";
        public const string CompletedDatabaseOnly = "completed-database-only";
        public const string Error = "error";
        public const string Superseded = "superseded";
    }

    public static class Items
    {
        public const string Pending = "pending";
        public const string InProgress = "in-progress";
        public const string Persisted = "persisted";
        public const string Complete = "complete";
        public const string Error = "error";
        public const string Superseded = "superseded";
    }

    public static class Attempts
    {
        public const string Active = "active";
        public const string Accepted = "accepted";
        public const string Superseded = "superseded";
        public const string Error = "error";
    }

    public static class Stages
    {
        public const string Pending = "pending";
        public const string InProgress = "in-progress";
        public const string Complete = "complete";
        public const string Error = "error";
    }

    public static class Snapshots
    {
        public const string Creating = "creating";
        public const string Promoted = "promoted";
        public const string Ready = "ready";
        public const string Error = "error";
    }

    public static void EnsureModeTransition(string current, string next)
    {
        bool legal = (current, next) switch
        {
            (ProcessorModes.Legacy, ProcessorModes.CuttingOver) => true,
            (ProcessorModes.CuttingOver, ProcessorModes.RunBacked) => true,
            _ when string.Equals(current, next, StringComparison.Ordinal) => true,
            _ => false,
        };

        if (!legal)
        {
            throw new InvalidOperationException($"Illegal authoring processor mode transition '{current}' -> '{next}'.");
        }
    }

    public static void EnsureRunTransition(string current, string next)
    {
        bool legal = (current, next) switch
        {
            (Runs.Queued, Runs.Running) => true,
            (Runs.Running, Runs.Finalizing) => true,
            (Runs.Running, Runs.Error) => true,
            (Runs.Running, Runs.Superseded) => true,
            (Runs.Queued, Runs.Superseded) => true,
            (Runs.Finalizing, Runs.Error) => true,
            (Runs.Error, Runs.Running) => true,
            (Runs.Error, Runs.Finalizing) => true,
            (Runs.Error, Runs.Superseded) => true,
            (Runs.Finalizing, Runs.Completed) => true,
            (Runs.Finalizing, Runs.CompletedDatabaseOnly) => true,
            _ when string.Equals(current, next, StringComparison.Ordinal) => true,
            _ => false,
        };

        if (!legal)
        {
            throw new InvalidOperationException($"Illegal authoring run transition '{current}' -> '{next}'.");
        }
    }

    public static void EnsureItemTransition(string current, string next)
    {
        bool legal = (current, next) switch
        {
            (Items.Pending, Items.InProgress) => true,
            (Items.InProgress, Items.Persisted) => true,
            (Items.InProgress, Items.Error) => true,
            (Items.InProgress, Items.Superseded) => true,
            (Items.Persisted, Items.Complete) => true,
            (Items.Persisted, Items.Error) => true,
            (Items.Pending, Items.Superseded) => true,
            (Items.Error, Items.Superseded) => true,
            (Items.Error, Items.Pending) => true,
            (Items.Error, Items.Persisted) => true,
            _ when string.Equals(current, next, StringComparison.Ordinal) => true,
            _ => false,
        };

        if (!legal)
        {
            throw new InvalidOperationException($"Illegal authoring item transition '{current}' -> '{next}'.");
        }
    }
}
