namespace WindowsDriverCore.ErrorHandling;

public class WebDriverException : Exception
{
    public string ErrorCode { get; }
    public int HttpStatus { get; }

    public WebDriverException(string errorCode, string message, int httpStatus = 500)
        : base(message)
    {
        ErrorCode = errorCode;
        HttpStatus = httpStatus;
    }
}
