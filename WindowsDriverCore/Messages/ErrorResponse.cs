namespace WindowsDriverCore.Messages;

public record WebDriverError(string Error, string Message, string Stacktrace);

public record ErrorResponse(WebDriverError Value);
