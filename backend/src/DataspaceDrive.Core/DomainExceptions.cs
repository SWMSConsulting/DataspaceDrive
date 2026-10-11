namespace DataspaceDrive.Core;

/// <summary>Fachliche Fehler, die die API auf ProblemDetails abbildet.</summary>
public abstract class DomainException(string message) : Exception(message);

public sealed class NotFoundException(string message) : DomainException(message);

public sealed class AccessDeniedException(string message) : DomainException(message);

public sealed class ConflictException(string message) : DomainException(message);

public sealed class InvalidInputException(string message) : DomainException(message);
