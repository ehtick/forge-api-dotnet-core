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
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Polly;
using System.Net;
using System.Net.Http.Headers;

namespace Autodesk.Forge.Core
{
    /// <summary>
    /// Represents a handler for Forge API requests.
    /// </summary>
    public class ForgeHandler : DelegatingHandler
    {
        private static SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        private readonly Random rand = new Random();
        private readonly IAsyncPolicy<HttpResponseMessage> resiliencyPolicies;

        /// <summary>
        /// Gets or sets the Forge configuration options.
        /// </summary>
        protected readonly IOptions<ForgeConfiguration> configuration;

        /// <summary>
        /// Gets or sets the token cache.
        /// </summary>
        protected ITokenCache TokenCache { get; private set; }

        private bool IsDefaultClient(string user) => string.IsNullOrEmpty(user) || user == ForgeAgentHandler.defaultAgentName;

        /// <summary>
        /// Initializes a new instance of the <see cref="ForgeHandler"/> class.
        /// </summary>
        /// <param name="configuration">The Forge configuration options.</param>
        public ForgeHandler(IOptions<ForgeConfiguration> configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.TokenCache = new TokenCache();
            this.resiliencyPolicies = GetResiliencyPolicies(GetDefaultTimeout());
        }
        /// <summary>
        /// Gets the default timeout value.
        /// </summary>
        /// <returns>The default timeout value.</returns>
        protected virtual TimeSpan GetDefaultTimeout()
        {
            // use timeout greater than the forge gateways (10s), we handle the GatewayTimeout response
            return TimeSpan.FromSeconds(15);
        }
        /// <summary>
        /// Gets the retry parameters for resiliency policies.
        /// </summary>
        /// <returns>A tuple containing the base delay in milliseconds and the multiplier.</returns>
        protected virtual (int baseDelayInMs, int multiplier) GetRetryParameters()
        {
            return (500, 1000);
        }
        /// <summary>
        /// Sends an HTTP request asynchronously.
        /// </summary>
        /// <param name="request">The HTTP request message.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the request URI is null.</exception>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri == null)
            {
                throw new ArgumentNullException($"{nameof(HttpRequestMessage)}.{nameof(HttpRequestMessage.RequestUri)}");
            }

            IAsyncPolicy<HttpResponseMessage> policies;

            // check if request wants custom timeout
            if (request.Options.TryGetValue(ForgeConfiguration.TimeoutKey, out var timeoutValue))
            {
                policies = GetResiliencyPolicies(TimeSpan.FromSeconds(timeoutValue));
            }
            else
            {
                policies = this.resiliencyPolicies;
            }


