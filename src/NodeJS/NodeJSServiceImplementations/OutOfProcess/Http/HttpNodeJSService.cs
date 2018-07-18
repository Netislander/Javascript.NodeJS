using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jering.JavascriptUtils.NodeJS
{
    /// <summary>
    /// A specialisation of the OutOfProcessNodeInstance base class that uses HTTP to perform RPC invocations.
    ///
    /// The Node child process starts an HTTP listener on an arbitrary available port (except where a nonzero
    /// port number is specified as a constructor parameter), and signals which port was selected using the same
    /// input/output-based mechanism that the base class uses to determine when the child process is ready to
    /// accept RPC invocations.
    /// </summary>
    /// <seealso cref="HostingModels.BaseNodeInstance" />
    public class HttpNodeJSService : OutOfProcessNodeJSService
    {
        private const string SERVER_SCRIPT_NAME = "HttpServer.js";

        private readonly IHttpContentFactory _httpContentFactory;
        private readonly IJsonService _jsonService;
        private readonly IHttpClientService _httpClientService;

        private bool _disposed;
        internal string Endpoint;

        public HttpNodeJSService(IOptions<OutOfProcessNodeJSServiceOptions> outOfProcessNodeHostOptionsAccessor,
            IHttpContentFactory httpContentFactory,
            IEmbeddedResourcesService embeddedResourcesService,
            IHttpClientService httpClientService,
            IJsonService jsonService,
            INodeJSProcessFactory nodeProcessFactory,
            ILogger<HttpNodeJSService> nodeServiceLogger) :
            base(nodeProcessFactory,
                nodeServiceLogger,
                outOfProcessNodeHostOptionsAccessor,
                embeddedResourcesService,
                SERVER_SCRIPT_NAME)
        {
            _httpClientService = httpClientService;
            _jsonService = jsonService;
            _httpContentFactory = httpContentFactory;
        }

        protected override async Task<(bool, T)> TryInvokeAsync<T>(InvocationRequest invocationRequest, CancellationToken cancellationToken)
        {
            using (HttpContent httpContent = _httpContentFactory.Create(invocationRequest))
            {
                // All HttpResponseMessage.Dispose does is call HttpContent.Dispose. Using default options, this is unecessary, for the following reason:
                // HttpClient loads the response content into a MemoryStream
                // FinishSendAsyncBuffered - https://github.com/dotnet/corefx/blob/c42b2cd477976504b1ae0e4b71d48e92f0459d49/src/System.Net.Http/src/System/Net/Http/HttpClient.cs#L468
                // LoadIntoBufferAsync - https://github.com/dotnet/corefx/blob/c42b2cd477976504b1ae0e4b71d48e92f0459d49/src/System.Net.Http/src/System/Net/Http/HttpContent.cs#L374
                // Disposing a MemoryStream instance toggles some flags
                // Dispose - https://github.com/dotnet/corefx/blob/c42b2cd477976504b1ae0e4b71d48e92f0459d49/src/Common/src/CoreLib/System/IO/MemoryStream.cs#L124
                // MemoryStream doesn't use unmanaged resources, so calling Dispose on it is only necessary if you want to
                // toggle those flags.
                HttpResponseMessage httpResponseMessage = await _httpClientService.PostAsync(Endpoint, httpContent, cancellationToken).ConfigureAwait(false);

                if (httpResponseMessage.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, default(T));
                }

                if (httpResponseMessage.StatusCode == HttpStatusCode.InternalServerError)
                {
                    using (Stream responseStream = await httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var stringReader = new StreamReader(responseStream))
                    using (var jsonTextReader = new JsonTextReader(stringReader))
                    {
                        InvocationError invocationError = _jsonService.Deserialize<InvocationError>(jsonTextReader);
                        throw new InvocationException(invocationError.ErrorMessage, invocationError.ErrorStack);
                    }
                }

                if (httpResponseMessage.StatusCode == HttpStatusCode.OK)
                {
                    Stream stream = await httpResponseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var value = default(T);

                    if (typeof(T) == typeof(Stream))
                    {
                        value = (T)(object)stream;
                    }
                    else if (typeof(T) == typeof(string))
                    {
                        // Stream reader closes the stream when it is diposed
                        using (var streamReader = new StreamReader(stream))
                        {
                            value = (T)(object)await streamReader.ReadToEndAsync().ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        using (var streamReader = new StreamReader(stream))
                        using (var jsonTextReader = new JsonTextReader(streamReader))
                        {
                            value = _jsonService.Deserialize<T>(jsonTextReader);
                        }
                    }

                    return (true, value);
                }

                throw new InvocationException($"Http response received with unexpected status code: {httpResponseMessage.StatusCode}.");
            }
        }

        protected override void OnConnectionEstablishedMessageReceived(string connectionEstablishedMessage)
        {
            // Start after message start and "IP - "
            int startIndex = CONNECTION_ESTABLISHED_MESSAGE_START.Length + 5;
            var stringBuilder = new StringBuilder("http://");

            for (int i = startIndex; i < connectionEstablishedMessage.Length; i++)
            {
                char currentChar = connectionEstablishedMessage[i];

                if (currentChar == ':')
                {
                    // ::1
                    stringBuilder.Append("[::1]");
                    i += 2;
                }
                else if (currentChar == ' ')
                {
                    stringBuilder.Append(':');

                    // Skip over "Port - "
                    i += 7;
                    continue;
                }
                else if (currentChar == ']')
                {
                    Endpoint = stringBuilder.ToString();
                    return;
                }
                else
                {
                    stringBuilder.Append(currentChar);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            base.Dispose(disposing);

            _disposed = true;
        }
    }
}
