internal sealed record MigrationOperationReport(
    string Outcome,
    string? Error,
    string Message);

internal sealed record MigrationValidationReport(
    string Outcome,
    bool RollbackViable,
    IReadOnlyList<MigrationDiagnostic> Diagnostics);

internal sealed record MigrationBackupManifest(
    int SchemaVersion,
    string OperationId,
    string PlanHash,
    IReadOnlyList<MigrationBackupEntry> Entries);

internal sealed record MigrationBackupEntry(
    string RelativePath,
    string Kind,
    string? Sha256);

internal sealed record ParentMigrationJournal(
    int SchemaVersion,
    string OperationId,
    string PlanHash,
    string BackupPath,
    bool Committed,
    IReadOnlyList<MigrationJournalEntry> Entries);

internal sealed record MigrationJournalEntry(
    string RelativePath,
    string Kind,
    string? OriginalHash,
    string UpdatedHash,
    string? OriginalBase64,
    string UpdatedBase64);