            if (request.Headers.Authorization == null &&
                request.Options.TryGetValue(ForgeConfiguration.ScopeKey, out _))
            {
                // no authorization header so we manage authorization
                await RefreshTokenAsync(request, false, cancellationToken);
                // add a retry policy so that we refresh invalid tokens
                policies = policies.WrapAsync(GetTokenRefreshPolicy());
            }
            return await policies.ExecuteAsync(async (ct) => await base.SendAsync(request, ct), cancellationToken);
        }
        /// <summary>
        /// Gets the token refresh policy.
        /// A policy that attempts to retry exactly once when a 401 error is received after obtaining a new token.
        /// </summary>
        /// <returns>The token refresh policy.</returns>
        protected virtual IAsyncPolicy<HttpResponseMessage> GetTokenRefreshPolicy()
        {
            return Policy
                .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.Unauthorized)
                .RetryAsync(
                    retryCount: 1,
                    onRetryAsync: async (outcome, retryNumber, context) => await RefreshTokenAsync(outcome.Result.RequestMessage, true, CancellationToken.None)
                );
        }
        /// <summary>
        /// Gets the resiliency policies for handling HTTP requests.
        /// </summary>
        /// <param name="timeoutValue">The timeout value for the policies.</param>
        /// <returns>The resiliency policies.</returns>
        protected virtual IAsyncPolicy<HttpResponseMessage> GetResiliencyPolicies(TimeSpan timeoutValue)
        {
            // Retry when HttpRequestException is thrown (low level network error) or 
            // the server returns an error code that we think is transient
            //
            int[] retriable = {
                        (int)HttpStatusCode.RequestTimeout, // 408
                        429, //too many requests
                        (int)HttpStatusCode.BadGateway, // 502
                        (int)HttpStatusCode.ServiceUnavailable, // 503
                        (int)HttpStatusCode.GatewayTimeout // 504
                        };
            var (retryBaseDelay, retryMultiplier) = GetRetryParameters();
            var retry = Policy
                .Handle<HttpRequestException>()
                .Or<Polly.Timeout.TimeoutRejectedException>()// thrown by Polly's TimeoutPolicy if the inner call times out
                .OrResult<HttpResponseMessage>(response =>
                {
                    return retriable.Contains((int)response.StatusCode);
                })
                .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: (retryCount, response, context) =>
                {
                    // First see how long the server wants us to wait
                    var serverWait = response.Result?.Headers.RetryAfter?.Delta;
                    // Calculate how long we want to wait in milliseconds
                    var clientWait = (double)rand.Next(retryBaseDelay /*500*/, (int)Math.Pow(2, retryCount) * retryMultiplier /*1000*/);
                    var wait = clientWait;
                    if (serverWait.HasValue)
                    {
                        wait = serverWait.Value.TotalMilliseconds + clientWait;
                    }
                    return TimeSpan.FromMilliseconds(wait);
                },
                onRetryAsync: (response, sleepTime, retryCount, content) => Task.CompletedTask);

            // break circuit after 3 errors and keep it broken for 1 minute
            var breaker = Policy
                .Handle<HttpRequestException>()
                .Or<Polly.Timeout.TimeoutRejectedException>()// thrown by Polly's TimeoutPolicy if the inner call times out
                .OrResult<HttpResponseMessage>(response =>
                {
                    //we want to break the circuit if retriable errors persist or internal errors from the server
                    return retriable.Contains((int)response.StatusCode) || 
                           response.StatusCode == HttpStatusCode.InternalServerError;
                })
                .CircuitBreakerAsync(3, TimeSpan.FromMinutes(1));

            // timeout handler
            // https://github.com/App-vNext/Polly/wiki/Polly-and-HttpClientFactory#use-case-applying-timeouts
            var timeout = Policy.TimeoutAsync<HttpResponseMessage>(timeoutValue);

            // ordering is important here!
            return Policy.WrapAsync<HttpResponseMessage>(breaker, retry, timeout);
        }

        /// <summary>
        /// Refreshes the token asynchronously.
        /// </summary>
        /// <param name="request">The HTTP request message.</param>
        /// <param name="ignoreCache">A flag indicating whether to ignore the cache and always refresh the token.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The task representing the asynchronous operation.</returns>
        protected virtual async Task RefreshTokenAsync(HttpRequestMessage request, bool ignoreCache, CancellationToken cancellationToken)
        {
            if (request.Options.TryGetValue(ForgeConfiguration.ScopeKey, out var scope))
            {
                var user = string.Empty;
                request.Options.TryGetValue(ForgeConfiguration.AgentKey, out user);
                var cacheKey = user + scope;
                // it is possible that multiple threads get here at the same time, only one of them should 
                // attempt to refresh the token. 
                // NOTE: We could use different semaphores for different cacheKey here. It is a minor optimization.
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (ignoreCache || !TokenCache.TryGetValue(cacheKey, out var token))
                    {
                        TimeSpan expiry;
                        (token, expiry) = await this.Get2LeggedTokenAsync(user, scope, cancellationToken);
                        TokenCache.Add(cacheKey, token, expiry);
                    }
                    request.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Gets a 2-legged token asynchronously.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="scope">The scope.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A tuple containing the token and its expiry time.</returns>
        protected virtual async Task<(string, TimeSpan)> Get2LeggedTokenAsync(string user, string scope, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage())
            {
                var config = this.configuration.Value;
                var clientId = this.IsDefaultClient(user) ? config.ClientId : config.Agents[user].ClientId;
                if (string.IsNullOrEmpty(clientId))
                {
                    throw new ArgumentNullException($"{nameof(ForgeConfiguration)}.{nameof(ForgeConfiguration.ClientId)}");
                }
                var clientSecret = this.IsDefaultClient(user) ? config.ClientSecret : config.Agents[user].ClientSecret;
                if (string.IsNullOrEmpty(clientSecret))
                {
                    throw new ArgumentNullException($"{nameof(ForgeConfiguration)}.{nameof(ForgeConfiguration.ClientSecret)}");
                }
                var clientIdSecret = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                request.Content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", scope)
                });
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", clientIdSecret);
                request.RequestUri = config.AuthenticationAddress;
                request.Method = HttpMethod.Post;

                var response = await this.resiliencyPolicies.ExecuteAsync(async () => await base.SendAsync(request, cancellationToken));

                response.EnsureSuccessStatusCode();
                var responseContent = await response.Content.ReadAsStringAsync();
                var resValues = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseContent);
                return (resValues["token_type"] + " " + resValues["access_token"], TimeSpan.FromSeconds(double.Parse(resValues["expires_in"])));
            }
        }
    }
}
