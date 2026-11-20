using FileSync.Records;
using FileSync.Sync;

namespace FileSync.Web;

public static class RecordPlanRoutes
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<RecordPlanStore>();
        services.AddSingleton<RecordPlanService>();
        services.AddSingleton<RecordExecutionEnvironment>();
        services.AddSingleton<RecordExecutionServices>();
        services.AddSingleton<RecordPlanExecution>();
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/record-repositories/{repositoryId}/plans", (string repositoryId, RecordPlanService plans, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(plans.List(repositoryId))));
        app.MapGet("/api/record-repositories/{repositoryId}/plans/{id}", (string repositoryId, string id,
            RecordPlanService plans, SyncEngine engine) => engine.Exclusive(() => Task.FromResult(plans.Details(repositoryId, id))));
        app.MapPost("/api/record-repositories/{repositoryId}/plans/preview", (string repositoryId, RecordPlanRequest request,
            RecordPlanService plans, SyncEngine engine) => engine.Exclusive(() => Task.FromResult(plans.Preview(repositoryId, request))));
        app.MapPost("/api/record-repositories/{repositoryId}/plans", (string repositoryId, RecordPlanRequest request,
            RecordPlanService plans, SyncEngine engine) => engine.Exclusive(() => Task.FromResult(plans.Save(repositoryId, request))));
        app.MapPost("/api/record-repositories/{repositoryId}/plans/{id}/prepare", (string repositoryId, string id,
            RecordPlanConnection request, RecordPlanExecution execution, SyncEngine engine, CancellationToken token) =>
            engine.PrepareSelection(execution, new(repositoryId, id, request.PeerId), token));
        app.MapPost("/api/record-repositories/{id}/enable-lan", (string id, RecordService records, SyncEngine engine) =>
            engine.Exclusive(() => Task.FromResult(records.EnableLan(id))));
        app.MapGet("/peer/record-repositories/{id}/connection", (string id, RecordService records) => records.Connection(id));
        app.MapGet("/record-plans.js", () => Results.Text(LocalRoutes.Resource("record-plans.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/record-selection.js", () => Results.Text(LocalRoutes.Resource("record-selection.js"), "text/javascript; charset=utf-8"));
    }
}
