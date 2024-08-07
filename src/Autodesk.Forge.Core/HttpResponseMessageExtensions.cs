﻿/* 
 * Forge SDK
 *
 * The Forge Platform contains an expanding collection of web service components that can be used with Autodesk cloud-based products or your own technologies. Take advantage of Autodesk’s expertise in design and engineering.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System.Net;

namespace Autodesk.Forge.Core
{
    /// <summary>
    /// Ensures that the HTTP response message is a success status code. Throws exceptions for non-success status codes.
    /// </summary>
    public static class HttpResponseMessageExtensions
    {
        /// <summary>
        /// Ensures that the HTTP response message is a success status code. Throws exceptions for non-success status codes.
        /// </summary>
        /// <param name="msg">The HTTP response message.</param>
        /// <returns>The original HTTP response message if it is a success status code.</returns>
        /// <exception cref="TooManyRequestsException">Thrown when the server returns a TooManyRequests status code.</exception>
        /// <exception cref="HttpRequestException">Thrown when the server returns a non-success status code other than TooManyRequests.</exception>
        public static async Task<HttpResponseMessage> EnsureSuccessStatusCodeAsync(this HttpResponseMessage msg)
        {
            string errorMessage = string.Empty;
            if (!msg.IsSuccessStatusCode)
            {
                // Disposing content just like HttpResponseMessage.EnsureSuccessStatusCode
                if (msg.Content != null)
                {
                    // read more detailed error message if available 
                    errorMessage = await msg.Content.ReadAsStringAsync();
                    msg.Content.Dispose();
                }
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    errorMessage = $"\nMore error details:\n{errorMessage}.";
                }
                var message = $"The server returned the non-success status code {(int)msg.StatusCode} ({msg.ReasonPhrase}).{errorMessage}";

                if (msg.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfterHeader = msg.Headers.RetryAfter.Delta;
                    throw new TooManyRequestsException(message, msg.StatusCode, retryAfterHeader);
                }
                
                throw new HttpRequestException(message, null, msg.StatusCode);
            }
            return msg;
        }
    }

    /// <summary>
    /// Exception thrown when the server returns a TooManyRequests status code.
    /// </summary>
    public class TooManyRequestsException : HttpRequestException
    {
        /// <summary>
        /// Exception thrown when the server returns a TooManyRequests status code.
        /// </summary>
        /// <param name="message">Exception message.</param>
        /// <param name="statusCode">Status code.</param>
        /// <param name="retryAfter">Retry after time.</param>
        public TooManyRequestsException(string message, HttpStatusCode statusCode, TimeSpan? retryAfter)
            :base(message, null, statusCode)
        {
            this.RetryAfter = retryAfter;
        }

        /// <summary>
        /// Retry after time.
        /// </summary>
        public TimeSpan? RetryAfter { get; init; }
    }
}
