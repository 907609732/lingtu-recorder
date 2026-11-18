using System.Net;

namespace UnpackVision.Agent.Protocol;

public class AgentException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class AgentValidationException(string message)
    : AgentException(AgentErrorCodes.InvalidArgument, message);

public sealed class AgentUnavailableException(string message, Exception? innerException = null)
    : AgentException(AgentErrorCodes.Unavailable, message, innerException);

public sealed class AgentApiException(
    string code,
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null)
    : AgentException(code, message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public static class AgentExitCodes
{
    public const int Success = 0;
    public const int UsageOrValidation = 1;
    public const int Unavailable = 2;
    public const int Unauthorized = 3;
    public const int NotFound = 4;
    public const int Conflict = 5;
    public const int ServerError = 6;
    public const int Unexpected = 7;

    public static int For(AgentException exception) => exception.Code switch
    {
        AgentErrorCodes.InvalidArgument => UsageOrValidation,
        AgentErrorCodes.Unavailable => Unavailable,
        AgentErrorCodes.Unauthorized => Unauthorized,
        AgentErrorCodes.NotFound => NotFound,
        AgentErrorCodes.Conflict => Conflict,
        AgentErrorCodes.ServerError or AgentErrorCodes.UnexpectedResponse or AgentErrorCodes.InternalError => ServerError,
        _ => Unexpected
    };
}
