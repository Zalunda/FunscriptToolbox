using System;
using System.Net.Http;
using System.Net;
using System.Runtime.Serialization;
using System.Text;

namespace FunscriptToolbox.SubtitlesVerbs.Infra
{
    [Serializable]
    public class AIEngineAPIException : Exception
    {




        public AIEngineErrorType ErrorType { get; }
        public string EngineIdentifier { get; }

        public AIEngineAPIException()
        {
            ErrorType = AIEngineErrorType.Other;
        }

        public AIEngineAPIException(string message) : base(message)
        {
            ErrorType = AIEngineErrorType.Other;
        }

        public AIEngineAPIException(string message, AIEngineErrorType errorType, string engineIdentifier = null)
            : base(message)
        {
            ErrorType = errorType;
            EngineIdentifier = engineIdentifier;
        }

        public AIEngineAPIException(string message, Exception innerException) : base(message, innerException)
        {
            ErrorType = AIEngineErrorType.Other;
        }

        public AIEngineAPIException(string message, Exception innerException, AIEngineErrorType errorType, string engineIdentifier = null)
            : base(message, innerException)
        {
            ErrorType = errorType;
            EngineIdentifier = engineIdentifier;
        }

        protected AIEngineAPIException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            ErrorType = (AIEngineErrorType)info.GetInt32(nameof(ErrorType));
            EngineIdentifier = info.GetString(nameof(EngineIdentifier));
        }

        public static AIEngineAPIException FromAggregateException(AggregateException ex, string engineIdentifier)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Error while communicating with the API (aggregate): {ex.Message}");
            var errorType = AIEngineErrorType.Other;
            foreach (var innerException in ex.InnerExceptions)
            {
                sb.AppendLine($"    InnerException: {innerException.Message}");
                var innerErrorType = ClassifyException(innerException);
                if (innerErrorType != AIEngineErrorType.Other)
                {
                    errorType = innerErrorType;
                }
            }
            throw new AIEngineAPIException(sb.ToString(), ex, errorType, engineIdentifier);
        }

        public static Exception FromHttpRequestException(HttpRequestException ex, string engineIdentifier)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Error while communicating with the API (http): {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"    InnerException: {ex.InnerException.Message}");
            }
            var errorType = ClassifyException(ex);
            throw new AIEngineAPIException(sb.ToString(), ex, errorType, engineIdentifier);
        }

        public static Exception FromHttpStatusCode(HttpResponseMessage response, string engineIdentifier)
        {
            var errorType = ClassifyHttpStatusCode(response.StatusCode);
            throw new AIEngineAPIException(
                $"Error: {response.StatusCode} - {response.ReasonPhrase}",
                errorType,
                engineIdentifier);
        }

        public static Exception FromErrorInResponseBody(dynamic errorInResponseBody, string engineIdentifier)
        {
            var errorType = errorInResponseBody.type?.ToString();
            var message = errorInResponseBody?.message?.ToString();
            var errorCode = errorInResponseBody?.code?.ToString();
            var classifiedError = ClassifyErrorMessage(message, errorCode);

            throw new AIEngineAPIException(
                $"type={errorType}, errorCode={errorCode}, message={message}",
                classifiedError,
                engineIdentifier);
        }

        private static AIEngineErrorType ClassifyException(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                var message = httpEx.Message;
                if (message.Contains("429"))
                    return AIEngineErrorType.QuotasUsed;
                if (message.Contains("502") || message.Contains("503") || message.Contains("504"))
                    return AIEngineErrorType.ServiceUnavailable;
            }
            return AIEngineErrorType.Other;
        }

        private static AIEngineErrorType ClassifyHttpStatusCode(HttpStatusCode statusCode)
        {
            switch ((int)statusCode)
            {
                case 429: // Too Many Requests
                    return AIEngineErrorType.QuotasUsed;
                case 402: // Payment Required
                    return AIEngineErrorType.QuotasUsed;
                case 503: // Service Unavailable
                    return AIEngineErrorType.ServiceUnavailable;
                case 502: // Bad Gateway
                    return AIEngineErrorType.ServiceUnavailable;
                case 504: // Gateway Timeout
                    return AIEngineErrorType.ServiceUnavailable;
                case 500: // Internal Server Error
                    return AIEngineErrorType.ServiceUnavailable;
                default:
                    return AIEngineErrorType.Other;
            }
        }

        private static AIEngineErrorType ClassifyErrorMessage(string message, string errorCode = null)
        {
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(errorCode))
                return AIEngineErrorType.Other;

            var lowerMessage = (message ?? "").ToLowerInvariant();
            var lowerCode = (errorCode ?? "").ToLowerInvariant();

            // Quota/Rate limit related
            if (lowerMessage.Contains("rate limit") ||
                lowerMessage.Contains("rate_limit") ||
                lowerMessage.Contains("quota") ||
                lowerMessage.Contains("exceeded") ||
                lowerMessage.Contains("too many requests") ||
                lowerMessage.Contains("insufficient_quota") ||
                lowerMessage.Contains("billing") ||
                lowerMessage.Contains("credit") ||
                lowerMessage.Contains("limit reached") ||
                lowerCode.Contains("rate_limit") ||
                lowerCode.Contains("quota") ||
                lowerCode.Contains("insufficient"))
            {
                return AIEngineErrorType.QuotasUsed;
            }

            // Service unavailable related
            if (lowerMessage.Contains("service unavailable") ||
                lowerMessage.Contains("temporarily unavailable") ||
                lowerMessage.Contains("overloaded") ||
                lowerMessage.Contains("capacity") ||
                lowerMessage.Contains("server error") ||
                lowerCode.Contains("unavailable") ||
                lowerCode.Contains("overloaded"))
            {
                return AIEngineErrorType.ServiceUnavailable;
            }

            return AIEngineErrorType.Other;
        }
    }
}