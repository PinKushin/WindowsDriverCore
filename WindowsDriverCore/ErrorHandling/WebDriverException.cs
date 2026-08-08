namespace WindowsDriverCore.ErrorHandling;

public class WebDriverException : Exception
{
    public string ErrorCode { get; }
    public int HttpStatus { get; }

    public WebDriverException(string errorCode, string message, int httpStatus = -1)
        : base(message)
    {
        ErrorCode = errorCode;
        HttpStatus = httpStatus >= 0 ? httpStatus : ErrorType.GetHttpStatus(errorCode);
    }
}
