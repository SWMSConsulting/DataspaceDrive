using System.Data.Common;
using DataspaceDrive.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Api;

/// <summary>Bildet fachliche Fehler auf ProblemDetails mit deutscher Meldung ab.</summary>
public sealed class DriveExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, e.Message),
            AccessDeniedException e => (StatusCodes.Status403Forbidden, e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, e.Message),
            InvalidInputException e => (StatusCodes.Status400BadRequest, e.Message),
            AntiforgeryValidationException => (StatusCodes.Status400BadRequest, "Die Anfrage ist nicht gegen Cross-Site-Requests abgesichert. Bitte die Seite neu laden."),
            BadHttpRequestException e when e.StatusCode == StatusCodes.Status413PayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, "Das Teilstück ist zu groß."),
            // Gleichzeitige Änderungen, die die Vorabprüfungen der Dienste überholt haben.
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "Das Element wurde gerade von jemand anderem geändert. Bitte neu laden und erneut versuchen."),
            DbUpdateException { InnerException: DbException { SqlState: "23505" } } => (StatusCodes.Status409Conflict,
                "Ein Element mit diesem Namen wurde gerade von jemand anderem angelegt. Bitte neu laden."),
            _ => (0, null),
        };
        if (status == 0)
            return false;
        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
            Exception = exception,
        });
    }
}
