namespace WindowsDriverCore.ErrorHandling;

public static class ErrorType
{
    public const string NoSuchElement = "no such element";
    public const string NoSuchSession = "invalid session id";
    public const string NoSuchWindow = "no such window";
    public const string UnknownCommand = "unknown command";
    public const string InvalidArgument = "invalid argument";
    public const string UnknownError = "unknown error";
    public const string InvalidSessionId = "invalid session id";
    public const string ElementNotVisible = "element not visible";
    public const string StaleElementReference = "stale element reference";
    public const string StaleSessionReference = "stale session reference";
}
