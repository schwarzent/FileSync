using System.Text.Json;
using FileSync.Core;
using FileSync.Sync;

namespace FileSync.Web;

public static class TaskBackupRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/configuration/tasks/export", (TaskBackupService service) =>
        {
            var backup = service.Export();
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(backup, TaskBackupJson.Options),
                "application/json; charset=utf-8", $"FileSync-tasks-{backup.ExportedUtc:yyyyMMdd-HHmmss}.json");
        });
        app.MapPost("/api/configuration/tasks/preview", (HttpRequest request, TaskBackupService service,
            SyncEngine engine, CancellationToken token) => engine.Exclusive(async () =>
                service.Preview(await TaskBackupJson.Read<TaskConfigurationBackup>(request.Body, token))));
        app.MapPost("/api/configuration/tasks/import", (HttpRequest request, TaskBackupService service,
            SyncEngine engine, CancellationToken token) => engine.Exclusive(async () =>
        {
            engine.EnsureRecovered();
            return service.Import(await TaskBackupJson.Read<TaskImportRequest>(request.Body, token));
        }));
    }
}
