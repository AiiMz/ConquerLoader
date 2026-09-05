using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CLCore.Patching
{
    /// <summary>
    /// The network half of the patcher.
    ///
    /// SYNCHRONOUS ON PURPOSE. This runs on the loader's BackgroundWorker, which
    /// is already off the UI thread and already does exactly one thing at a time.
    /// Making it async would buy nothing but a colour on every method in the call
    /// chain, and .NET Framework has no synchronous HttpClient.Send to call
    /// instead - so the waits are explicit GetAwaiter().GetResult() rather than
    /// hidden.
    ///
    /// One HttpClient for the run, so hundreds of possible requests share one
    /// connection and one TLS handshake.
    /// </summary>
    public sealed class PatchServer : IDisposable
    {
        private const int Attempts = 3;

        private readonly HttpClient _http;
        private readonly Uri _baseUrl;

        public PatchServer(Uri baseUrl, TimeSpan timeout)
        {
            _baseUrl = baseUrl;

            // .NET Framework picks its TLS versions from ServicePointManager and
            // its default on an un-patched machine predates TLS 1.2, which every
            // host worth fetching from now requires. Adding rather than assigning
            // leaves anything the host application already enabled alone.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // A Windows build too old to know the value. Nothing to do about
                // it here, and the request below will say so in its own words.
            }

            HttpClientHandler handler = new HttpClientHandler
            {
                // DecompressionMethods.All is .NET Core; these two are what
                // Framework offers and what a web server actually negotiates.
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

                // The patcher writes files to disk from whatever it is handed, so
                // it follows redirects to nowhere. A redirect here would mean the
                // site is misconfigured, and quietly fetching from the new
                // location is how a captive portal serves a few hundred copies of
                // a login page over a working install.
                AllowAutoRedirect = false,
            };

            _http = new HttpClient(handler) { Timeout = timeout };
            _http.DefaultRequestHeaders.Add("User-Agent", "ConquerLoader-Patcher/1.0");
        }

        public Uri BaseUrl
        {
            get { return _baseUrl; }
        }

        public string GetManifestJson()
        {
            Uri url = new Uri(_baseUrl, "manifest.json");

            using (HttpResponseMessage response = Send(url, NotFoundIsMissingManifest))
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Fetches one file and installs it, verified. Retries a few times,
        /// because the failure this is aimed at is a dropped connection on a
        /// domestic line rather than a server that is down - and a run that gave
        /// up on file 300 of 844 would leave an install in a state the next run
        /// has to work out from scratch anyway.
        /// </summary>
        public void Download(ManifestFile entry, string clientRoot)
        {
            Exception last = null;

            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    using (HttpResponseMessage response = Send(ClientPaths.FileUrl(_baseUrl, entry.Path), NotFoundIsMissingFile))
                    using (Stream body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        FileInstaller.Install(clientRoot, entry, body);

                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                    if (attempt < Attempts)
                        Thread.Sleep(TimeSpan.FromSeconds(attempt));
                }
            }

            throw new IOException(entry.Path + ": " + last.Message, last);
        }

        /// <summary>
        /// A hash mismatch is NOT transient and is not retried. It means the
        /// bytes on the server do not match the document describing them, and
        /// asking again three times cannot fix that - it can only turn one clear
        /// error into a slow one. InvalidDataException derives from IOException,
        /// so it has to be excluded explicitly rather than by listing types.
        ///
        /// TaskCanceledException is how HttpClient reports its own timeout,
        /// which is the most transient thing that happens to this code.
        /// </summary>
        private static bool IsTransient(Exception ex)
        {
            if (ex is InvalidDataException) return false;

            return ex is HttpRequestException
                || ex is TaskCanceledException
                || ex is IOException;
        }

        /// <summary>
        /// The two 404s mean opposite things and the hint has to say which.
        /// Sending the file hint for a missing manifest tells a player their
        /// deploy is in progress when what actually happened is that the server
        /// entry points at the wrong URL - which is how a wrong PatchManifestUrl
        /// survives a support conversation.
        /// </summary>
        private const string NotFoundIsMissingManifest =
            " There is no patch server at that address, or the site is not serving one yet.";

        private const string NotFoundIsMissingFile =
            " The server has the manifest but not this file, which usually means a deploy is still in progress.";

        private HttpResponseMessage Send(Uri url, string notFoundHint)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

            // Belt and braces against an intermediate cache. The patch layer is
            // normally served no-store, but this runs on networks nobody here
            // owns.
            request.Headers.Add("Cache-Control", "no-cache");
            request.Headers.Add("Pragma", "no-cache");

            HttpResponseMessage response = _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                response.Dispose();

                // A 404 is worth its own sentence either way: on a file it is the
                // signature of a manifest and a patch layer published out of
                // step, or of a case-sensitivity mismatch between the generator
                // and the web server.
                string detail = status == 404 ? notFoundHint : string.Empty;

                throw new HttpRequestException("HTTP " + status + " for " + url + "." + detail);
            }

            return response;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
