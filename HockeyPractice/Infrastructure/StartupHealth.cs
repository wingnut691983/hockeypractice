namespace HockeyPractice.Infrastructure;

/// <summary>
/// Things that went wrong at startup and would otherwise only exist in the log.
///
/// The app deliberately keeps serving when migrations fail, so that it can come up and report
/// rather than crash-loop silently. The cost of that choice is a site which looks fine and fails
/// on every data operation, with one line in a log nobody is watching. This carries the failure
/// to the admin page so it is visible to the person who can act on it.
///
/// Restoring an old archive is exactly when a forward migration can fail, which is why this
/// arrived alongside restore rather than before it.
/// </summary>
public class StartupHealth
{
    /// <summary>Null when migrations applied cleanly, which is the normal case.</summary>
    public string? MigrationError { get; private set; }

    public bool MigrationsFailed => MigrationError is not null;

    public void RecordMigrationFailure(string error) => MigrationError = error;
}
