using DataspaceDrive.Api.Auth;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Notifications;
using DataspaceDrive.Core.Settings;
using DataspaceDrive.Core.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace DataspaceDrive.Api;

public sealed record CreateFolderRequest(Guid ParentId, string Name);

public sealed record RenameRequest(string Name);

public sealed record MoveRequest(Guid TargetFolderId);

public sealed record StartUploadRequest(Guid FolderId, string FileName, long Size);

public sealed record CompleteUploadRequest(string? ContentType);

public sealed record MeResponse(Guid Id, string Name, string? Email, IReadOnlyCollection<string> Roles, bool CanShareExternally,
    bool CanManageInbox, bool IsAdmin, bool CanApprove, bool SharingRequiresApproval, bool EmailNotifications);

public sealed record PreferencesRequest(bool EmailNotifications);

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var development = configuration["Auth:Mode"] == AuthSetup.DevelopmentScheme;
        endpoints.MapGet("/api/auth/login", (string? returnUrl, HttpContext context) =>
        {
            var target = IsLocalUrl(returnUrl) ? returnUrl! : "/";
            return development
                ? Results.Redirect(target)
                : Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
        });
        endpoints.MapGet("/api/auth/logout", () => development
            ? Results.Redirect("/")
            : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
        return endpoints;
    }

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization(AuthSetup.UserPolicy);

        api.MapGet("/me", async (CurrentUser current, IInstanceSettings settings, CancellationToken ct) =>
        {
            var user = await current.UserAsync(ct);
            var principal = await current.PrincipalAsync(ct);
            return new MeResponse(user.Id, user.DisplayName, user.Email, principal.Roles,
                principal.Roles.Contains(DrivePrincipal.RoleExternalShare), principal.Roles.Contains(DrivePrincipal.RoleInbox),
                principal.IsAdmin, principal.Roles.Contains(DrivePrincipal.RoleApprover), (await settings.SharingAsync(ct)).RequireApproval,
                user.EmailNotifications);
        });

        api.MapPut("/me/preferences", async (PreferencesRequest request, CurrentUser current, IUserProvisioning users, CancellationToken ct) =>
        {
            await users.SetEmailNotificationsAsync(await current.UserAsync(ct), request.EmailNotifications, ct);
            return Results.NoContent();
        });

        api.MapGet("/notifications/summary", async (CurrentUser current, INotificationService notifications, CancellationToken ct) =>
            await notifications.SummaryAsync(await current.PrincipalAsync(ct), ct));

        api.MapGet("/spaces", async (CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.GetSpacesAsync(await current.PrincipalAsync(ct), ct));

        api.MapGet("/folders/{folderId:guid}/children", async (Guid folderId, CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.GetChildrenAsync(await current.PrincipalAsync(ct), folderId, ct));

        api.MapPost("/folders", async (CreateFolderRequest request, CurrentUser current, IFileService files, CancellationToken ct) =>
            Results.Created((string?)null, await files.CreateFolderAsync(await current.PrincipalAsync(ct), request.ParentId, request.Name, ct)));

        api.MapPatch("/items/{itemId:guid}", async (Guid itemId, RenameRequest request, CurrentUser current, IFileService files,
            CancellationToken ct) => await files.RenameAsync(await current.PrincipalAsync(ct), itemId, request.Name, ct));

        api.MapPost("/items/{itemId:guid}/move", async (Guid itemId, MoveRequest request, CurrentUser current, IFileService files,
            CancellationToken ct) => await files.MoveAsync(await current.PrincipalAsync(ct), itemId, request.TargetFolderId, ct));

        api.MapDelete("/items/{itemId:guid}", async (Guid itemId, CurrentUser current, IFileService files, CancellationToken ct) =>
        {
            await files.DeleteAsync(await current.PrincipalAsync(ct), itemId, ct);
            return Results.NoContent();
        });

        api.MapGet("/files/{fileId:guid}/versions", async (Guid fileId, CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.GetVersionsAsync(await current.PrincipalAsync(ct), fileId, ct));

        api.MapGet("/files/{fileId:guid}/content", async (Guid fileId, int? version, CurrentUser current, IFileService files,
            IBlobStore blobs, HttpContext context, CancellationToken ct) =>
        {
            var content = await files.GetContentAsync(await current.PrincipalAsync(ct), fileId, version, ct);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var stream = await blobs.OpenReadAsync(content.BlobKey, ct);
            // Immer als Anhang ausliefern: eine hochgeladene HTML-Datei darf nicht im Kontext der Anwendung laufen.
            return Results.File(stream, content.ContentType, content.FileName, enableRangeProcessing: true);
        });

        api.MapGet("/files/{fileId:guid}/preview", async (Guid fileId, int? version, CurrentUser current, IFileService files,
            IBlobStore blobs, HttpContext context, CancellationToken ct) =>
        {
            var content = await files.GetContentAsync(await current.PrincipalAsync(ct), fileId, version, ct);
            var kind = FilePreview.KindOf(content.ContentType);
            if (kind is null)
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Für diesen Dateityp gibt es keine Vorschau.");
            if (kind == FilePreview.Kind.Text && content.Size > FilePreview.MaxTextBytes)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Die Textdatei ist für eine Vorschau zu groß.");
            FilePreview.ApplyHeaders(context.Response, kind.Value, content.FileName);
            return Results.File(await blobs.OpenReadAsync(content.BlobKey, ct), kind == FilePreview.Kind.Text ? "text/plain; charset=utf-8" : content.ContentType,
                enableRangeProcessing: true);
        });

        api.MapPost("/uploads", async (StartUploadRequest request, CurrentUser current, IUploadService uploads, CancellationToken ct) =>
            await uploads.StartAsync(await current.PrincipalAsync(ct), request.FolderId, request.FileName, request.Size, ct));

        api.MapPut("/uploads/{uploadId:guid}/chunks/{index:int}", async (Guid uploadId, int index, HttpRequest request,
            CurrentUser current, IUploadService uploads, CancellationToken ct) =>
        {
            var received = await uploads.AppendChunkAsync(await current.PrincipalAsync(ct), uploadId, index, request.Body, ct);
            return Results.Ok(new { receivedBytes = received });
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024 * 1024));

        api.MapPost("/uploads/{uploadId:guid}/complete", async (Guid uploadId, CompleteUploadRequest? request, CurrentUser current,
            IUploadService uploads, CancellationToken ct) =>
            await uploads.CompleteAsync(await current.PrincipalAsync(ct), uploadId, request?.ContentType ?? "", ct));

        api.MapDelete("/uploads/{uploadId:guid}", async (Guid uploadId, CurrentUser current, IUploadService uploads, CancellationToken ct) =>
        {
            await uploads.AbortAsync(await current.PrincipalAsync(ct), uploadId, ct);
            return Results.NoContent();
        });

        api.MapGet("/trash", async (CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.GetTrashAsync(await current.PrincipalAsync(ct), ct));

        api.MapPost("/trash/{itemId:guid}/restore", async (Guid itemId, CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.RestoreAsync(await current.PrincipalAsync(ct), itemId, ct));

        api.MapDelete("/trash/{itemId:guid}", async (Guid itemId, CurrentUser current, IFileService files, CancellationToken ct) =>
        {
            await files.PurgeAsync(await current.PrincipalAsync(ct), itemId, ct);
            return Results.NoContent();
        });

        api.MapGet("/search", async (string q, CurrentUser current, IFileService files, CancellationToken ct) =>
            await files.SearchAsync(await current.PrincipalAsync(ct), q, ct));

        return endpoints;
    }

    private static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal);
}
